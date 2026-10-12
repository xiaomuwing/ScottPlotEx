using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace ScottPlotEx.SoakTest;

/// <summary>一次“数据写入方式”测量结果。</summary>
internal sealed record IngestMeasurement(
    string Key,
    string Name,
    string Feeding,
    string Rendering,
    int Round,
    double Seconds,
    int Curves,
    double TargetPointsPerSecond,
    double AchievedPointsPerSecond,
    double DroppedPointsPerSecond,
    double CpuPercentSingleCore,
    double UiLatencyP50Ms,
    double UiLatencyP95Ms,
    double UiLatencyMaxMs,
    double Fps,
    double ForcedRefreshMs,
    double ManagedHeapDeltaMb,
    double WorkingSetDeltaMb,
    int Gc0Delta,
    int Gc2Delta,
    string Notes);

/// <summary>
/// 数据写入方式对比实验：在 10 条曲线 × 每条 1000 点/秒（合计 10,000 点/秒）的工况下，
/// 比较几种“把数据送进控件 / ScottPlot”的通路在 CPU、UI 延迟、帧率、内存上的差异。
/// 只做测量，不修改被测控件。
/// </summary>
internal static class IngestBenchmark
{
    private const int WindowPoints = 60_000;   // 每条曲线显示/保留 60 秒（1000 点/秒）
    private const double FrameIntervalSeconds = 1.0 / 60.0;
    private static readonly double[] YRange = [-15, 15];

    public static int Run(SoakOptions options, SoakContext context, SoakHost host)
    {
        SoakLogger logger = context.Logger;
        int curves = options.IngestCurves;
        double ratePerCurve = options.IngestRatePerCurve;
        double totalRate = curves * ratePerCurve;
        double seconds = options.IngestSecondsPerRound;
        int rounds = options.IngestRounds;

        logger.Event("实验", $"数据写入方式对比：{curves} 条曲线 × {ratePerCurve:F0} 点/秒 = {totalRate:F0} 点/秒，" +
            $"每条保留/显示 {WindowPoints:N0} 点，每轮 {seconds:F0} 秒 × {rounds} 轮，随机种子 {options.Seed}。");

        List<IngestMeasurement> results = [];
        List<string> notes = [];

        for (int round = 1; round <= rounds; round++)
        {
            foreach (Strategy strategy in BuildStrategies())
            {
                logger.Event("实验", $"第 {round} 轮 · {strategy.Name}：开始（{seconds:F0} 秒）。");

                IngestMeasurement measurement;

                try
                {
                    measurement = Measure(host, context, strategy, round, seconds, curves, ratePerCurve);
                    results.Add(measurement);
                    logger.Event(
                        "实验",
                        $"第 {round} 轮 · {strategy.Name}：达成 {measurement.AchievedPointsPerSecond:N0} 点/秒，" +
                        $"CPU 单核 {measurement.CpuPercentSingleCore:F0}%，UI 延迟 p95 {measurement.UiLatencyP95Ms:F1} ms，" +
                        $"FPS {measurement.Fps:F1}，强制整帧 {measurement.ForcedRefreshMs:F1} ms，" +
                        $"托管堆 {measurement.ManagedHeapDeltaMb:+0.0;-0.0;0.0} MB，工作集 {measurement.WorkingSetDeltaMb:+0.0;-0.0;0.0} MB。");
                }
                catch (Exception exception)
                {
                    notes.Add($"{strategy.Name}：测量失败 —— {exception.GetType().Name}: {exception.Message}");
                    logger.Event("ERROR", $"{strategy.Name} 测量失败：{exception}");
                }

                // 两个方案之间留出冷却时间，避免上一轮的 GC / 渲染积压影响下一轮
                Thread.Sleep(3000);
            }
        }

        string report = BuildReport(options, results, notes, curves, ratePerCurve, seconds, rounds);
        string reportPath = Path.Combine(context.RunDirectory, "ingest-benchmark-report.md");
        File.WriteAllText(reportPath, report, Encoding.UTF8);

        string latestPath = Path.Combine(context.RootDirectory, "..", "ingest-benchmark-report.md");
        File.WriteAllText(Path.GetFullPath(latestPath), report, Encoding.UTF8);

        logger.Event("实验", "对比实验完成，报告：" + reportPath);
        Console.WriteLine();
        Console.WriteLine(report);

        return 0;
    }

