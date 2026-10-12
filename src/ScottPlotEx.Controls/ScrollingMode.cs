namespace ScottPlotEx.Controls;

/// <summary>
/// 曲线自动滚动模式。
/// </summary>
public enum ScrollingMode
{
    /// <summary>
    /// 堆叠模式（默认）：X 轴范围为 0 ~ 当前数据点数量 + 2，
    /// Y 轴范围为已添加数据的最大值与最小值各向外让出 5%。
    /// </summary>
    Stacked = 0,

    /// <summary>
    /// 跟随模式：X 轴范围为最后（最新）30% 的数据，最大值为 当前数据点数量 + 2，
    /// Y 轴范围为当前显示数据的最大值与最小值各向外让出 5%。
    /// </summary>
    Follow = 1,

    /// <summary>
    /// 手动模式：曲线停止滚动，视图完全由用户控制。
    /// 重新选择 <see cref="Stacked"/> 或 <see cref="Follow"/> 即可恢复滚动。
    /// </summary>
    Manual = 2,
}
