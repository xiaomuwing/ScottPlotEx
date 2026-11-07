using ScottPlotEx.Controls;

namespace ScottPlotEx.Tests;

public class PlotDataBufferTests
{
    [Fact(DisplayName = "缓冲区：X 坐标从 1 开始递增")]
    public void Add_IncrementsXFromOne()
    {
        PlotDataBuffer buffer = new();

        buffer.Add(5);
        buffer.Add(-3);

        Assert.Equal(2, buffer.Count);
        Assert.Equal(2, buffer.LatestX);
        Assert.Equal(-3, buffer.LatestValue);
    }

    [Fact(DisplayName = "缓冲区：NaN、Infinity、绝对值超过 1e300 的值按缺失值占位（X 不错位），取值范围跳过它们")]
    public void Add_KeepsMissingValuesAsPlaceholders()
    {
        PlotDataBuffer buffer = new();

        Assert.True(buffer.Add(double.NaN));
        Assert.True(buffer.Add(double.PositiveInfinity));
        Assert.True(buffer.Add(double.NegativeInfinity));
        Assert.True(buffer.Add(1e301));
        Assert.True(buffer.Add(1));

        Assert.Equal(5, buffer.Count);
        Assert.Equal(4, buffer.SkippedCount);
        Assert.Equal(1, buffer.FiniteCount);
        Assert.Equal(5, buffer.LatestX); // 第 5 个点 X = 5：缺失值占了前 4 个位置
        Assert.All(buffer.Values.Take(4), value => Assert.True(double.IsNaN(value)));

        Assert.True(buffer.TryGetValueRange(out double min, out double max));
        Assert.Equal(1, min);
        Assert.Equal(1, max);
        Assert.False(buffer.TryGetValueRange(1, 4, out _, out _)); // X 1~4 全是缺失值
    }

    [Fact(DisplayName = "缓冲区：全部数据的取值范围")]
    public void TryGetValueRange_ReturnsFullRange()
    {
        PlotDataBuffer buffer = new();
        buffer.AddRange([3, -1, 8, 2]);

        Assert.True(buffer.TryGetValueRange(out double min, out double max));
        Assert.Equal(-1, min);
        Assert.Equal(8, max);
    }

    [Fact(DisplayName = "缓冲区：按 X 范围统计数据（跟随模式 Y 轴）")]
    public void TryGetValueRange_ByXRange()
    {
        PlotDataBuffer buffer = new();
        buffer.AddRange([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        // X = 8~10 对应第 8、9、10 个点（值 8、9、10）
        Assert.True(buffer.TryGetValueRange(8, 10, out double min, out double max));
        Assert.Equal(8, min);
        Assert.Equal(10, max);

        // X = 1~3
        Assert.True(buffer.TryGetValueRange(1, 3, out min, out max));
        Assert.Equal(1, min);
        Assert.Equal(3, max);
    }

    [Fact(DisplayName = "缓冲区：范围颠倒时返回 false")]
    public void TryGetValueRange_WithInvertedRange_ReturnsFalse()
    {
        PlotDataBuffer buffer = new();
        buffer.AddRange([1, 2, 3]);

        Assert.False(buffer.TryGetValueRange(3, 1, out _, out _));
    }

    [Fact(DisplayName = "缓冲区：超出数据的 X 范围会被裁剪到最近的数据")]
    public void TryGetValueRange_ClampsOutOfRangeX()
    {
        PlotDataBuffer buffer = new();
        buffer.AddRange([1, 2, 3]);

        Assert.True(buffer.TryGetValueRange(100, 200, out double min, out double max));
        Assert.Equal(3, min);
        Assert.Equal(3, max);
    }

    [Fact(DisplayName = "缓冲区：清空后回到初始状态")]
    public void Clear_ResetsEverything()
    {
        PlotDataBuffer buffer = new();
        buffer.AddRange([1, 2, 3]);
        buffer.Add(double.NaN);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.SkippedCount);
        Assert.False(buffer.TryGetValueRange(out _, out _));
        Assert.True(double.IsNaN(buffer.LatestValue));
    }
}
