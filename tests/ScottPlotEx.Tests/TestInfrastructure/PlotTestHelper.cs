using System.Drawing;
using System.Windows.Forms;
using ScottPlot;
using ScottPlotEx.Controls;

namespace ScottPlotEx.Tests.TestInfrastructure;

/// <summary>
/// 绘图区相关的测试辅助方法。
/// </summary>
internal static class PlotTestHelper
{
    /// <summary>
    /// 确保绘图区至少渲染过一次（ScottPlot 的鼠标命中测试依赖最近一次渲染的绘图区矩形），
    /// 并返回绘图区矩形。
    /// </summary>
    public static ScottPlot.PixelRect EnsureRendered(ScrollingPlotControl plot)
    {
        ScottPlot.PixelRect rect = default;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            rect = plot.Plot.RenderManager.LastRender.DataRect;

            if (rect.Width > 0 && rect.Height > 0)
            {
                ScottPlot.Pixel probe = new(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

                if (plot.GetPlotAtPixel(probe) is not null)
                    return rect;
            }

            plot.Refresh();
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert.Fail($"绘图区尚未完成渲染，无法进行鼠标命中测试。DataRect = ({rect.Left}, {rect.Right}, {rect.Top}, {rect.Bottom})。");
        return rect;
    }

    /// <summary>取绘图区内一个安全的位置（按比例定位，避免落在坐标轴边距上）。</summary>
    public static Point PointInDataArea(ScottPlot.PixelRect rect, double fractionX, double fractionY)
    {
        double left = Math.Min(rect.Left, rect.Right);
        double top = Math.Min(rect.Top, rect.Bottom);
        double width = Math.Abs(rect.Right - rect.Left);
        double height = Math.Abs(rect.Bottom - rect.Top);

        double x = left + (width * fractionX);
        double y = top + (height * fractionY);

        return new Point((int)Math.Round(x), (int)Math.Round(y));
    }

    /// <summary>
    /// 取得曲线画布控件（真实鼠标事件由该控件接收，并由它转发给 ScottPlot 的输入处理器），
    /// 以便在测试中注入与真实鼠标完全一致的输入。
    /// </summary>
    public static Control GetInputCanvas(ScrollingPlotControl plot)
    {
        Control canvas = plot.Canvas;

        Assert.True(canvas is not null, "找不到曲线画布控件。");
        return canvas!;
    }

    /// <summary>获取当前 X 轴范围（自动处理坐标轴反向的情况）。</summary>
    public static (double Min, double Max) GetXRange(ScrollingPlotControl plot)
    {
        ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
        return (Math.Min(limits.Left, limits.Right), Math.Max(limits.Left, limits.Right));
    }

    /// <summary>获取当前 Y 轴范围（自动处理坐标轴反向的情况）。</summary>
    public static (double Min, double Max) GetYRange(ScrollingPlotControl plot)
    {
        ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
        return (Math.Min(limits.Bottom, limits.Top), Math.Max(limits.Bottom, limits.Top));
    }

    /// <summary>添加一串有起伏的数据，便于观察 Y 轴自动缩放。</summary>
    public static void AddWave(ScrollingPlotControl plot, int count, double amplitude = 10)
    {
        for (int i = 1; i <= count; i++)
            plot.AddValue(amplitude * Math.Sin(i / 3.0));
    }
}
