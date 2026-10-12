namespace ScottPlotEx.SoakTest;

/// <summary>一次压测运行的全部上下文（路径、配置、日志、统计）。</summary>
internal sealed class SoakContext
{
    public required SoakOptions Options { get; init; }

    public required SoakLogger Logger { get; init; }

    public required SoakIssues Issues { get; init; }

    public required SoakStats Stats { get; init; }

    public required string RunId { get; init; }

    /// <summary>本次运行的输出目录（artifacts\soak\&lt;运行标识&gt;）。</summary>
    public required string RunDirectory { get; init; }

    /// <summary>压测根目录（artifacts\soak），用于放置“最新状态”与停止标记。</summary>
    public required string RootDirectory { get; init; }

    public required string CsvPath { get; init; }

    public required string StatusPath { get; init; }

    /// <summary>固定位置的状态文件（artifacts\soak\current.json），便于外部脚本 / 自动化读取。</summary>
    public required string CurrentStatusPath { get; init; }

    /// <summary>出现该文件即优雅停止。</summary>
    public required string StopFilePath { get; init; }

    public required string ReportPath { get; init; }

    /// <summary>固定位置的报告（artifacts\soak-report.md）。</summary>
    public required string LatestReportPath { get; init; }

    public required string SnapshotDirectory { get; init; }

    public required DateTime StartedLocal { get; init; }

    public double PlannedSeconds => Options.DurationSeconds;
}

/// <summary>线程安全的日志（文件 + 控制台）。</summary>
internal sealed class SoakLogger
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _quiet;

    public SoakLogger(string path, bool quiet)
    {
        _path = path;
        _quiet = quiet;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public string Path => _path;

    public void Write(string message) => Write(message, console: true);

    public void Event(string kind, string detail) => Write($"[{kind}] {detail}");

    public void Write(string message, bool console)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";

        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }

        if (console && !_quiet)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch (IOException)
            {
                // 没有控制台时忽略
            }
        }
    }
}

/// <summary>运行期间发现的问题（错误 / 告警）。</summary>
internal sealed class SoakIssues
{
    private readonly object _gate = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];

    public void Error(string source, string detail) => Add(_errors, "错误", source, detail);

    public void Error(string source, Exception exception) =>
        Add(_errors, "错误", source, exception.ToString().ReplaceLineEndings(" | "));

    public void Warning(string source, string detail) => Add(_warnings, "告警", source, detail);

    public int ErrorCount
    {
        get
        {
            lock (_gate)
            {
                return _errors.Count;
            }
        }
    }

    public int WarningCount
    {
        get
        {
            lock (_gate)
            {
                return _warnings.Count;
            }
        }
    }

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate)
            {
                return [.. _errors];
            }
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return [.. _warnings];
            }
        }
    }

    private void Add(List<string> target, string kind, string source, string detail)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {source}：{detail}";

        lock (_gate)
        {
            target.Add(line);
        }
    }
}

/// <summary>运行统计（UI 线程与后台线程都会更新）。</summary>
internal sealed class SoakStats
{
    private readonly object _gate = new();

    private long _points;
    private long _skipped;
    private long _interactions;
    private long _realMessageInteractions;
    private long _modeSwitches;
    private long _displayPercentChanges;
    private long _structureOperations;
    private long _controlRecreations;
    private long _menusOpened;
    private long _helpWindowsOpened;
    private long _autoClears;
    private long _snapshots;
    private long _integrityViolations;
    private long _stalls;

    private double _uiLatencyMaxMs;
    private double _uiLatencyTotalMs;
    private long _uiLatencySamples;
    private double _lastRenderMs;
    private double _lastRenderDataCount;
    private readonly List<(double ElapsedSeconds, double PhaseSeconds, string Phase, double LatencyMs, int DataCount)> _renderSamples = [];

    public void AddPoints(int count) => Interlocked.Add(ref _points, count);

    public void AddSkipped(int count) => Interlocked.Add(ref _skipped, count);

