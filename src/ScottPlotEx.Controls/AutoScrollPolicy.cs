namespace ScottPlotEx.Controls;

/// <summary>用户操作后对自动滚动采取的动作。</summary>
public enum AutoScrollAction
{
    /// <summary>不改变当前状态。</summary>
    None = 0,

    /// <summary>停止滚动（切换到手动模式）。</summary>
    StopScrolling = 1,

    /// <summary>保持滚动，并把当前 X 轴最小值作为新的基准。</summary>
    KeepScrollingAtCurrentXMin = 2,

    /// <summary>保持滚动，并复位 X 轴偏移。</summary>
    ResetOffset = 3,

    /// <summary>取消“停止滚动”，恢复停止前的滚动模式。</summary>
    ResumePreviousMode = 4,
}

/// <summary>
/// 自动滚动决策结果。
/// </summary>
public readonly record struct AutoScrollDecision(AutoScrollAction Action)
{
    /// <summary>不改变状态。</summary>
    public static AutoScrollDecision None { get; } = new(AutoScrollAction.None);

    /// <summary>停止滚动。</summary>
    public static AutoScrollDecision Stop { get; } = new(AutoScrollAction.StopScrolling);

    /// <summary>保持滚动并使用当前 X 轴最小值。</summary>
    public static AutoScrollDecision KeepAtCurrentXMin { get; } = new(AutoScrollAction.KeepScrollingAtCurrentXMin);

    /// <summary>保持滚动并复位偏移。</summary>
    public static AutoScrollDecision ResetOffset { get; } = new(AutoScrollAction.ResetOffset);

    /// <summary>恢复停止滚动之前的模式。</summary>
    public static AutoScrollDecision ResumePreviousMode { get; } = new(AutoScrollAction.ResumePreviousMode);
}

/// <summary>
/// 自动滚动策略：把“用户操作 + 是否还能看到最新数据”映射为对自动滚动的处理方式。
/// 该类型为纯逻辑，便于单独验证需求 3、6、7。
/// </summary>
public static class AutoScrollPolicy
{
    /// <summary>
    /// 计算“整体显示（中键单击 / 菜单自动缩放）”之后应该采用的滚动模式。
    /// <list type="bullet">
    /// <item>停止滚动状态：先回到暂停之前的模式。</item>
    /// <item>跟随模式：只显示最新 30% 数据，与“整体显示”冲突，因此自动切换为堆叠模式。</item>
    /// <item>堆叠模式：保持不变。</item>
    /// </list>
    /// </summary>
    /// <param name="mode">当前模式。</param>
    /// <param name="lastAutoScrollMode">停止滚动之前的自动滚动模式。</param>
    /// <param name="options">取值范围参数。</param>
    public static ScrollingMode ResolveAutoscaleMode(
        ScrollingMode mode,
        ScrollingMode lastAutoScrollMode,
        ScrollingAxisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ScrollingMode target = mode == ScrollingMode.Manual ? lastAutoScrollMode : mode;

        if (target == ScrollingMode.Follow && options.SwitchToStackedOnAutoscale)
            target = ScrollingMode.Stacked;

        return target;
    }

    /// <summary>
    /// 根据用户操作决定是否继续保持自动滚动。
    /// </summary>
    /// <param name="mode">当前滚动模式。</param>
    /// <param name="gesture">用户完成的鼠标操作。</param>
    /// <param name="latestDataVisible">操作完成后是否还能看到最新数据。</param>
    /// <param name="options">取值范围参数。</param>
    public static AutoScrollDecision Decide(
        ScrollingMode mode,
        PlotViewGesture gesture,
        bool latestDataVisible,
        ScrollingAxisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (mode == ScrollingMode.Manual)
        {
            // 中键单击 = 整体显示（自动缩放）：取消“停止滚动”，恢复原来的模式。
            return gesture == PlotViewGesture.Autoscale && options.ResumeModeAfterAutoscale
                ? AutoScrollDecision.ResumePreviousMode
                : AutoScrollDecision.None;
        }

        switch (gesture)
        {
            // 需求 6：左键拖动后，若能看见最新数据则保持模式并把 X 轴最小值设为当前最小值，否则停止滚动。
            // 需求 7：中键框选放大与左键拖动逻辑相同。
            case PlotViewGesture.LeftDragPan:
            case PlotViewGesture.MiddleDragZoomRectangle:
                if (!latestDataVisible)
                    return AutoScrollDecision.Stop;
                return options.KeepScrollingWhenLatestDataVisible
                    ? AutoScrollDecision.KeepAtCurrentXMin
                    : AutoScrollDecision.Stop;

            // 需求 3：放大局部数据（滚轮缩放、右键拖动缩放）后停止滚动。
            case PlotViewGesture.WheelZoom:
                return options.StopScrollingOnWheelZoom
                    ? AutoScrollDecision.Stop
                    : DecideLikeDrag(latestDataVisible, options);

            case PlotViewGesture.RightDragZoom:
                return options.StopScrollingOnRightDragZoom
                    ? AutoScrollDecision.Stop
                    : DecideLikeDrag(latestDataVisible, options);

            // 自动缩放会显示全部数据，此时保持模式并复位偏移。
            case PlotViewGesture.Autoscale:
                return options.ResetOffsetOnAutoscale
                    ? AutoScrollDecision.ResetOffset
                    : AutoScrollDecision.None;

            default:
                return AutoScrollDecision.None;
        }
    }

    private static AutoScrollDecision DecideLikeDrag(bool latestDataVisible, ScrollingAxisOptions options)
    {
        if (!latestDataVisible)
            return AutoScrollDecision.Stop;

        return options.KeepScrollingWhenLatestDataVisible
            ? AutoScrollDecision.KeepAtCurrentXMin
            : AutoScrollDecision.Stop;
    }
}
