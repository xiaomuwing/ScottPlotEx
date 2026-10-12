using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// X 轴显示长度百分比接口：
/// 两种模式都表示“显示多少数据”：数值越大看到的数据越多。
/// 堆叠模式 = 显示最新“百分比”的数据（100 为全部）；跟随模式 = 显示最后“百分比”的数据窗口。
/// </summary>
public class XAxisDisplayPercentTests
{
    [Fact(DisplayName = "堆叠模式：显示百分比决定可见数据量，最大值仍为 点数+2")]
    public void StackedMode_DisplayPercent()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            // 默认 100%：从 0 开始显示全部数据
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);
            Assert.Equal(100.0, plot.CurrentXAxisDisplayPercent, 9);
            (double xMinDefault, double xMaxDefault) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMinDefault, 6);
            Assert.Equal(102.0, xMaxDefault, 6);

            // 设为 20%：在“当前 100 个点”这个时刻把原点固定在 102 × 80% = 81.6
            plot.StackedDisplayPercent = 20;

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(102.0 * 0.8, xMin, 6);
            Assert.Equal(102.0, xMax, 6);
            Assert.Equal(102.0 * 0.8, plot.StackedOriginX, 6);

            // 继续添加数据：原点固定不动（这是与跟随模式的关键区别），右端随数据增长
            for (int i = 0; i < 50; i++)
                plot.AddValue(i);

            (xMin, xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(102.0 * 0.8, xMin, 6);
            Assert.Equal(102.0 * 0.8, plot.StackedOriginX, 6);
            Assert.Equal(152.0, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "堆叠模式：显示百分比会把 Y 轴自动缩放到当前显示的数据")]
    public void StackedMode_DisplayPercent_ScalesYToVisibleData()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            plot.StackedDisplayPercent = 50; // 只显示最新 50%（x ≥ 51，值 51~100）

            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(51 - (49 * 0.05), yMin, 6);
            Assert.Equal(100 + (49 * 0.05), yMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "跟随模式：窗口百分比决定显示长度")]
    public void FollowMode_WindowPercent()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            plot.Mode = ScrollingMode.Follow;

            // 默认 30%
            Assert.Equal(30.0, plot.FollowWindowPercent, 9);
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(72.0, xMin, 6);
            Assert.Equal(102.0, xMax, 6);

            // 改为 10%：窗口长度 10
            plot.FollowWindowPercent = 10;
            (xMin, xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(92.0, xMin, 6);
            Assert.Equal(102.0, xMax, 6);
            Assert.Equal(10.0, xMax - xMin, 6);

            // 继续添加数据：窗口长度保持 点数 × 10%
            for (int i = 0; i < 100; i++)
                plot.AddValue(i);

            (xMin, xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(200 * 0.1, xMax - xMin, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "统一接口 XAxisDisplayPercent：两种模式方向一致（越大看得越多）")]
    public void XAxisDisplayPercent_IsModeAware()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));

            // 堆叠模式：读出的是“显示长度百分比”（默认 100%）
            Assert.Equal(100.0, plot.CurrentXAxisDisplayPercent, 9);
            plot.SetXAxisDisplayPercent(40, ScrollingMode.Stacked);

            Assert.Equal(40.0, plot.StackedDisplayPercent, 9);
            Assert.Equal(30.0, plot.FollowWindowPercent, 9); // 跟随模式的值不受影响
            Assert.Equal(40.0, plot.CurrentXAxisDisplayPercent, 9);
            Assert.Equal(40.0, plot.GetXAxisDisplayPercent(ScrollingMode.Stacked), 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(102.0 * 0.6, xMin, 6); // 显示最新 40%
            Assert.Equal(102.0, xMax, 6);

            // 跟随模式：读取到的是“窗口百分比”，写入也只影响窗口
            plot.Mode = ScrollingMode.Follow;
            Assert.Equal(30.0, plot.CurrentXAxisDisplayPercent, 9);
            Assert.Equal(30.0, plot.GetXAxisDisplayPercent(ScrollingMode.Follow), 9);

            plot.SetXAxisDisplayPercent(50, ScrollingMode.Follow);
            Assert.Equal(50.0, plot.FollowWindowPercent, 9);
            Assert.Equal(40.0, plot.StackedDisplayPercent, 9); // 堆叠模式的值不受影响

            (xMin, xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(52.0, xMin, 6); // 102 - 100×50%
            Assert.Equal(102.0, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "停止滚动时：百分比接口作用于暂停前的模式")]
    public void XAxisDisplayPercent_WhileStopped_TargetsPreviousMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            plot.Mode = ScrollingMode.Follow;
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        // 滚轮缩放 -> 停止滚动（手动模式）
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), 120);
        });

        host.Invoke(() =>
        {
            Assert.True(plot.IsScrollingStopped);
            Assert.Equal(ScrollingMode.Follow, plot.LastAutoScrollMode);

            // 手动模式下写入，仍然写给“暂停前的模式”（跟随）
            plot.SetXAxisDisplayPercent(20, ScrollingMode.Manual);
            Assert.Equal(20.0, plot.FollowWindowPercent, 9);

            // 停止滚动期间设置的百分比会一直保留到重新选择模式为止
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
            Assert.Equal(20.0, plot.FollowWindowPercent, 9);

            // 重新选择跟随模式 -> 回到该模式默认视图（30%），百分比也复位
            plot.Mode = ScrollingMode.Follow;

            Assert.Equal(30.0, plot.FollowWindowPercent, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(102 - (100 * 0.3), xMin, 6);
            Assert.Equal(102.0, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "百分比接口：越界值被自动限制在有效范围内")]
    public void PercentValues_AreClamped()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));

            plot.StackedDisplayPercent = -20;
            Assert.Equal(1.0, plot.StackedDisplayPercent, 9);

            plot.StackedDisplayPercent = 500;
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);

            plot.FollowWindowPercent = 0;
            Assert.Equal(0.1, plot.FollowWindowPercent, 9);

            plot.FollowWindowPercent = double.NaN;
            Assert.Equal(30.0, plot.FollowWindowPercent, 9);

            plot.FollowWindowPercent = 999;
            Assert.Equal(100.0, plot.FollowWindowPercent, 9);
        });

        host.AssertNoUnhandledExceptions();
    }
}
