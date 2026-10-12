using System.Globalization;

namespace ScottPlotEx.SoakTest;

/// <summary>压测配置。</summary>
internal sealed class SoakOptions
{
    /// <summary>运行模式：soak（默认，长时间压力测试）/ ingest（数据写入方式对比实验）。</summary>
    public string Mode { get; set; } = "soak";

    /// <summary>写入方式对比实验：曲线数量。</summary>
    public int IngestCurves { get; set; } = 10;

    /// <summary>写入方式对比实验：每条曲线的点数/秒。</summary>
    public double IngestRatePerCurve { get; set; } = 1000;

    /// <summary>写入方式对比实验：每种方案每轮测量时长（秒）。</summary>
    public double IngestSecondsPerRound { get; set; } = 120;

    /// <summary>写入方式对比实验：轮次。</summary>
    public int IngestRounds { get; set; } = 2;

    /// <summary>总运行时长（小时）。</summary>
    public double DurationHours { get; set; } = 8.0;

    /// <summary>一个“负载循环”的时长（分钟）。循环内包含 6 个不同强度的阶段。</summary>
    public double CycleMinutes { get; set; } = 20.0;

    /// <summary>负载档位：light（减半）/ standard（默认）/ heavy（加倍）。</summary>
    public string Profile { get; set; } = "standard";

    /// <summary>输出目录（默认 &lt;仓库&gt;\artifacts\soak\&lt;运行标识&gt;）。</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>运行标识（用于文件名）。</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>随机种子（保证可复现）。</summary>
    public int Seed { get; set; } = 20260923;

    /// <summary>指标采样间隔（秒）。</summary>
    public double SampleSeconds { get; set; } = 15.0;

    /// <summary>窗口截图间隔（分钟，0 = 不截图）。</summary>
    public double SnapshotMinutes { get; set; } = 60.0;

    /// <summary>渲染耗时探针间隔（分钟）。</summary>
    public double RenderProbeMinutes { get; set; } = 5.0;

    /// <summary>状态文件刷新间隔（秒）。</summary>
    public double StatusSeconds { get; set; } = 20.0;

    /// <summary>数据点数量超过该值时自动清空（避免把机器内存吃满）。</summary>
    public int AutoClearPoints { get; set; } = 250_000;

    /// <summary>true = 近透明 + 鼠标穿透的“幽灵窗口”（默认，不干扰用户）；false = 正常可见窗口。</summary>
    public bool GhostWindow { get; set; } = true;

    /// <summary>是否执行鼠标 / 键盘交互（交互风暴阶段）。</summary>
    public bool EnableInteractions { get; set; } = true;

    /// <summary>是否执行窗口尺寸变化、最小化、清空、重建控件等结构操作。</summary>
    public bool EnableStructureOperations { get; set; } = true;

    /// <summary>窗体客户区尺寸。</summary>
    public Size ClientSize { get; set; } = new(1100, 660);

    /// <summary>控制台静默（只写日志文件）。</summary>
    public bool Quiet { get; set; }

    /// <summary>启动后立即隐藏控制台窗口（适合长时间无人值守运行）。</summary>
    public bool HideConsole { get; set; }

    /// <summary>出现未处理异常时的处理策略上限（超过该数量即中止并判定失败）。</summary>
    public int MaxUnhandledErrors { get; set; } = 10;

    /// <summary>坐标轴等“不变量”被破坏多少次后中止运行。</summary>
    public int MaxIntegrityViolations { get; set; } = 5;

    public double DurationSeconds => DurationHours * 3600.0;

    public double CycleSeconds => CycleMinutes * 60.0;

    /// <summary>负载缩放系数。</summary>
    public double LoadScale => Profile.ToLowerInvariant() switch
    {
        "light" => 0.5,
        "heavy" => 2.0,
        _ => 1.0,
    };

    public static string Usage =>
        """
        ScottPlotEx 时间压力测试（soak test）

          --hours <n>              运行时长（小时），默认 8
          --cycle-minutes <n>      一个负载循环的分钟数，默认 20
          --profile <p>            light / standard / heavy，默认 standard
          --minutes <n>            运行时长（分钟），用于短时冒烟验证
          --out <dir>              输出目录
          --tag <name>             运行标识（文件名前缀）
          --seed <n>               随机种子，默认 20260923
          --sample-seconds <n>     指标采样间隔，默认 15
          --snapshot-minutes <n>   截图间隔，默认 60（0 = 关闭）
          --render-probe-minutes <n> 渲染耗时探针间隔，默认 5
          --auto-clear <n>         数据点超过该值自动清空，默认 250000
          --visible                使用正常可见窗口（默认是鼠标穿透的近透明窗口）
          --no-interactions        不做鼠标 / 键盘交互
          --no-structure-ops       不做窗口尺寸、最小化、清空、重建控件等操作
          --quiet                  不输出到控制台
          --no-console             启动后立即隐藏控制台窗口（长时间无人值守运行）
          --mode <soak|ingest>     soak = 长时间压力测试（默认）；ingest = 数据写入方式对比实验
          --ingest-curves <n>      对比实验：曲线数量，默认 10
          --ingest-rate <n>        对比实验：每条曲线点数/秒，默认 1000
          --ingest-seconds <n>     对比实验：每种方案每轮测量秒数，默认 120
          --ingest-rounds <n>      对比实验：轮次，默认 2
        """;

    public static SoakOptions Parse(string[] args)
    {
        SoakOptions options = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"参数 {arg} 缺少取值。");

            switch (arg)
            {
                case "--hours":
                    options.DurationHours = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--minutes":
                    options.DurationHours = double.Parse(Next(), CultureInfo.InvariantCulture) / 60.0;
                    break;
                case "--cycle-minutes":
                    options.CycleMinutes = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--profile":
                    options.Profile = Next();
                    break;
                case "--out":
                    options.OutputDirectory = Next();
                    break;
                case "--tag":
                    options.Tag = Next();
                    break;
                case "--seed":
                    options.Seed = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--sample-seconds":
                    options.SampleSeconds = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--snapshot-minutes":
                    options.SnapshotMinutes = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--render-probe-minutes":
                    options.RenderProbeMinutes = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--auto-clear":
                    options.AutoClearPoints = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--visible":
                    options.GhostWindow = false;
                    break;
                case "--no-interactions":
                    options.EnableInteractions = false;
                    break;
                case "--no-structure-ops":
                    options.EnableStructureOperations = false;
                    break;
                case "--quiet":
                    options.Quiet = true;
                    break;
                case "--no-console":
                    options.HideConsole = true;
                    break;
                case "--mode":
                    options.Mode = Next().ToLowerInvariant();
                    break;
                case "--ingest-curves":
                    options.IngestCurves = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--ingest-rate":
                    options.IngestRatePerCurve = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--ingest-seconds":
                    options.IngestSecondsPerRound = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--ingest-rounds":
                    options.IngestRounds = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--help":
                case "-h":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"无法识别的参数：{arg}");
            }
        }

        if (options.DurationHours <= 0)
            throw new ArgumentException("运行时长必须大于 0。");

        if (options.CycleMinutes <= 0)
            throw new ArgumentException("循环时长必须大于 0。");

        if (options.CycleMinutes * 60.0 > options.DurationSeconds)
            options.CycleMinutes = options.DurationSeconds / 60.0;

        if (options.SampleSeconds < 1)
            options.SampleSeconds = 1;

        return options;
    }
}

/// <summary>请求显示用法说明。</summary>
internal sealed class HelpRequestedException : Exception
{
}
