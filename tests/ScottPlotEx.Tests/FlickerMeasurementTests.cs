using System.Drawing;
using System.Text;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace ScottPlotEx.Tests;

/// <summary>
/// 闪烁量化：快速刷新期间高速采样窗口画面，统计出现“大面积背景（内容被擦除）”的帧比例。
/// 结果写入 artifacts/flicker-report.md；采样只在内存中分析，不保存任何截图。
/// </summary>
[Trait("Category", "Performance")]
public class FlickerMeasurementTests
{
    private const int DurationMs = 1200;

    private readonly ITestOutputHelper _output;

    public FlickerMeasurementTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "闪烁量化：控件尺寸与刷新方式对擦除闪帧比例的影响")]
    public void MeasureFlickerRatios()
    {
        StringBuilder report = new();
        report.AppendLine("# 闪烁量化：快速刷新时的擦除闪帧比例");
        report.AppendLine();
        report.AppendLine($"- 采样窗口：每帧 {DurationMs} ms 连续刷新，屏幕采样间隔 5 ms（仅内存分析，不保存截图）");

        List<(Size Size, string Variant, ScreenFlickerMeter.Result Result)> results = [];
        Size maxBuffer = Size.Empty;

        foreach (Size size in new[] { new Size(900, 600), new Size(1800, 1100) })
        {
            using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), size);
            ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
            Control canvas = host.Invoke(() => PlotTestHelper.GetInputCanvas(plot));
            maxBuffer = host.Invoke(() => BufferedGraphicsManager.Current.MaximumBuffer);

            host.Invoke(() =>
            {
                PlotTestHelper.AddWave(plot, 2000);
                PlotTestHelper.EnsureRendered(plot);
            });

            results.Add((size, "修复前：画布会擦除背景 + 父控件一起失效", Measure(host, plot, canvas, opaque: false, invalidateParent: true)));
            results.Add((size, "修复后：画布 Opaque（不擦背景）+ 只让画布失效", Measure(host, plot, canvas, opaque: true, invalidateParent: false)));

            host.AssertNoUnhandledExceptions();
        }

        report.AppendLine($"- WinForms 双缓冲上限（BufferedGraphicsManager.Current.MaximumBuffer）：{maxBuffer.Width} × {maxBuffer.Height}");
        report.AppendLine($"  —— 控件尺寸超过该上限时，`DoubleBuffered` 会退化为直接绘制，背景擦除会直接显示在屏幕上。");
        report.AppendLine();
        report.AppendLine("| 控件尺寸 | 刷新方式 | 采样帧数 | 擦除闪帧数 | 擦除闪帧比例 | 单帧背景占比（最大 / 平均） |");
        report.AppendLine("|---|---|---|---|---|---|");

        foreach ((Size size, string variant, ScreenFlickerMeter.Result result) in results)
        {
            report.AppendLine(
                $"| {size.Width} × {size.Height} | {variant} | {result.Frames} | {result.FlashedFrames} | " +
                $"**{result.FlashedFrameRatio:P1}** | {result.MaxBackgroundFraction:P1} / {result.AverageBackgroundFraction:P1} |");
        }

        report.AppendLine();
        report.AppendLine("说明：单帧背景占比 = 该帧中与 `SystemColors.Control` 接近的像素比例。");
        report.AppendLine("曲线控件的绘图区是白色、四周留白是灰色，因此正常帧该比例约 15%~25%；");
        report.AppendLine("若出现“整屏被擦成背景色”的中间状态，该比例会飙升到接近 100%。");

        string markdown = report.ToString();
        string path = Path.Combine(TestPaths.ArtifactsDirectory, "flicker-report.md");
        File.WriteAllText(path, markdown, Encoding.UTF8);

        _output.WriteLine(markdown);
        _output.WriteLine($"报告已写入：{path}");

        // 修复后不应出现整屏擦除闪帧
        foreach ((Size size, string variant, ScreenFlickerMeter.Result result) in results.Where(x => x.Variant.StartsWith("修复后")))
            Assert.True(result.FlashedFrameRatio < 0.05, $"{size.Width}×{size.Height} 修复后仍有 {result.FlashedFrameRatio:P1} 的擦除闪帧。");
    }

    private static ScreenFlickerMeter.Result Measure(
        WinFormsTestHost host,
        ScrollingPlotControl plot,
        Control canvas,
        bool opaque,
        bool invalidateParent)
    {
        host.Invoke(() => SetOpaque(canvas, opaque));

        return ScreenFlickerMeter.Measure(
            host,
            host.Form,
            plot,
            beforeEachFrame: invalidateParent ? () => plot.Invalidate(invalidateChildren: true) : null,
            durationMs: DurationMs);
    }

    /// <summary>通过反射切换画布的 Opaque 样式（用于对比修复前后的行为）。</summary>
    private static void SetOpaque(Control control, bool opaque)
    {
        typeof(Control)
            .GetMethod("SetStyle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(control, [ControlStyles.Opaque, opaque]);
    }
}
