using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 鼠标手势（按住并拖动）进行中时，新数据不得移动坐标轴：
/// 否则每来一个数据点，自动滚动都会把视图拉回自动窗口（表现为“鼠标一操作就卡一下”），
/// 甚至把用户的拖动结果覆盖掉。
/// </summary>
public class AutoScrollDuringGestureTests
{
    private const int DefaultPointCount = 20;

    [Fact(DisplayName = "鼠标拖动过程中到来的新数据不会把视图拉回自动窗口")]
    public void DataArrivingDuringDrag_DoesNotMoveAxes()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        ScottPlot.AxisLimits duringDrag = default;
        ScottPlot.AxisLimits afterData = default;

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point from = PlotTestHelper.PointInDataArea(rect, 0.70, 0.5);
            Point to = PlotTestHelper.PointInDataArea(rect, 0.30, 0.5);

            MouseInput.Down(canvas, from, MouseButtons.Left);

            for (int step = 1; step <= 6; step++)
            {
                int x = from.X + ((to.X - from.X) * step / 6);
                MouseInput.Move(canvas, new Point(x, from.Y), MouseButtons.Left);
            }

            Assert.True(plot.IsUserGestureInProgress, "按住左键拖动后应处于手势进行中状态。");

            duringDrag = plot.Plot.Axes.GetLimits();
            Assert.True(duringDrag.Left > 0, $"拖动后 X 轴最小值应为正值，实际为 {duringDrag.Left}。");

            // 拖动过程中照常采集数据
            plot.AddValue(1.5);
            afterData = plot.Plot.Axes.GetLimits();

