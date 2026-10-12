using ScottPlot;
using ScottPlotEx.Controls;

namespace ScottPlotEx.Tests;

/// <summary>
/// 升级护栏：金字塔数据源必须与 ScottPlot 内置数据源（SignalSourceGenericList）行为一致。
/// 升级 ScottPlot 后如果这些测试失败，说明内置数据源的实现发生了变化，需要同步调整 <c>PyramidSignalSource</c>。
/// </summary>
public class PyramidSignalSourceTests
{
    private const int Width = 800;
    private const int Height = 500;

    [Theory(DisplayName = "金字塔数据源：渲染结果、自动缩放范围、最近点查询与内置数据源一致")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]       // 低密度模式（逐点连线）
    [InlineData(1_000)]
    [InlineData(50_000)]   // 高密度模式（逐像素列）
    public void MatchesBuiltInSource(int count)
    {
        Random random = new(count + 1);
        List<double> values = [];
        PlotDataBuffer buffer = new();

        for (int i = 0; i < count; i++)
        {
            double value = (Math.Sin(i / 50.0) * 10) + (random.NextDouble() - 0.5);
            values.Add(value);
            buffer.Add(value);
        }

        (byte[] builtInImage, AxisLimits builtInLimits, string builtInNearest) = Render(plot => plot.Add.Signal(values, 1.0), values);
        (byte[] pyramidImage, AxisLimits pyramidLimits, string pyramidNearest) = Render(plot => plot.Add.Signal(new PyramidSignalSource(buffer)), values);

        Assert.True(builtInImage.AsSpan().SequenceEqual(pyramidImage), $"{count} 点时渲染结果与内置数据源不一致。");
        Assert.Equal(builtInLimits.ToString(), pyramidLimits.ToString());
        Assert.Equal(builtInNearest, pyramidNearest);
    }

    [Fact(DisplayName = "金字塔数据源：追加数据后无需重建即可反映到曲线上")]
    public void ReflectsAppendedData()
    {
        PlotDataBuffer buffer = new();
        PyramidSignalSource source = new(buffer) { XOffset = 1 };

        buffer.AddRange([5, -2, 9]);

        Assert.Equal(3, source.Length);
        AxisLimits limits = source.GetLimits();
        Assert.Equal(1, limits.Left);
        Assert.Equal(3, limits.Right);
        Assert.Equal(-2, limits.Bottom);
        Assert.Equal(9, limits.Top);
    }

    private static (byte[] Image, AxisLimits Limits, string Nearest) Render(
        Func<Plot, ScottPlot.Plottables.Signal> addSignal,
        IReadOnlyList<double> values)
    {
        using Plot plot = new();
        ScottPlot.Plottables.Signal signal = addSignal(plot);
        signal.Data.XOffset = 1;
        signal.LineWidth = 1;
        signal.Color = Colors.Blue;

        AxisLimits limits = signal.GetAxisLimits();

        if (values.Count > 0)
            plot.Axes.SetLimits(0, values.Count + 2, -12, 12);
        else
            plot.Axes.SetLimits(0, 1, -0.5, 0.5);

        using ScottPlot.Image rendered = plot.GetImage(Width, Height);
        byte[] image = rendered.GetImageBytes();

        string nearest = string.Empty;

        if (values.Count > 0)
        {
            foreach (double fraction in new[] { 0.0, 0.13, 0.5, 0.77, 1.0, 1.3 })
            {
                int index = Math.Min(values.Count - 1, (int)(values.Count * fraction));
                Coordinates location = new((values.Count * fraction) + 0.4, values[index] + 0.3);
                DataPoint point = signal.GetNearest(location, plot.RenderManager.LastRender, 15);
                DataPoint pointX = signal.GetNearestX(location, plot.RenderManager.LastRender, 15);
                nearest += $"{point.Index}/{point.X}/{point.Y}|{pointX.Index}/{pointX.X}/{pointX.Y};";
            }
        }

        return (image, limits, nearest);
    }
}
