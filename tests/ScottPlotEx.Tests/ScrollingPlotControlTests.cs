using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>控件基本行为验证（在真实窗体与消息循环中运行）。</summary>
public class ScrollingPlotControlTests
{
    [Fact(DisplayName = "控件：内嵌 ScottPlot 并实现 IPlotControl 全部接口")]
    public void Control_ImplementsPlotControlInterface()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        Assert.IsAssignableFrom<ScottPlot.IPlotControl>(plot);

        host.Invoke(() =>
        {
            Assert.NotNull(plot.Plot);
            Assert.NotNull(plot.Canvas);
            Assert.NotNull(plot.UserInputProcessor);
            Assert.NotNull(plot.Multiplot);
            Assert.IsType<ChinesePlotMenu>(plot.Menu);

            // 画布本身也实现 IPlotControl，并与控件共用同一个 Plot / 输入处理器 / 菜单
            ScottPlot.IPlotControl canvas = Assert.IsAssignableFrom<ScottPlot.IPlotControl>(plot.Canvas);
            Assert.Same(plot.Plot, canvas.Plot);
            Assert.Same(plot.UserInputProcessor, canvas.UserInputProcessor);
            Assert.Same(plot.Menu, canvas.Menu);
            Assert.Same(plot.Canvas, plot.Plot.PlotControl);

            Assert.True(plot.DisplayScale > 0);
            Assert.Equal(plot.DisplayScale, plot.Plot.ScaleFactor, 6);
            Assert.Null(plot.GRContext); // 软件渲染
            Assert.NotNull(plot.Signal);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "空视图：新建与重建的控件在首个数据点之前就具备有效坐标轴")]
    public void NewControl_HasStableEmptyViewBeforeFirstDataPoint()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            Assert.Equal(0, plot.DataCount);

            // ScottPlot 的坐标轴在“未设置”状态下的 GetLimits() 是 (Left=+∞, Right=-∞)，
            // 控件必须在构造完成时就建立稳定空视图，否则新建 / 释放后重建的控件
            // 在首个数据点到达前会暴露非法的坐标轴范围（长时间压测曾复现）。
            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            ScrollingAxisOptions options = plot.AxisOptions;

            Assert.True(limits.IsReal, "新建控件的坐标轴必须是实数范围。");
            Assert.True(
                double.IsFinite(limits.Left) && double.IsFinite(limits.Right)
                && double.IsFinite(limits.Bottom) && double.IsFinite(limits.Top),
                $"新建控件的坐标轴必须是有限值，实际为 X: [{limits.Left}, {limits.Right}]、Y: [{limits.Bottom}, {limits.Top}]。");

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);

            Assert.Equal(options.EmptyXMin, xMin, 6);
            Assert.Equal(options.EmptyXMax, xMax, 6);
            Assert.Equal(options.EmptyYMin, yMin, 6);
            Assert.Equal(options.EmptyYMax, yMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "默认堆叠模式：X 轴 0 ~ 点数+2，Y 轴上下各留 5%")]
    public void StackedMode_IsDefaultAndFitsRequirements()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            for (int i = 0; i < 10; i++)
                plot.AddValue(i);
        });

        host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(12.0, xMax, 6); // 10 个点 + 2

            // 数据 0~9，跨度 9，上下各留 5% = 0.45
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(-0.45, yMin, 6);
            Assert.Equal(9.45, yMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求2：跟随模式 X 轴为最新 30% 数据，Y 轴按当前显示数据计算")]
    public void FollowMode_UsesTrailingDataAndVisibleYRange()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            for (int i = 1; i <= 100; i++)
                plot.AddValue(i);

            plot.Mode = ScrollingMode.Follow;
        });

        host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(72.0, xMin, 6);
            Assert.Equal(102.0, xMax, 6); // 100 个点 + 2

            // 可见范围内是第 72~100 个点（值 72~100），跨度 28，上下各留 5% = 1.4
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(70.6, yMin, 6);
            Assert.Equal(101.4, yMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求3：手动模式下继续添加数据不会滚动视图")]
    public void ManualMode_DoesNotScrollWhenDataIsAdded()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        ScottPlot.AxisLimits frozen = default;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 30);
            plot.Mode = ScrollingMode.Manual;
            PlotTestHelper.EnsureRendered(plot);
            frozen = plot.Plot.Axes.GetLimits();
        });

        host.Invoke(() =>
        {
            for (int i = 0; i < 20; i++)
                plot.AddValue(i % 7);
        });

        host.Invoke(() =>
        {
            ScottPlot.AxisLimits now = plot.Plot.Axes.GetLimits();

            Assert.Equal(frozen.Left, now.Left, 9);
            Assert.Equal(frozen.Right, now.Right, 9);
            Assert.Equal(frozen.Bottom, now.Bottom, 9);
            Assert.Equal(frozen.Top, now.Top, 9);
            Assert.True(plot.IsScrollingStopped);
            Assert.Equal(50, plot.DataCount);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求3：重新选择堆叠/跟随模式后恢复滚动")]
    public void SelectingModeAgain_ResumesScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            plot.Mode = ScrollingMode.Manual;
            plot.AddValue(5);
            Assert.True(plot.IsScrollingStopped);

            // 重新选择跟随模式
            plot.Mode = ScrollingMode.Follow;
        });

        host.Invoke(() =>
        {
            Assert.False(plot.IsScrollingStopped);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal((21 * 0.7) + 2, xMin, 6);
            Assert.Equal(21.0 + 2, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "清空数据后回到空视图，且不会出现非法坐标")]
    public void ClearData_ResetsToEmptyView()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 10);
            plot.ClearData();
        });

        host.Invoke(() =>
        {
            Assert.Equal(0, plot.DataCount);
            Assert.True(double.IsNaN(plot.LatestValue));

            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            Assert.True(limits.IsReal);
            Assert.Equal(plot.AxisOptions.EmptyXMin, limits.Left, 9);
            Assert.Equal(plot.AxisOptions.EmptyXMax, limits.Right, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "兼容性：完整保留 ScottPlot 自带的鼠标/键盘交互响应")]
    public void UserInputProcessor_KeepsScottPlotDefaults()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            string[] expected = [.. ScottPlot.Interactivity.UserInputProcessor.DefaultUserResponses()
                .Select(response => response.GetType().Name)];
            string[] actual = [.. plot.UserInputProcessor.UserActionResponses
                .Select(response => response.GetType().Name)];

            Assert.Equal(expected, actual);
            Assert.True(plot.UserInputProcessor.IsEnabled);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "线程安全：从后台线程添加数据不会崩溃")]
    public async Task AddValue_FromBackgroundThread_IsSafe()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        Task task = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
                plot.AddValue(Math.Sin(i));
        });

        await task.WaitAsync(TimeSpan.FromSeconds(15));

        host.Pump(300);
        host.Invoke(() => Assert.Equal(50, plot.DataCount));
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "健壮性：忽略 NaN 与 Infinity，不产生非法坐标")]
    public void NonFiniteValues_AreIgnored()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValue(double.NaN);
            plot.AddValue(double.PositiveInfinity);
            plot.AddValue(double.NegativeInfinity);
            plot.AddValue(3.5);
        });

        host.Invoke(() =>
        {
            Assert.Equal(1, plot.DataCount);
            Assert.Equal(3, plot.SkippedValueCount);

            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            Assert.True(limits.IsReal);
            Assert.True(double.IsFinite(limits.Left) && double.IsFinite(limits.Right));
        });

        host.AssertNoUnhandledExceptions();
    }
}
