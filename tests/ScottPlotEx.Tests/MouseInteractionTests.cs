using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 需求 3、5、6、7：各类鼠标操作的完整验证。
/// 鼠标事件注入到 ScottPlot 画布控件上，走的是与真实鼠标完全相同的处理管线。
/// </summary>
public class MouseInteractionTests
{
    private const int DefaultPointCount = 10;

    [Fact(DisplayName = "需求6：左键拖动后仍能看到最新数据 -> 保持堆叠模式，X 轴最小值取当前最小值")]
    public void LeftDrag_WithLatestDataVisible_KeepsStackedMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);

            // 向左拖动 -> 视图向更新的数据移动，最新数据依然可见
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.60, 0.5),
                PlotTestHelper.PointInDataArea(rect, 0.20, 0.5),
                MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.XOffset > 0, $"X 轴偏移应为正值，实际为 {plot.XOffset}。");

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(plot.XOffset, xMin, 6);

            // 拖动本身不改变跨度（此时仍显示用户平移后的视图）
            Assert.Equal(plot.XOffset + DefaultPointCount + 2.0, xMax, 6);
        });

        // 继续每秒添加数据：保持堆叠模式，X 轴最小值保持为用户拖动后的最小值，
        // X 轴最大值恢复为“数据点数量 + 2”
        host.Invoke(() => plot.AddValue(2.5));
        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(plot.XOffset, xMin, 6);
            Assert.Equal(DefaultPointCount + 1 + 2.0, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求6：左键拖动后看不到最新数据 -> 曲线停止滚动")]
    public void LeftDrag_WithoutLatestData_StopsScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);

            // 向右拖动 -> 视图回到较早的数据，看不到最新数据
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.30, 0.5),
                PlotTestHelper.PointInDataArea(rect, 0.80, 0.5),
                MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
            Assert.True(plot.IsScrollingStopped);
            Assert.Equal(0.0, plot.XOffset, 9);
        });

        // 停止滚动后继续添加数据，视图保持不变
        ScottPlot.AxisLimits frozen = default;
        host.Invoke(() => frozen = plot.Plot.Axes.GetLimits());
        host.Invoke(() =>
        {
            for (int i = 0; i < 5; i++)
                plot.AddValue(-3);
        });
        host.Invoke(() =>
        {
            ScottPlot.AxisLimits now = plot.Plot.Axes.GetLimits();
            Assert.Equal(frozen.Left, now.Left, 9);
            Assert.Equal(frozen.Right, now.Right, 9);
            Assert.Equal(15, plot.DataCount);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求6：停止滚动后重新选择跟随模式 -> 恢复滚动")]
    public void AfterStopping_SelectingModeAgain_ResumesScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.30, 0.5),
                PlotTestHelper.PointInDataArea(rect, 0.85, 0.5),
                MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            Assert.True(plot.IsScrollingStopped);
            plot.Mode = ScrollingMode.Follow;
        });

        host.Invoke(() =>
        {
            Assert.False(plot.IsScrollingStopped);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal((DefaultPointCount * 0.7) + 2, xMin, 6);
            Assert.Equal(DefaultPointCount + 2.0, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求7：中键框选放大后仍能看到最新数据 -> 保持滚动")]
    public void MiddleDragZoom_WithLatestDataVisible_KeepsScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);

            // 框选靠近右侧的区域，最新数据仍在窗口内
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.15, 0.2),
                PlotTestHelper.PointInDataArea(rect, 0.98, 0.8),
                MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.XOffset > 0, $"X 轴偏移应为正值，实际为 {plot.XOffset}。");

            (double xMin, _) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(plot.XOffset, xMin, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求7：中键框选放大后看不到最新数据 -> 停止滚动")]
    public void MiddleDragZoom_WithoutLatestData_StopsScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);

            // 只框选左侧的早期数据
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.02, 0.2),
                PlotTestHelper.PointInDataArea(rect, 0.35, 0.8),
                MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
            Assert.True(plot.IsScrollingStopped);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求3：滚轮缩放后停止滚动")]
    public void WheelZoom_StopsScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), 120);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
            Assert.True(plot.IsScrollingStopped);
        });

        // 继续添加数据时视图保持不变
        ScottPlot.AxisLimits frozen = default;
        host.Invoke(() => frozen = plot.Plot.Axes.GetLimits());
        host.Invoke(() => plot.AddValue(42));
        host.Invoke(() =>
        {
            ScottPlot.AxisLimits now = plot.Plot.Axes.GetLimits();
            Assert.Equal(frozen.Left, now.Left, 9);
            Assert.Equal(frozen.Right, now.Right, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求3：滚轮缩小后同样停止滚动")]
    public void WheelZoomOut_AlsoStopsScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), -120);
        });

        host.Invoke(() => Assert.True(plot.IsScrollingStopped));
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求3：右键拖动缩放后停止滚动")]
    public void RightDragZoom_StopsScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.5, 0.8),
                PlotTestHelper.PointInDataArea(rect, 0.5, 0.2),
                MouseButtons.Right);
        });

        host.Invoke(() => Assert.True(plot.IsScrollingStopped));
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "中键单击自动缩放：保持模式并复位偏移，重新显示全部数据")]
    public void MiddleClickAutoscale_KeepsModeAndResetsOffset()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        // 先制造一个偏移（左键向左拖动）
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.6, 0.5),
                PlotTestHelper.PointInDataArea(rect, 0.2, 0.5),
                MouseButtons.Left);
        });

        host.Invoke(() => Assert.True(plot.XOffset > 0));

        // 中键单击 -> ScottPlot 自带的自动缩放
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(0.0, plot.XOffset, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.True(xMin <= 1.0 && xMax >= DefaultPointCount, $"自动缩放后应包含全部数据，实际 X = [{xMin}, {xMax}]。");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求4：右键单击弹出中文菜单，且不改变滚动模式")]
    public void RightClick_ShowsChineseContextMenu()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);
        int menuOpened = 0;

        host.Invoke(() => plot.PlotMenu.ContextMenuOpened += (_, _) => menuOpened++);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Right);
        });

        host.Pump(200);

        host.Invoke(() =>
        {
            Assert.Equal(1, menuOpened);
            Assert.NotNull(plot.PlotMenu.LastMenuShown);

            List<string> texts = [.. plot.PlotMenu.LastMenuShown!.Items
                .OfType<ToolStripMenuItem>()
                .Select(item => item.Text ?? string.Empty)];

            Assert.Equal(
                [
                    "保存图像", "复制到剪贴板", "自动缩放", "在新窗口中打开",
                    "堆叠模式", "跟随模式", "手动模式（停止滚动）",
                    "显示帮助",
                ],
                texts);

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            plot.PlotMenu.LastMenuShown!.Close();
        });

        host.Pump(100);
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "新增：跟随模式下中键单击（整体显示）→ 自动换成堆叠模式")]
    public void MiddleClickAutoscale_InFollowMode_SwitchesToStackedMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        ScrollingModeChangedEventArgs? lastChange = null;
        Control canvas = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 40);
            plot.Mode = ScrollingMode.Follow;
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        host.Invoke(() => plot.ModeChanged += (_, e) => lastChange = e);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);

            // 跟随模式：X 轴只显示最新 30%，最大值同样为 点数 + 2
            (double beforeMin, double beforeMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal((40 * 0.7) + 2, beforeMin, 6);
            Assert.Equal(40.0 + 2, beforeMax, 6);

            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(0.0, plot.XOffset, 9);

            // 切换后立即按堆叠模式显示全部数据
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(40 + 2, xMax, 6);

            Assert.NotNull(lastChange);
            Assert.Equal(ScrollingMode.Follow, lastChange!.PreviousMode);
            Assert.Equal(ScrollingMode.Stacked, lastChange.Mode);
            Assert.Equal(ScrollingModeChangeReason.Autoscale, lastChange.Reason);
        });

        // 之后继续按堆叠模式滚动
        host.Invoke(() => plot.AddValue(1.0));
        host.Invoke(() =>
        {
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(41 + 2, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "新增：跟随模式停止滚动后中键单击（整体显示）→ 退出停止状态并换成堆叠模式")]
    public void MiddleClickAutoscale_WhileStoppedInFollowMode_SwitchesToStackedMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        Control canvas = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            plot.Mode = ScrollingMode.Follow;
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        // 滚轮缩放 -> 停止滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), 120);
        });

        host.Invoke(() =>
        {
            Assert.True(plot.IsScrollingStopped);
            Assert.Equal(ScrollingMode.Follow, plot.LastAutoScrollMode);
        });

        // 中键单击 -> 整体显示，并恢复原来的跟随模式
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.False(plot.IsScrollingStopped);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(0.0, plot.XOffset, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(20 + 2, xMax, 6);
        });

        // 继续添加数据 -> 堆叠模式继续滚动
        host.Invoke(() => plot.AddValue(1.5));
        host.Invoke(() =>
        {
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(21 + 2, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "新增：拖动停止滚动后中键单击（整体显示）→ 恢复原来的堆叠模式并继续滚动")]
    public void MiddleClickAutoscale_AfterDragStopped_ResumesStackedMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);
        ScrollingModeChangedEventArgs? lastChange = null;

        host.Invoke(() => plot.ModeChanged += (_, e) => lastChange = e);

        // 向右拖动 -> 看不到最新数据 -> 停止滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.30, 0.5),
                PlotTestHelper.PointInDataArea(rect, 0.85, 0.5),
                MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
            Assert.Equal(ScrollingMode.Stacked, plot.LastAutoScrollMode);
        });

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(0.0, plot.XOffset, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.True(xMin <= 1.0 && xMax >= DefaultPointCount, $"整体显示后应包含全部数据，实际 X = [{xMin}, {xMax}]。");
        });

        // 恢复滚动后继续添加数据：X 轴最大值随点数增长
        host.Invoke(() => plot.AddValue(2.0));
        host.Invoke(() =>
        {
            (_, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(DefaultPointCount + 1 + 2, xMax, 6);
        });

        // 模式变化事件应说明原因是“整体显示”
        host.Invoke(() =>
        {
            Assert.NotNull(lastChange);
            Assert.Equal(ScrollingMode.Manual, lastChange!.PreviousMode);
            Assert.Equal(ScrollingMode.Stacked, lastChange.Mode);
            Assert.Equal(ScrollingModeChangeReason.Autoscale, lastChange.Reason);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "新增：菜单“自动缩放”（整体显示）同样会退出停止状态并换成堆叠模式")]
    public void MenuAutoscale_WhileStopped_ResumesPreviousMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 30);
            plot.Mode = ScrollingMode.Follow;
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), -120);
            Assert.True(plot.IsScrollingStopped);
        });

        host.Invoke(() =>
        {
            using ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot);
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "自动缩放").PerformClick();
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.False(plot.IsScrollingStopped);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(30 + 2, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "停止滚动后其它鼠标操作仍然不会恢复滚动")]
    public void OtherGestures_WhileStopped_DoNotResumeMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        // 先停止滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), 120);
        });

        host.Invoke(() => Assert.True(plot.IsScrollingStopped));

        // 拖动、滚轮、右键都不应恢复滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), PlotTestHelper.PointInDataArea(rect, 0.3, 0.5), MouseButtons.Left);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.4, 0.5), 120);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Right);
        });

        host.Pump(150);

        host.Invoke(() =>
        {
            plot.AddValue(9.9);
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
        });

        host.Invoke(() => plot.PlotMenu.LastMenuShown?.Close());
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "鼠标操作无障碍：左键单击不改变模式，也不弹出菜单")]
    public void LeftClick_DoesNothing()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);
        int menuOpened = 0;

        host.Invoke(() => plot.PlotMenu.ContextMenuOpened += (_, _) => menuOpened++);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Left);
        });

        host.Pump(100);

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(0, menuOpened);
            Assert.Equal(0.0, plot.XOffset, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求5：键盘方向键平移不影响既有鼠标功能，也不产生异常")]
    public void KeyboardPan_DoesNotThrow()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            MouseInput.KeyDown(canvas, Keys.Left);
            MouseInput.KeyUp(canvas, Keys.Left);
            MouseInput.KeyDown(canvas, Keys.Up);
            MouseInput.KeyUp(canvas, Keys.Up);
        });

        host.Invoke(() =>
        {
            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            Assert.True(limits.IsReal);
            Assert.True(double.IsFinite(limits.Left) && double.IsFinite(limits.Right));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求5：左键双击（ScottPlot 自带基准测试）不会导致崩溃")]
    public void DoubleClickBenchmark_DoesNotThrow()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point point = PlotTestHelper.PointInDataArea(rect, 0.5, 0.5);

            MouseInput.Click(canvas, point, MouseButtons.Left);
            MouseInput.Click(canvas, point, MouseButtons.Left);
            MouseInput.DoubleClick(canvas);
        });

        host.Pump(300);
        host.Invoke(() =>
        {
            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            Assert.True(limits.IsReal);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "健壮性：极端框选（高度为 0 的矩形）不会产生非法坐标或崩溃")]
    public void DegenerateZoomRectangle_DoesNotBreakAxes()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point from = PlotTestHelper.PointInDataArea(rect, 0.2, 0.5);
            Point to = PlotTestHelper.PointInDataArea(rect, 0.8, 0.5); // Y 完全相同
            MouseInput.Drag(canvas, from, to, MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            Assert.True(double.IsFinite(limits.Left) && double.IsFinite(limits.Right), "X 轴坐标非法。");
            Assert.True(double.IsFinite(limits.Bottom) && double.IsFinite(limits.Top), "Y 轴坐标非法。");
            Assert.NotEqual(limits.Bottom, limits.Top);
        });

        host.AssertNoUnhandledExceptions();
    }

    private static Control PrepareCanvas(WinFormsTestHost host, ScrollingPlotControl plot) =>
        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, DefaultPointCount);
            plot.Mode = ScrollingMode.Stacked;
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });
}