    private static IEnumerable<Strategy> BuildStrategies() =>
    [
        new(
            "S1",
            "S1 控件现成 API · 后台线程逐点调用（跨线程编组）",
            "后台线程逐点 AddValue（控件内部按帧合并编组）",
            "控件自带 Signal（全量数据，无窗口裁剪）",
            StrategyKind.ControlCrossThread,
            1),
        new(
            "S2",
            "S2 控件现成 API · UI 线程批量 AddValues",
            "UI 线程每帧批量 AddValues（合并渲染）",
            "控件自带 Signal（全量数据，无窗口裁剪）",
            StrategyKind.ControlUiBatch,
            1),
        new(
            "S3",
            "S3 自建定长缓冲 + 只画窗口尾部（后台直写，无编组）",
            "后台线程直写预分配数组（无跨线程调用）",
            "Signal + MinRenderIndex/MaxRenderIndex 只画最近 60 秒",
            StrategyKind.RingTailSignal,
            0),
        new(
            "S4",
            "S4 ScottPlot DataStreamer（原生定容量环形流）",
            "UI 线程每帧批量 Add（定容量环形缓冲）",
            "DataStreamer 内置视图（只画缓冲内的点）",
            StrategyKind.DataStreamer,
            0),
        new(
            "S5",
            "S5 ScottPlot DataLogger + ViewSlide（原生流式 + 逐帧抽稀）",
            "UI 线程每帧批量 Add",
            "DataLogger 渲染时自动抽稀（内存持续增长）",
            StrategyKind.DataLogger,
            0),
    ];

    private enum StrategyKind
    {
        ControlCrossThread,
        ControlUiBatch,
        RingTailSignal,
        DataStreamer,
        DataLogger,
    }

    private sealed record Strategy(
        string Key,
        string Name,
        string Feeding,
        string Rendering,
        StrategyKind Kind,
        int CurvesThroughControl);

    /// <summary>执行一次测量。</summary>
    private static IngestMeasurement Measure(
        SoakHost host,
        SoakContext context,
        Strategy strategy,
        int round,
        double seconds,
        int curves,
        double ratePerCurve)
    {
        SoakLogger logger = context.Logger;
        BenchFixture fixture = new(host, context, strategy, curves, ratePerCurve);

        try
        {
            host.Invoke(fixture.Setup);

            // 预热：让 JIT、Signal 数组与渲染管线进入稳定状态
            fixture.RunPhase(TimeSpan.FromSeconds(8), collect: false);

            IngestMeasurement measurement = fixture.RunPhase(TimeSpan.FromSeconds(seconds), collect: true);
            return measurement with { Round = round };
        }
        finally
        {
            host.Invoke(fixture.Teardown);
        }
    }

    /// <summary>一个方案的完整夹具：建曲线、按策略写入、按帧刷新视图并采集指标。</summary>
    private sealed class BenchFixture
    {
        private readonly SoakHost _host;
        private readonly SoakOptions _options;
        private readonly Strategy _strategy;
        private readonly int _curves;
        private readonly double _ratePerCurve;
        private readonly List<double[]> _buffers = [];
        private readonly List<ScottPlot.Plottables.Signal> _signals = [];
        private readonly List<ScottPlot.Plottables.DataStreamer> _streamers = [];
        private readonly List<ScottPlot.Plottables.DataLogger> _loggers = [];
        private readonly List<Thread> _writers = [];
        private readonly List<double> _uiLatencies = [];
        private readonly object _gate = new();
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private readonly Random _controlRandom = new(20260923);
        private readonly Random _streamerRandom = new(20260924);
        private readonly Random _loggerRandom = new(20260925);

        private ScrollingPlotControl? _plot;
        private Thread? _scheduler;
        private Thread? _latencyProbe;
        private volatile bool _stop;
        private volatile bool _writersStop;
        private int _curveCount;               // 本次方案实际承载的曲线数
        private double _feedRatePerCurve;      // 每条曲线实际写入速率
        private double _lastFrameSeconds;
        private double _frameCarry;
        private long _controlSubmitted;
        private long[] _totalWritten = [];      // 单调累计写入数（环形缓冲的位置指针会回绕，不能直接用它算速率）
        private int[] _written = [];           // 每条曲线已写入的点数（环形缓冲用）
        private int _controlWritten;           // 控件自带信号写入的点数
        private long _framesSeen;
        private string? _lastFrameError;

        public BenchFixture(SoakHost host, SoakContext context, Strategy strategy, int curves, double ratePerCurve)
        {
            _host = host;
            _options = context.Options;
            _strategy = strategy;
            _curves = curves;
            _ratePerCurve = ratePerCurve;
        }

