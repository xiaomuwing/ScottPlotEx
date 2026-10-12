using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Forms;

namespace ScottPlotEx.SoakTest;

/// <summary>一次采样得到的进程指标。</summary>
internal sealed record SoakSample(
    DateTime TimeLocal,
    double ElapsedSeconds,
    string Phase,
    string Mode,
    int DataCount,
    int SkippedValues,
    double XMin,
    double XMax,
    double YMin,
    double YMax,
    bool AxisValid,
    double WorkingSetMb,
    double PrivateMb,
    double ManagedHeapMb,
    int Gc0,
    int Gc1,
    int Gc2,
    int ThreadCount,
    int HandleCount,
    int GdiObjects,
    int UserObjects,
    double CpuSeconds,
    int FeedThreads,
    double UiLatencyMaxMs,
    double UiLatencyAverageMs,
    double LastRenderMs,
    double LastRenderDataCount,
    long PointsTotal,
    long SkippedTotal,
    long Interactions,
    long StructureOperations,
    long ControlRecreations,
    long IntegrityViolations,
    long Stalls,
    double TimerIdleSeconds)
{
    public int Hour => (int)(ElapsedSeconds / 3600.0);
}

/// <summary>
/// 指标采样与看门狗：
/// 1) 定时采集工作集 / 托管堆 / GC / 句柄 / GDI / 线程等指标，写入 CSV；
/// 2) 通过 BeginInvoke 往返测量 UI 线程响应延迟（发现卡死 / 长时间卡顿）；
/// 3) 定时刷新状态文件，供外部脚本与自动化读取。
/// </summary>
internal sealed class SoakMonitor
{
    private const double Megabyte = 1024.0 * 1024.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SoakContext _context;
    private readonly SoakHost _host;
    private readonly SoakWorkload _workload;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly List<SoakSample> _samples = [];
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly object _samplesGate = new();

    private Thread? _samplerThread;
    private Thread? _watchdogThread;
    private SoakSample? _lastSample;
    private bool _memoryWarning;
    private bool _handleWarning;
    private string _state = "starting";
    private DateTime? _timerStarvedSince;
    private bool _timerStarvedNotified;

    /// <summary>WM_TIMER 连续多久没有触发就视为“被饿死”（秒）。</summary>
    private const double TimerStarveThresholdSeconds = 20;

    public SoakMonitor(SoakContext context, SoakHost host, SoakWorkload workload)
    {
        _context = context;
        _host = host;
        _workload = workload;
    }

    public IReadOnlyList<SoakSample> Samples
    {
        get
        {
            lock (_samplesGate)
            {
                return [.. _samples];
            }
        }
    }

    public string State => _state;

    /// <summary>UI 线程是否真的卡死（看门狗的 BeginInvoke 超过 120 秒未返回）。</summary>
    public bool UiHangDetected { get; private set; }

    /// <summary>WM_TIMER 饿死（窗口消息队列被持续占用导致定时器拿不到机会）的观测结果。</summary>
    public int TimerStarvedEvents { get; private set; }

    public double TimerStarvedSeconds { get; private set; }

    public double TimerMaxStarvedSeconds { get; private set; }

    public double CurrentTimerIdleSeconds { get; private set; }

