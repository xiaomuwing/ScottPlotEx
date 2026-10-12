using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 用真实的 Windows 消息（PostMessage 到画布窗口）驱动控件，
/// 验证鼠标行为不仅在被托管的 OnMouseDown 调用下成立，在窗口过程（WndProc）路径下同样成立。
/// </summary>
public class RealMessageInputTests
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEWHEEL = 0x020A;

    private const int MK_LBUTTON = 0x0001;
    private const int MK_RBUTTON = 0x0002;
    private const int MK_MBUTTON = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    [Fact(DisplayName = "真实窗口消息：左键拖动平移生效")]
    public void LeftDrag_ThroughRealMessages_Pans()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        IntPtr canvas = IntPtr.Zero;
        Point from = default;
        Point to = default;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            canvas = PlotTestHelper.GetInputCanvas(plot).Handle;
            from = PlotTestHelper.PointInDataArea(rect, 0.6, 0.5);
            to = PlotTestHelper.PointInDataArea(rect, 0.3, 0.5);
        });

        host.Invoke(() =>
        {
            _ = PostMessage(canvas, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(from.X, from.Y));
            _ = PostMessage(canvas, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, MakeLParam(from.X, from.Y));

            for (int step = 1; step <= 6; step++)
            {
                int x = from.X + ((to.X - from.X) * step / 6);
                _ = PostMessage(canvas, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, MakeLParam(x, from.Y));
            }

            _ = PostMessage(canvas, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(to.X, from.Y));
        });

        host.Pump(200);

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.XOffset > 0, $"真实消息拖动后 X 轴偏移应为正值，实际为 {plot.XOffset}。");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "真实窗口消息：右键单击弹出中文菜单")]
    public void RightClick_ThroughRealMessages_OpensMenu()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        IntPtr canvas = IntPtr.Zero;
        Point point = default;
        int menuOpened = 0;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            canvas = PlotTestHelper.GetInputCanvas(plot).Handle;
            point = PlotTestHelper.PointInDataArea(rect, 0.5, 0.5);
            plot.PlotMenu.ContextMenuOpened += (_, _) => menuOpened++;
        });

        host.Invoke(() =>
        {
            _ = PostMessage(canvas, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(point.X, point.Y));
            _ = PostMessage(canvas, WM_RBUTTONDOWN, (IntPtr)MK_RBUTTON, MakeLParam(point.X, point.Y));
            _ = PostMessage(canvas, WM_RBUTTONUP, IntPtr.Zero, MakeLParam(point.X, point.Y));
        });

        host.Pump(300);

        host.Invoke(() =>
        {
            Assert.Equal(1, menuOpened);
            Assert.NotNull(plot.PlotMenu.LastMenuShown);
            plot.PlotMenu.LastMenuShown!.Close();
        });

        host.Pump(100);
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "真实窗口消息：中键单击整体显示，跟随模式自动换成堆叠模式")]
    public void MiddleClick_ThroughRealMessages_SwitchesFollowToStacked()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        IntPtr canvas = IntPtr.Zero;
        Point point = default;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            plot.Mode = ScrollingMode.Follow;
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            canvas = PlotTestHelper.GetInputCanvas(plot).Handle;
            point = PlotTestHelper.PointInDataArea(rect, 0.5, 0.5);
        });

        // 滚轮缩放 -> 停止滚动
        host.Invoke(() =>
        {
            IntPtr wheelLParam = (IntPtr)(((point.Y) << 16) | (point.X & 0xFFFF));
            _ = PostMessage(canvas, WM_MOUSEWHEEL, (IntPtr)(120 << 16), wheelLParam);
        });

        host.Pump(200);
        host.Invoke(() => Assert.True(plot.IsScrollingStopped));

        // 中键单击 -> 整体显示并恢复原来的跟随模式
        host.Invoke(() =>
        {
            _ = PostMessage(canvas, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(point.X, point.Y));
            _ = PostMessage(canvas, WM_MBUTTONDOWN, (IntPtr)MK_MBUTTON, MakeLParam(point.X, point.Y));
            _ = PostMessage(canvas, WM_MBUTTONUP, IntPtr.Zero, MakeLParam(point.X, point.Y));
        });

        host.Pump(200);

        host.Invoke(() =>
        {
            Assert.False(plot.IsScrollingStopped);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(20 + 2, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }
}
