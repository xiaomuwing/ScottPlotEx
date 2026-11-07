using System.Runtime.InteropServices;

namespace ScottPlotEx.Controls;

/// <summary>
/// 实时数据缓冲区。第 i 个数据点（下标从 0 开始）的 X 坐标为 <see cref="XStart"/> + i × <see cref="XStep"/>；
/// 默认 <c>XStart = 1</c>、<c>XStep = 1</c>，即第 n 个数据点 X = n（点数单位），
/// 因此 X 轴范围“0 ~ 数据点数 + 2”恰好包含全部数据点并在右侧保留 2 个数据点的余量。
/// 时间单位下由控件设置为“起始时间距参考时刻的秒数”与“采样间隔（秒）”。
/// 内部维护一个极值金字塔，任意 X 范围的取值范围查询开销与范围长度无关。
/// <para>
/// 缺失值（NaN、±Infinity、绝对值超过 <see cref="MaxAbsoluteValue"/> 的值）按 NaN 保存、占一个下标：
/// 之后的数据点 X 不会错位（多条曲线按序号 / 按时间的对齐保持不变），绘制时在缺失处断开，取值范围统计跳过缺失值。
/// </para>
/// </summary>
public sealed class PlotDataBuffer
{
    /// <summary>
    /// 绝对值超过它的数据按缺失值处理：接近 <see cref="double.MaxValue"/> 的哨兵值会让坐标轴跨度溢出、绘制失败。
    /// </summary>
    public const double MaxAbsoluteValue = 1e300;

    private readonly List<double> _values = [];
    private readonly MinMaxPyramid _pyramid;
    private double _min = double.NaN;
    private double _max = double.NaN;
    private int _finiteCount;

    /// <summary>连续缺失值的下标区间 [Start, End]（按下标递增，只追加），用于断线绘制与跳过缺失值的查询。</summary>
    private readonly List<(int Start, int End)> _missingRuns = [];
    private double _xStart = 1.0;
    private double _xStep = 1.0;

    private readonly ReadOnlyValueList _view;

    /// <summary>创建空缓冲区。</summary>
    public PlotDataBuffer()
    {
        _pyramid = new MinMaxPyramid(_values);
        _view = new ReadOnlyValueList(_values);
    }

    /// <summary>
    /// Y 值列表：内部数据的实时只读视图（不复制；随写入实时变化）。
    /// 不能转换回可修改的列表——数据只能经由本缓冲区写入，极值金字塔才能保持同步。
    /// </summary>
    public IReadOnlyList<double> Values => _view;

    /// <summary>内部原始列表（供 <see cref="PyramidSignalSource"/> 直接读取）。</summary>
    internal List<double> RawValues => _values;

    /// <summary>与原始列表同步维护的极值金字塔。</summary>
    internal MinMaxPyramid Pyramid => _pyramid;

    /// <summary>数据点数量。</summary>
    public int Count => _values.Count;

    /// <summary>缺失值数量（NaN、±Infinity、绝对值超过 <see cref="MaxAbsoluteValue"/> 的值；按 NaN 占位保存）。</summary>
    public int SkippedCount { get; private set; }

    /// <summary>有效（非缺失）数据点的数量。</summary>
    public int FiniteCount => _finiteCount;

    /// <summary>连续缺失值的下标区间（按下标递增）。</summary>
    internal ReadOnlySpan<(int Start, int End)> MissingRuns => CollectionsMarshal.AsSpan(_missingRuns);

    /// <summary>第一个数据点（下标 0）的 X 坐标。默认 1（点数单位：第 n 个点 X = n）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">不是有限值时抛出。</exception>
    public double XStart
    {
        get => _xStart;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "XStart 必须是有限值。");