    public void Start(string state)
    {
        _state = state;
        WriteCsvHeader();
        WriteStatus(state);

        _samplerThread = new Thread(SampleLoop)
        {
            IsBackground = true,
            Name = "压测指标采样线程",
        };
        _samplerThread.Start();

        _watchdogThread = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "压测 UI 看门狗线程",
        };
        _watchdogThread.Start();
    }

    public void Stop(string state)
    {
        _state = state;
        _stop.Set();
        _samplerThread?.Join(5000);
        _watchdogThread?.Join(5000);

        // 收尾时把“仍在进行中”的饿死事件也计入统计
        if (_timerStarvedSince is { } since)
        {
            double duration = Math.Max(0, (DateTime.UtcNow - since).TotalSeconds);
            TimerStarvedEvents++;
            TimerStarvedSeconds += duration;
            TimerMaxStarvedSeconds = Math.Max(TimerMaxStarvedSeconds, duration);
            _timerStarvedSince = null;
        }

        WriteStatus(state);
    }

    private void SampleLoop()
    {
        TimeSpan sampleInterval = TimeSpan.FromSeconds(_context.Options.SampleSeconds);
        TimeSpan statusInterval = TimeSpan.FromSeconds(_context.Options.StatusSeconds);

        DateTime nextSample = DateTime.UtcNow;
        DateTime nextStatus = DateTime.UtcNow + statusInterval;

        while (!_stop.Wait(200))
        {
            DateTime now = DateTime.UtcNow;

            if (now >= nextSample)
            {
                Collect();
                nextSample = now + sampleInterval;
            }

            if (now >= nextStatus)
            {
                WriteStatus(_state);
                nextStatus = now + statusInterval;
            }
        }
    }

    private void Collect()
    {
        try
        {
            _process.Refresh();
            SoakPlotState state = _workload.State;
            SoakStatsSnapshot stats = _context.Stats.Snapshot();

            SoakSample sample = new(
                DateTime.Now,
                _workload.ElapsedSeconds,
                state.Phase,
                state.Mode,
                state.DataCount,
                state.SkippedValues,
                state.XMin,
                state.XMax,
                state.YMin,
                state.YMax,
                state.AxisValid,
                _process.WorkingSet64 / Megabyte,
                _process.PrivateMemorySize64 / Megabyte,
                GC.GetTotalMemory(forceFullCollection: false) / Megabyte,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                _process.Threads.Count,
                _process.HandleCount,
                ReadGuiObjects(NativeMethods.GR_GDIOBJECTS),
                ReadGuiObjects(NativeMethods.GR_USEROBJECTS),
                _process.TotalProcessorTime.TotalSeconds,
                _workload.ActiveFeedThreads,
                stats.UiLatencyMaxMs,
                stats.UiLatencyAverageMs,
                stats.LastRenderMs,
                stats.LastRenderDataCount,
                stats.Points,
                stats.Skipped,
                stats.Interactions,
                stats.StructureOperations,
                stats.ControlRecreations,
                stats.IntegrityViolations,
                stats.Stalls,
                CurrentTimerIdleSeconds);

            lock (_samplesGate)
            {
                _samples.Add(sample);
            }

            _lastSample = sample;
            AppendCsv(sample);
            CheckThresholds(sample);
            CheckTimerStarvation(state);
        }
        catch (Exception exception)
        {
            _context.Issues.Warning("指标采样", exception.Message);
        }
    }

    /// <summary>
    /// 观测 WM_TIMER 饿死：Windows 只在消息队列空闲时才合成 WM_TIMER / WM_PAINT，
    /// 跨线程高频灌入（控件早期版本逐次 BeginInvoke；现已按帧合并写入）+ 数据持续触发重绘时，定时器会被无限期挤掉。
    /// 这不是“线程卡死”（看门狗仍然能收到消息），因此只记为观测结论与告警，不中止运行。
    /// </summary>
    private void CheckTimerStarvation(SoakPlotState state)
    {
        double idleSeconds = Math.Max(0, (DateTime.UtcNow - _workload.LastTimerCanaryUtc).TotalSeconds);
        CurrentTimerIdleSeconds = idleSeconds;

        if (idleSeconds >= TimerStarveThresholdSeconds)
        {
            _timerStarvedSince ??= DateTime.UtcNow.AddSeconds(-idleSeconds);

            if (!_timerStarvedNotified)
            {
                _timerStarvedNotified = true;
                _context.Issues.Warning(
                    "WM_TIMER 饿死",
                    $"窗口定时器已连续 {idleSeconds:F0} 秒未触发（阶段 {state.Phase}，后台灌入线程 {_workload.ActiveFeedThreads} 个）：" +
                    "消息队列被持续占用，界面在此期间不会重绘。");
                _context.Logger.Event("观测", $"WM_TIMER 饿死开始（阶段 {state.Phase}，数据点 {state.DataCount:N0}）。");
            }

            return;
        }

        if (_timerStarvedSince is not { } since)
            return;

        double duration = Math.Max(0, (DateTime.UtcNow - since).TotalSeconds);
        TimerStarvedEvents++;
        TimerStarvedSeconds += duration;
        TimerMaxStarvedSeconds = Math.Max(TimerMaxStarvedSeconds, duration);
        _timerStarvedSince = null;
        _timerStarvedNotified = false;
        _context.Logger.Event(
            "观测",
            $"WM_TIMER 饿死结束：本次持续 {duration:F1} 秒；累计 {TimerStarvedEvents} 次 / {TimerStarvedSeconds:F0} 秒。");
    }

    private void CheckThresholds(SoakSample sample)
    {
        if (!_memoryWarning && sample.ManagedHeapMb > 1500)
        {
            _memoryWarning = true;
            _context.Issues.Warning("内存", $"托管堆已达到 {sample.ManagedHeapMb:F0} MB（数据点 {sample.DataCount:N0}）。");
        }

        if (!_handleWarning && sample.HandleCount > 3000)
        {
            _handleWarning = true;
            _context.Issues.Warning("句柄", $"进程句柄数已达到 {sample.HandleCount:N0}。");
        }

        if (!sample.AxisValid)
            _context.Issues.Warning("指标", "采样时坐标轴处于无效状态（已由不变量检查记录）。");
    }

    private static int ReadGuiObjects(int flag)
    {
        try
        {
            return NativeMethods.GetGuiResources(Process.GetCurrentProcess().Handle, flag);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>UI 线程响应延迟（BeginInvoke 往返）。</summary>
    private void WatchdogLoop()
    {
        while (!_stop.Wait(200))
        {
            if (!_host.IsAlive)
                return;

            ManualResetEventSlim done = new(false);
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                _host.Form.BeginInvoke(new Action(() =>
                {
                    stopwatch.Stop();
                    _workload.ReportUiLatency(stopwatch.Elapsed.TotalMilliseconds);
                    done.Set();
                }));
            }
            catch (Exception)
            {
                // 窗口句柄正在销毁
                done.Dispose();
                Thread.Sleep(200);
                continue;
            }

            if (!done.Wait(TimeSpan.FromSeconds(120)))
            {
                UiHangDetected = true;
                _context.Issues.Error(
                    "UI 线程卡死",
                    "看门狗的 BeginInvoke 超过 120 秒仍未返回：窗口消息队列完全不再被处理（与“WM_TIMER 被饿死”不同，后者消息仍能正常处理）。");
            }

            done.Dispose();
        }
    }

    private void WriteCsvHeader()
    {
        const string header =
            "时间,已运行秒,阶段,模式,数据点,已忽略值,X最小,X最大,Y最小,Y最大,坐标轴有效,工作集MB,私有MB,托管堆MB," +
            "GC0,GC1,GC2,线程数,句柄数,GDI对象,User对象,CPU秒,后台灌入线程,UI延迟最大ms,UI延迟平均ms,最近渲染ms,渲染时数据点," +
            "累计数据点,累计忽略值,交互次数,结构操作,控件重建,不变量破坏,卡顿次数,定时器空闲秒";

        Directory.CreateDirectory(_context.RunDirectory);
        File.WriteAllText(_context.CsvPath, header + Environment.NewLine, Encoding.UTF8);
    }

    private void AppendCsv(SoakSample sample)
    {
        StringBuilder line = new();

        void Add(object value)
        {
            if (line.Length > 0)
                line.Append(',');

            line.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        Add(sample.TimeLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Add(Math.Round(sample.ElapsedSeconds, 1));
        Add(sample.Phase);
        Add(sample.Mode);
        Add(sample.DataCount);
        Add(sample.SkippedValues);
        Add(Math.Round(sample.XMin, 3));
        Add(Math.Round(sample.XMax, 3));
        Add(Math.Round(sample.YMin, 3));
        Add(Math.Round(sample.YMax, 3));
        Add(sample.AxisValid ? 1 : 0);
        Add(Math.Round(sample.WorkingSetMb, 1));
        Add(Math.Round(sample.PrivateMb, 1));
        Add(Math.Round(sample.ManagedHeapMb, 2));
        Add(sample.Gc0);
        Add(sample.Gc1);
        Add(sample.Gc2);
        Add(sample.ThreadCount);
        Add(sample.HandleCount);
        Add(sample.GdiObjects);
        Add(sample.UserObjects);
        Add(Math.Round(sample.CpuSeconds, 1));
        Add(sample.FeedThreads);
        Add(Math.Round(sample.UiLatencyMaxMs, 1));
        Add(Math.Round(sample.UiLatencyAverageMs, 2));
        Add(Math.Round(sample.LastRenderMs, 2));
        Add(Math.Round(sample.LastRenderDataCount, 0));
        Add(sample.PointsTotal);
        Add(sample.SkippedTotal);
        Add(sample.Interactions);
        Add(sample.StructureOperations);
        Add(sample.ControlRecreations);
        Add(sample.IntegrityViolations);
        Add(sample.Stalls);
        Add(Math.Round(sample.TimerIdleSeconds, 1));

        File.AppendAllText(_context.CsvPath, line.Append(Environment.NewLine).ToString(), Encoding.UTF8);
    }

    /// <summary>写入状态文件（本次运行目录 + 固定位置各一份）。</summary>
    public void WriteStatus(string state)
    {
        _state = state;

        try
        {
            SoakPlotState plot = _workload.State;
            SoakSample? last = _lastSample;
            double elapsed = _workload.ElapsedSeconds;
            double planned = _context.PlannedSeconds;

            Dictionary<string, object?> payload = new()
            {
                ["state"] = state,
                ["runId"] = _context.RunId,
                ["startedLocal"] = _context.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["updatedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["elapsedSeconds"] = Math.Round(elapsed, 1),
                ["plannedSeconds"] = Math.Round(planned, 1),
                ["percentComplete"] = Math.Round(planned <= 0 ? 0 : Math.Min(100.0, (elapsed / planned) * 100.0), 2),
                ["phase"] = plot.Phase,
                ["cycle"] = _workload.CycleNumber,
                ["mode"] = plot.Mode,
                ["dataCount"] = plot.DataCount,
                ["axisValid"] = plot.AxisValid,
                ["pointsTotal"] = _context.Stats.Snapshot().Points,
                ["workingSetMb"] = last is null ? null : Math.Round(last.WorkingSetMb, 1),
                ["managedHeapMb"] = last is null ? null : Math.Round(last.ManagedHeapMb, 2),
                ["handleCount"] = last?.HandleCount,
                ["gdiObjects"] = last?.GdiObjects,
                ["threadCount"] = last?.ThreadCount,
                ["errors"] = _context.Issues.ErrorCount,
                ["warnings"] = _context.Issues.WarningCount,
                ["processId"] = Environment.ProcessId,
                ["logPath"] = _context.Logger.Path,
                ["csvPath"] = _context.CsvPath,
                ["reportPath"] = _context.ReportPath,
            };

            string json = JsonSerializer.Serialize(payload, JsonOptions);
            File.WriteAllText(_context.StatusPath, json, Encoding.UTF8);
            File.WriteAllText(_context.CurrentStatusPath, json, Encoding.UTF8);
        }
        catch (Exception exception)
        {
            _context.Logger.Event("WARN", "状态文件写入失败：" + exception.Message);
        }
    }
}
