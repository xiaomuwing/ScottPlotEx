using System.Diagnostics;
using System.Windows.Forms;
using ScottPlot;
using ScottPlotEx.Controls;

namespace ScottPlotEx.SoakTest;

/// <summary>一个负载阶段。</summary>
internal sealed record SoakPhase(
    string Name,
    double DurationSeconds,
    double UiPointsPerSecond,
    double BackgroundPointsPerSecond,
    int BackgroundThreads,
    double InteractionSeconds,
    double StructureOperationSeconds,
    bool BatchFeed)
{
    public static SoakPhase Transition { get; } = new("启动", 1, 0, 0, 0, 0, 0, false);
}

/// <summary>让负载逻辑以最小依赖访问宿主窗体。</summary>
internal sealed class SoakFormHostAccessor
{
    public required Form HostForm { get; init; }
}

/// <summary>
/// 压测负载：按“循环 + 多阶段”的方式驱动控件。
/// 所有直接触碰控件的操作都在 UI 线程上执行；需要模拟“采集线程直接灌数据”时使用后台线程，
/// 从而走控件内部的跨线程编组路径。
/// </summary>
internal sealed class SoakWorkload
{
    private const int TickMilliseconds = 20;
    private const int BatchFeedThreshold = 100;
    private const double StructuralIntervalSeconds = 2.0;

    private readonly SoakContext _context;
    private readonly SoakFormHostAccessor _accessor;
    private readonly SoakOptions _options;
    private readonly SoakLogger _logger;
    private readonly SoakIssues _issues;
    private readonly SoakStats _stats;
    private readonly List<SoakPhase> _phases;
    private readonly double _cycleSeconds;
    private readonly Random _random;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Thread> _backgroundThreads = [];
    private readonly Dictionary<string, (double Max, double Sum, long Count)> _latencyByPhase = [];
    private readonly List<string> _structureOperationLog = [];

    private Thread? _schedulerThread;
    private System.Windows.Forms.Timer? _timerCanary;
    private volatile ScrollingPlotControl? _plot;
    private volatile SoakPlotState _state;
    private volatile bool _backgroundRunning;
    private volatile bool _schedulerStop;
    private int _dispatchPending;
    private long _timerCanaryTicks;
    private DateTime _lastTimerCanaryUtc = DateTime.UtcNow;
    private double _backgroundRatePerThread;
    private DateTime _lastTickUtc = DateTime.UtcNow;

    private double _lastTickSeconds;
    private double _rateCarry;
    private double _phaseStartSeconds;
    private double _nextInteractionAt;
    private double _nextStructureOperationAt;
    private double _nextRenderProbeAt;
    private double _nextSnapshotAt;
    private long _valueIndex;
    private long _backgroundValueIndex;
    private long _skippedCandidates;
    private int _phaseIndex = -1;
    private int _cycleNumber;
    private int _structureOperationIndex;
    private int _integrityViolations;
    private int _publishCounter;
    private int _snapshotIndex;
    private bool _stopped;

    public SoakWorkload(SoakContext context, SoakFormHostAccessor accessor)
    {
        _context = context;
        _accessor = accessor;
        _options = context.Options;
        _logger = context.Logger;
        _issues = context.Issues;
        _stats = context.Stats;
        _random = new Random(_options.Seed);
        _phases = BuildPhases(_options.CycleSeconds, _options.LoadScale);
        _cycleSeconds = _phases.Sum(x => x.DurationSeconds);
        _state = CreateInitialState();
    }

    public SoakPlotState State => _state;

    /// <summary>最近一次负载切片在 UI 线程上执行的时间。</summary>
    public DateTime LastTickUtc => _lastTickUtc;

    /// <summary>WM_TIMER 探针：最近一次收到 WM_TIMER 的时间（用于观测定时器饿死）。</summary>
    public DateTime LastTimerCanaryUtc => _lastTimerCanaryUtc;

    public long TimerCanaryTicks => Interlocked.Read(ref _timerCanaryTicks);