            _xStart = value;
        }
    }

    /// <summary>相邻两个数据点的 X 间距。默认 1（点数单位）；时间单位下为采样间隔（秒）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">不是正的有限值时抛出。</exception>
    public double XStep
    {
        get => _xStep;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "XStep 必须是正的有限值。");

            _xStep = value;
        }
    }

    /// <summary>
    /// 最新数据点的 X 坐标（= <see cref="XStart"/> + (点数 − 1) × <see cref="XStep"/>）。
    /// 没有数据时为 <see cref="XStart"/> − <see cref="XStep"/>（第一个点之前一个间距的位置；点数单位下即 0）。
    /// </summary>
    public double LatestX => GetX(_values.Count - 1);

    /// <summary>下标 → X 坐标（下标从 0 开始，可以超出现有数据范围）。</summary>
    public double GetX(int index) => _xStart + (index * _xStep);

    /// <summary>X 坐标 → 下标（不取整、不裁剪，可能为小数或超出现有数据范围）。</summary>
    public double GetIndex(double x) => (x - _xStart) / _xStep;

    /// <summary>最新数据点的 Y 值，没有数据时返回 <see cref="double.NaN"/>（最新的点是缺失值时同样为 NaN）。</summary>
    public double LatestValue => _values.Count > 0 ? _values[^1] : double.NaN;

    /// <summary>
    /// 添加一个数据点。缺失值（NaN、±Infinity、绝对值超过 <see cref="MaxAbsoluteValue"/>）按 NaN 占位保存并计入 <see cref="SkippedCount"/>。
    /// </summary>
    /// <returns>总是 true（缺失值也占一个位置）。</returns>
    public bool Add(double y)
    {
        if (!double.IsFinite(y) || Math.Abs(y) > MaxAbsoluteValue)
        {
            int index = _values.Count;
            _values.Add(double.NaN);
            _pyramid.OnAppended(double.NaN);
            SkippedCount++;

            if (_missingRuns.Count > 0 && _missingRuns[^1].End == index - 1)
                _missingRuns[^1] = (_missingRuns[^1].Start, index);
            else
                _missingRuns.Add((index, index));

            return true;
        }

        if (_finiteCount == 0)
        {
            _min = y;
            _max = y;
        }
        else
        {
            if (y < _min)
                _min = y;
            if (y > _max)
                _max = y;
        }

        _finiteCount++;
        _values.Add(y);
        _pyramid.OnAppended(y);
        return true;
    }

    /// <summary>下标处是否为缺失值。</summary>
    internal bool IsMissing(int index) => (uint)index < (uint)_values.Count && double.IsNaN(_values[index]);

    /// <summary>从 <paramref name="index"/> 起向后第一个有效数据点的下标；没有时返回 -1。</summary>
    internal int NextFiniteIndex(int index)
    {
        if (index < 0)
            index = 0;

        if (index >= _values.Count)
            return -1;

        if (!double.IsNaN(_values[index]))
            return index;

        int run = FindRunContaining(index);
        int next = run >= 0 ? _missingRuns[run].End + 1 : index + 1;
        return next < _values.Count ? next : -1;
    }

    /// <summary>从 <paramref name="index"/> 起向前第一个有效数据点的下标；没有时返回 -1。</summary>
    internal int PreviousFiniteIndex(int index)
    {
        if (index >= _values.Count)
            index = _values.Count - 1;

        if (index < 0)
            return -1;

        if (!double.IsNaN(_values[index]))
            return index;

        int run = FindRunContaining(index);
        int previous = run >= 0 ? _missingRuns[run].Start - 1 : index - 1;
        return previous >= 0 ? previous : -1;
    }

    /// <summary>离 <paramref name="index"/> 最近的有效数据点下标（距离相同时取前一个）；没有有效数据时返回 -1。</summary>
    internal int NearestFiniteIndex(int index)
    {
        int next = NextFiniteIndex(index);
        int previous = PreviousFiniteIndex(index);

        if (previous < 0)
            return next;

        if (next < 0)
            return previous;

        return index - previous <= next - index ? previous : next;
    }

    /// <summary>第一个 End 不小于 <paramref name="index"/> 的缺失区间序号（区间按下标递增）；没有时返回区间数量。</summary>
    internal int FirstMissingRunEndingAtOrAfter(int index)
    {
        int lo = 0;
        int hi = _missingRuns.Count;

        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_missingRuns[mid].End < index)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private int FindRunContaining(int index)
    {
        int run = FirstMissingRunEndingAtOrAfter(index);
        return run < _missingRuns.Count && _missingRuns[run].Start <= index ? run : -1;
    }

    /// <summary>
    /// 批量添加数据点（数组）。单独提供数组重载：在 C# 12（net8.0 的默认语言版本）下，
    /// 数组实参同时匹配 <see cref="IEnumerable{T}"/> 与 <see cref="ReadOnlySpan{T}"/> 两个重载会报 CS0121 二义性错误。
    /// </summary>
    /// <returns>实际被接受的数据点数量。</returns>
    public int AddRange(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddRange(new ReadOnlySpan<double>(values));
    }

    /// <summary>批量添加数据点。</summary>
    /// <returns>实际被接受的数据点数量。</returns>
    public int AddRange(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        switch (values)
        {
            case double[] array:
                return AddRange(new ReadOnlySpan<double>(array));
            case List<double> list:
                return AddRange(CollectionsMarshal.AsSpan(list));
            case ReadOnlyValueList view:
                // 缓冲区的只读视图（包括自身：buffer.AddRange(buffer.Values)）按 Span 处理：
                // 扩容前已固定待写入的范围，不会因为边枚举边追加而抛异常
                return AddRange(CollectionsMarshal.AsSpan(view.Inner));
        }

        int added = 0;
        foreach (double value in values)
        {
            if (Add(value))
                added++;
        }

        return added;
    }

    /// <summary>批量添加数据点（不复制调用方的数据）。</summary>
    /// <returns>实际被接受的数据点数量。</returns>
    public int AddRange(ReadOnlySpan<double> values)
    {
        // 一次性扩容，避免大批量写入时多次倍增复制
        // （即使 values 指向本缓冲区自身的内部数组也安全：扩容后它仍指向旧数组，内容不变）
        _values.EnsureCapacity(_values.Count + values.Length);

        int added = 0;
        foreach (double value in values)
        {
            if (Add(value))
                added++;
        }

        return added;
    }

    /// <summary>清空全部数据。</summary>
    public void Clear()
    {
        _values.Clear();
        _pyramid.Clear();
        _missingRuns.Clear();
        _min = double.NaN;
        _max = double.NaN;
        _finiteCount = 0;
        SkippedCount = 0;
    }

    /// <summary>获取全部数据的取值范围（跳过缺失值）。没有有效数据时返回 false。</summary>
    public bool TryGetValueRange(out double min, out double max)
    {
        min = _min;
        max = _max;
        return _finiteCount > 0;
    }

    /// <summary>
    /// 获取指定 X 范围内数据的取值范围（用于跟随模式 / 裁剪后的堆叠模式按当前显示数据自动缩放 Y 轴），跳过缺失值。
    /// 基于极值金字塔，开销与范围内的点数无关。范围内没有有效数据时返回 false。
    /// </summary>
    public bool TryGetValueRange(double xMin, double xMax, out double min, out double max)
    {
        min = double.NaN;
        max = double.NaN;

        if (!TryGetIndexRange(xMin, xMax, out int start, out int end))
            return false;

        // 区间内全是缺失值时金字塔返回 min = +∞、max = −∞
        return _pyramid.Query(start, end + 1, out min, out max) && min <= max;
    }

    /// <summary>
    /// 把 X 范围换算成落在其中的数据点下标区间 [<paramref name="start"/>, <paramref name="end"/>]（X = XStart + 下标 × XStep）。
    /// 超出数据的范围会被裁剪到最近的数据点；区间内没有数据点时返回 false。
    /// </summary>
    public bool TryGetIndexRange(double xMin, double xMax, out int start, out int end)
    {
        start = 0;
        end = -1;

        if (_values.Count == 0 || !double.IsFinite(xMin) || !double.IsFinite(xMax) || xMax < xMin)
            return false;

        // 换算出的下标允许极小的浮点误差：X 恰好落在某个数据点上时（例如 0.3 / 0.1 = 2.9999999…）不会漏掉该点
        const double Tolerance = 1e-9;
        double firstIndex = Math.Clamp(GetIndex(xMin) - Tolerance, 0, _values.Count - 1);
        double lastIndex = Math.Clamp(GetIndex(xMax) + Tolerance, 0, _values.Count - 1);

        start = (int)Math.Ceiling(firstIndex);
        end = (int)Math.Floor(lastIndex);

        return start <= end;
    }
}
