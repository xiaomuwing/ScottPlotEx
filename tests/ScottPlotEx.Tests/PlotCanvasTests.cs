using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScottPlot;
using ScottPlot.WinForms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace ScottPlotEx.Tests;

/// <summary>
/// 自绘画布（SkiaSharp → GDI DIB → BitBlt）：渲染结果、高 DPI 下的布局与命中、输入转发、GDI 资源释放。
/// </summary>
public class PlotCanvasTests
{
    [Fact(DisplayName = "画布：BitBlt 到窗口的画面与 ScottPlot 离屏渲染逐像素一致")]
    public void Canvas_Output_MatchesOffscreenRender()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(900, 560));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 2000);
            PlotTestHelper.EnsureRendered(plot);

            Size size = plot.Canvas.ClientSize;
            using Bitmap onScreen = CaptureCanvas(plot);
            using Bitmap expected = RenderOffscreen(plot, size);

            // 参照图与画布使用同一种像素格式（BGRA、预乘 Alpha）与同一个渲染入口（Multiplot.Render），
            // 因此任何差异都只可能来自 DIB → BitBlt 的拷贝环节（通道顺序、偏移、缩放等）
            (int differing, int total) = CountDifferingPixels(onScreen, expected, tolerance: 0);
            Assert.True(differing == 0, $"画布画面与离屏渲染有 {differing} / {total} 个像素不同（像素格式或拷贝方式有误）。");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Theory(DisplayName = "高 DPI：布局按物理像素缩放，拖动平移 / 滚轮缩放 / 右键命中都按物理像素换算")]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    public void HighDpi_LayoutAndHitTesting_UsePhysicalPixels(float scale)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(1000, 620));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() => plot.Canvas);

        PixelRect physical = host.Invoke(() =>
        {
            plot.DisplayScale = scale;
            Assert.Equal(scale, plot.Plot.ScaleFactor, 6);
            Assert.Equal(scale, ((IPlotControl)canvas).DisplayScale, 6);

            PlotTestHelper.AddWave(plot, 200);

            // 宿主启动时已按 1 倍渲染过一帧；先同步渲染一次，让 LastRender 反映新的缩放比例
            plot.Refresh();
            PixelRect logical = PlotTestHelper.EnsureRendered(plot);
            PixelRect rect = Scale(logical, scale);

            // 布局按物理像素放大后仍然放得下，且没有被错误地缩成逻辑尺寸
            Assert.True(rect.Right <= canvas.ClientSize.Width + 1, $"数据区右边界 {rect.Right} 超出画布宽度 {canvas.ClientSize.Width}。");
            Assert.True(rect.Bottom <= canvas.ClientSize.Height + 1, $"数据区下边界 {rect.Bottom} 超出画布高度 {canvas.ClientSize.Height}。");
            Assert.True(rect.Width > canvas.ClientSize.Width * 0.6, $"数据区宽度 {rect.Width} 过小，布局可能没有按物理像素绘制。");

            // 像素 ↔ 坐标换算使用物理像素
            Coordinates probe = new(100, 0);
            Pixel pixel = plot.Plot.GetPixel(probe);
            Assert.InRange(pixel.X, rect.Left, rect.Right);
            Assert.InRange(pixel.Y, rect.Top, rect.Bottom);
            Assert.Equal(probe.X, plot.GetCoordinates(pixel).X, 3);

            // 实际画面：数据区底边内侧是白色绘图背景，外侧不是（若按逻辑尺寸绘制，内侧会落在数据区之外）
            using Bitmap image = CaptureCanvas(plot);
            int centerX = (int)((rect.Left + rect.Right) / 2);
            Assert.True(IsWhite(image.GetPixel(centerX, (int)rect.Bottom - 3)), "数据区底边内侧应为白色绘图背景。");
            Assert.False(IsWhite(image.GetPixel(centerX, (int)rect.Bottom + 4)), "数据区底边外侧不应为白色绘图背景。");

            // 连续多帧画面保持不变：ScottPlot 每次渲染都会对画布执行 Scale(ScaleFactor)，画布必须逐帧恢复变换矩阵
            plot.Refresh();
            plot.Refresh();
            using Bitmap later = CaptureCanvas(plot);
            Assert.Equal(0, CountDifferingPixels(image, later, tolerance: 0).Differing);
            Assert.Equal(logical.Width, plot.Plot.RenderManager.LastRender.DataRect.Width, 3);

            return rect;
        });

        // 拖动平移：向左拖 120 个物理像素，视图右移 120 × (X 跨度 / 物理数据区宽度)
        host.Invoke(() =>
        {
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            double unitsPerPixel = (xMax - xMin) / physical.Width;
            Point from = PointIn(physical, 0.6, 0.5);
            Point to = new(from.X - 120, from.Y);

            MouseInput.Drag(canvas, from, to, MouseButtons.Left);

            (double newMin, _) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(120 * unitsPerPixel, newMin - xMin, (120 * unitsPerPixel) * 0.03);
        });

        // 滚轮缩放：以鼠标所在位置为中心，鼠标下的坐标保持不变
        host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            Point at = PointIn(physical, 0.3, 0.4);
            Coordinates before = plot.GetCoordinates(new Pixel(at.X, at.Y));

            MouseInput.Wheel(canvas, at, 120);
            PlotTestHelper.EnsureRendered(plot);

            Coordinates after = plot.GetCoordinates(new Pixel(at.X, at.Y));
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(before.X, after.X, (xMax - xMin) * 0.01);
            Assert.True(plot.IsScrollingStopped, "滚轮缩放后应停止滚动。");
        });

        // 右键单击：按物理像素命中绘图区并弹出中文菜单
        host.Invoke(() =>
        {
            Point at = PointIn(physical, 0.9, 0.9);
            Assert.NotNull(plot.GetPlotAtPixel(new Pixel(at.X, at.Y)));

            MouseInput.Click(canvas, at, MouseButtons.Right);
        });

        host.Pump(150);

        host.Invoke(() =>
        {
            ContextMenuStrip? menu = plot.PlotMenu.LastMenuShown;
            Assert.NotNull(menu);
            Assert.Equal("保存图像", menu!.Items[0].Text);
            menu.Close();
        });

        host.Pump(50);
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "DisplayScale：同步设置 Plot.ScaleFactor，Reset 后对新 Plot 仍然生效，非法值回落为 1")]
    public void DisplayScale_AppliesToPlot_AndSurvivesReset()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            Assert.Equal(plot.DetectDisplayScale(), plot.DisplayScale, 6);

            plot.DisplayScale = 1.5f;
            Assert.Equal(1.5, plot.Plot.ScaleFactor, 6);

            plot.Reset();
            Assert.Equal(1.5, plot.Plot.ScaleFactor, 6);

            Plot replacement = new();
            plot.Reset(replacement);
            Assert.Same(replacement, plot.Plot);
            Assert.Equal(1.5, replacement.ScaleFactor, 6);

            // 传入当前 Plot 时不能把它释放掉
            plot.Reset(plot.Plot, disposeOldPlot: true);
            plot.AddValue(1);
            plot.Refresh();

            plot.DisplayScale = 0;
            Assert.Equal(1f, plot.DisplayScale);
            plot.DisplayScale = float.NaN;
            Assert.Equal(1f, plot.DisplayScale);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "拖动平移：缩放比例为 1 时保留 ScottPlot 原生响应，≠ 1 时换成距离正确的版本且保留设置")]
    public void DragPan_IsReplacedOnlyWhenScaled()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() => plot.Canvas);

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 50);
            PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point at = PlotTestHelper.PointInDataArea(rect, 0.5, 0.5);
            List<ScottPlot.Interactivity.IUserActionResponse> responses = plot.UserInputProcessor.UserActionResponses;

            // 1 倍：保持 ScottPlot 原样
            MouseInput.Click(canvas, at, MouseButtons.Left);
            ScottPlot.Interactivity.UserActionResponses.MouseDragPan stock =
                Assert.Single(responses.OfType<ScottPlot.Interactivity.UserActionResponses.MouseDragPan>());
            Assert.Empty(responses.OfType<DpiAwareMouseDragPan>());

            // ≠ 1 倍：下一次按下鼠标前替换，并保留原有设置
            stock.LockY = true;
            plot.DisplayScale = 1.5f;
            plot.Refresh();
            MouseInput.Click(canvas, at, MouseButtons.Left);

            DpiAwareMouseDragPan replaced = Assert.Single(responses.OfType<DpiAwareMouseDragPan>());
            Assert.True(replaced.LockY);
            Assert.Equal(stock.MouseButton, replaced.MouseButton);
            Assert.Empty(responses.OfType<ScottPlot.Interactivity.UserActionResponses.MouseDragPan>());

            // 输入处理器复位后重新加入的默认响应同样会被替换
            plot.UserInputProcessor.Reset();
            MouseInput.Click(canvas, at, MouseButtons.Left);
            Assert.Single(responses.OfType<DpiAwareMouseDragPan>());
            Assert.Empty(responses.OfType<ScottPlot.Interactivity.UserActionResponses.MouseDragPan>());
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "输入转发：方向键在 PreviewKeyDown 中交给 ScottPlot（与 FormsPlot 一致），双击事件照常触发")]
    public void Keyboard_And_DoubleClick_AreForwarded()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() => plot.Canvas);
        int doubleClicks = 0;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 100);
            PlotTestHelper.EnsureRendered(plot);
            canvas.DoubleClick += (_, _) => doubleClicks++;

            (double xMin, _) = PlotTestHelper.GetXRange(plot);

            // 方向键默认不会进入 KeyDown，ScottPlot 依赖 PreviewKeyDown 处理键盘平移
            InvokeProtected(canvas, "OnPreviewKeyDown", new PreviewKeyDownEventArgs(Keys.Right));
            InvokeProtected(canvas, "OnKeyUp", new KeyEventArgs(Keys.Right));

            (double newMin, _) = PlotTestHelper.GetXRange(plot);
            Assert.NotEqual(xMin, newMin);

            MouseInput.DoubleClick(canvas);
            Assert.Equal(1, doubleClicks);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "单遍渲染：坐标轴滚动时每次 Refresh 只渲染一遍（关闭后为 ScottPlot 原本的两遍）")]
    public void ScrollingFrame_RendersOnce()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        PlotCanvas canvas = (PlotCanvas)host.Invoke(() => plot.Canvas);

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 500);
            PlotTestHelper.EnsureRendered(plot);

            int RendersPerScrollingFrame()
            {
                int before = plot.Plot.RenderManager.RenderCount;

                for (int i = 0; i < 10; i++)
                {
                    plot.AddValue(i); // 堆叠模式：每个点都让 X 轴右端前移
                    plot.Refresh();
                }

                return plot.Plot.RenderManager.RenderCount - before;
            }

            Assert.True(canvas.SinglePassRendering);
            Assert.Equal(10, RendersPerScrollingFrame());
            Assert.True(plot.Plot.RenderManager.EnableRendering, "渲染结束后必须恢复 EnableRendering。");

            // 护栏：关闭后回到 ScottPlot 原本的行为（坐标轴变化后再渲染一遍）。
            // 若升级 ScottPlot 后这里变成 10，说明上游已不再重复渲染，单遍渲染可以去掉。
            canvas.SinglePassRendering = false;
            Assert.Equal(20, RendersPerScrollingFrame());
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "单遍渲染：AxisLimitsChanged 处理器改动坐标轴后，画面仍反映最终范围")]
    public void AxisLimitsChangedHandler_ChangesAreRendered()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        bool clampOnce = false;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 200);
            PlotTestHelper.EnsureRendered(plot);

            // 模拟联动坐标轴 / 用户代码：在坐标轴变化事件里改动 Y 轴
            plot.Plot.RenderManager.AxisLimitsChanged += (_, _) =>
            {
                if (!clampOnce)
                    return;

                clampOnce = false;
                plot.Plot.Axes.SetLimitsY(-100, 100);
            };

            clampOnce = true;
            plot.AddValue(1);
            plot.Refresh();

            // 画出最后一帧所用的范围必须是处理器改动后的范围
            ScottPlot.CoordinateRange rendered = plot.Plot.RenderManager.LastRender.AxisLimitsByAxis[plot.Plot.Axes.Left];
            Assert.Equal(-100, rendered.Min, 9);
            Assert.Equal(100, rendered.Max, 9);
            Assert.True(plot.Plot.RenderManager.EnableRendering);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "单遍渲染：用户自行关闭 EnableRendering 时不渲染，且保持关闭")]
    public void UserDisabledRendering_IsRespected()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 50);
            PlotTestHelper.EnsureRendered(plot);

            plot.Plot.RenderManager.EnableRendering = false;
            int before = plot.Plot.RenderManager.RenderCount;

            plot.AddValue(1);
            plot.Refresh();

            Assert.Equal(before, plot.Plot.RenderManager.RenderCount);
            Assert.False(plot.Plot.RenderManager.EnableRendering);

            plot.Plot.RenderManager.EnableRendering = true;
            plot.Refresh();
            Assert.Equal(before + 1, plot.Plot.RenderManager.RenderCount);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "画布：反复改变尺寸时 DIB 随之重建，GDI 对象不泄漏")]
    public void Resizing_DoesNotLeakGdiObjects()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(800, 500));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        int before = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 500);
            PlotTestHelper.EnsureRendered(plot);
            return GdiObjectCount();
        });

        for (int i = 0; i < 30; i++)
        {
            Size size = new(600 + ((i % 6) * 97), 400 + ((i % 5) * 61));
            host.Invoke(() =>
            {
                host.Form.ClientSize = size;
                plot.Refresh();
            });
        }

        int after = host.Invoke(() =>
        {
            plot.Refresh();
            return GdiObjectCount();
        });

        Assert.True(after - before <= 10, $"30 次尺寸变化后 GDI 对象从 {before} 增加到 {after}，DIB / 内存 DC 可能没有释放。");
        host.AssertNoUnhandledExceptions();
    }

    /// <summary>经 WM_PRINT 让画布把当前画面绘制到位图（走与屏幕相同的 OnPaint → BitBlt 路径）。</summary>
    private static Bitmap CaptureCanvas(ScrollingPlotControl plot)
    {
        Control canvas = plot.Canvas;
        Bitmap bitmap = new(canvas.ClientSize.Width, canvas.ClientSize.Height, PixelFormat.Format32bppArgb);
        canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, canvas.ClientSize));
        return bitmap;
    }

    /// <summary>用与画布相同的像素格式和渲染入口离屏渲染一帧，作为逐像素比较的参照。</summary>
    private static Bitmap RenderOffscreen(ScrollingPlotControl plot, Size size)
    {
        SkiaSharp.SKImageInfo info = new(size.Width, size.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using SkiaSharp.SKSurface surface = SkiaSharp.SKSurface.Create(info);
        plot.Multiplot.Render(surface);

        Bitmap bitmap = new(size.Width, size.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            // GDI+ 的 32bppArgb 内存布局同样是 BGRA；背景不透明，预乘与否不影响比较
            surface.ReadPixels(info, data.Scan0, data.Stride, 0, 0);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static (int Differing, int Total) CountDifferingPixels(Bitmap actual, Bitmap expected, int tolerance)
    {
        Assert.Equal(expected.Size, actual.Size);

        Rectangle area = new(Point.Empty, actual.Size);
        BitmapData a = actual.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData b = expected.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            int differing = 0;
            byte[] rowA = new byte[area.Width * 4];
            byte[] rowB = new byte[area.Width * 4];

            for (int y = 0; y < area.Height; y++)
            {
                Marshal.Copy(a.Scan0 + (y * a.Stride), rowA, 0, rowA.Length);
                Marshal.Copy(b.Scan0 + (y * b.Stride), rowB, 0, rowB.Length);

                for (int x = 0; x < rowA.Length; x += 4)
                {
                    // 只比较 BGR：屏幕拷贝不保留 Alpha
                    if (Math.Abs(rowA[x] - rowB[x]) > tolerance
                        || Math.Abs(rowA[x + 1] - rowB[x + 1]) > tolerance
                        || Math.Abs(rowA[x + 2] - rowB[x + 2]) > tolerance)
                    {
                        differing++;
                    }
                }
            }

            return (differing, area.Width * area.Height);
        }
        finally
        {
            actual.UnlockBits(a);
            expected.UnlockBits(b);
        }
    }

    private static bool IsWhite(System.Drawing.Color color) => color.R > 245 && color.G > 245 && color.B > 245;

    private static PixelRect Scale(PixelRect rect, float scale) =>
        new(rect.Left * scale, rect.Right * scale, rect.Bottom * scale, rect.Top * scale);

    private static Point PointIn(PixelRect rect, double fractionX, double fractionY) =>
        new((int)Math.Round(rect.Left + (rect.Width * fractionX)), (int)Math.Round(rect.Top + (rect.Height * fractionY)));

    private static void InvokeProtected(Control control, string methodName, EventArgs args)
    {
        MethodInfo method = control.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(control.GetType().FullName, methodName);
        method.Invoke(control, [args]);
    }

    private static int GdiObjectCount() => (int)GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
