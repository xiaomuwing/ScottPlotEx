namespace ScottPlotEx.Controls;

/// <summary>
/// 用户在绘图区完成的鼠标操作类型。
/// </summary>
public enum PlotViewGesture
{
    /// <summary>没有改变视图的操作（例如单纯点击）。</summary>
    None = 0,

    /// <summary>左键拖动平移（ScottPlot 默认行为）。</summary>
    LeftDragPan = 1,

    /// <summary>中键拖动框选放大（ScottPlot 默认行为）。</summary>
    MiddleDragZoomRectangle = 2,

    /// <summary>右键拖动缩放（ScottPlot 默认行为）。</summary>
    RightDragZoom = 3,

    /// <summary>鼠标滚轮缩放。</summary>
    WheelZoom = 4,

    /// <summary>自动缩放（中键单击，或右键菜单中的“自动缩放”）。</summary>
    Autoscale = 5,
}
