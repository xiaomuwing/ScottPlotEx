using System.Runtime.InteropServices;

namespace ScottPlotEx.Controls;

/// <summary>
/// 实时数据缓冲区。第 i 个数据点（下标从 0 开始）的 X 坐标为 <see cref="XStart"/> + i × <see cref="XStep"/>；
/// 默认 <c>XStart = 1</c>、<c>XStep = 1</c>，即第 n 个数据点 X = n（点数单位），
/// 因此 X 轴范围“0 ~ 数据点数 + 2”恰好包含全部数据点并在右侧保留 2 个数据点的余量。
/// 时间单位下由控件设置为“起始时间距参考时刻的秒数”与“采样间隔（秒）”。
/// 内部维护一个极值金字塔，任意 X 范围的取值范围查询开销与范围长度无关。
/// </summary>
public sealed class PlotDataBuffer
{
    private readonly List<double> _values = [];
    private readonly MinMaxPyramid _pyramid;
    private double _min = double.NaN;
    private double _max = double.NaN;
    private double _xStart = 1.0;
    private double _xStep = 1.0;

    /// <summary>创建空缓冲区。</summary>
    public PlotDataBuffer() => _pyramid = new MinMaxPyramid(_values);

    /// <summary>Y 值列表（内部实时列表，直接交给 ScottPlot 绘制）。</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>内部原始列表（供 <see cref="PyramidSignalSource"/> 直接读取）。</summary>
    internal List<double> RawValues => _values;

    /// <summary>与原始列表同步维护的极值金字塔。</summary>
    internal MinMaxPyramid Pyramid => _pyramid;

    /// <summary>数据点数量。</summary>
    public int Count => _values.Count;

    /// <summary>被忽略的非有限值（NaN / Infinity）数量。</summary>
    public int SkippedCount { get; private set; }

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

    /// <summary>最新数据点的 Y 值，没有数据时返回 <see cref="double.NaN"/>。</summary>
    public double LatestValue => _values.Count > 0 ? _values[^1] : double.NaN;

    /// <summary>添加一个数据点，非有限值会被忽略。</summary>
    /// <returns>数据被接受时返回 true。</returns>
    public bool Add(double y)
    {
        if (!double.IsFinite(y))
        {
            SkippedCount++;
            return false;
        }

        if (_values.Count == 0)
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

        _values.Add(y);
        _pyramid.OnAppended(y);
        return true;
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
        _min = double.NaN;
        _max = double.NaN;
        SkippedCount = 0;
    }

    /// <summary>获取全部数据的取值范围。</summary>
    public bool TryGetValueRange(out double min, out double max)
    {
        min = _min;
        max = _max;
        return _values.Count > 0;
    }

    /// <summary>
    /// 获取指定 X 范围内数据的取值范围（用于跟随模式 / 裁剪后的堆叠模式按当前显示数据自动缩放 Y 轴）。
    /// 基于极值金字塔，开销与范围内的点数无关。
    /// </summary>
    public bool TryGetValueRange(double xMin, double xMax, out double min, out double max)
    {
        min = double.NaN;
        max = double.NaN;

        if (!TryGetIndexRange(xMin, xMax, out int start, out int end))
            return false;

        return _pyramid.Query(start, end + 1, out min, out max);
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
