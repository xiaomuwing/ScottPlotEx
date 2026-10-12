namespace ScottPlotEx.Controls;

/// <summary>
/// 滚动模式发生变化的原因。
/// </summary>
public enum ScrollingModeChangeReason
{
    /// <summary>用户（或程序）主动选择模式。</summary>
    UserRequested = 0,

    /// <summary>用户鼠标操作导致曲线停止滚动。</summary>
    UserInteraction = 1,

    /// <summary>数据被清空。</summary>
    DataCleared = 2,

    /// <summary>整体显示（自动缩放）后恢复原来的滚动模式。</summary>
    Autoscale = 3,
}
