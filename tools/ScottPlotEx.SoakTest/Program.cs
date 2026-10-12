using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ScottPlotEx.SoakTest;

/// <summary>
/// ScottPlotEx 控件时间压力测试（默认 8 小时）。
/// 全程持续驱动 <c>ScrollingPlotControl</c>：常态采集 → 中速 → 高速突发 → 多线程极端突发 →
/// 交互风暴 → 结构变化（尺寸 / 最小化 / 清空 / 重建控件），并记录稳定性、内存、句柄与渲染耗时趋势。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SoakOptions options;

        try
        {
            options = SoakOptions.Parse(args);
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(SoakOptions.Usage);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("参数错误：" + exception.Message);
            Console.WriteLine(SoakOptions.Usage);
            return 3;
        }

        if (options.HideConsole)
        {
            // 长时无人值守运行：启动后立刻把控制台窗口藏起来，避免在桌面上留一个黑框
            IntPtr console = NativeMethods.GetConsoleWindow();

            if (console != IntPtr.Zero)
                NativeMethods.ShowWindow(console, NativeMethods.SW_HIDE);
        }

        return Run(options);
    }

    private static int Run(SoakOptions options)
    {
        DateTime startedLocal = DateTime.Now;
        string repositoryRoot = FindRepositoryRoot();
        string root = Path.Combine(repositoryRoot, "artifacts", "soak");
        string runId = string.IsNullOrWhiteSpace(options.Tag)
            ? $"soak-{startedLocal:yyyyMMdd-HHmmss}"
            : $"{options.Tag}-{startedLocal:yyyyMMdd-HHmmss}";
        string runDirectory = Path.Combine(root, runId);

        Directory.CreateDirectory(runDirectory);

        SoakContext context = new()
        {
            Options = options,
            Logger = new SoakLogger(Path.Combine(runDirectory, "soak.log"), options.Quiet),
            Issues = new SoakIssues(),
            Stats = new SoakStats(),
            RunId = runId,
            RunDirectory = runDirectory,
            RootDirectory = root,
            CsvPath = Path.Combine(runDirectory, "metrics.csv"),
            StatusPath = Path.Combine(runDirectory, "status.json"),
            CurrentStatusPath = Path.Combine(root, "current.json"),
            StopFilePath = Path.Combine(root, "STOP"),
            ReportPath = Path.Combine(runDirectory, "soak-report.md"),
            LatestReportPath = Path.Combine(repositoryRoot, "artifacts", "soak-report.md"),
            SnapshotDirectory = Path.Combine(runDirectory, "snapshots"),
            StartedLocal = startedLocal,
        };

        SoakLogger logger = context.Logger;
        logger.Event("启动", $"运行标识 {runId}，输出目录 {runDirectory}");

        if (File.Exists(context.StopFilePath))
        {
            File.Delete(context.StopFilePath);
            logger.Event("启动", "已删除上一次运行遗留的停止标记（artifacts\\soak\\STOP）。");
        }

        RegisterGlobalExceptionHandlers(context);

        if (string.Equals(options.Mode, "ingest", StringComparison.OrdinalIgnoreCase))
            return RunIngestMode(options, context);

        SoakHost? host = null;
        SoakWorkload? workload = null;
        SoakMonitor? monitor = null;
        string state = "failed";
        string stopReason = "未开始";

        try
        {
            host = SoakHost.Start(options, logger, options.ClientSize);
            SoakHost startedHost = host;
            workload = startedHost.Invoke(() =>
            {
                SoakWorkload instance = new(context, new SoakFormHostAccessor { HostForm = startedHost.Form });
                instance.Start();
                return instance;
            });

            SoakWorkload startedWorkload = workload;
            monitor = new SoakMonitor(context, host, startedWorkload);
            monitor.Start("running");

            logger.Event("运行", $"压测开始：计划 {options.DurationHours:F2} 小时，循环 {options.CycleMinutes:F1} 分钟，档位 {options.Profile}，" +
                $"窗口模式 {(options.GhostWindow ? "近透明（鼠标穿透）" : "可见")}。");

            (state, stopReason) = RunMainLoop(options, context, host, startedWorkload, monitor);
        }
        catch (Exception exception)
        {
            context.Issues.Error("主流程", exception);
            state = "failed";
            stopReason = "主流程异常：" + exception.Message;
            logger.Event("ERROR", "主流程异常：" + exception);
        }
        finally
        {
            if (host is not null && workload is not null)
            {
                try
                {
                    SoakWorkload stopping = workload;
                    host.Invoke(() => stopping.Stop());
                }
                catch (Exception exception)
                {
                    logger.Event("WARN", "停止负载时出现异常：" + exception.Message);
                }
            }

            try
            {
                monitor?.Stop(state);
            }
            catch (Exception exception)
            {
                logger.Event("WARN", "停止采样时出现异常：" + exception.Message);
            }

            SoakRunSummary summary = BuildSummary(options, context, workload, monitor, state, stopReason, repositoryRoot);

            try
            {
                string report = SoakReport.Build(options, summary);
                File.WriteAllText(context.ReportPath, report, Encoding.UTF8);
                File.Copy(context.ReportPath, context.LatestReportPath, overwrite: true);
                logger.Event("完成", $"报告已写入 {context.ReportPath}");
                logger.Event("完成", $"报告副本已写入 {context.LatestReportPath}");
            }
            catch (Exception exception)
            {
                logger.Event("ERROR", "生成报告失败：" + exception);
            }

            Console.WriteLine();
            Console.WriteLine($"结论：{(state == "completed" ? "按计划跑满" : stopReason)}");
            Console.WriteLine($"运行时长：{SoakReport.FormatDuration(summary.ElapsedSeconds)}，累计数据点 {summary.Stats.Points:N0}，交互 {summary.Stats.Interactions:N0} 次");
            Console.WriteLine($"错误：{summary.Errors.Count}，告警：{summary.Warnings.Count}，不变量破坏：{summary.Stats.IntegrityViolations}");
            Console.WriteLine($"报告：{context.ReportPath}");

            host?.Dispose();
        }

        return state switch
        {
            "completed" => 0,
            "stopped" => 2,
            _ => 1,
        };
    }

    /// <summary>运行“数据写入方式对比实验”。</summary>
    private static int RunIngestMode(SoakOptions options, SoakContext context)
    {
        SoakHost? host = null;

        try
        {
            host = SoakHost.Start(options, context.Logger, options.ClientSize);
            return IngestBenchmark.Run(options, context, host);
        }
        catch (Exception exception)
        {
            context.Issues.Error("对比实验", exception);
            context.Logger.Event("ERROR", "对比实验失败：" + exception);
            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }

    private static (string State, string StopReason) RunMainLoop(
        SoakOptions options,
        SoakContext context,
        SoakHost host,
        SoakWorkload workload,
        SoakMonitor monitor)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(options.DurationSeconds);
        DateTime lastProgress = DateTime.UtcNow;
        string state = "completed";
        string stopReason = "达到计划时长";

        while (true)
        {
            if (DateTime.UtcNow >= deadline)
                break;

            if (File.Exists(context.StopFilePath))
            {
                state = "stopped";
                stopReason = "收到停止文件（artifacts\\soak\\STOP）";
                break;
            }

            if (context.Issues.ErrorCount > options.MaxUnhandledErrors)
            {
                state = "failed";
                stopReason = $"错误数量超过上限（{context.Issues.ErrorCount} > {options.MaxUnhandledErrors}）";
                break;
            }

            if (workload.IntegrityViolationCount > options.MaxIntegrityViolations)
            {
                state = "failed";
                stopReason = $"控件不变量被破坏次数超过上限（{workload.IntegrityViolationCount} > {options.MaxIntegrityViolations}）";
                break;
            }

            if (monitor.UiHangDetected)
            {
                state = "failed";
                stopReason = "UI 线程卡死（看门狗的 BeginInvoke 超过 120 秒未返回）";
                break;
            }

            if (!host.IsAlive)
            {
                state = "failed";
                stopReason = "UI 线程已退出（窗口被关闭或发生崩溃）";
                break;
            }

            if (DateTime.UtcNow - lastProgress >= TimeSpan.FromMinutes(30))
            {
                lastProgress = DateTime.UtcNow;
                LogProgress(context, workload, options);
            }

            Thread.Sleep(1000);
        }

        return (state, stopReason);
    }

    private static void LogProgress(SoakContext context, SoakWorkload workload, SoakOptions options)
    {
        SoakPlotState plot = workload.State;
        double elapsed = workload.ElapsedSeconds;
        double percent = options.DurationSeconds <= 0 ? 0 : (elapsed / options.DurationSeconds) * 100.0;
        SoakStatsSnapshot stats = context.Stats.Snapshot();

        context.Logger.Event(
            "进度",
            $"{percent:F1}%（已运行 {SoakReport.FormatDuration(elapsed)} / 计划 {options.DurationHours:F2} 小时）·" +
            $" 第 {workload.CycleNumber} 轮 · {plot.Phase} · 数据点 {plot.DataCount:N0}（累计 {stats.Points:N0}）·" +
            $" 模式 {plot.Mode} · {plot.LimitsText} · 错误 {context.Issues.ErrorCount} / 告警 {context.Issues.WarningCount}");
    }

    private static SoakRunSummary BuildSummary(
        SoakOptions options,
        SoakContext context,
        SoakWorkload? workload,
        SoakMonitor? monitor,
        string state,
        string stopReason,
        string repositoryRoot)
    {
        SoakStatsSnapshot stats = context.Stats.Snapshot();
        IReadOnlyList<SoakSample> samples = monitor?.Samples ?? [];

        return new SoakRunSummary(
            state,
            stopReason,
            context.StartedLocal,
            DateTime.Now,
            workload?.ElapsedSeconds ?? (samples.Count > 0 ? samples[^1].ElapsedSeconds : 0),
            options.DurationSeconds,
            context.RunId,
            context.RunDirectory,
            context.Logger.Path,
            context.CsvPath,
            context.SnapshotDirectory,
            DescribeEnvironment(options, repositoryRoot),
            samples,
            stats,
            workload?.LatencyByPhase ?? new Dictionary<string, (double Max, double Sum, long Count)>(),
            workload?.StructureOperationLog ?? [],
            context.Issues.Errors,
            context.Issues.Warnings,
            monitor?.TimerStarvedEvents ?? 0,
            monitor?.TimerStarvedSeconds ?? 0,
            monitor?.TimerMaxStarvedSeconds ?? 0);
    }

    private static string DescribeEnvironment(SoakOptions options, string repositoryRoot)
    {
        StringBuilder text = new();
        text.AppendLine($"- 操作系统：{RuntimeInformation.OSDescription}（{(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}）");
        text.AppendLine($"- 处理器：{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}（逻辑核心数 {Environment.ProcessorCount}）");
        text.AppendLine($"- 运行时：{RuntimeInformation.FrameworkDescription}，进程架构 {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"- 仓库根目录：`{repositoryRoot}`");
        text.AppendLine($"- 进程 ID：{Environment.ProcessId}，启动时间：{Process.GetCurrentProcess().StartTime:yyyy-MM-dd HH:mm:ss}");

        try
        {
            Screen primary = Screen.PrimaryScreen!;
            text.AppendLine($"- 主显示器：{primary.Bounds.Width} × {primary.Bounds.Height}（工作区 {primary.WorkingArea.Width} × {primary.WorkingArea.Height}）");
        }
        catch (Exception)
        {
            // 无显示器信息时忽略
        }

        try
        {
            Size maximum = System.Drawing.BufferedGraphicsManager.Current.MaximumBuffer;
            text.AppendLine($"- WinForms 双缓冲上限：{maximum.Width} × {maximum.Height}（控件尺寸 {options.ClientSize.Width} × {options.ClientSize.Height}）");
        }
        catch (Exception)
        {
            // 忽略
        }

        Assembly controlsAssembly = typeof(ScottPlotEx.Controls.ScrollingPlotControl).Assembly;
        text.AppendLine($"- 被测控件版本：{controlsAssembly.GetName().Version}（{controlsAssembly.Location}）");
        text.AppendLine($"- 本机时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}（时区 {TimeZoneInfo.Local.DisplayName}）");

        return text.ToString();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScottPlotEx.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static void RegisterGlobalExceptionHandlers(SoakContext context)
    {
        Application.ThreadException += (_, e) => context.Issues.Error("UI 线程未处理异常", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                context.Issues.Error("未处理异常（AppDomain）", exception);
            else
                context.Issues.Error("未处理异常（AppDomain）", Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture) ?? "未知");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            context.Issues.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

        Console.CancelKeyPress += (_, e) =>
        {
            context.Logger.Event("停止", "收到 Ctrl+C，正在优雅停止……");
            File.WriteAllText(context.StopFilePath, "stop requested by Ctrl+C");
            e.Cancel = true;
        };
    }
}
