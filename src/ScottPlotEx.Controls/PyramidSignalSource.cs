using ScottPlot;
using ScottPlot.DataSources;

namespace ScottPlotEx.Controls;

/// <summary>
/// 基于 <see cref="MinMaxPyramid"/> 的 Signal 数据源。
/// <para>
/// 对 <see cref="IReadOnlyList{T}"/>，ScottPlot 默认使用 <c>SignalSourceGenericList</c>：每帧对每个像素列
/// 通过接口索引器逐点扫描，数据全部可见时（堆叠模式默认情况）每帧 O(N)。
/// 这里把“像素列内求极值”换成金字塔查询，每列 O(16 × 级数)，单帧耗时基本不再随数据量增长。
/// </para>
/// <para>
/// 行为与内置数据源保持一致（渲染结果逐像素相同，自动缩放范围、最近点查询结果相同）：
/// <c>GetY</c> / <c>GetYs</c> / <c>GetLimitsY(int, int)</c> 返回未缩放的原始值，YScale / YOffset 由基类统一施加；
/// <c>GetYs(int, int)</c> 会把下标裁剪到现有数据范围内。
/// </para>
/// <para>
/// “下标 ↔ X”换算全部交给基类的 <see cref="SignalSourceBase.Period"/> / <see cref="SignalSourceBase.XOffset"/>（GetX / GetIndex），
/// 由控件按 X 轴单位设置：点数单位 Period = 1、XOffset = 1；时间单位 Period = 采样间隔（秒）、XOffset = 起始时间距参考时刻的秒数。
/// 因此渲染、自动缩放范围与最近点查询返回的 X 都随单位换算，与内置数据源在相同 Period / XOffset 下的结果一致。
/// </para>
/// </summary>
internal sealed class PyramidSignalSource : SignalSourceBase, ISignalSource, IDataSource
{
    private readonly List<double> _ys;
    private readonly MinMaxPyramid _pyramid;

    /// <summary>以缓冲区的原始数据与金字塔创建数据源（缓冲区后续追加的数据会直接反映到曲线上）。</summary>
    public PyramidSignalSource(PlotDataBuffer buffer, double period = 1.0)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _ys = buffer.RawValues;
        _pyramid = buffer.Pyramid;
        Period = period;
    }

    /// <inheritdoc />
    public override int Length => _ys.Count;

    /// <inheritdoc />
    public IReadOnlyList<double> GetYs() => _ys;

    /// <inheritdoc />
    public IEnumerable<double> GetYs(int index1, int index2)
    {
        int last = Math.Min(index2, _ys.Count - 1);

        for (int i = Math.Max(index1, 0); i <= last; i++)
            yield return _ys[i];
    }

    /// <inheritdoc />
    public double GetY(int index) => _ys[index];

    /// <inheritdoc />
    public override SignalRangeY GetLimitsY(int firstIndex, int lastIndex)
    {
        // 空区间时 min = +∞、max = −∞，与内置数据源一致
        _pyramid.Query(firstIndex, lastIndex + 1, out double min, out double max);
        return new SignalRangeY(min, max);
    }

    /// <inheritdoc />
    public PixelColumn GetPixelColumn(IAxes axes, int xPixelIndex)
    {
        float xPixel = axes.DataRect.Left + xPixelIndex;
        double xRangeMin = axes.GetCoordinateX(xPixel);
        float xUnitsPerPixel = (float)(axes.XAxis.Width / axes.DataRect.Width);
        double xRangeMax = xRangeMin + xUnitsPerPixel;

        if (!RangeContainsSignal(xRangeMin, xRangeMax))
            return PixelColumn.WithoutData(xPixel);

        int i1 = GetIndex(xRangeMin, true);
        int i2 = GetIndex(xRangeMax, true);

        float yEnter = axes.GetPixelY((_ys[i1] * YScale) + YOffset);
        float yExit = axes.GetPixelY((_ys[i2] * YScale) + YOffset);

        // 内置数据源在这里逐点扫描 [i1, i2]；金字塔查询与列宽（每列点数）无关
        _pyramid.Query(i1, i2 + 1, out double yMin, out double yMax);
        float yBottom = axes.GetPixelY((yMin * YScale) + YOffset);
        float yTop = axes.GetPixelY((yMax * YScale) + YOffset);

        return new PixelColumn(xPixel, yEnter, yExit, yBottom, yTop);
    }

    #region IDataSource（最近点查询 / 鼠标交互使用）

    bool IDataSource.PreferCoordinates => false;

    int IDataSource.GetXClosestIndex(Coordinates mouseLocation) => GetIndex(mouseLocation.X, true);

    Coordinates IDataSource.GetCoordinate(int index) => new(GetX(index), _ys[index]);

    Coordinates IDataSource.GetCoordinateScaled(int index) => new(GetX(index), (_ys[index] * YScale) + YOffset);

    double IDataSource.GetXScaled(int index) => GetX(index);

    double IDataSource.GetYScaled(int index) => (_ys[index] * YScale) + YOffset;

    bool IDataSource.IsSorted() => true;

    #endregion
}