            MouseInput.Up(canvas, to, MouseButtons.Left);
            Assert.False(plot.IsUserGestureInProgress, "松开左键后手势应结束。");
        });

        host.Invoke(() =>
        {
            Assert.Equal(duringDrag.Left, afterData.Left, 9);
            Assert.Equal(duringDrag.Right, afterData.Right, 9);
            Assert.Equal(DefaultPointCount + 1, plot.DataCount);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "拖动过程中到来的新数据不会覆盖拖动结果（松开后继续滚动）")]
    public void DataArrivingDuringDrag_DoesNotDiscardDragResult()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        double draggedXMin = 0;

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point start = PlotTestHelper.PointInDataArea(rect, 0.70, 0.5);
            Point end = PlotTestHelper.PointInDataArea(rect, 0.30, 0.5);

            MouseInput.Down(canvas, start, MouseButtons.Left);

            for (int step = 1; step <= 6; step++)
            {
                int x = start.X + ((end.X - start.X) * step / 6);
                MouseInput.Move(canvas, new Point(x, start.Y), MouseButtons.Left);
            }

            draggedXMin = PlotTestHelper.GetXRange(plot).Min;
            Assert.True(draggedXMin > 0, $"向左拖动后应停留在较晚的数据窗口上，实际 X 轴最小值为 {draggedXMin}。");

            // 拖动尚未结束就来了一条新数据：不能把视图拉回自动窗口
            plot.AddValue(2.5);
            Assert.Equal(draggedXMin, PlotTestHelper.GetXRange(plot).Min, 9);

            MouseInput.Up(canvas, end, MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            // 最新数据仍然可见 -> 保持堆叠模式，并把当前 X 轴最小值作为新的滚动基准
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(draggedXMin, plot.XOffset, 6);
            Assert.Equal(draggedXMin, PlotTestHelper.GetXRange(plot).Min, 6);
        });

        // 手势结束后，自动滚动恢复：X 轴最小值保持不变，最大值恢复为“数据点数量 + 2”
        host.Invoke(() => plot.AddValue(3.5));
        host.Invoke(() =>
        {
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(plot.XOffset, xMin, 6);
            Assert.Equal(DefaultPointCount + 2 + 2.0, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "仅按住鼠标但未拖动时，新数据仍会自动滚动")]
    public void HoldingButtonWithoutDrag_KeepsAutoScrolling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point point = PlotTestHelper.PointInDataArea(rect, 0.5, 0.5);

            MouseInput.Down(canvas, point, MouseButtons.Left);
            Assert.False(plot.IsUserGestureInProgress, "只按下鼠标（未拖动）不应进入手势状态。");

            plot.AddValue(4.5);

            // 未进入拖动状态：新数据照常触发自动滚动
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.True(Math.Abs(xMin) < 1e-6, $"X 轴最小值应为 0，实际为 {xMin}。");
            Assert.True(
                Math.Abs(xMax - (DefaultPointCount + 1 + 2.0)) < 1e-6,
                $"X 轴最大值应为 {DefaultPointCount + 3}，实际为 {xMax}。");

            MouseInput.Up(canvas, point, MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            // 抬起鼠标后恢复自动滚动：X 轴范围重新按“数据点数量 + 2”计算
            plot.AddValue(5.5);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.True(Math.Abs(xMin) < 1e-6, $"X 轴最小值应为 0，实际为 {xMin}。");
            Assert.True(
                Math.Abs(xMax - (DefaultPointCount + 2 + 2.0)) < 1e-6,
                $"X 轴最大值应为 {DefaultPointCount + 4}，实际为 {xMax}。");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "关闭“手势期间暂停自动滚动”选项后，拖动过程中新数据照常移动坐标轴")]
    public void PauseDuringGestureDisabled_DataDuringDragStillMovesAxes()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        host.Invoke(() =>
        {
            plot.AxisOptions.PauseAutoScrollDuringGesture = false;

            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point from = PlotTestHelper.PointInDataArea(rect, 0.70, 0.5);
            Point to = PlotTestHelper.PointInDataArea(rect, 0.30, 0.5);

            MouseInput.Down(canvas, from, MouseButtons.Left);

            for (int step = 1; step <= 6; step++)
            {
                int x = from.X + ((to.X - from.X) * step / 6);
                MouseInput.Move(canvas, new Point(x, from.Y), MouseButtons.Left);
            }

            Assert.True(plot.IsUserGestureInProgress, "按住左键拖动后应处于手势进行中状态。");
            Assert.True(PlotTestHelper.GetXRange(plot).Min > 0, "拖动后应停留在较晚的数据窗口上。");

            plot.AddValue(1.5);

            // 关闭该选项后保持旧行为：新数据照样把视图拉回自动窗口
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.True(Math.Abs(xMin) < 1e-6, $"X 轴最小值应为 0，实际为 {xMin}。");
            Assert.True(
                Math.Abs(xMax - (DefaultPointCount + 1 + 2.0)) < 1e-6,
                $"X 轴最大值应为 {DefaultPointCount + 3}，实际为 {xMax}。");

            MouseInput.Up(canvas, to, MouseButtons.Left);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "中键框选放大过程中到来的新数据不会移动坐标轴")]
    public void DataArrivingDuringMiddleDrag_DoesNotMoveAxes()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        ScottPlot.AxisLimits beforeData = default;
        ScottPlot.AxisLimits afterData = default;

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point from = PlotTestHelper.PointInDataArea(rect, 0.30, 0.30);
            Point to = PlotTestHelper.PointInDataArea(rect, 0.70, 0.70);

            MouseInput.Down(canvas, from, MouseButtons.Middle);

            for (int step = 1; step <= 6; step++)
            {
                int x = from.X + ((to.X - from.X) * step / 6);
                int y = from.Y + ((to.Y - from.Y) * step / 6);
                MouseInput.Move(canvas, new Point(x, y), MouseButtons.Middle);
            }

            Assert.True(plot.IsUserGestureInProgress, "中键框选过程中应处于手势进行中状态。");

            beforeData = plot.Plot.Axes.GetLimits();
            plot.AddValue(-2.5);
            afterData = plot.Plot.Axes.GetLimits();

            MouseInput.Up(canvas, to, MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.Equal(beforeData.Left, afterData.Left, 9);
            Assert.Equal(beforeData.Right, afterData.Right, 9);
            Assert.Equal(beforeData.Bottom, afterData.Bottom, 9);
            Assert.Equal(beforeData.Top, afterData.Top, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "拖动过程中绘制出的每一帧都停留在用户拖动的位置（不会闪回自动窗口）")]
    public void RenderedFramesDuringDrag_StayAtDraggedPosition()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);
        List<double> frameXMin = [];
        Point from = default;
        Point to = default;

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            plot.Plot.RenderManager.RenderFinished += (_, details) =>
                frameXMin.Add(Math.Min(details.AxisLimits.Left, details.AxisLimits.Right));

            from = PlotTestHelper.PointInDataArea(rect, 0.75, 0.5);
            to = PlotTestHelper.PointInDataArea(rect, 0.25, 0.5);
            MouseInput.Down(canvas, from, MouseButtons.Left);
        });

        for (int step = 1; step <= 12; step++)
        {
            int x = from.X + ((to.X - from.X) * step / 12);
            host.Invoke(() => MouseInput.Move(canvas, new Point(x, from.Y), MouseButtons.Left));

            // 每一步都来一条新数据：修复前这一帧会被自动滚动画到自动窗口上（用户看到的闪回）
            host.Invoke(() => plot.AddValue(Math.Sin(step) * 3));
            host.Pump(5);
        }

        host.Invoke(() => MouseInput.Up(canvas, to, MouseButtons.Left));

        host.Invoke(() =>
        {
            Assert.True(frameXMin.Count >= 6, $"拖动过程中应绘制多帧，实际只绘制 {frameXMin.Count} 帧。");

            for (int i = 1; i < frameXMin.Count; i++)
            {
                Assert.True(
                    frameXMin[i] >= frameXMin[i - 1] - 1e-6,
                    $"第 {i + 1} 帧闪回了自动窗口：X 轴最小值从 {frameXMin[i - 1]} 回到 {frameXMin[i]}。");
            }
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