    public string CurrentPhase => _phaseIndex < 0 ? SoakPhase.Transition.Name : _phases[_phaseIndex].Name;

    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    public double PhaseSeconds => _phaseIndex < 0 ? 0 : ElapsedSeconds - _phaseStartSeconds;

    public int CycleNumber => _cycleNumber;

    public IReadOnlyList<string> StructureOperationLog => _structureOperationLog;

    public IReadOnlyDictionary<string, (double Max, double Sum, long Count)> LatencyByPhase => _latencyByPhase;

    public int IntegrityViolationCount => _integrityViolations;

    public int ActiveFeedThreads
    {
        get
        {
            lock (_backgroundThreads)
            {
                return _backgroundThreads.Count;
            }
        }
    }

    /// <summary>记录一次 UI 线程响应延迟（由看门狗线程调用）。</summary>
    public void ReportUiLatency(double latencyMs)
    {
        string phase = State.Phase;

        lock (_latencyByPhase)
        {
            (double Max, double Sum, long Count) current = _latencyByPhase.TryGetValue(phase, out (double Max, double Sum, long Count) value)
                ? value
                : (0, 0, 0);

            _latencyByPhase[phase] = (Math.Max(current.Max, latencyMs), current.Sum + latencyMs, current.Count + 1);
        }

        _stats.ReportUiLatency(latencyMs);

        if (latencyMs >= 1000)
        {
            _stats.CountStall();
            _issues.Warning("UI 响应", $"UI 线程连续 {latencyMs:F0} ms 未响应消息（阶段：{phase}）。");
        }
    }

    /// <summary>创建控件并启动负载调度（必须在 UI 线程上调用）。</summary>
    public void Start()
    {
        CreateControl();
        _clock.Restart();
        _lastTickSeconds = 0;
        _phaseIndex = -1;
        _nextRenderProbeAt = 10.0;
        _nextSnapshotAt = _options.SnapshotMinutes * 60.0;

        // WM_TIMER 探针：只用来观测“窗口消息队列被持续占用时 WM_TIMER 是否还能拿到机会”，
        // 负载本身不依赖它（否则一旦定时器被饿死，压测就会自我中止，测不到真实现象）。
        _timerCanary = new System.Windows.Forms.Timer { Interval = 100 };
        _timerCanary.Tick += (_, _) =>
        {
            Interlocked.Increment(ref _timerCanaryTicks);
            _lastTimerCanaryUtc = DateTime.UtcNow;
        };
        _timerCanary.Start();

        _schedulerThread = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "压测调度线程",
        };
        _schedulerThread.Start();

