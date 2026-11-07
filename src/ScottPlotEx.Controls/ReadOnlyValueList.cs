using System.Collections;

namespace ScottPlotEx.Controls;

/// <summary>
/// 数据缓冲区原始列表的只读视图（<see cref="PlotDataBuffer.Values"/> / <see cref="PlotSeries.Values"/> 返回它）。
/// 实时反映缓冲区的内容、不复制数据；与直接返回内部 <see cref="List{T}"/> 不同，调用方无法把它强制转换回列表去修改，
/// 因此不会绕过缓冲区让极值金字塔失步（曲线绘制与 Y 轴范围出错）。
/// </summary>
internal sealed class ReadOnlyValueList : IReadOnlyList<double>
{
    private readonly List<double> _values;

    public ReadOnlyValueList(List<double> values) => _values = values;

    /// <summary>被包装的原始列表（只供本程序集按 Span 高效读取）。</summary>
    internal List<double> Inner => _values;

    public double this[int index] => _values[index];

    public int Count => _values.Count;

    public IEnumerator<double> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
