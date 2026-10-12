using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Demo;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 在测试进程内驱动真实的演示窗体（<see cref="MainForm"/>）并生成文档截图，
/// 因此截图内容可重复、且不会截到桌面上的其它窗口。
/// </summary>
public class DemoScreenshotTests
{
    private static readonly Size DemoSize = new(1100, 660);

    [Fact(DisplayName = "生成演示程序文档截图（堆叠 / 拖动停止 / 滚轮 / 中键整体显示 / 跟随）")]
    public void RenderDemoScreenshots()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MainForm(), DemoSize);
        MainForm form = host.ControlAs<MainForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);
        Control canvas = host.Invoke(() => PlotTestHelper.GetInputCanvas(plot));

        // 先补一些数据，保证截图里能看到完整曲线（默认堆叠模式）
        host.Invoke(() =>
        {
            for (int i = 0; i < 40; i++)
                plot.AddValue(50 + (25 * Math.Sin(i / 4.0)) + (3 * Math.Sin(i / 1.5)));

            plot.Mode = ScrollingMode.Stacked;
            PlotTestHelper.EnsureRendered(plot);
        });

        host.Pump(300);
        host.Invoke(() => Assert.Equal(ScrollingMode.Stacked, plot.Mode));
        CaptureTo(host, form, "demo-01-stacked.png");

        // 左键向右拖动 → 看不到最新数据 → 停止滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.30, 0.5),
                PlotTestHelper.PointInDataArea(rect, 0.85, 0.5),
                MouseButtons.Left);
        });

        host.Pump(200);
        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
            Assert.True(plot.IsScrollingStopped);
        });

        CaptureTo(host, form, "demo-02-after-drag.png");

        // 滚轮缩放 → 仍然保持停止滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), 120);
        });

        host.Pump(200);
        host.Invoke(() => Assert.True(plot.IsScrollingStopped));
        CaptureTo(host, form, "demo-03-after-wheel.png");

        // 中键单击（整体显示）→ 退出停止状态并继续滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Middle);
        });

        host.Pump(300);
        host.Invoke(() =>
        {
            Assert.False(plot.IsScrollingStopped);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
        });

        CaptureTo(host, form, "demo-05-middle-click-resume.png");

        // 跟随模式
        host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Follow;
            PlotTestHelper.EnsureRendered(plot);
        });

        host.Pump(200);
        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Follow, plot.Mode);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            int count = plot.DataCount;
            Assert.Equal((count * 0.7) + 2, xMin, 6);
            Assert.Equal(count + 2.0, xMax, 6);
        });

        CaptureTo(host, form, "demo-06-follow-mode.png");

        // 跟随模式 + 中键单击 → 自动换成堆叠模式
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Middle);
        });

        host.Pump(300);
        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(plot.DataCount + 2.0, xMax, 6);
        });

        CaptureTo(host, form, "demo-07-follow-to-stacked.png");

        // 百分比调整后再切回堆叠模式：应回到“显示全部数据”，而不是停留在被裁短的视图
        host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Follow;
            plot.SetXAxisDisplayPercent(50, ScrollingMode.Follow); // 跟随窗口改成后 50%
        });

        host.Pump(200);
        host.Invoke(() =>
        {
            (double followMin, double followMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(followMax - (plot.DataCount * 0.5), followMin, 6);
        });

        host.Invoke(() => plot.Mode = ScrollingMode.Stacked); // 重新选择堆叠模式

        host.Pump(250);
        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(plot.DataCount + 2.0, xMax, 6);
        });

        CaptureTo(host, form, "demo-08-stacked-after-percent.png");

        // 堆叠模式把显示百分比设成 90%：应显示约 90% 的数据（不是跟随样子的窄窗口）
        host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Stacked;
            // 模拟用户在“堆叠显示 %”输入框里输入 90
            FindControl<NumericUpDown>(form)!.Value = 90;
        });

        host.Pump(250);
        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(90.0, plot.StackedDisplayPercent, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(plot.StackedOriginX, xMin, 6);   // 原点固定为设置时的位置
            Assert.Equal(plot.DataCount + 2.0, xMax, 6);  // 右端随数据增长
            Assert.True(xMin < xMax * 0.2, "90% 只裁掉最早的一小部分数据。");
        });

        CaptureTo(host, form, "demo-09-stacked-90-percent.png");
        host.AssertNoUnhandledExceptions();
    }

    private static T? FindControl<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed)
                return typed;

            T? nested = FindControl<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static void CaptureTo(WinFormsTestHost host, Form demoForm, string fileName)
    {
        string path = Path.Combine(TestPaths.DocsDirectory, fileName);

        host.Invoke(() =>
        {
            // 测试宿主窗体平时接近全透明（避免干扰用户），截图时临时恢复为不透明
            host.Form.Opacity = 1.0;
        });

        host.Pump(250);

        host.Invoke(() =>
        {
            host.Form.Refresh();
            demoForm.Refresh();
            WindowCapture.Save(demoForm.Handle, path);
            host.Form.Opacity = 0.01;
        });

        Assert.True(File.Exists(path), $"未生成截图 {fileName}。");
    }
}
