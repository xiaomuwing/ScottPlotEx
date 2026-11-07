namespace ScottPlotEx.Controls;

/// <summary>
/// UI 线程上逐点写入数据（<see cref="ScrollingPlotControl.AddValue"/> / <see cref="PlotSeries.AddValue"/> 等）时，
/// 自动滚动的坐标轴在什么时候更新。跨线程写入本来就按帧合并，每帧只计算一次，不受此设置影响。
/// <para>
/// 每次更新坐标轴都要遍历全部可见曲线（跟随模式下逐条查询极值金字塔），开销与可见曲线数成正比；
/// 曲线很多又在 UI 线程上逐点写入时，逐点立即更新会占满 UI 线程。合并更新把同一轮消息处理中的多次写入合并成一次计算。
/// </para>
/// </summary>
public enum AxisUpdateMode
{
    /// <summary>
    /// 自动（默认）：参与坐标轴计算的可见曲线不超过 <see cref="ScrollingPlotControl.AutoDeferVisibleSeriesThreshold"/> 条时立即更新，
    /// 超过时按 <see cref="Deferred"/> 合并更新。
    /// </summary>
    Auto = 0,

    /// <summary>立即：每写入一次数据都立即重算坐标轴（写入后马上读取 <c>Plot.Axes.GetLimits()</c> 得到的就是新范围）。</summary>
    Immediate = 1,

    /// <summary>
    /// 合并：写入只标记“坐标轴待更新”，在本轮消息处理结束后（投递一条消息）、下一次绘制之前、
    /// 或鼠标 / 键盘输入到达之前统一计算一次。写入之后在同一段代码里直接读取 <c>Plot.Axes.GetLimits()</c>
    /// 会得到更新之前的范围；需要立即得到新范围时调用 <see cref="ScrollingPlotControl.UpdateAxesNow"/>。
    /// 控件句柄创建之前没有消息循环可用，始终立即更新。
    /// </summary>
    Deferred = 2,
}
