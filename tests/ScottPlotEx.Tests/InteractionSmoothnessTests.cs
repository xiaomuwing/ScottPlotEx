using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 鼠标操作瞬间不应该出现额外卡顿：
/// ScottPlot 默认在画布失去焦点时同步重绘整帧（<c>Refresh()</c>），
/// 而焦点变化往往正好发生在用户刚开始操作鼠标的那一刻（例如单击窗口把它激活），
/// 于是表现为“鼠标一操作就卡一下”。本控件把它改成合并重绘，同时保留终止拖动的语义。
/// </summary>
public class InteractionSmoothnessTests
{
    private const int DefaultPointCount = 20;

    [Fact(DisplayName = "焦点丢失时不同步重绘整帧，只请求合并重绘")]
    public void LostFocus_RequestsCoalescedRenderInsteadOfSynchronousRepaint()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        int renders = 0;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, DefaultPointCount);
            PlotTestHelper.EnsureRendered(plot);

            // 先把排队中的重绘全部执行掉：否则测量开始时残留的合并重绘会被消息循环执行，
            // 被误记成“焦点丢失引起的同步重绘”。
            plot.Refresh();
            Application.DoEvents();

            plot.Plot.RenderManager.RenderFinished += (_, _) => renders++;
            renders = 0;
        });

        // 模拟刚按下鼠标时发生的焦点变化。
        // 注意：断言必须与 ProcessLostFocus 在同一个消息循环回合内完成 ——
        // 一旦跨 host.Invoke 观测，排队中的合并重绘就会被消息循环执行，测到的是那次合并重绘而不是同步重绘。
        host.Invoke(() =>
        {
            plot.UserInputProcessor.ProcessLostFocus();

            Assert.Equal(0, renders); // 关键：不能同步重绘（同步整帧重绘就是那一下卡顿）
        });

        host.Pump(200);
        host.Invoke(() => Assert.True(renders > 0, "焦点丢失后仍应通过合并重绘把画面刷新一次。"));

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "焦点丢失仍然终止进行中的拖动（保持 ScottPlot 语义）")]
    public void LostFocus_StillEndsDragInProgress()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareCanvas(host, plot);

        ScottPlot.AxisLimits limitsAfterFocusLoss = default;

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point from = PlotTestHelper.PointInDataArea(rect, 0.70, 0.5);
            Point to = PlotTestHelper.PointInDataArea(rect, 0.30, 0.5);

            MouseInput.Down(canvas, from, MouseButtons.Left);
            MouseInput.Move(canvas, new Point((from.X + to.X) / 2, from.Y), MouseButtons.Left);
            Assert.True(plot.IsUserGestureInProgress, "按下并拖动后应处于手势进行中状态。");

            plot.UserInputProcessor.ProcessLostFocus();
            limitsAfterFocusLoss = plot.Plot.Axes.GetLimits();

            // 拖动已经被终止：继续移动鼠标不再平移视图
            for (int step = 1; step <= 4; step++)
                MouseInput.Move(canvas, new Point(to.X - (step * 10), to.Y), MouseButtons.Left);

            ScottPlot.AxisLimits afterMoreMoves = plot.Plot.Axes.GetLimits();
            Assert.Equal(limitsAfterFocusLoss.Left, afterMoreMoves.Left, 9);
            Assert.Equal(limitsAfterFocusLoss.Right, afterMoreMoves.Right, 9);

            MouseInput.Up(canvas, to, MouseButtons.Left);
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
