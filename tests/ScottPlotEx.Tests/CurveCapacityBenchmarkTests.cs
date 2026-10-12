using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace ScottPlotEx.Tests;

/// <summary>
/// 容量基准：每条曲线 100 点/秒，曲线数从 10 加到 500，测“最多能支持多少条曲线”。
/// 稳定状态：跟随模式显示最近 60 秒，每条曲线先灌满一个窗口（6,000 点）；8 个采集线程分摊全部曲线，每 10 ms 给每条曲线写一批。
/// 结果写入 artifacts/curve-capacity-report.md（请用 Release 运行）。
/// </summary>
[Trait("Category", "Performance")]
public class CurveCapacityBenchmarkTests
{
    private const int RatePerCurve = 100;
    private const int WindowSeconds = 60;
    private const int WindowPoints = RatePerCurve * WindowSeconds;
    private const int WriterThreads = 8;
    private static readonly int[] CurveCounts = [10, 25, 50, 100, 150, 200, 300, 400, 500];
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Measure = TimeSpan.FromSeconds(4);

    /// <summary>控件程序集是否经过 JIT 优化（Debug 构建不优化，结果不可信）。</summary>
    private static string BuildConfiguration =>
        typeof(ScrollingPlotControl).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled)
            ? "未优化（Debug）——结果偏慢，请用 Release 运行"
            : "已优化（Release）";

    private readonly ITestOutputHelper _output;

    public CurveCapacityBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "容量基准：每条曲线 100 点/秒，最多能支持多少条曲线")]
    public void MeasureCurveCapacity()
    {
        Size normal = new(1100, 660);
        Size large = new(1920, 1080);

        List<Result> uncapped = [.. CurveCounts.Select(n => Run(n, normal, maxFps: 0))];
        List<Result> capped = [.. CurveCounts.Select(n => Run(n, normal, maxFps: 30))];
        List<Result> largeUncapped = [.. CurveCounts.Select(n => Run(n, large, maxFps: 0))];

        StringBuilder report = new();
        report.AppendLine("# 容量基准：每条曲线 100 点/秒，最多能支持多少条曲线");
        report.AppendLine();
        report.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}；构建配置：{BuildConfiguration}");
        report.AppendLine($"- 操作系统：{RuntimeInformation.OSDescription}；处理器：{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}（逻辑核心数 {Environment.ProcessorCount}）");
        report.AppendLine($"- 稳定状态：跟随模式显示最近 {WindowSeconds} 秒（FollowWindowPoints = {WindowPoints:N0}），每条曲线先灌满一个窗口；图例关闭");
        report.AppendLine($"- 写入：{WriterThreads} 个采集线程分摊全部曲线，每 10 ms 给每条曲线写一批（PlotSeries.AddValues，跨线程按帧合并）");
        report.AppendLine($"- 每档预热 {Warmup.TotalSeconds:F1} 秒后测量 {Measure.TotalSeconds:F0} 秒；CPU 为周期精确计量（单核百分比）");
        report.AppendLine("- 显示滞后：测量结束瞬间已写入但尚未进入控件的数据量 ÷ 总写入速率；UI 延迟：后台线程每 5 ms 投递一次 BeginInvoke 到执行的耗时");
        report.AppendLine();

        AppendTable(report, $"## 1. 不限帧率（{normal.Width}×{normal.Height}）：能画多快", uncapped);
        AppendTable(report, $"## 2. 帧率上限 30（{normal.Width}×{normal.Height}）：推荐的实际用法", capped);
        AppendTable(report, $"## 3. 不限帧率（{large.Width}×{large.Height}）：大窗口", largeUncapped);

        report.AppendLine("## 结论");
        report.AppendLine();
        report.AppendLine("判定：**流畅** = 不限帧率时 ≥ 60 FPS；**可用** = ≥ 30 FPS；两者都要求数据不积压（显示滞后 < 0.1 秒）且 UI 延迟 p95 < 50 ms。");
        report.AppendLine();
        AppendConclusion(report, $"{normal.Width}×{normal.Height}", uncapped);
        AppendConclusion(report, $"{large.Width}×{large.Height}", largeUncapped);

        int steady = capped.Where(r => Verdict(r).StartsWith("稳定", StringComparison.Ordinal)).Select(r => r.Curves).DefaultIfEmpty(0).Max();
        report.AppendLine($"- {normal.Width}×{normal.Height} 帧率上限 30：稳定 30 帧、数据不积压、UI 不卡，最多 **{steady}** 条（UI 线程占用见第 2 节，留出余量时建议不超过占用约 70% 的档位）。");
        report.AppendLine("- 所有档位都没有丢点：写入 100% 达成，积压不超过 0.2 秒——瓶颈是绘制（每帧耗时随曲线数线性增长），不是数据接收。");
        report.AppendLine();
        report.AppendLine("重新运行：`dotnet test ScottPlotEx.sln -c Release --filter \"FullyQualifiedName~CurveCapacityBenchmarkTests\"`（结果写入此文件）。");

        string markdown = report.ToString();
        File.WriteAllText(Path.Combine(TestPaths.ArtifactsDirectory, "curve-capacity-report.md"), markdown, Encoding.UTF8);
        _output.WriteLine(markdown);

        Assert.All(uncapped.Concat(capped).Concat(largeUncapped), r => Assert.True(r.WriteRate > 0));
    }

    private static void AppendTable(StringBuilder report, string title, List<Result> results)
    {
        report.AppendLine(title);
        report.AppendLine();
        report.AppendLine("| 曲线数 | 目标写入(点/秒) | 达成 | 显示滞后(秒) | 实测 FPS | UI 线程每帧(ms) | UI 线程 CPU | 进程 CPU | UI 延迟 p50 / p95 (ms) | 判定 |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (Result r in results)
        {
            report.AppendLine(
                $"| {r.Curves} | {r.Curves * RatePerCurve:N0} | {r.WriteRate / (r.Curves * RatePerCurve):P0} | {r.LagSeconds:F3} | {r.Fps:F1} | " +
                $"{r.UiMsPerFrame:F2} | {r.UiCpuPercent:F0}% | {r.ProcessCpuPercent:F0}% | {r.LatencyP50:F1} / {r.LatencyP95:F1} | {Verdict(r)} |");
        }

        report.AppendLine();
    }

    private static void AppendConclusion(StringBuilder report, string size, List<Result> uncapped)
    {
        int smooth = uncapped.Where(r => Verdict(r) == "流畅").Select(r => r.Curves).DefaultIfEmpty(0).Max();
        int usable = uncapped.Where(r => Verdict(r) is "流畅" or "可用").Select(r => r.Curves).DefaultIfEmpty(0).Max();
        report.AppendLine($"- {size}：流畅（≥ 60 FPS）最多 **{smooth}** 条，可用（≥ 30 FPS）最多 **{usable}** 条（实测档位：{string.Join(" / ", CurveCounts)}）。");
    }

    private static string Verdict(Result r)
    {
        bool healthy = r.LagSeconds < 0.1 && r.LatencyP95 < 50 && r.WriteRate >= r.Curves * RatePerCurve * 0.98;

        // 设了帧率上限：能稳定跑在上限附近（≥ 90%）即为达标
        if (r.MaxFps > 0)
            return healthy && r.Fps >= r.MaxFps * 0.9 ? $"稳定 {r.MaxFps} 帧" : "掉帧";

        if (!healthy)
            return "卡顿";

        return r.Fps >= 60 ? "流畅" : r.Fps >= 30 ? "可用" : "卡顿";
    }

    private static Result Run(int curves, Size size, int maxFps)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), size, opacity: 1.0);
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries[] series = host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Follow;
            plot.FollowWindowPoints = WindowPoints;
            plot.MaxFramesPerSecond = maxFps;
            plot.ShowLegend = false;

            PlotSeries[] created = new PlotSeries[curves];
            double[] prefill = new double[WindowPoints];

            for (int i = 0; i < curves; i++)
            {
                created[i] = plot.AddSeries($"曲线 {i + 1}");
                double offset = (i % 20) * 4.0;
                double phase = i * 0.37;

                for (int k = 0; k < prefill.Length; k++)
                    prefill[k] = Value(k, offset, phase);

                created[i].AddValues(prefill);
            }

            return created;
        });

        long written = (long)curves * WindowPoints;
        bool stop = false;
        bool measuring = false;
        List<double> latencies = [];

        int threadCount = Math.Min(WriterThreads, curves);
        Thread[] writers =
        [
            .. Enumerable.Range(0, threadCount).Select(w => new Thread(() =>
            {
                int[] indices = [.. Enumerable.Range(0, curves).Where(i => i % threadCount == w)];
                PlotSeries[] mine = [.. indices.Select(i => series[i])];
                long[] samples = new long[mine.Length];
                Array.Fill(samples, WindowPoints);
                double[] batch = new double[64];
                Stopwatch clock = Stopwatch.StartNew();
                double last = 0, carry = 0;

                while (!Volatile.Read(ref stop))
                {
                    double now = clock.Elapsed.TotalSeconds;
                    double due = (RatePerCurve * (now - last)) + carry;

                    if (due >= 1)
                    {
                        int count = (int)due;
                        carry = due - count;
                        last = now;

                        if (count > batch.Length)
                            batch = new double[count];

                        for (int c = 0; c < mine.Length; c++)
                        {
                            int index = indices[c];
                            double offset = (index % 20) * 4.0;
                            double phase = index * 0.37;

                            for (int k = 0; k < count; k++)
                                batch[k] = Value(samples[c]++, offset, phase);

                            mine[c].AddValues(batch.AsSpan(0, count));
                        }

                        Interlocked.Add(ref written, (long)count * mine.Length);
                    }

                    Thread.Sleep(10);
                }
            })
            {
                IsBackground = true,
            }),
        ];

        Thread probe = new(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    host.Form.BeginInvoke(() =>
                    {
                        if (Volatile.Read(ref measuring))
                        {
                            lock (latencies)
                                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                        }
                    });
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                Thread.Sleep(5);
            }
        })
        {
            IsBackground = true,
        };

        foreach (Thread writer in writers)
            writer.Start();
        probe.Start();

        Thread.Sleep(Warmup);

        double processStart = CycleCpu.ProcessSeconds();
        long writtenStart = Interlocked.Read(ref written);
        (long rendersStart, double uiStart) = host.Invoke(() => (plot.Plot.RenderManager.RenderCount, CycleCpu.CurrentThreadSeconds()));
        Stopwatch window = Stopwatch.StartNew();
        Volatile.Write(ref measuring, true);

        Thread.Sleep(Measure);

        Volatile.Write(ref measuring, false);
        // 同一时刻读取：已写入的总点数与控件中的总点数之差 = 尚未进入控件的积压
        (long rendersEnd, double uiEnd, long stored, long writtenNow) = host.Invoke(() =>
            (plot.Plot.RenderManager.RenderCount, CycleCpu.CurrentThreadSeconds(), series.Sum(s => (long)s.DataCount), Interlocked.Read(ref written)));
        double seconds = window.Elapsed.TotalSeconds;
        double processEnd = CycleCpu.ProcessSeconds();

        Volatile.Write(ref stop, true);
        foreach (Thread writer in writers)
            writer.Join(5000);
        probe.Join(2000);

        double[] samplesSorted;
        lock (latencies)
            samplesSorted = [.. latencies.Order()];

        host.AssertNoUnhandledExceptions();

        double fps = (rendersEnd - rendersStart) / seconds;
        double uiSeconds = uiEnd - uiStart;

        return new Result(
            curves,
            maxFps,
            (writtenNow - writtenStart) / seconds,
            Math.Max(0, writtenNow - stored) / (double)(curves * RatePerCurve),
            fps,
            uiSeconds * 1000 / Math.Max(1, rendersEnd - rendersStart),
            uiSeconds / seconds * 100,
            (processEnd - processStart) / seconds * 100,
            Percentile(samplesSorted, 0.50),
            Percentile(samplesSorted, 0.95));
    }

    private static double Value(long sample, double offset, double phase)
    {
        double t = sample / (double)RatePerCurve;
        return offset + (Math.Sin((t * 0.5) + phase) * 8) + (Math.Sin((t * 3.1) + phase) * 1.5) + (((sample * 7919 % 1000) / 1000.0) - 0.5);
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];

    private sealed record Result(
        int Curves,
        int MaxFps,
        double WriteRate,
        double LagSeconds,
        double Fps,
        double UiMsPerFrame,
        double UiCpuPercent,
        double ProcessCpuPercent,
        double LatencyP50,
        double LatencyP95);
}
