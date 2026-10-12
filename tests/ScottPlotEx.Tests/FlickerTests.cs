using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 闪烁防护验证：快速刷新时只重绘画布，不重绘父控件背景（大面积背景重绘是闪烁的主要来源）。
/// </summary>
public class FlickerTests
{
    [Fact(DisplayName = "快速刷新只重绘画布：父控件背景不再重绘（无大面积背景闪烁）")]
    public void FastRefresh_RepaintsCanvasOnly()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(1400, 900));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() => PlotTestHelper.GetInputCanvas(plot));
        int canvasPaints = 0;
        int controlPaints = 0;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 500);
            PlotTestHelper.EnsureRendered(plot);

            // 画布是控件的直接子控件：父控件（控件自身）不应被重绘
            Assert.Same(plot, canvas.Parent);

            canvas.Paint += (_, _) => canvasPaints++;
            plot.Paint += (_, _) => controlPaints++;

            canvasPaints = 0;
            controlPaints = 0;

            // 快速刷新：连续添加数据点，让消息循环不断绘制
            for (int i = 0; i < 40; i++)
            {
                plot.AddValue(i % 50);
                host.Pump(5);
            }
        });

        host.Invoke(() =>
        {
            Assert.True(canvasPaints > 0, "画布应当被重绘。");
            Assert.Equal(0, controlPaints); // 关键：父控件背景不再重绘
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "闪烁防护：画布已设为 Opaque（跳过背景擦除）")]
    public void Canvas_IsOpaque_SoBackgroundIsNotErased()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(1400, 900));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() => PlotTestHelper.GetInputCanvas(plot));

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 500);
            PlotTestHelper.EnsureRendered(plot);

            Assert.True(HasStyle(canvas, ControlStyles.Opaque));
            Assert.True(HasStyle(canvas, ControlStyles.AllPaintingInWmPaint));
            Assert.True(HasStyle(canvas, ControlStyles.UserPaint));

            // DIB 本身就是后台缓冲：画布不再叠加 WinForms 双缓冲（否则每帧多一次整幅拷贝）
            Assert.False(HasStyle(canvas, ControlStyles.OptimizedDoubleBuffer));
        });

        host.AssertNoUnhandledExceptions();
    }

    /// <summary>读取控件上的 <see cref="ControlStyles"/> 标志（GetStyle 是受保护成员）。</summary>
    private static bool HasStyle(Control control, ControlStyles style)
    {
        object? result = typeof(Control)
            .GetMethod("GetStyle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(control, [style]);

        return result is true;
    }

    [Fact(DisplayName = "同步 Refresh() 同样只重绘画布")]
    public void SynchronousRefresh_RepaintsCanvasOnly()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(1400, 900));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() => PlotTestHelper.GetInputCanvas(plot));
        int canvasPaints = 0;
        int controlPaints = 0;

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 500);
            PlotTestHelper.EnsureRendered(plot);

            canvas.Paint += (_, _) => canvasPaints++;
            plot.Paint += (_, _) => controlPaints++;

            canvasPaints = 0;
            controlPaints = 0;

            for (int i = 0; i < 20; i++)
            {
                plot.AddValue(i);
                plot.Refresh(); // 同步刷新（最保守用法）
            }
        });

        host.Invoke(() =>
        {
            Assert.True(canvasPaints > 0, "画布应当被同步重绘。");
            Assert.Equal(0, controlPaints);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "无闪烁渲染辅助：可为窗体启用 / 关闭 WS_EX_COMPOSITED")]
    public void FlickerFreeRendering_TogglesCompositedStyle()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(900, 600));
        Form form = host.Form;

        host.Invoke(() =>
        {
            Assert.False(FlickerFreeRendering.IsComposited(form));

            FlickerFreeRendering.EnableComposited(form);
            Assert.True(FlickerFreeRendering.IsComposited(form));

            FlickerFreeRendering.DisableComposited(form);
            Assert.False(FlickerFreeRendering.IsComposited(form));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "无闪烁渲染辅助：可按控件尺寸调大 WinForms 双缓冲上限")]
    public void FlickerFreeRendering_RaisesMaximumBuffer()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(1600, 1000));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            Size original = FlickerFreeRendering.GetMaximumBufferSize();

            try
            {
                FlickerFreeRendering.EnsureMaximumBufferFor(plot);

                Size updated = FlickerFreeRendering.GetMaximumBufferSize();
                Assert.True(updated.Width >= plot.Width && updated.Height >= plot.Height, "双缓冲上限应当覆盖控件尺寸。");

                // 小于当前上限时不再调整
                FlickerFreeRendering.EnsureMaximumBufferFor(plot);
                Assert.Equal(updated, FlickerFreeRendering.GetMaximumBufferSize());
            }
            finally
            {
                FlickerFreeRendering.SetMaximumBufferSize(original);
            }
        });

        host.AssertNoUnhandledExceptions();
    }
}