        _logger.Event("负载", $"负载已启动：循环 {_cycleSeconds / 60:F1} 分钟，档位 {_options.Profile}（×{_options.LoadScale:F2}）。" +
            "负载由独立调度线程驱动（不依赖 WM_TIMER），并附带 WM_TIMER 探针用于观测定时器饿死。");
    }

    /// <summary>停止负载（必须在 UI 线程上调用）。</summary>
    public void Stop()
    {
        _stopped = true;
        _schedulerStop = true;
        _timerCanary?.Stop();
        _schedulerThread?.Join(3000);
        StopBackgroundFeed();
        PublishState(force: true);
    }

    /// <summary>
    /// 负载调度线程：按固定节奏把“一个负载切片”投递到 UI 线程执行。
    /// 之所以不用 System.Windows.Forms.Timer：WM_TIMER 是低优先级合成消息，窗口消息队列被持续占用时
    /// 会被无限期饿死（这正是长时间压测要观测的现象之一），而负载本身不应该因此停摆。
    /// </summary>
    private void SchedulerLoop()
    {
        const double sliceSeconds = 0.2;
        Stopwatch clock = Stopwatch.StartNew();
        double nextDispatch = 0;

        while (!_schedulerStop)
        {
            double now = clock.Elapsed.TotalSeconds;

            if (now >= nextDispatch && Interlocked.CompareExchange(ref _dispatchPending, 1, 0) == 0)
            {
                nextDispatch = now + sliceSeconds;

                try
                {
                    _accessor.HostForm.BeginInvoke(new Action(() =>
                    {
                        Interlocked.Exchange(ref _dispatchPending, 0);
                        OnSlice();
                    }));
                }
                catch (Exception)
                {
                    // 窗口句柄正在销毁
                    Interlocked.Exchange(ref _dispatchPending, 0);
                    return;
                }
            }

            Thread.Sleep(2);
        }
    }

    private static List<SoakPhase> BuildPhases(double cycleSeconds, double scale)
    {
        (string Name, double Share, double UiRate, double BackgroundRate, int Threads, double Interaction, double Structure, bool Batch)[] template =
        [
            ("常态采集（1 点/秒）", 0.30, 1, 0, 0, 0, 0, false),
            ("中速采集 + 交互", 0.25, 20, 0, 0, 3.0, 0, false),
            ("高速突发（200 点/秒）", 0.15, 200, 0, 0, 5.0, 0, true),
            ("多线程极端突发（3000 点/秒）", 0.05, 2000, 1000, 4, 5.0, 0, true),
            ("交互风暴", 0.15, 5, 0, 0, 0.25, 0, false),
            ("结构变化（尺寸/最小化/清空/重建）", 0.10, 10, 0, 0, 1.0, StructuralIntervalSeconds, false),
        ];

        List<SoakPhase> phases = [];

        foreach ((string name, double share, double uiRate, double backgroundRate, int threads, double interaction, double structure, bool batch) in template)
        {
            phases.Add(new SoakPhase(
                name,
                Math.Max(1.0, cycleSeconds * share),
                Math.Max(0.1, uiRate * scale),
                backgroundRate * scale,
                backgroundRate > 0 ? threads : 0,
                interaction,
                structure,
                batch));
        }

        return phases;
    }

    private static SoakPlotState CreateInitialState() =>
        new(0, 0, 0, "Stacked", 0, 1, -0.5, 0.5, SoakPhase.Transition.Name, 0, 0, true, "启动中");

    private void CreateControl()
    {
        ScrollingPlotControl plot = new() { Dock = DockStyle.Fill };
        plot.Plot.Title("ScottPlotEx 时间压力测试");
        plot.Plot.XLabel("数据点序号");
        plot.Plot.YLabel("数值");
        _accessor.HostForm.Controls.Add(plot);
        plot.Visible = true;
        _plot = plot;
    }

    private void RecreateControl()
    {
        ScrollingPlotControl? previous = _plot;

        if (previous is not null)
        {
            _accessor.HostForm.Controls.Remove(previous);
            previous.Dispose();
        }

        CreateControl();
    }

    private void OnSlice()
    {
        if (_stopped)
            return;

        _lastTickUtc = DateTime.UtcNow;

        try
        {
            Tick();
        }
        catch (Exception exception)
        {
            _issues.Error("负载切片", exception);
            _logger.Event("ERROR", "负载执行失败（已记录，继续运行）：" + exception.Message);
        }
    }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double delta = now - _lastTickSeconds;
        _lastTickSeconds = now;

        if (delta <= 0)
            delta = TickMilliseconds / 1000.0;

        // 单次 Tick 的追赶上限：长时间卡顿后不要一次性灌入过量数据
        if (delta > 1.0)
            delta = 1.0;

        SoakPhase phase = ResolvePhase(now);
        double due = (phase.UiPointsPerSecond * delta) + _rateCarry;
        int count = (int)due;
        _rateCarry = due - count;

        if (count > 20_000)
            count = 20_000;

        if (count > 0)
            FeedUi(count, phase);

        EnsureBackgroundFeed(phase.BackgroundThreads, phase.BackgroundPointsPerSecond);

        if (_options.EnableInteractions && phase.InteractionSeconds > 0 && now >= _nextInteractionAt)
        {
            RunRandomInteraction();
            _nextInteractionAt = now + phase.InteractionSeconds;
        }

        if (_options.EnableStructureOperations && phase.StructureOperationSeconds > 0 && now >= _nextStructureOperationAt)
        {
            RunStructureOperation();
            _nextStructureOperationAt = now + phase.StructureOperationSeconds;
        }

        if (now >= _nextRenderProbeAt)
        {
            RunRenderProbe(phase, now);
            _nextRenderProbeAt = now + Math.Max(0.5, _options.RenderProbeMinutes * 60.0);
        }

        if (_options.SnapshotMinutes > 0 && now >= _nextSnapshotAt)
        {
            RunSnapshot();
            _nextSnapshotAt = now + (_options.SnapshotMinutes * 60.0);
        }

        if (++_publishCounter % 25 == 0)
            PublishState();
    }

    private SoakPhase ResolvePhase(double now)
    {
        double position = _cycleSeconds <= 0 ? 0 : now % _cycleSeconds;
        int index = 0;
        double accumulated = 0;

        for (int i = 0; i < _phases.Count; i++)
        {
            if (position < accumulated + _phases[i].DurationSeconds)
            {
                index = i;
                break;
            }

            accumulated += _phases[i].DurationSeconds;
        }

        if (index == _phaseIndex)
            return _phases[_phaseIndex];

        _phaseIndex = index;
        _phaseStartSeconds = now - (position - accumulated);
        _cycleNumber = (int)(now / _cycleSeconds) + 1;

        SoakPhase phase = _phases[index];
        _nextInteractionAt = now + Math.Max(0.1, phase.InteractionSeconds);
        _nextStructureOperationAt = now + 1.0;

        _logger.Event("阶段", $"第 {_cycleNumber} 轮 · {index + 1}/{_phases.Count} {phase.Name}（UI {phase.UiPointsPerSecond:F0} 点/秒" +
            (phase.BackgroundThreads > 0 ? $" + 后台 {phase.BackgroundThreads} 线程 {phase.BackgroundPointsPerSecond:F0} 点/秒" : string.Empty) +
            (phase.InteractionSeconds > 0 ? $"，每 {phase.InteractionSeconds:F2} 秒一次交互" : string.Empty) +
            $"，持续 {phase.DurationSeconds:F0} 秒）");

        return phase;
    }

    private void FeedUi(int count, SoakPhase phase)
    {
        ScrollingPlotControl? plot = _plot;

        if (plot is null || plot.IsDisposed)
            return;

        int skipped = 0;

        if (phase.BatchFeed && count >= BatchFeedThreshold)
        {
            double[] values = new double[count];

            for (int i = 0; i < count; i++)
                values[i] = NextValue(ref skipped);

            plot.AddValues(values);
        }
        else
        {
            for (int i = 0; i < count; i++)
                plot.AddValue(NextValue(ref skipped));
        }

        _stats.AddPoints(count - skipped);
        _stats.AddSkipped(skipped);
        TryAutoClear(plot);
    }

    private double NextValue(ref int skipped)
    {
        long index = ++_valueIndex;

        // 少量非有限值：控件应当忽略并计入 SkippedValueCount（健壮性验证）
        if (index % 9973 == 0)
        {
            _skippedCandidates++;
            skipped++;
            return double.NaN;
        }

        if (index % 19997 == 0)
        {
            _skippedCandidates++;
            skipped++;
            return double.PositiveInfinity;
        }

        double t = index / 10.0;
        return (Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3) + ((_random.NextDouble() - 0.5) * 0.8);
    }

    private void TryAutoClear(ScrollingPlotControl plot)
    {
        if (_options.AutoClearPoints <= 0 || plot.DataCount < _options.AutoClearPoints)
            return;

        int before = plot.DataCount;
        plot.ClearData();
        _stats.CountAutoClear();
        _logger.Event("负载", $"数据点达到 {before:N0}（上限 {_options.AutoClearPoints:N0}），已清空缓冲区并重置视图。");
    }

    private void EnsureBackgroundFeed(int threads, double totalRate)
    {
        lock (_backgroundThreads)
        {
            if (threads <= 0 || totalRate <= 0)
            {
                StopBackgroundFeedLocked();
                return;
            }

            if (_backgroundThreads.Count == threads && _backgroundRunning)
            {
                Volatile.Write(ref _backgroundRatePerThread, totalRate / threads);
                return;
            }

            StopBackgroundFeedLocked();
            Volatile.Write(ref _backgroundRatePerThread, totalRate / threads);
            _backgroundRunning = true;

            for (int i = 0; i < threads; i++)
            {
                int index = i;
                Thread thread = new(() => BackgroundLoop(index))
                {
                    IsBackground = true,
                    Name = $"压测后台灌入线程 {index + 1}",
                };
                _backgroundThreads.Add(thread);
                thread.Start();
            }

            _logger.Event("负载", $"启动 {threads} 个后台灌入线程，每线程约 {Volatile.Read(ref _backgroundRatePerThread):F0} 点/秒（模拟采集线程跨线程小批量灌入）。");
        }
    }

    private void StopBackgroundFeed()
    {
        lock (_backgroundThreads)
        {
            StopBackgroundFeedLocked();
        }
    }

    private void StopBackgroundFeedLocked()
    {
        if (_backgroundThreads.Count == 0)
            return;

        _backgroundRunning = false;
        Thread[] threads = [.. _backgroundThreads];
        _backgroundThreads.Clear();

        foreach (Thread thread in threads)
            thread.Join(5000);
    }

    private void BackgroundLoop(int index)
    {
        // 注意：控件的 AddValue / AddValues 在非 UI 线程上先进入内部待处理列表，由 UI 线程按帧整批写入
        // （同一时刻最多一条合并写入消息）。早期版本逐次 BeginInvoke，逐点调用会淹没消息队列；
        // 这里仍按小批量（25 点或 20 ms）投递，以保持与历史压测结果可比。
        const int BatchSize = 25;
        const double FlushIntervalSeconds = 0.02;

        Stopwatch clock = Stopwatch.StartNew();
        Random random = new(_options.Seed + (index * 7919));
        List<double> staging = new(BatchSize * 2);
        double last = 0;
        double lastFlush = 0;
        double carry = 0;
        long localIndex = Interlocked.Increment(ref _backgroundValueIndex) * 1_000_000L;
        int handleErrors = 0;

        while (_backgroundRunning)
        {
            double now = clock.Elapsed.TotalSeconds;
            double delta = now - last;
            last = now;

            if (delta <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            double due = (Volatile.Read(ref _backgroundRatePerThread) * delta) + carry;
            int count = (int)due;
            carry = due - count;

            if (count > 2_000)
                count = 2_000;

            for (int i = 0; i < count; i++)
            {
                localIndex++;
                double t = localIndex / 10.0;
                staging.Add((Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3) + ((random.NextDouble() - 0.5) * 0.5));
            }

            bool dueToFlush = staging.Count > 0 && (staging.Count >= BatchSize || (now - lastFlush) >= FlushIntervalSeconds);

            if (dueToFlush)
            {
                ScrollingPlotControl? plot = _plot;
                int batch = staging.Count;

                if (plot is null || plot.IsDisposed || !plot.IsHandleCreated)
                {
                    staging.Clear();
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    // 非 UI 线程调用：控件内部会自动编组到 UI 线程
                    plot.AddValues(staging);
                    _stats.AddPoints(batch);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    // 重建控件期间句柄状态变化属正常，忽略（少量记录）
                    if (++handleErrors <= 3)
                        _issues.Warning("后台灌入", $"第 {index + 1} 号线程遇到 {exception.GetType().Name}（控件句柄变化期间属正常，已忽略）。");

                    staging.Clear();
                    Thread.Sleep(1);
                    continue;
                }
                catch (Exception exception)
                {
                    _issues.Error("后台灌入", exception);
                    staging.Clear();
                }

                staging.Clear();
                lastFlush = now;
            }

            Thread.Sleep(1);
        }
    }

    private void PublishState(bool force = false)
    {
        ScrollingPlotControl? plot = _plot;

        if (plot is null || plot.IsDisposed)
        {
            _state = CreateInitialState() with { ElapsedSeconds = ElapsedSeconds, Phase = CurrentPhase };
            return;
        }

        try
        {
            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            bool valid = CheckIntegrity(plot, limits, out string limitsText);

            _state = new SoakPlotState(
                plot.DataCount,
                plot.LatestX,
                plot.SkippedValueCount,
                plot.Mode.ToString(),
                limits.Left,
                limits.Right,
                limits.Bottom,
                limits.Top,
                CurrentPhase,
                PhaseSeconds,
                ElapsedSeconds,
                valid,
                limitsText);
        }
        catch (Exception exception)
        {
            if (force)
                _issues.Warning("状态发布", exception.Message);
        }
    }

    /// <summary>不变量检查：坐标轴必须有效，数据量、点数与 X 轴序号必须一致。</summary>
    private bool CheckIntegrity(ScrollingPlotControl plot, ScottPlot.AxisLimits limits, out string text)
    {
        double xMin = Math.Min(limits.Left, limits.Right);
        double xMax = Math.Max(limits.Left, limits.Right);
        double yMin = Math.Min(limits.Bottom, limits.Top);
        double yMax = Math.Max(limits.Bottom, limits.Top);
        text = $"X: [{xMin:F3}, {xMax:F3}] Y: [{yMin:F3}, {yMax:F3}]";

        List<string> failures = [];

        if (!limits.IsReal)
            failures.Add("坐标轴不是实数范围（IsReal = false）");

        if (!double.IsFinite(limits.Left) || !double.IsFinite(limits.Right)
            || !double.IsFinite(limits.Bottom) || !double.IsFinite(limits.Top))
        {
            failures.Add("坐标轴包含非有限值");
        }

        if (xMax <= xMin)
            failures.Add($"X 轴范围退化：{xMin:F3} ~ {xMax:F3}");

        if (yMax <= yMin)
            failures.Add($"Y 轴范围退化：{yMin:F3} ~ {yMax:F3}");

        if (plot.Values.Count != plot.DataCount)
            failures.Add($"数据点数量不一致：Values.Count = {plot.Values.Count}，DataCount = {plot.DataCount}");

        if (plot.DataCount > 0 && Math.Abs(plot.LatestX - plot.DataCount) > 1e-9)
            failures.Add($"最新点序号与数据量不一致：LatestX = {plot.LatestX}，DataCount = {plot.DataCount}");

        if (failures.Count == 0)
            return true;

        _stats.CountIntegrityViolation();
        _integrityViolations++;

        string detail = string.Join("；", failures) + $"（当前 {text}，模式 {plot.Mode}，数据点 {plot.DataCount}）";
        _issues.Error("不变量", detail);
        _logger.Event("ERROR", "控件不变量被破坏：" + detail);
        return false;
    }

    private Control? CanvasControl()
    {
        ScrollingPlotControl? plot = _plot;

        if (plot is null || plot.IsDisposed)
            return null;

        // 控件的曲线画布（接收真实鼠标 / 键盘消息并转发给 ScottPlot）
        return plot.Canvas;
    }

    /// <summary>确保绘图区至少渲染过一次（鼠标命中测试依赖最近一次渲染的绘图区矩形），并返回绘图区矩形。</summary>
    private ScottPlot.PixelRect EnsureRendered(int attempts = 30)
    {
        ScottPlot.PixelRect rect = default;
        ScrollingPlotControl? plot = _plot;

        if (plot is null || plot.IsDisposed)
            return rect;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            rect = plot.Plot.RenderManager.LastRender.DataRect;

            if (rect.Width > 0 && rect.Height > 0)
            {
                ScottPlot.Pixel probe = new(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

                if (plot.GetPlotAtPixel(probe) is not null)
                    return rect;
            }

            plot.Refresh();
            Application.DoEvents();
            Thread.Sleep(5);
        }

        return rect;
    }

    private static Point PointInDataArea(ScottPlot.PixelRect rect, double fractionX, double fractionY)
    {
        double left = Math.Min(rect.Left, rect.Right);
        double top = Math.Min(rect.Top, rect.Bottom);
        double width = Math.Abs(rect.Right - rect.Left);
        double height = Math.Abs(rect.Bottom - rect.Top);

        return new Point(
            (int)Math.Round(left + (width * fractionX)),
            (int)Math.Round(top + (height * fractionY)));
    }

    private void RunRandomInteraction()
    {
        ScrollingPlotControl? plot = _plot;
        Control? canvas = CanvasControl();

        if (plot is null || canvas is null || canvas.IsDisposed)
            return;

        ScottPlot.PixelRect rect = EnsureRendered(attempts: 4);

        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        Point from = PointInDataArea(rect, 0.15 + (_random.NextDouble() * 0.7), 0.15 + (_random.NextDouble() * 0.7));
        Point to = PointInDataArea(rect, 0.15 + (_random.NextDouble() * 0.7), 0.15 + (_random.NextDouble() * 0.7));
        MouseButtons button = _random.Next(10) switch
        {
            0 => MouseButtons.Right,
            <= 4 => MouseButtons.Middle,
            _ => MouseButtons.Left,
        };

        bool realMessage = _random.Next(5) == 0;

        switch (_random.Next(9))
        {
            case 0:
            case 1:
                if (realMessage)
                    InputSimulator.PostDrag(canvas.Handle, from, to, button);
                else
                    InputSimulator.Drag(canvas, from, to, button);
                break;
            case 2:
                int delta = _random.Next(2) == 0 ? 120 : -120;

                if (realMessage)
                    InputSimulator.PostWheel(canvas.Handle, from, delta);
                else
                    InputSimulator.Wheel(canvas, from, delta);
                break;
            case 3:
                InputSimulator.Click(canvas, from, MouseButtons.Middle);
                break;
            case 4:
                InputSimulator.Click(canvas, from, MouseButtons.Left);
                break;
            case 5:
                OpenAndCloseContextMenu(plot, from);
                break;
            case 6:
                InputSimulator.Move(canvas, to);
                break;
            case 7:
                Keys key = _random.Next(2) == 0 ? Keys.Left : Keys.Right;
                InputSimulator.KeyDown(canvas, key);
                InputSimulator.KeyUp(canvas, key);
                break;
            default:
                plot.Mode = _random.Next(2) == 0 ? ScrollingMode.Stacked : ScrollingMode.Follow;
                _stats.CountModeSwitch();
                break;
        }

        _stats.CountInteraction(realMessage);
    }

    private void OpenAndCloseContextMenu(ScrollingPlotControl plot, Point point)
    {
        if (point.X <= 0 || point.Y <= 0)
            return;

        plot.ShowContextMenu(new ScottPlot.Pixel(point.X, point.Y));
        _stats.CountMenu();
        Application.DoEvents();

        ContextMenuStrip? menu = plot.PlotMenu.LastMenuShown;

        if (menu is { IsDisposed: false })
            menu.Close();
    }

    private void RunStructureOperation()
    {
        ScrollingPlotControl? plot = _plot;

        if (plot is null || plot.IsDisposed)
        {
            RecreateControl();
            _stats.CountStructureOperation(controlRecreated: true);
            _structureOperationLog.Add("释放并重建控件（控件已释放）");
            return;
        }

        string name;

        switch (_structureOperationIndex++ % 14)
        {
            case 0:
                name = "窗口放大到 1600×900";
                _accessor.HostForm.ClientSize = new Size(1600, 900);
                break;
            case 1:
                name = "窗口缩小到 700×420";
                _accessor.HostForm.ClientSize = new Size(700, 420);
                break;
            case 2:
                name = "窗口恢复到 1100×660";
                _accessor.HostForm.ClientSize = new Size(1100, 660);
                break;
            case 3:
                name = "最小化并恢复";
                MinimizeAndRestore();
                break;
            case 4:
                name = "清空数据";
                plot.ClearData();
                break;
            case 5:
                name = "切换滚动模式";
                plot.Mode = plot.Mode switch
                {
                    ScrollingMode.Stacked => ScrollingMode.Follow,
                    ScrollingMode.Follow => ScrollingMode.Manual,
                    _ => ScrollingMode.Stacked,
                };
                _stats.CountModeSwitch();
                break;
            case 6:
                name = "调整 X 轴显示百分比";
                double percent = new double[] { 0, 30, 50, 90, 100 }[_random.Next(5)];
                plot.SetXAxisDisplayPercent(percent, plot.Mode == ScrollingMode.Manual ? ScrollingMode.Stacked : plot.Mode);
                _stats.CountDisplayPercentChange();
                break;
            case 7:
                name = "整体显示（显示全部数据）";
                plot.ShowAllData();
                _stats.CountDisplayPercentChange();
                break;
            case 8:
                name = "打开并关闭帮助窗口";
                OpenAndCloseHelpWindow(plot);
                break;
            case 9:
                name = "打开并关闭右键菜单";
                OpenAndCloseContextMenu(plot, PointInDataArea(EnsureRendered(attempts: 4), 0.5, 0.5));
                break;
            case 10:
                name = "Reset 重建绘图对象";
                plot.Reset();
                break;
            case 11:
                name = "释放并重建控件";
                RecreateControl();
                _stats.CountStructureOperation(controlRecreated: true);
                _structureOperationLog.Add(name);
                return;
            case 12:
                name = "键盘平移";
                Control? canvas = CanvasControl();

                if (canvas is not null && !canvas.IsDisposed)
                {
                    InputSimulator.KeyDown(canvas, Keys.Right);
                    InputSimulator.KeyUp(canvas, Keys.Right);
                }

                break;
            default:
                name = "滚轮缩放";
                Control? wheelCanvas = CanvasControl();

                if (wheelCanvas is not null && !wheelCanvas.IsDisposed)
                    InputSimulator.Wheel(wheelCanvas, PointInDataArea(EnsureRendered(attempts: 4), 0.5, 0.5), 120);

                break;
        }

        _stats.CountStructureOperation();
        _structureOperationLog.Add(name);
    }

    private void MinimizeAndRestore()
    {
        Form form = _accessor.HostForm;
        form.WindowState = FormWindowState.Minimized;

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(600);

        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        form.WindowState = FormWindowState.Normal;
        Application.DoEvents();
    }

    private void OpenAndCloseHelpWindow(ScrollingPlotControl plot)
    {
        plot.ShowHelp();
        Application.DoEvents();
        Thread.Sleep(30);

        foreach (Form form in Application.OpenForms.OfType<FormHelp>().ToArray())
        {
            _stats.CountHelpWindow();
            form.Close();
            form.Dispose();
        }

        Application.DoEvents();
    }

    private void RunRenderProbe(SoakPhase phase, double now)
    {
        ScrollingPlotControl? plot = _plot;

        if (plot is null || plot.IsDisposed)
            return;

        try
        {
            plot.Refresh(); // 预热
            List<double> samples = [];

            for (int i = 0; i < 3; i++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                plot.Refresh();
                stopwatch.Stop();
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            samples.Sort();
            _stats.ReportRender(samples[1], plot.DataCount, phase.Name, now, PhaseSeconds);
        }
        catch (Exception exception)
        {
            _issues.Warning("渲染探针", exception.Message);
        }
    }

    private void RunSnapshot()
    {
        ScrollingPlotControl? plot = _plot;

        if (plot is null || plot.IsDisposed)
            return;

        try
        {
            Directory.CreateDirectory(_context.SnapshotDirectory);
            _snapshotIndex++;
            string path = Path.Combine(_context.SnapshotDirectory, $"snapshot-{_snapshotIndex:D2}-{DateTime.Now:HHmmss}.png");

            using ScottPlot.Image image = plot.Plot.GetImage(1100, 660);
            image.SavePng(path);

            _stats.CountSnapshot();
            _logger.Event("快照", $"已保存截图 {Path.GetFileName(path)}（数据点 {plot.DataCount:N0}，模式 {plot.Mode}）。");
        }
        catch (Exception exception)
        {
            _issues.Warning("截图", exception.Message);
        }
    }
}
