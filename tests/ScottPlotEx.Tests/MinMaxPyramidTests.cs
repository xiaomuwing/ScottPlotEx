using ScottPlotEx.Controls;

namespace ScottPlotEx.Tests;

/// <summary>极值金字塔：任意区间的查询结果必须与线性扫描完全一致。</summary>
public class MinMaxPyramidTests
{
    [Fact(DisplayName = "极值金字塔：随机区间查询与线性扫描一致（边追加边查询）")]
    public void Query_MatchesLinearScan_WhileAppending()
    {
        Random random = new(20261010);
        List<double> raw = [];
        MinMaxPyramid pyramid = new(raw);

        // 覆盖跨越多个级别的长度：16、256、4096、65536 附近都会经过
        foreach (int target in new[] { 1, 15, 16, 17, 255, 256, 257, 4_097, 70_000, 300_000 })
        {
            while (raw.Count < target)
            {
                double value = (random.NextDouble() - 0.5) * 1000;

                // 偶尔插入极端值，确保它们被正确向上传播
                if (random.Next(5_000) == 0)
                    value *= 1e6;

                raw.Add(value);
                pyramid.OnAppended(value);
            }

            for (int i = 0; i < 400; i++)
            {
                int start = random.Next(raw.Count);
                int end = start + 1 + random.Next(raw.Count - start);
                AssertMatches(raw, pyramid, start, end);
            }

            // 尾部区间（跟随模式的典型查询）与全量区间
            AssertMatches(raw, pyramid, raw.Count - Math.Max(1, raw.Count * 3 / 10), raw.Count);
            AssertMatches(raw, pyramid, 0, raw.Count);
        }
    }

    [Fact(DisplayName = "极值金字塔：越界区间被裁剪，空区间返回 false")]
    public void Query_ClampsAndHandlesEmptyRanges()
    {
        List<double> raw = [3, -1, 8, 2];
        MinMaxPyramid pyramid = new(raw);
        foreach (double value in raw)
            pyramid.OnAppended(value);

        Assert.True(pyramid.Query(-10, 100, out double min, out double max));
        Assert.Equal(-1, min);
        Assert.Equal(8, max);

        Assert.False(pyramid.Query(2, 2, out min, out max));
        Assert.Equal(double.PositiveInfinity, min);
        Assert.Equal(double.NegativeInfinity, max);

        Assert.False(pyramid.Query(10, 20, out _, out _));
    }

    [Fact(DisplayName = "极值金字塔：清空后重新追加，结果不受旧数据影响")]
    public void Clear_ThenAppend_ForgetsOldData()
    {
        List<double> raw = [];
        MinMaxPyramid pyramid = new(raw);

        for (int i = 0; i < 10_000; i++)
        {
            raw.Add(i % 2 == 0 ? 1000 : -1000);
            pyramid.OnAppended(raw[^1]);
        }

        raw.Clear();
        pyramid.Clear();

        for (int i = 0; i < 5_000; i++)
        {
            raw.Add(i % 7);
            pyramid.OnAppended(raw[^1]);
        }

        Assert.True(pyramid.Query(0, raw.Count, out double min, out double max));
        Assert.Equal(0, min);
        Assert.Equal(6, max);
    }

    [Fact(DisplayName = "缓冲区：大数据量按 X 范围统计与线性扫描一致")]
    public void Buffer_TryGetValueRange_MatchesLinearScanOnLargeData()
    {
        PlotDataBuffer buffer = new();
        Random random = new(7);
        double[] values = new double[200_000];

        for (int i = 0; i < values.Length; i++)
            values[i] = Math.Sin(i / 500.0) * 10 + (random.NextDouble() - 0.5);

        buffer.AddRange(values);

        foreach ((double xMin, double xMax) in new[] { (140_000.5, 200_002.0), (20_000.0, 200_002.0), (1.0, 3.0), (77_777.3, 77_790.9) })
        {
            Assert.True(buffer.TryGetValueRange(xMin, xMax, out double min, out double max));

            int start = (int)Math.Ceiling(Math.Clamp(xMin - 1, 0, values.Length - 1));
            int end = (int)Math.Floor(Math.Clamp(xMax - 1, 0, values.Length - 1));
            Assert.Equal(values[start..(end + 1)].Min(), min);
            Assert.Equal(values[start..(end + 1)].Max(), max);
        }
    }

    [Fact(DisplayName = "缓冲区：批量写入自身数据（Span 指向内部数组）也安全")]
    public void Buffer_AddRange_FromItsOwnValues_IsSafe()
    {
        PlotDataBuffer buffer = new();
        buffer.AddRange([1, 2, 3]);

        Assert.Equal(3, buffer.AddRange(buffer.Values));
        Assert.Equal([1.0, 2, 3, 1, 2, 3], buffer.Values);
        Assert.True(buffer.TryGetValueRange(4, 6, out double min, out double max));
        Assert.Equal(1, min);
        Assert.Equal(3, max);
    }

    private static void AssertMatches(List<double> raw, MinMaxPyramid pyramid, int start, int end)
    {
        double expectedMin = double.PositiveInfinity;
        double expectedMax = double.NegativeInfinity;

        for (int i = start; i < end; i++)
        {
            expectedMin = Math.Min(expectedMin, raw[i]);
            expectedMax = Math.Max(expectedMax, raw[i]);
        }

        Assert.True(pyramid.Query(start, end, out double min, out double max));
        Assert.True(expectedMin == min && expectedMax == max, $"区间 [{start}, {end}) 不一致：期望 {expectedMin}~{expectedMax}，实际 {min}~{max}（共 {raw.Count} 点）。");
    }
}