        public void Setup()
        {
            Form form = _host.Form;

            // 释放上一种方案留下的控件，避免测量之间互相干扰
            foreach (Control existing in form.Controls.OfType<Control>().ToArray())
            {
                form.Controls.Remove(existing);
                existing.Dispose();
            }

            ScrollingPlotControl plot = new() { Dock = DockStyle.Fill };
            form.Controls.Add(plot);
            plot.Visible = true;
            plot.Mode = ScrollingMode.Manual; // 统一：本实验里控件不做自动滚动，视图由实验控制
            _plot = plot;

            _buffers.Clear();
            _signals.Clear();
            _streamers.Clear();
            _loggers.Clear();

            // 控件现成 API 只有 1 条曲线，因此 S1/S2 用它承载“全部 10,000 点/秒”，
            // 用来测控件自身通路的极限；S3~S5 则承载完整的 10 条曲线。
            _curveCount = _strategy.Kind is StrategyKind.ControlCrossThread or StrategyKind.ControlUiBatch ? 1 : _curves;
            _feedRatePerCurve = _curveCount == 1 ? _curves * _ratePerCurve : _ratePerCurve;
            _written = new int[_curveCount];
            _totalWritten = new long[_curveCount];

            for (int i = 0; i < _curveCount; i++)
                _buffers.Add(new double[WindowPoints * 2]);

            switch (_strategy.Kind)
            {
                case StrategyKind.ControlCrossThread:
                case StrategyKind.ControlUiBatch:
                    break;

                case StrategyKind.RingTailSignal:
                    for (int i = 0; i < _curveCount; i++)
                        _signals.Add(plot.Plot.Add.Signal(_buffers[i], period: 1));

                    break;

                case StrategyKind.DataStreamer:
                    for (int i = 0; i < _curveCount; i++)
                    {
                        ScottPlot.Plottables.DataStreamer streamer = plot.Plot.Add.DataStreamer(WindowPoints, period: 1);
                        streamer.ManageAxisLimits = true;
                        plot.Plot.Axes.ContinuouslyAutoscale = false;
                        _streamers.Add(streamer);
                    }

                    break;

                case StrategyKind.DataLogger:
                    for (int i = 0; i < _curveCount; i++)
                    {
                        ScottPlot.Plottables.DataLogger dataLogger = plot.Plot.Add.DataLogger();
                        dataLogger.Period = 1;
                        dataLogger.ManageAxisLimits = true;
                        _loggers.Add(dataLogger);
                    }

                    break;
            }

            plot.Plot.Axes.SetLimits(-WindowPoints, 0, YRange[0], YRange[1]);
            _stop = false;
            _writersStop = false;
            _controlWritten = 0;
            _controlSubmitted = 0;
            _framesSeen = 0;
            _lastFrameSeconds = 0;
            _frameCarry = 0;

            // 预填充：让每种方案在测量开始时就已经处于“窗口填满”的稳态，
            // 否则前 60 秒的爬坡段会稀释 CPU / 帧率 / 绘制耗时。
            Prefill();

            // 写入线程：只有“后台直写数组”和“跨线程调用控件”两种方案需要
            if (_strategy.Kind is StrategyKind.ControlCrossThread or StrategyKind.RingTailSignal)
            {
                for (int i = 0; i < _curveCount; i++)
                {
                    int index = i;
                    Thread writer = new(() => WriterLoop(index)) { IsBackground = true, Name = $"写入线程 {i + 1}" };
                    _writers.Add(writer);
                    writer.Start();
                }
            }

            _scheduler = new Thread(SchedulerLoop) { IsBackground = true, Name = "帧调度线程" };
            _scheduler.Start();

            _latencyProbe = new Thread(LatencyLoop) { IsBackground = true, Name = "UI 延迟探针" };
            _latencyProbe.Start();
        }

        /// <summary>把每条曲线预填到窗口满（模拟“系统已经连续采了很久”的稳态）。</summary>
        private void Prefill()
        {
            double[] values = new double[WindowPoints];

            for (int i = 0; i < values.Length; i++)
            {
                double t = i / 25.0;
                values[i] = (Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3);
            }

            switch (_strategy.Kind)
            {
                case StrategyKind.ControlCrossThread:
                case StrategyKind.ControlUiBatch:
                    _plot!.AddValues(values);
                    _controlWritten = _plot.DataCount;
                    _controlSubmitted = _controlWritten;
                    break;

                case StrategyKind.RingTailSignal:
                    for (int c = 0; c < _curveCount; c++)
                    {
                        Array.Copy(values, _buffers[c], values.Length);
                        _written[c] = values.Length;
                        _totalWritten[c] = values.Length;
                        _signals[c].MinRenderIndex = 0;
                        _signals[c].MaxRenderIndex = values.Length - 1;
                    }

                    break;

                case StrategyKind.DataStreamer:
                    foreach (ScottPlot.Plottables.DataStreamer streamer in _streamers)
                        streamer.Add(values);

                    break;

                case StrategyKind.DataLogger:
                    foreach (ScottPlot.Plottables.DataLogger dataLogger in _loggers)
                        dataLogger.Add(values);

                    break;
            }
        }

        public void Teardown()
        {
            _stop = true;
            _writersStop = true;
            _scheduler?.Join(2000);
            _latencyProbe?.Join(2000);

            foreach (Thread writer in _writers)
                writer.Join(2000);

            _writers.Clear();
            _scheduler = null;
            _latencyProbe = null;
        }

