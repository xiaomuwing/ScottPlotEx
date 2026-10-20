using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace ScottPlotEx.Tests;

/// <summary>
/// 性能基准：测量本控件每秒最多能接收多少个数据点。
/// 结果写入 artifacts/performance-report.md，并在测试输出中打印。
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceBenchmarkTests
{
    private const int ControlWidth = 1100;
    private const int ControlHeight = 660;
    private const int LargeWidth = 1920;
    private const int LargeHeight = 1080;

    private readonly ITestOutputHelper _output;

    public PerformanceBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "性能基准：控件最大每秒数据更新数量")]
    public void MeasureMaxDataUpdateRate()
    {
        // 不透明窗口：近透明的分层窗口会让绘制多走一层合成，测得的帧耗时偏高、与真实窗口不符
        using WinFormsTestHost host = WinFormsTestHost.Start(
            () => new ScrollingPlotControl(),
            new Size(ControlWidth, ControlHeight),
            opacity: 1.0);

        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        StringBuilder report = new();

#if DEBUG
        const string BuildConfiguration = "Debug（控件代码未经过 JIT 优化，性能数字会明显偏低，请用 Release 复测）";
#else
        const string BuildConfiguration = "Release（已优化）";
#endif

        report.AppendLine("# ScottPlotEx 控件性能基准：最大每秒数据更新数量");
        report.AppendLine();
        report.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"- 构建配置：{BuildConfiguration}");
        report.AppendLine($"- 操作系统：{RuntimeInformation.OSDescription}");
        report.AppendLine($"- 处理器：{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}（逻辑核心数 {Environment.ProcessorCount}）");
        report.AppendLine($"- 运行时：{RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"- 控件尺寸：{ControlWidth} × {ControlHeight}，另测 {LargeWidth} × {LargeHeight}（ScottPlot {GetScottPlotVersion()}，SkiaSharp 软件渲染 → GDI DIB → BitBlt）");
        report.AppendLine();

        // ---------- 1. 数据管线吞吐（合并渲染：控件默认行为，绘制由消息循环合并）----------
        // “堆叠模式（显示 90%）”与“拖动后继续滚动”（X 轴偏移 > 0）走同一条路径：窗口没有覆盖全部数据，
        // 需要按可见范围统计 Y 轴；跟随模式始终如此。这几组以前是每点 O(窗口) 扫描，现在走极值金字塔。
        List<(string Scenario, int Buffer, int Appends, double Ms)> pipeline =
        [
            ("堆叠模式", 10_000, 50_000, MeasureAppendMilliseconds(10_000, 50_000, ScrollingMode.Stacked)),
            ("堆叠模式", 1_000_000, 50_000, MeasureAppendMilliseconds(1_000_000, 50_000, ScrollingMode.Stacked)),
            ("堆叠模式（显示 90%）", 1_000_000, 50_000, MeasureAppendMilliseconds(1_000_000, 50_000, ScrollingMode.Stacked, stackedPercent: 90)),
            ("跟随模式", 10_000, 20_000, MeasureAppendMilliseconds(10_000, 20_000, ScrollingMode.Follow)),
            ("跟随模式", 100_000, 20_000, MeasureAppendMilliseconds(100_000, 20_000, ScrollingMode.Follow)),
            ("跟随模式", 1_000_000, 50_000, MeasureAppendMilliseconds(1_000_000, 50_000, ScrollingMode.Follow)),
        ];

        report.AppendLine("## 1. 数据接入吞吐（合并渲染，控件默认行为）");
        report.AppendLine();
        report.AppendLine("只测量 `AddValue()` 数据管线本身（含自动滚动范围计算、事件、异步重绘请求），不等待绘制；");
        report.AppendLine("每组先预热再重复 3 次，取中位数。");
        report.AppendLine();
        report.AppendLine("| 模式 | 缓冲区已有数据点 | 追加数据点 | 单点耗时 | **每秒可接收数据点** |");
        report.AppendLine("|---|---|---|---|---|");

        foreach ((string scenario, int buffer, int appends, double ms) in pipeline)
        {
            double perPointUs = ms * 1000 / appends;
            double perSecond = appends / (ms / 1000);
            report.AppendLine($"| {scenario} | {buffer:N0} | {appends:N0} | {perPointUs:F2} µs | **{perSecond:N0}** |");
        }

        report.AppendLine();

        // ---------- 2. 单帧渲染耗时（同步 Refresh：SkiaSharp 渲染进 DIB + BitBlt 到窗口）----------
        int[] sizes = [0, 1_000, 10_000, 100_000, 300_000, 1_000_000];
        Dictionary<int, double> frameMs = [];

        foreach (int size in sizes)
            frameMs[size] = MeasureFrameMilliseconds(size, frames: size >= 1_000_000 ? 20 : 40, appendPerFrame: false);

        // 大窗口（1920×1080）：呈现开销随像素面积增长，单独测一组
        Dictionary<int, double> largeFrameMs = [];

        using (WinFormsTestHost largeHost = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(LargeWidth, LargeHeight), opacity: 1.0))
        {
            ScrollingPlotControl largePlot = largeHost.ControlAs<ScrollingPlotControl>();

            foreach (int size in new[] { 0, 300_000, 1_000_000 })
                largeFrameMs[size] = MeasureFrameMilliseconds(size, frames: size >= 1_000_000 ? 20 : 40, appendPerFrame: false, largeHost, largePlot);

            largeHost.AssertNoUnhandledExceptions();
        }

        report.AppendLine("## 2. 单帧渲染耗时（堆叠模式，同步 Refresh：SkiaSharp 渲染进 DIB + BitBlt 到窗口）");
        report.AppendLine();
        report.AppendLine("测试窗口为普通不透明窗口（与真实应用一致）；每组先预热，再取多帧平均。");
        report.AppendLine("测试数据为高频正弦 sin(i/10)：数据点多时每个像素列都覆盖整个 Y 范围，相当于每帧把数据区整片涂满，");
        report.AppendLine("是光栅化的最坏情况，大窗口下的帧耗时明显高于普通信号（普通信号的前后对比见 README“性能基准”一节）。");
        report.AppendLine();
        report.AppendLine($"| 缓冲区数据点 | {ControlWidth}×{ControlHeight} 毫秒/帧 | 理论最大刷新率 | {LargeWidth}×{LargeHeight} 毫秒/帧 | 理论最大刷新率 |");
        report.AppendLine("|---|---|---|---|---|");

        foreach (int size in sizes)
        {
            string large = largeFrameMs.TryGetValue(size, out double ms) ? $"{ms:F2} ms | {1000 / ms:N0} FPS" : "— | —";
            report.AppendLine($"| {(size == 0 ? "0（空图基线）" : size.ToString("N0"))} | {frameMs[size]:F2} ms | {1000 / frameMs[size]:N0} FPS | {large} |");
        }

        report.AppendLine();

        // ---------- 3. 每个数据点后立即绘制一帧（最保守用法）----------
        List<(int Buffer, double Ms)> perUpdate = [];

        foreach (int size in new[] { 1_000, 10_000, 100_000 })
            perUpdate.Add((size, MeasureFrameMilliseconds(size, frames: 10, appendPerFrame: true)));

        report.AppendLine("## 3. 每个数据点后立即绘制一帧（最保守用法：更新与绘制串行）");
        report.AppendLine();
        report.AppendLine("| 缓冲区数据点 | 每次更新+绘制耗时 | **最大更新率（点/秒）** |");
        report.AppendLine("|---|---|---|");

        foreach ((int buffer, double ms) in perUpdate)
            report.AppendLine($"| {buffer:N0} | {ms:F2} ms | **{1000 / ms:N0}** |");

        report.AppendLine();

        // ---------- 4. 真实 1 秒内可持续完成的更新数（数据与绘制交错）----------
        const int WindowMs = 1000;
        List<(string Mode, int Buffer, double Rate)> sustained =
        [
            ("堆叠模式", 1_000, MeasureSustainedRate(1_000, ScrollingMode.Stacked, WindowMs)),
            ("堆叠模式", 10_000, MeasureSustainedRate(10_000, ScrollingMode.Stacked, WindowMs)),
            ("堆叠模式", 100_000, MeasureSustainedRate(100_000, ScrollingMode.Stacked, WindowMs)),
            ("跟随模式", 10_000, MeasureSustainedRate(10_000, ScrollingMode.Follow, WindowMs)),
        ];

        report.AppendLine("## 4. 实际可持续更新率（数据 + 真实绘制交错，1 秒窗口）");
        report.AppendLine();
        report.AppendLine("| 模式 | 缓冲区数据点 | **每秒实际完成更新数** |");
        report.AppendLine("|---|---|---|");

        foreach ((string mode, int buffer, double rate) in sustained)
            report.AppendLine($"| {mode} | {buffer:N0} | **{rate:N0}** |");

        report.AppendLine();
        report.AppendLine("## 结论");
        report.AppendLine();

        double worstPipeline = pipeline.Min(x => x.Appends / (x.Ms / 1000));
        double stackedPipeline = pipeline.Where(x => x.Scenario == "堆叠模式").Min(x => x.Appends / (x.Ms / 1000));
        double stackedPipelineUs = pipeline.Where(x => x.Scenario == "堆叠模式").Max(x => x.Ms * 1000 / x.Appends);
        double croppedPipeline = pipeline.First(x => x.Scenario == "堆叠模式（显示 90%）").Appends / (pipeline.First(x => x.Scenario == "堆叠模式（显示 90%）").Ms / 1000);

        report.AppendLine("### 最大每秒数据更新数量");
        report.AppendLine();
        report.AppendLine("| 使用方式 | 最大更新率 | 说明 |");
        report.AppendLine("|---|---|---|");
        report.AppendLine($"| 仅数据接入（堆叠模式，合并渲染） | **≈ {stackedPipeline:N0} 点/秒** | O(1)/点，与缓冲区大小无关 |");
        report.AppendLine($"| 仅数据接入（堆叠模式显示 90% / 拖动后继续滚动，100 万点） | **≈ {croppedPipeline:N0} 点/秒** | 可见范围查询走极值金字塔，与窗口长度无关 |");
        report.AppendLine($"| 仅数据接入（跟随模式） | **≈ {pipeline.Where(x => x.Scenario == "跟随模式").Max(x => x.Appends / (x.Ms / 1000)):N0} ~ {pipeline.Where(x => x.Scenario == "跟随模式").Min(x => x.Appends / (x.Ms / 1000)):N0} 点/秒** | 可见范围查询走极值金字塔，与窗口长度无关 |");
        report.AppendLine($"| 数据 + 真实绘制交错（堆叠 1,000 点） | **≈ {sustained.First(x => x.Buffer == 1_000).Rate:N0} 点/秒** | 绘制成为瓶颈 |");
        report.AppendLine($"| 数据 + 真实绘制交错（堆叠 10,000 点） | **≈ {sustained.First(x => x.Buffer == 10_000 && x.Mode == "堆叠模式").Rate:N0} 点/秒** | 屏上点数越多越慢 |");
        report.AppendLine($"| 数据 + 真实绘制交错（堆叠 100,000 点） | **≈ {sustained.First(x => x.Buffer == 100_000).Rate:N0} 点/秒** | |");
        report.AppendLine($"| 每个点后立即绘制一帧（1,000 / 10,000 / 100,000 点） | **≈ {string.Join(" / ", perUpdate.Select(x => $"{1000 / x.Ms:N0}"))} 点/秒** | 最保守用法 |");
        report.AppendLine();
        report.AppendLine("### 工程结论");
        report.AppendLine();
        report.AppendLine($"- 数据接入本身很快：堆叠模式约 **{stackedPipeline:N0} 点/秒**（{stackedPipelineUs:F2} µs/点），各场景最低也在 **{worstPipeline:N0} 点/秒** 以上（可见范围 Y 轴统计不再随窗口长度变慢）。");
        report.AppendLine($"- 单帧绘制（{ControlWidth}×{ControlHeight}）：空图 {frameMs[0]:F2} ms，10,000 点 {frameMs[10_000]:F2} ms，300,000 点 {frameMs[300_000]:F2} ms，1,000,000 点 {frameMs[1_000_000]:F2} ms，基本不随点数增长（极值金字塔 + 自绘画布）。");
        report.AppendLine($"- 大窗口（{LargeWidth}×{LargeHeight}）：空图 {largeFrameMs[0]:F2} ms，300,000 点 {largeFrameMs[300_000]:F2} ms（≈ {1000 / largeFrameMs[300_000]:N0} FPS），1,000,000 点 {largeFrameMs[1_000_000]:F2} ms；呈现开销随像素面积增长。");
        report.AppendLine($"- 按 60 FPS（16.7 ms/帧）规划时，屏上点数建议控制在约 {LargestSizeForFps(frameMs, 60):N0} 点以内；本例 10,000 点为 {1000 / frameMs[10_000]:N0} FPS。");
        report.AppendLine("- 每秒 1 点（本控件默认使用场景）或每秒几十~几百点的采集卡场景：余量在百倍以上，无压力。");
        report.AppendLine("- 每秒上万点的场景：建议灌入照常（不丢点），同时限制屏上点数（例如滚动缓冲、抽稀显示）以获得稳定的刷新率。");
        report.AppendLine();
        report.AppendLine($"重新运行：`dotnet test ScottPlotEx.sln --filter \"Category=Performance\"`（结果写入此文件）。");

        string markdown = report.ToString();
        string path = Path.Combine(TestPaths.ArtifactsDirectory, "performance-report.md");
        File.WriteAllText(path, markdown, Encoding.UTF8);

        _output.WriteLine(markdown);
        _output.WriteLine($"报告已写入：{path}");

        // 基本健壮性断言：所有场景都应产生有效数值
        Assert.All(pipeline, x => Assert.True(x.Ms > 0));
        Assert.All(sustained, x => Assert.True(x.Rate > 0));
        host.AssertNoUnhandledExceptions();

        // ---------- 本地函数 ----------

        double MeasureAppendMilliseconds(int bufferSize, int appendCount, ScrollingMode mode, int repetitions = 3, double stackedPercent = 100)
        {
            return host.Invoke(() =>
            {
                // 预热：JIT 与首次分配不进入计时
                FillBuffer(plot, Math.Min(bufferSize, 10_000));
                SelectMode(mode, stackedPercent);

                for (int i = 0; i < 20_000; i++)
                    plot.AddValue(i % 100);

                List<double> samples = [];

                for (int repetition = 0; repetition < repetitions; repetition++)
                {
                    FillBuffer(plot, bufferSize);
                    SelectMode(mode, stackedPercent);

                    Stopwatch stopwatch = Stopwatch.StartNew();

                    for (int i = 0; i < appendCount; i++)
                        plot.AddValue(i % 100);

                    stopwatch.Stop();
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }

                samples.Sort();
                return samples[samples.Count / 2]; // 中位数
            });
        }

        double MeasureFrameMilliseconds(
            int bufferSize,
            int frames,
            bool appendPerFrame,
            WinFormsTestHost? targetHost = null,
            ScrollingPlotControl? targetPlot = null)
        {
            WinFormsTestHost h = targetHost ?? host;
            ScrollingPlotControl p = targetPlot ?? plot;

            return h.Invoke(() =>
            {
                FillBuffer(p, bufferSize);
                p.Mode = ScrollingMode.Stacked;
                PlotTestHelper.EnsureRendered(p);

                for (int i = 0; i < 3; i++) // 预热
                {
                    if (appendPerFrame)
                        p.AddValue(i);

                    p.Refresh();
                }

                Stopwatch stopwatch = Stopwatch.StartNew();

                for (int i = 0; i < frames; i++)
                {
                    if (appendPerFrame)
                        p.AddValue(i);

                    p.Refresh();
                }

                stopwatch.Stop();
                return stopwatch.Elapsed.TotalMilliseconds / frames;
            });
        }

        double MeasureSustainedRate(int bufferSize, ScrollingMode mode, int windowMs)
        {
            return host.Invoke(() =>
            {
                FillBuffer(plot, bufferSize);
                plot.Mode = mode;

                int count = 0;
                Stopwatch stopwatch = Stopwatch.StartNew();

                while (stopwatch.ElapsedMilliseconds < windowMs)
                {
                    plot.AddValue(count % 100);
                    Application.DoEvents(); // 让真实绘制插入进来
                    count++;
                }

                stopwatch.Stop();
                return count / stopwatch.Elapsed.TotalSeconds;
            });
        }

        void SelectMode(ScrollingMode mode, double stackedPercent)
        {
            plot.Mode = mode; // 重新选择模式会把显示百分比恢复为默认值，因此百分比要在之后设置

            if (mode == ScrollingMode.Stacked && stackedPercent < 100)
                plot.SetXAxisDisplayPercent(stackedPercent, ScrollingMode.Stacked);
        }

        static void FillBuffer(ScrollingPlotControl target, int size)
        {
            target.ClearData();

            double[] values = new double[size];
            for (int i = 0; i < size; i++)
                values[i] = Math.Sin(i / 10.0) * 10;

            target.AddValues(values);
        }
    }

    private static int LargestSizeForFps(Dictionary<int, double> frameMs, int fps)
    {
        double budget = 1000.0 / fps;
        int best = 0;

        foreach ((int size, double ms) in frameMs)
        {
            if (ms <= budget)
                best = Math.Max(best, size);
        }

        return best;
    }

    private static string GetScottPlotVersion() =>
        typeof(ScottPlot.Plot).Assembly.GetName().Version?.ToString() ?? "unknown";
}