    public void CountInteraction(bool realMessage = false)
    {
        Interlocked.Increment(ref _interactions);

        if (realMessage)
            Interlocked.Increment(ref _realMessageInteractions);
    }

    public void CountModeSwitch() => Interlocked.Increment(ref _modeSwitches);

    public void CountDisplayPercentChange() => Interlocked.Increment(ref _displayPercentChanges);

    public void CountStructureOperation(bool controlRecreated = false)
    {
        Interlocked.Increment(ref _structureOperations);

        if (controlRecreated)
            Interlocked.Increment(ref _controlRecreations);
    }

    public void CountMenu() => Interlocked.Increment(ref _menusOpened);

    public void CountHelpWindow() => Interlocked.Increment(ref _helpWindowsOpened);

    public void CountAutoClear() => Interlocked.Increment(ref _autoClears);

    public void CountSnapshot() => Interlocked.Increment(ref _snapshots);

    public void CountIntegrityViolation() => Interlocked.Increment(ref _integrityViolations);

    public void CountStall() => Interlocked.Increment(ref _stalls);

    /// <summary>记录一次 UI 线程响应延迟（由看门狗线程调用）。</summary>
    public void ReportUiLatency(double latencyMs)
    {
        lock (_gate)
        {
            _uiLatencyTotalMs += latencyMs;
            _uiLatencySamples++;

            if (latencyMs > _uiLatencyMaxMs)
                _uiLatencyMaxMs = latencyMs;
        }
    }

    /// <summary>记录一次“强制整帧绘制”的耗时（由 UI 线程调用）。</summary>
    public void ReportRender(double milliseconds, int dataCount, string phase, double elapsedSeconds, double phaseSeconds)
    {
        lock (_gate)
        {
            _lastRenderMs = milliseconds;
            _lastRenderDataCount = dataCount;
            _renderSamples.Add((elapsedSeconds, phaseSeconds, phase, milliseconds, dataCount));
        }
    }

    public SoakStatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SoakStatsSnapshot(
                Interlocked.Read(ref _points),
                Interlocked.Read(ref _skipped),
                Interlocked.Read(ref _interactions),
                Interlocked.Read(ref _realMessageInteractions),
                Interlocked.Read(ref _modeSwitches),
                Interlocked.Read(ref _displayPercentChanges),
                Interlocked.Read(ref _structureOperations),
                Interlocked.Read(ref _controlRecreations),
                Interlocked.Read(ref _menusOpened),
                Interlocked.Read(ref _helpWindowsOpened),
                Interlocked.Read(ref _autoClears),
                Interlocked.Read(ref _snapshots),
                Interlocked.Read(ref _integrityViolations),
                Interlocked.Read(ref _stalls),
                _uiLatencyMaxMs,
                _uiLatencySamples == 0 ? 0 : _uiLatencyTotalMs / _uiLatencySamples,
                _lastRenderMs,
                _lastRenderDataCount,
                [.. _renderSamples]);
        }
    }
}

internal sealed record SoakStatsSnapshot(
    long Points,
    long Skipped,
    long Interactions,
    long RealMessageInteractions,
    long ModeSwitches,
    long DisplayPercentChanges,
    long StructureOperations,
    long ControlRecreations,
    long MenusOpened,
    long HelpWindowsOpened,
    long AutoClears,
    long Snapshots,
    long IntegrityViolations,
    long Stalls,
    double UiLatencyMaxMs,
    double UiLatencyAverageMs,
    double LastRenderMs,
    double LastRenderDataCount,
    IReadOnlyList<(double ElapsedSeconds, double PhaseSeconds, string Phase, double LatencyMs, int DataCount)> RenderSamples);

/// <summary>UI 线程发布的实时状态（供采样线程无阻塞读取）。</summary>
internal sealed record SoakPlotState(
    int DataCount,
    double LatestX,
    int SkippedValues,
    string Mode,
    double XMin,
    double XMax,
    double YMin,
    double YMax,
    string Phase,
    double PhaseSeconds,
    double ElapsedSeconds,
    bool AxisValid,
    string LimitsText);