        /// <summary>写入线程：按目标速率产生数据。</summary>
        private void WriterLoop(int curveIndex)
        {
            Stopwatch clock = Stopwatch.StartNew();
            Random random = new(_options.Seed + curveIndex);
            double last = 0;
            double carry = 0;
            long index = 0;

            while (!_writersStop)
            {
                double now = clock.Elapsed.TotalSeconds;
                double delta = now - last;
                last = now;

                if (delta <= 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                double due = (_feedRatePerCurve * delta) + carry;
                int count = (int)due;
                carry = due - count;

                if (count <= 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                if (count > 2_000)
                    count = 2_000;

                if (_strategy.Kind == StrategyKind.ControlCrossThread)
                {
                    // 逐点跨线程调用控件 API（控件内部按帧合并编组）——这是最省事的写法
                    ScrollingPlotControl? plot = _plot;

                    if (plot is not null && !plot.IsDisposed)
                    {
                        // 安全阀：积压过多时暂停写入，避免压测自身把内存吃满
                        long backlog = Interlocked.Read(ref _controlSubmitted) - plot.DataCount;

                        if (backlog > 200_000)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        for (int i = 0; i < count; i++)
                        {
                            index++;
                            double t = index / 25.0;
                            double value = (Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3) + ((random.NextDouble() - 0.5) * 0.6);

                            try
                            {
                                plot.AddValue(value);
                                Interlocked.Increment(ref _controlSubmitted);
                            }
                            catch (Exception)
                            {
                                // 控件状态变化期间忽略
                            }
                        }
                    }
                }
                else
                {
                    AppendToBuffer(curveIndex, count, ref index, random);
                }

                Thread.Sleep(1);
            }
        }

        private void AppendToBuffer(int curveIndex, int count, ref long index, Random random)
        {
            double[] buffer = _buffers[curveIndex];
            ref int written = ref _written[curveIndex];
            long total = _totalWritten[curveIndex];

            for (int i = 0; i < count; i++)
            {
                index++;
                double t = index / 25.0;
                double value = (Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3) + ((random.NextDouble() - 0.5) * 0.6);

                if (written >= buffer.Length)
                {
                    // 紧凑：把尾部窗口整体前移，保持“定内存 + 只画尾部”
                    int keep = WindowPoints;
                    Array.Copy(buffer, buffer.Length - keep, buffer, 0, keep);
                    written = keep;
                }

                buffer[written++] = value;
                total++;
            }

            _totalWritten[curveIndex] = total;
        }

        private static double[] BuildValues(ref long index, int count, Random random)
        {
            double[] values = new double[count];

            for (int i = 0; i < count; i++)
            {
                index++;
                double t = index / 25.0;
                values[i] = (Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3) + ((random.NextDouble() - 0.5) * 0.6);
            }

            return values;
        }

        /// <summary>帧调度线程：把“一帧”投递到 UI 线程执行。</summary>
        private void SchedulerLoop()
        {
            Stopwatch clock = Stopwatch.StartNew();
            double next = 0;

            while (!_stop)
            {
                double now = clock.Elapsed.TotalSeconds;

                if (now >= next)
                {
                    next = now + FrameIntervalSeconds;

                    try
                    {
                        _host.Form.BeginInvoke(new Action(OnFrame));
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }

                Thread.Sleep(1);
            }
        }

        /// <summary>一帧：按策略写入（UI 线程方案）并更新视图。</summary>
        private void OnFrame()
        {
            ScrollingPlotControl? plot = _plot;

            if (plot is null || plot.IsDisposed)
                return;

            try
            {
                // 按“距上一帧的真实耗时”追赶写入量：帧率低于 60 FPS 时也能保持目标速率
                double now = _frameClock.Elapsed.TotalSeconds;
                double delta = now - _lastFrameSeconds;
                _lastFrameSeconds = now;

                if (delta <= 0 || delta > 0.5)
                    delta = FrameIntervalSeconds;

                double due = (_feedRatePerCurve * delta) + _frameCarry;
                int framePoints = (int)due;
                _frameCarry = due - framePoints;

                switch (_strategy.Kind)
                {
                    case StrategyKind.ControlUiBatch:
                        // 控件 API：每帧把该曲线应写入的点批量交给控件
                        FeedControlBatch(framePoints);
                        break;

                    case StrategyKind.DataStreamer:
                        FeedStreamers(framePoints);
                        break;

                    case StrategyKind.DataLogger:
                        FeedLoggers(framePoints);
                        break;
                }

                // 视图：统一显示“每条曲线最近 60 秒”
                if (_strategy.Kind is StrategyKind.RingTailSignal)
                {
                    for (int i = 0; i < _signals.Count; i++)
                    {
                        int written = _written[i];
                        _signals[i].MinRenderIndex = Math.Max(0, written - WindowPoints);
                        _signals[i].MaxRenderIndex = Math.Max(0, written - 1);
                    }

                    int reference = _written.Length > 0 ? _written[0] : 0;
                    plot.Plot.Axes.SetLimits(Math.Max(0, reference - WindowPoints), Math.Max(WindowPoints, reference), YRange[0], YRange[1]);
                }
                else if (_strategy.Kind is StrategyKind.ControlCrossThread or StrategyKind.ControlUiBatch)
                {
                    _controlWritten = plot.DataCount;
                    int reference = _controlWritten;
                    plot.Plot.Axes.SetLimits(Math.Max(0, reference - WindowPoints), Math.Max(WindowPoints, reference), YRange[0], YRange[1]);
                }
                else if (_strategy.Kind is StrategyKind.DataLogger)
                {
                    // DataLogger 的原生“跟随最新数据”视图：只显示最近 60 秒，渲染时自动抽稀
                    foreach (ScottPlot.Plottables.DataLogger dataLogger in _loggers)
                        dataLogger.ViewSlide(WindowPoints);
                }

                plot.RequestRender();
                Interlocked.Increment(ref _framesSeen);
            }
            catch (Exception exception)
            {
                _lastFrameError ??= $"{exception.GetType().Name}: {exception.Message}";
            }
        }

        private void FeedControlBatch(int points)
        {
            if (points <= 0)
                return;

            ScrollingPlotControl plot = _plot!;
            long baseIndex = Math.Max(_controlWritten, plot.DataCount);
            double[] values = new double[points];

            for (int i = 0; i < points; i++)
            {
                double t = (baseIndex + i + 1) / 25.0;
                values[i] = (Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3) + ((_controlRandom.NextDouble() - 0.5) * 0.6);
            }

            plot.AddValues(values);
            _controlSubmitted += points;
        }

        private void FeedStreamers(int points)
        {
            if (points <= 0)
                return;

            double[] values = new double[points];

            for (int c = 0; c < _streamers.Count; c++)
            {
                long baseIndex = _streamers[c].CountTotal;

                for (int i = 0; i < points; i++)
                {
                    double t = (baseIndex + i) / 25.0;
                    values[i] = (Math.Sin(t) * 10) + (Math.Cos(t / 7.0) * 3) + ((_streamerRandom.NextDouble() - 0.5) * 0.6);
                }

                _streamers[c].Add(values);
            }

        }

        private void FeedLoggers(int points)
        {
            if (points <= 0)
                return;

            double[] values = new double[points];

            for (int c = 0; c < _loggers.Count; c++)
            {
                for (int i = 0; i < points; i++)
                {
                    double t = (long)_loggers[c].Data.Coordinates.Count + i;
                    double x = t / 25.0;
                    values[i] = (Math.Sin(x) * 10) + (Math.Cos(x / 7.0) * 3) + ((_loggerRandom.NextDouble() - 0.5) * 0.6);
                }

                _loggers[c].Add(values);
            }
        }

        /// <summary>UI 延迟探针：BeginInvoke 往返（和压测工具一致的做法）。</summary>
        private void LatencyLoop()
        {
            while (!_stop)
            {
                ManualResetEventSlim done = new(false);
                Stopwatch stopwatch = Stopwatch.StartNew();

                try
                {
                    _host.Form.BeginInvoke(new Action(() =>
                    {
                        stopwatch.Stop();

                        lock (_gate)
                        {
                            _uiLatencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                        }

                        done.Set();
                    }));
                }
                catch (Exception)
                {
                    done.Dispose();
                    return;
                }

                done.Wait(TimeSpan.FromSeconds(30));
                done.Dispose();
                Thread.Sleep(100);
            }
        }

        /// <summary>跑一个阶段：duration 秒；collect = true 时返回该阶段的测量结果。</summary>
        public IngestMeasurement RunPhase(TimeSpan duration, bool collect)
        {
            ScrollingPlotControl? plot = _plot;
            Process process = Process.GetCurrentProcess();

            lock (_gate)
            {
                _uiLatencies.Clear();
            }

            long framesStart = Interlocked.Read(ref _framesSeen);
            int rendersStart = plot?.Plot.RenderManager.RenderCount ?? 0;
            long pointsStart = TotalWritten();
            process.Refresh();
            double cpuStart = process.TotalProcessorTime.TotalSeconds;
            double heapStart = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            double workingSetStart = process.WorkingSet64 / (1024.0 * 1024.0);
            int gc0Start = GC.CollectionCount(0);
            int gc2Start = GC.CollectionCount(2);

            Stopwatch stopwatch = Stopwatch.StartNew();
            Thread.Sleep(duration);
            stopwatch.Stop();

            int rendersEnd = plot?.Plot.RenderManager.RenderCount ?? 0;
            long pointsEnd = TotalWritten();
            process.Refresh();
            double cpuEnd = process.TotalProcessorTime.TotalSeconds;
            double heapEnd = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            double workingSetEnd = process.WorkingSet64 / (1024.0 * 1024.0);
            int gc0End = GC.CollectionCount(0);
            int gc2End = GC.CollectionCount(2);

            double wall = stopwatch.Elapsed.TotalSeconds;
            double achieved = (pointsEnd - pointsStart) / wall;
            double target = _curves * _ratePerCurve;

            List<double> latencies;

            lock (_gate)
            {
                latencies = [.. _uiLatencies.Where(x => !double.IsNaN(x))];
            }

            latencies.Sort();

            double forcedRefresh = MeasureForcedRefreshMs(TimeSpan.FromSeconds(15));
            long frames = Interlocked.Read(ref _framesSeen) - framesStart;

            return new IngestMeasurement(
                _strategy.Key,
                _strategy.Name,
                _strategy.Feeding,
                _strategy.Rendering,
                0,
                wall,
                _curveCount,
                target,
                achieved,
                Math.Max(0, target - achieved),
                (cpuEnd - cpuStart) / wall * 100.0,
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.95),
                latencies.Count == 0 ? 0 : latencies[^1],
                (rendersEnd - rendersStart) / wall,
                forcedRefresh,
                heapEnd - heapStart,
                workingSetEnd - workingSetStart,
                gc0End - gc0Start,
                gc2End - gc2Start,
                $"帧调度 {frames / wall:F0} 次/秒；UI 延迟样本 {latencies.Count} 个；" +
                $"每曲线保留 {WindowPoints:N0} 点" +
                (_lastFrameError is null ? string.Empty : $"；帧内异常：{_lastFrameError}"));
        }

        private long TotalWritten()
        {
            switch (_strategy.Kind)
            {
                case StrategyKind.ControlCrossThread:
                case StrategyKind.ControlUiBatch:
                {
                    long total = Math.Max(_controlWritten, _plot?.DataCount ?? 0);
                    return total;
                }

                case StrategyKind.DataStreamer:
                    return _streamers.Sum(s => (long)s.CountTotal);

                case StrategyKind.DataLogger:
                    return _loggers.Sum(l => (long)l.Data.Coordinates.Count);

                default:
                    return _totalWritten.Sum();
            }
        }

        /// <summary>强制整帧绘制耗时（中位数，UI 线程上执行，带超时保护）。</summary>
        private double MeasureForcedRefreshMs(TimeSpan timeout)
        {
            double result = double.NaN;
            ManualResetEventSlim done = new(false);

            try
            {
                _host.Form.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ScrollingPlotControl? plot = _plot;

                        if (plot is not null && !plot.IsDisposed)
                        {
                            plot.Refresh();
                            List<double> samples = [];

                            for (int i = 0; i < 3; i++)
                            {
                                Stopwatch stopwatch = Stopwatch.StartNew();
                                plot.Refresh();
                                stopwatch.Stop();
                                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                            }

                            samples.Sort();
                            result = samples[1];
                        }
                    }
                    finally
                    {
                        done.Set();
                    }
                }));
            }
            catch (Exception)
            {
                return double.NaN;
            }

