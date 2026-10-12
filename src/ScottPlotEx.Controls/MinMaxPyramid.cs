using System.Runtime.InteropServices;

namespace ScottPlotEx.Controls;

/// <summary>
/// 多级极值金字塔（16 叉，只追加）：第 L 级的每个元素保存原始数据中连续 16^(L+1) 个点的最小值 / 最大值。
/// <list type="bullet">
/// <item>查询任意下标区间的极值只需扫描各级首尾不足一整块的“零头”，开销约 O(16 × 级数)，与区间长度无关；</item>
/// <item>追加一个点通常只更新 1~2 级：某一级的块极值没有变化时，更高级的块也不会变化，可以提前结束；</item>
/// <item>额外内存约为原始数据的 13%。</item>
/// </list>
/// 同时服务于两处：可见范围的 Y 轴自动缩放（<see cref="PlotDataBuffer.TryGetValueRange(double, double, out double, out double)"/>）
/// 与曲线渲染时逐像素列求极值（<see cref="PyramidSignalSource"/>）。
/// 原始数据列表由 <see cref="PlotDataBuffer"/> 持有，每次向列表追加一个点后必须调用 <see cref="OnAppended"/>。
/// </summary>
internal sealed class MinMaxPyramid
{
    private const int Shift = 4;
    private const int Fan = 1 << Shift;

    /// <summary>级数：顶层每块 16^6 ≈ 1677 万点，即使 1 亿点顶层也只有 6 个元素。</summary>
    private const int Levels = 6;

    private readonly List<double> _raw;
    private readonly List<double>[] _min = new List<double>[Levels];
    private readonly List<double>[] _max = new List<double>[Levels];

    /// <summary>创建金字塔。</summary>
    /// <param name="raw">原始数据列表（金字塔只读取，不修改）。</param>
    public MinMaxPyramid(List<double> raw)
    {
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));

        for (int level = 0; level < Levels; level++)
        {
            _min[level] = [];
            _max[level] = [];
        }
    }

    /// <summary>原始数据追加了一个点（必须在 <c>raw.Add(y)</c> 之后调用）。</summary>
    public void OnAppended(double y)
    {
        int index = _raw.Count - 1;

        for (int level = 0; level < Levels; level++)
        {
            int block = index >> (Shift * (level + 1));
            List<double> min = _min[level];
            List<double> max = _max[level];

            if (block == min.Count)
            {
                // 新块：更高级的块可能也需要新建或更新，继续向上
                min.Add(y);
                max.Add(y);
                continue;
            }

            Span<double> minSpan = CollectionsMarshal.AsSpan(min);
            Span<double> maxSpan = CollectionsMarshal.AsSpan(max);
            bool changed = false;

            if (y < minSpan[block])
            {
                minSpan[block] = y;
                changed = true;
            }

            if (y > maxSpan[block])
            {
                maxSpan[block] = y;
                changed = true;
            }

            // 本级块的极值不变 → 包含它的更高级块也不会变
            if (!changed)
                break;
        }
    }

    /// <summary>清空全部级别（原始数据列表由调用方清空）。</summary>
    public void Clear()
    {
        for (int level = 0; level < Levels; level++)
        {
            _min[level].Clear();
            _max[level].Clear();
        }
    }

    /// <summary>
    /// 查询原始下标区间 [<paramref name="start"/>, <paramref name="endExclusive"/>) 的极值。
    /// 区间会被裁剪到现有数据范围内；裁剪后为空时返回 false，此时 min = +∞、max = −∞。
    /// </summary>
    public bool Query(int start, int endExclusive, out double min, out double max)
    {
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;

        int lo = Math.Max(start, 0);
        int hi = Math.Min(endExclusive, _raw.Count);

        if (lo >= hi)
            return false;

        // level = -1 表示原始数据；每上升一级，[lo, hi) 换算为上一级的下标
        for (int level = -1; lo < hi; level++)
        {
            ReadOnlySpan<double> minSpan = level < 0 ? CollectionsMarshal.AsSpan(_raw) : CollectionsMarshal.AsSpan(_min[level]);
            ReadOnlySpan<double> maxSpan = level < 0 ? minSpan : CollectionsMarshal.AsSpan(_max[level]);

            if (level == Levels - 1 || hi - lo < 2 * Fan)
            {
                Scan(minSpan, maxSpan, lo, hi, ref min, ref max);
                break;
            }

            int alignedLo = (lo + Fan - 1) & ~(Fan - 1);
            int alignedHi = hi & ~(Fan - 1);

            // 首尾零头在本级逐个扫描，中间的整块交给上一级
            // （整块必然已经写满：它们完全落在 [lo, hi) 之内，而 hi 不超过本级现有元素数）
            Scan(minSpan, maxSpan, lo, alignedLo, ref min, ref max);
            Scan(minSpan, maxSpan, alignedHi, hi, ref min, ref max);

            lo = alignedLo >> Shift;
            hi = alignedHi >> Shift;
        }

        return true;
    }

    private static void Scan(ReadOnlySpan<double> minSpan, ReadOnlySpan<double> maxSpan, int lo, int hi, ref double min, ref double max)
    {
        for (int i = lo; i < hi; i++)
        {
            if (minSpan[i] < min)
                min = minSpan[i];

            if (maxSpan[i] > max)
                max = maxSpan[i];
        }
    }
}
