using ScottPlotEx.Controls;

namespace ScottPlotEx.Tests;

/// <summary>需求 3、6、7 的滚动策略验证（纯逻辑）。</summary>
public class AutoScrollPolicyTests
{
    private readonly ScrollingAxisOptions _options = new();

    [Fact(DisplayName = "需求6：左键拖动后仍能看到最新数据 -> 保持滚动，X 轴最小值取当前最小值")]
    public void LeftDragPan_WithLatestDataVisible_KeepsScrolling()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Stacked, PlotViewGesture.LeftDragPan, latestDataVisible: true, _options);

        Assert.Equal(AutoScrollAction.KeepScrollingAtCurrentXMin, decision.Action);
    }

    [Fact(DisplayName = "需求6：左键拖动后看不到最新数据 -> 停止滚动")]
    public void LeftDragPan_WithoutLatestData_StopsScrolling()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Stacked, PlotViewGesture.LeftDragPan, latestDataVisible: false, _options);

        Assert.Equal(AutoScrollAction.StopScrolling, decision.Action);
    }

    [Fact(DisplayName = "需求7：中键框选放大后仍能看到最新数据 -> 保持滚动")]
    public void MiddleDragZoomRectangle_WithLatestDataVisible_KeepsScrolling()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Follow, PlotViewGesture.MiddleDragZoomRectangle, latestDataVisible: true, _options);

        Assert.Equal(AutoScrollAction.KeepScrollingAtCurrentXMin, decision.Action);
    }

    [Fact(DisplayName = "需求7：中键框选放大后看不到最新数据 -> 停止滚动")]
    public void MiddleDragZoomRectangle_WithoutLatestData_StopsScrolling()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Follow, PlotViewGesture.MiddleDragZoomRectangle, latestDataVisible: false, _options);

        Assert.Equal(AutoScrollAction.StopScrolling, decision.Action);
    }

    [Fact(DisplayName = "需求3：滚轮缩放后停止滚动")]
    public void WheelZoom_StopsScrolling()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Stacked, PlotViewGesture.WheelZoom, latestDataVisible: true, _options);

        Assert.Equal(AutoScrollAction.StopScrolling, decision.Action);
    }

    [Fact(DisplayName = "需求3：右键拖动缩放后停止滚动")]
    public void RightDragZoom_StopsScrolling()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Follow, PlotViewGesture.RightDragZoom, latestDataVisible: true, _options);

        Assert.Equal(AutoScrollAction.StopScrolling, decision.Action);
    }

    [Fact(DisplayName = "自动缩放后保持滚动并复位偏移")]
    public void Autoscale_ResetsOffset()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Stacked, PlotViewGesture.Autoscale, latestDataVisible: true, _options);

        Assert.Equal(AutoScrollAction.ResetOffset, decision.Action);
    }

    [Fact(DisplayName = "手动模式下普通鼠标操作不改变滚动状态")]
    public void ManualMode_IgnoresGestures()
    {
        foreach (PlotViewGesture gesture in Enum.GetValues<PlotViewGesture>().Where(g => g != PlotViewGesture.Autoscale))
        {
            AutoScrollDecision decision = AutoScrollPolicy.Decide(ScrollingMode.Manual, gesture, true, _options);
            Assert.Equal(AutoScrollAction.None, decision.Action);
        }
    }

    [Fact(DisplayName = "手动模式下中键单击（整体显示）→ 恢复原来的模式")]
    public void ManualMode_WithAutoscale_ResumesPreviousMode()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Manual, PlotViewGesture.Autoscale, latestDataVisible: true, _options);

        Assert.Equal(AutoScrollAction.ResumePreviousMode, decision.Action);
    }

    [Fact(DisplayName = "可以通过参数关闭“整体显示后恢复原来的模式”")]
    public void ResumeModeAfterAutoscale_CanBeDisabled()
    {
        ScrollingAxisOptions options = new() { ResumeModeAfterAutoscale = false };

        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Manual, PlotViewGesture.Autoscale, latestDataVisible: true, options);

        Assert.Equal(AutoScrollAction.None, decision.Action);
    }

    [Fact(DisplayName = "整体显示：跟随模式自动切换为堆叠模式")]
    public void ResolveAutoscaleMode_FollowSwitchesToStacked()
    {
        Assert.Equal(
            ScrollingMode.Stacked,
            AutoScrollPolicy.ResolveAutoscaleMode(ScrollingMode.Follow, ScrollingMode.Follow, _options));
    }

    [Fact(DisplayName = "整体显示：堆叠模式保持不变")]
    public void ResolveAutoscaleMode_StackedStaysStacked()
    {
        Assert.Equal(
            ScrollingMode.Stacked,
            AutoScrollPolicy.ResolveAutoscaleMode(ScrollingMode.Stacked, ScrollingMode.Stacked, _options));
    }

    [Fact(DisplayName = "整体显示：停止滚动时先回到暂停前的模式，跟随模式再切换为堆叠模式")]
    public void ResolveAutoscaleMode_ManualRestoresThenSwitches()
    {
        Assert.Equal(
            ScrollingMode.Stacked,
            AutoScrollPolicy.ResolveAutoscaleMode(ScrollingMode.Manual, ScrollingMode.Stacked, _options));

        Assert.Equal(
            ScrollingMode.Stacked,
            AutoScrollPolicy.ResolveAutoscaleMode(ScrollingMode.Manual, ScrollingMode.Follow, _options));
    }

    [Fact(DisplayName = "可以关闭“整体显示时跟随模式切换为堆叠模式”")]
    public void SwitchToStackedOnAutoscale_CanBeDisabled()
    {
        ScrollingAxisOptions options = new() { SwitchToStackedOnAutoscale = false };

        Assert.Equal(
            ScrollingMode.Follow,
            AutoScrollPolicy.ResolveAutoscaleMode(ScrollingMode.Follow, ScrollingMode.Follow, options));

        Assert.Equal(
            ScrollingMode.Follow,
            AutoScrollPolicy.ResolveAutoscaleMode(ScrollingMode.Manual, ScrollingMode.Follow, options));
    }

    [Fact(DisplayName = "参数可关闭“仍能看到最新数据就继续滚动”的行为")]
    public void KeepScrollingOption_CanBeDisabled()
    {
        ScrollingAxisOptions options = new() { KeepScrollingWhenLatestDataVisible = false };

        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Stacked, PlotViewGesture.LeftDragPan, latestDataVisible: true, options);

        Assert.Equal(AutoScrollAction.StopScrolling, decision.Action);
    }

    [Fact(DisplayName = "未改变视图的操作不改变滚动状态")]
    public void NoneGesture_DoesNothing()
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            ScrollingMode.Stacked, PlotViewGesture.None, latestDataVisible: true, _options);

        Assert.Equal(AutoScrollAction.None, decision.Action);
    }
}