            if (!done.Wait(timeout))
                result = double.NaN;

            done.Dispose();
            return result;
        }

        private static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted.Count == 0)
                return 0;

            int index = (int)Math.Round((sorted.Count - 1) * fraction);
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }
    }

    #region 报告

    private static string BuildReport(
        SoakOptions options,
        IReadOnlyList<IngestMeasurement> results,
        IReadOnlyList<string> notes,
        int curves,
        double ratePerCurve,
        double seconds,
        int rounds)
    {
        StringBuilder report = new();
        double totalRate = curves * ratePerCurve;

        List<IGrouping<string, IngestMeasurement>> groups = [.. results.GroupBy(x => x.Key).OrderBy(g => g.Key)];
        List<IngestMeasurement> summary = [.. groups.Select(g => Median(g.ToList()))];

        List<IngestMeasurement> candidates = [.. summary.Where(x => x.AchievedPointsPerSecond >= x.TargetPointsPerSecond * 0.98)];
        IngestMeasurement? winner = candidates
            .OrderBy(x => x.CpuPercentSingleCore)
            .ThenByDescending(x => x.Fps)
            .FirstOrDefault();

        report.AppendLine("# 数据写入方式对比实验报告（10 条曲线 × 1000 点/秒）");
        report.AppendLine();
        report.AppendLine($"- 结论：{(winner is null ? "**本次测量没有方案能稳定跑满目标速率**" : $"**推荐 {winner.Key}：{winner.Name}**")}");
        report.AppendLine($"- 工况：{curves} 条曲线 × 每条 {ratePerCurve:F0} 点/秒 = **{totalRate:N0} 点/秒**，每条保留/显示最近 {WindowPoints:N0} 点（60 秒），窗口 1100 × 660");
        report.AppendLine($"- 测量：每方案 {seconds:F0} 秒 × {rounds} 轮（轮次间交替执行，减小漂移）；另有 8 秒预热不计入统计");
        report.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();
        report.AppendLine("## 1. 结果（各方案多轮中位数）");
        report.AppendLine();
        report.AppendLine("| 方案 | 曲线数 | 写入通路 | 渲染通路 | 达成(点/秒) | 丢点(点/秒) | CPU(单核%) | UI 延迟 p50/p95/max (ms) | FPS | 强制整帧(ms) | 托管堆Δ(MB) | 工作集Δ(MB) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (IngestMeasurement item in summary)
        {
            report.AppendLine(
                $"| {item.Key} | {item.Curves} | {item.Feeding} | {item.Rendering} | {item.AchievedPointsPerSecond:N0} / {item.TargetPointsPerSecond:N0} | " +
                $"{item.DroppedPointsPerSecond:N0} | {item.CpuPercentSingleCore:F0}% | " +
                $"{item.UiLatencyP50Ms:F1} / {item.UiLatencyP95Ms:F1} / {item.UiLatencyMaxMs:F0} | {item.Fps:F1} | " +
                $"{(double.IsNaN(item.ForcedRefreshMs) ? "超时" : item.ForcedRefreshMs.ToString("F1"))} | " +
                $"{item.ManagedHeapDeltaMb:+0.0;-0.0;0.0} | {item.WorkingSetDeltaMb:+0.0;-0.0;0.0} |");
        }

        report.AppendLine();
        report.AppendLine("## 2. 逐方案说明");
        report.AppendLine();

        foreach (IngestMeasurement item in summary)
        {
            report.AppendLine($"### {item.Key}　{item.Name}");
            report.AppendLine();
            report.AppendLine($"- 写入：{item.Feeding}");
            report.AppendLine($"- 渲染：{item.Rendering}");
            report.AppendLine($"- 达成 {item.AchievedPointsPerSecond:N0} 点/秒（目标 {item.TargetPointsPerSecond:N0}），丢点 {item.DroppedPointsPerSecond:N0} 点/秒");
            report.AppendLine($"- CPU {item.CpuPercentSingleCore:F0}% 单核；UI 延迟 p50 {item.UiLatencyP50Ms:F1} ms / p95 {item.UiLatencyP95Ms:F1} ms / 最大 {item.UiLatencyMaxMs:F0} ms");
            report.AppendLine($"- 帧率 {item.Fps:F1} FPS；强制整帧绘制 {(double.IsNaN(item.ForcedRefreshMs) ? "超时（UI 线程积压）" : item.ForcedRefreshMs.ToString("F1") + " ms")}");
            report.AppendLine($"- 内存：托管堆 {item.ManagedHeapDeltaMb:+0.0;-0.0;0.0} MB，工作集 {item.WorkingSetDeltaMb:+0.0;-0.0;0.0} MB，GC0 +{item.Gc0Delta}，GC2 +{item.Gc2Delta}");
            report.AppendLine($"- 备注：{item.Notes}");
            report.AppendLine();
        }

        report.AppendLine("## 3. 数据通路的设计差异（决定长期可行性）");
        report.AppendLine();
        report.AppendLine("| 方案 | 数据存放 | 内存随时间 | 跨线程调用 | 是否适合 8 小时 × 2.88 亿点 |");
        report.AppendLine("|---|---|---|---|---|");
        report.AppendLine("| S1 | 控件自带缓冲（无上限） | 持续增长 | 每批一条窗口消息 | ✗ 消息队列会被淹没、内存无上限 |");
        report.AppendLine("| S2 | 控件自带缓冲（无上限） | 持续增长 | 无（UI 线程） | △ 不淹消息队列，但内存与绘制量随时间线性增长 |");
        report.AppendLine("| S3 | 自建定长数组（滑动窗口） | **有界** | 无（后台直写） | ✓ 内存与绘制量恒定 |");
        report.AppendLine("| S4 | DataStreamer 定容量环形 | **有界** | 无（UI 线程写入） | ✓ 内存恒定，绘制量 = 容量 |");
        report.AppendLine("| S5 | DataLogger（保留全部点） | 持续增长 | 无（UI 线程写入） | △ 渲染自动抽稀，但内存随运行时间增长，8 小时需定期清理 |");
        report.AppendLine();

        if (winner is not null)
        {
            report.AppendLine("## 4. 最佳实践（本次实测推荐）");
            report.AppendLine();
            report.AppendLine($"**{winner.Key}　{winner.Name}**：目标 {winner.TargetPointsPerSecond:N0} 点/秒 全部达成（丢点 {winner.DroppedPointsPerSecond:N0}），" +
                $"CPU {winner.CpuPercentSingleCore:F0}% 单核，UI 延迟 p95 {winner.UiLatencyP95Ms:F1} ms，帧率 {winner.Fps:F1} FPS。");
            report.AppendLine();
            report.AppendLine("推荐写法要点：");
            report.AppendLine();
            report.AppendLine("1. **不要逐点跨线程调用控件 API**（S1 的失败模式：每个点一条窗口消息 → 消息队列被淹没、界面定时器与重绘被饿死）；");
            report.AppendLine("2. 采集线程写**自建定长缓冲**（预分配数组 + 写指针），UI 线程只按帧更新“画哪一段 + 视图范围”，数据接入与渲染完全解耦；");
            report.AppendLine("3. 曲线只画**可见窗口内的点**（`Signal.MinRenderIndex/MaxRenderIndex`，或 `DataStreamer` / `DataLogger` 的原生视图）；");
            report.AppendLine("4. 视图范围按帧统一设置（`Axes.SetLimits`），Y 轴用固定范围或低频自动缩放，避免每帧全量扫描；");
            report.AppendLine("5. 每帧批量写入（约 17 点/曲线/帧 @1000 点/秒、60 FPS），不要逐点刷新。");
            report.AppendLine();
            report.AppendLine("示例（把采集线程与 UI 解耦，10 条曲线各 1000 点/秒）：");
            report.AppendLine();
            report.AppendLine("```csharp");
            report.AppendLine("// 采集线程：写自建滑动窗口，不碰 UI、不碰控件");
            report.AppendLine($"double[] buffer = new double[{WindowPoints:N0}];   // 每条曲线 60 秒");
            report.AppendLine("int written = 0;");
            report.AppendLine("void OnSample(double value)");
            report.AppendLine("{");
            report.AppendLine("    if (written == buffer.Length)          // 整体前移，保持定内存");
            report.AppendLine("    {");
            report.AppendLine("        Array.Copy(buffer, 1, buffer, 0, buffer.Length - 1);");
            report.AppendLine("        written--;");
            report.AppendLine("    }");
            report.AppendLine("    buffer[written++] = value;");
            report.AppendLine("}");
            report.AppendLine();
            report.AppendLine("// UI 线程：每帧只做两件事——告诉 Signal 画哪一段、设置视图窗口");
            report.AppendLine("Signal signal = plot.Add.Signal(buffer, period: 1);   // buffer 是上面那个定长数组，只需建一次");
            report.AppendLine("signal.MinRenderIndex = 0;");
            report.AppendLine("signal.MaxRenderIndex = written - 1;");
            report.AppendLine("plot.Axes.SetLimits(written - 60_000, written, yMin, yMax);");
            report.AppendLine("plot.RequestRender();");
            report.AppendLine("```");
            report.AppendLine();
        }

        report.AppendLine("## 5. 测量说明与局限");
        report.AppendLine();
        report.AppendLine("- 视图统一为“最近 60 秒 + 固定 Y 范围”，目的是隔离“写入通路”的差异；真实应用中若开启每帧自动缩放（Y 轴全量扫描）会额外增加每帧成本。");
        report.AppendLine("- 内存增量是 2 分钟窗口内的粗略值（受 GC 时机影响）；判断“内存是否有界”应以第 3 节的设计差异为准，而不是这两个数字。");
        report.AppendLine("- S1/S2 用控件的**单曲线** API 承载全部 10,000 点/秒（控件目前是单曲线设计，无法承载 10 条曲线），用来测控件自身通路的极限；");
        report.AppendLine("  S3~S5 承载完整的 10 条曲线，这才是“10 条曲线 × 1000 点/秒”的真实形态。");
        report.AppendLine("- FPS = 实际渲染次数 / 秒（`RenderManager.RenderCount` 差值）；强制整帧耗时为 UI 线程上 3 次 `Refresh()` 的中位数。");
        report.AppendLine();

        if (notes.Count > 0)
        {
            report.AppendLine("## 6. 异常与失败记录");
            report.AppendLine();

            foreach (string note in notes)
                report.AppendLine($"- {note}");

            report.AppendLine();
        }

        report.AppendLine("## 7. 复现命令");
        report.AppendLine();
        report.AppendLine("```powershell");
        report.AppendLine($"dotnet run --project tools\\ScottPlotEx.SoakTest -c Release -- --mode ingest --ingest-seconds {seconds:F0} --ingest-rounds {rounds}");
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("## 8. 各轮原始数据");
        report.AppendLine();
        report.AppendLine("| 方案 | 轮次 | 达成(点/秒) | CPU(单核%) | UI p95(ms) | UI max(ms) | FPS | 强制整帧(ms) | 托管堆Δ(MB) | 工作集Δ(MB) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (IngestMeasurement item in results.OrderBy(x => x.Key).ThenBy(x => x.Round))
        {
            report.AppendLine(
                $"| {item.Key} | {item.Round} | {item.AchievedPointsPerSecond:N0} | {item.CpuPercentSingleCore:F0}% | " +
                $"{item.UiLatencyP95Ms:F1} | {item.UiLatencyMaxMs:F0} | {item.Fps:F1} | " +
                $"{(double.IsNaN(item.ForcedRefreshMs) ? "超时" : item.ForcedRefreshMs.ToString("F1"))} | " +
                $"{item.ManagedHeapDeltaMb:+0.0;-0.0;0.0} | {item.WorkingSetDeltaMb:+0.0;-0.0;0.0} |");
        }

        report.AppendLine();
        report.AppendLine($"（环境：{RuntimeInformation.OSDescription}，{RuntimeInformation.FrameworkDescription}，逻辑核心 {Environment.ProcessorCount}）");

        return report.ToString();
    }

    private static IngestMeasurement Median(IReadOnlyList<IngestMeasurement> items)
    {
        IngestMeasurement first = items[0];

        double Median(Func<IngestMeasurement, double> selector)
        {
            List<double> values = [.. items.Select(selector).Where(v => !double.IsNaN(v)).Order()];
            return values.Count == 0 ? double.NaN : values[values.Count / 2];
        }

        return first with
        {
            Round = 0,
            Seconds = Median(x => x.Seconds),
            AchievedPointsPerSecond = Median(x => x.AchievedPointsPerSecond),
            DroppedPointsPerSecond = Median(x => x.DroppedPointsPerSecond),
            CpuPercentSingleCore = Median(x => x.CpuPercentSingleCore),
            UiLatencyP50Ms = Median(x => x.UiLatencyP50Ms),
            UiLatencyP95Ms = Median(x => x.UiLatencyP95Ms),
            UiLatencyMaxMs = Median(x => x.UiLatencyMaxMs),
            Fps = Median(x => x.Fps),
            ForcedRefreshMs = Median(x => x.ForcedRefreshMs),
            ManagedHeapDeltaMb = Median(x => x.ManagedHeapDeltaMb),
            WorkingSetDeltaMb = Median(x => x.WorkingSetDeltaMb),
            Gc0Delta = (int)Median(x => x.Gc0Delta),
            Gc2Delta = (int)Median(x => x.Gc2Delta),
            Notes = $"{items.Count} 轮中位数",
        };
    }

    #endregion
}
