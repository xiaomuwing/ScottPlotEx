namespace ScottPlotEx.Controls;

/// <summary>
/// 自动滚动模式的取值范围参数。默认值就是需求中描述的默认行为。
/// </summary>
public sealed class ScrollingAxisOptions
{
    /// <summary>堆叠模式显示长度百分比的默认值（100 = 显示全部数据，X 轴从 0 开始）。</summary>
    public const double DefaultStackedDisplayPercent = 100.0;

    /// <summary>跟随模式窗口百分比的默认值（30 = 显示最后 30% 的数据）。</summary>
    public const double DefaultFollowWindowPercent = 30.0;

    /// <summary>堆叠模式显示长度百分比的上限（1000 = X 轴长度为数据的 10 倍，曲线至少占宽度的 10%）。</summary>
    public const double MaxStackedDisplayPercent = 1000.0;

    /// <summary>
    /// 堆叠模式 X 轴显示长度百分比（1 ~ 1000）。默认 100，即从 0 开始显示全部数据，右端为“当前数据点数量 + 2”。
    /// <list type="bullet">
    /// <item>小于 100：只显示最新的这部分数据（左侧较早的数据被裁掉）。设置时把左端固定为 右端 ×（1 − 百分比），之后不随数据移动。
    /// 没有可见数据时无从裁剪：通过控件设置（<c>StackedDisplayPercent</c> / <c>SetXAxisDisplayPercent</c> / <c>ApplyAxisOptions</c>）不生效，保持 100。</item>
    /// <item>大于 100：显示全部数据并在右侧预留空白，分阶段扩展：设置时把右端固定为“（数据点数量 + 2）× 百分比”，
    /// 数据写到右端（右侧留白不足 2 个点）时再按同一比例扩展一次。例如 240 时曲线约占左侧 42%，坐标轴不会每来一个点就缩放一次。</item>
    /// </list>
    /// </summary>
    public double StackedDisplayPercent { get; set; } = DefaultStackedDisplayPercent;

    /// <summary>
    /// 堆叠模式 X 轴右侧留白比例（相对于数据点数）。默认 0，通常使用
    /// <see cref="StackedRightMarginPoints"/> 固定留白。
    /// </summary>
    public double StackedRightPaddingFraction { get; set; } = 0.0;

    /// <summary>
    /// 堆叠模式 X 轴右侧固定留白（单位：数据点）。默认 2，
    /// 即 X 轴最大值为“当前数据点数量 + 2”；时间单位下为“各曲线最新 X 的最大值 + 2 个最大的采样间隔”（跟着最快的曲线平滑前进）。
    /// </summary>
    public double StackedRightMarginPoints { get; set; } = 2.0;

    /// <summary>
    /// 跟随模式 X 轴显示窗口百分比（0.1 ~ 100）。默认 30，即 X 轴显示最后 30% 的数据窗口。
    /// </summary>
    public double FollowWindowPercent { get; set; } = DefaultFollowWindowPercent;

    /// <summary>
    /// 跟随模式固定窗口宽度（单位：数据点）。默认 0，即按 <see cref="FollowWindowPercent"/> 显示最后若干百分比的数据；
    /// 大于 0 时改为始终显示最后这么多个数据点（窗口宽度不随数据增长，相当于示波器式滚动，
    /// 例如每秒 1000 点、显示最近 60 秒即设为 60000）。数据不足一个窗口时左侧留空。
    /// </summary>
    public int FollowWindowPoints { get; set; }

    /// <summary>
    /// 跟随模式固定窗口时长（相对时间 / 绝对时间单位下使用）。默认 <see cref="TimeSpan.Zero"/>，即不按时长：
    /// 此时若 <see cref="FollowWindowPoints"/> 大于 0，则按各曲线的采样间隔把点数换算成时长（取最长的一条），否则按 <see cref="FollowWindowPercent"/>。
    /// 大于 0 时始终显示最近这么长时间的数据（示波器式滚动）。点数单位下不使用。
    /// </summary>
    public TimeSpan FollowWindowDuration { get; set; }

    /// <summary>跟随模式 X 轴右侧留白比例（相对于数据点数）。默认 0，即 X 轴最大值就是最新数据点。</summary>
    public double FollowRightPaddingFraction { get; set; } = 0.0;

    /// <summary>
    /// 跟随模式 X 轴右侧固定留白（单位：数据点）。默认 2，
    /// 即 X 轴最大值同样为“当前数据点数量 + 2”。
    /// </summary>
    public double FollowRightMarginPoints { get; set; } = 2.0;

    /// <summary>Y 轴上下留白比例（相对于数据跨度）。默认 0.05，即各向外让出 5%。</summary>
    public double YPaddingFraction { get; set; } = 0.05;

    /// <summary>数据完全水平（最大值等于最小值）时 Y 轴的最小半宽，避免 Y 轴退化成一条线。</summary>
    public double FlatDataHalfSpan { get; set; } = 0.5;

    /// <summary>判断“最新数据是否可见”时允许的边界容差（相对于 X 轴跨度）。默认 0.001。</summary>
    public double LatestVisibilityToleranceFraction { get; set; } = 0.001;

    /// <summary>滚轮缩放后是否停止滚动。默认 true（对应需求 3）。</summary>
    public bool StopScrollingOnWheelZoom { get; set; } = true;

    /// <summary>右键拖动缩放后是否停止滚动。默认 true（对应需求 3）。</summary>
    public bool StopScrollingOnRightDragZoom { get; set; } = true;

    /// <summary>左键拖动 / 中键框选放大后，若仍能看到最新数据是否保持滚动。默认 true（对应需求 6、7）。</summary>
    public bool KeepScrollingWhenLatestDataVisible { get; set; } = true;

    /// <summary>执行“自动缩放”后是否复位 X 轴偏移。默认 true。</summary>
    public bool ResetOffsetOnAutoscale { get; set; } = true;

    /// <summary>
    /// 鼠标中键单击（整体显示 / 自动缩放）后，是否取消“停止滚动”并恢复原来的模式。默认 true。
    /// </summary>
    public bool ResumeModeAfterAutoscale { get; set; } = true;

    /// <summary>
    /// 重新选择滚动模式（设置 <c>Mode</c>）时，是否把该模式的 X 轴显示长度百分比恢复为默认值。
    /// 默认 true：选择“堆叠模式”即回到 100%（显示全部数据），选择“跟随模式”即回到 30% 窗口，
    /// 避免之前调整过的百分比让视图一直停留在被裁短的状态。设为 false 可让百分比跨模式切换保持。
    /// </summary>
    public bool ResetDisplayPercentOnModeChange { get; set; } = true;

    /// <summary>
    /// 鼠标中键单击（整体显示 / 自动缩放）时，跟随模式是否自动切换为堆叠模式。默认 true。
    /// 跟随模式只显示最新 30% 数据，与“整体显示”冲突，因此默认切换为可显示全部数据的堆叠模式。
    /// </summary>
    public bool SwitchToStackedOnAutoscale { get; set; } = true;

    /// <summary>
    /// 鼠标手势（按住并拖动：左键平移 / 中键框选 / 右键缩放）进行中时是否暂停自动滚动。默认 true：
    /// 手势过程中到来的新数据不移动坐标轴，避免视图被拉回自动窗口造成一次卡顿、并覆盖用户的拖动结果；
    /// 松开鼠标后仍按 <see cref="AutoScrollPolicy"/> 的规则决定继续滚动或停止滚动。
    /// </summary>
    public bool PauseAutoScrollDuringGesture { get; set; } = true;

    /// <summary>没有数据时 X 轴的最小值。</summary>
    public double EmptyXMin { get; set; } = 0.0;

    /// <summary>没有数据时 X 轴的最大值。</summary>
    public double EmptyXMax { get; set; } = 1.0;

    /// <summary>没有数据时 Y 轴的最小值。</summary>
    public double EmptyYMin { get; set; } = -0.5;

    /// <summary>没有数据时 Y 轴的最大值。</summary>
    public double EmptyYMax { get; set; } = 0.5;

    /// <summary>复制一份参数。</summary>
    public ScrollingAxisOptions Clone() => (ScrollingAxisOptions)MemberwiseClone();

    /// <summary>
    /// 校验参数：数值必须是有限值；留白比例、留白点数、容差不能为负；数据水平时的最小半宽必须为正；
    /// 空视图的最大值必须大于最小值。百分比越界不算错误（使用时自动限制在有效范围内）。
    /// </summary>
    /// <exception cref="ArgumentException">参数无效时抛出，消息中给出参数名。</exception>
    public void Validate()
    {
        RequireNonNegative(StackedRightPaddingFraction, nameof(StackedRightPaddingFraction));
        RequireNonNegative(StackedRightMarginPoints, nameof(StackedRightMarginPoints));
        RequireNonNegative(FollowRightPaddingFraction, nameof(FollowRightPaddingFraction));
        RequireNonNegative(FollowRightMarginPoints, nameof(FollowRightMarginPoints));
        RequireNonNegative(YPaddingFraction, nameof(YPaddingFraction));
        RequireNonNegative(LatestVisibilityToleranceFraction, nameof(LatestVisibilityToleranceFraction));

        if (!double.IsFinite(FlatDataHalfSpan) || FlatDataHalfSpan <= 0)
            throw new ArgumentException($"{nameof(FlatDataHalfSpan)} 必须是正的有限值（当前 {FlatDataHalfSpan}）。", nameof(FlatDataHalfSpan));

        RequireRange(EmptyXMin, EmptyXMax, nameof(EmptyXMin), nameof(EmptyXMax));
        RequireRange(EmptyYMin, EmptyYMax, nameof(EmptyYMin), nameof(EmptyYMax));

        static void RequireNonNegative(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentException($"{name} 必须是非负的有限值（当前 {value}）。", name);
        }

        static void RequireRange(double min, double max, string minName, string maxName)
        {
            if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
                throw new ArgumentException($"{maxName} 必须大于 {minName}，且两者都是有限值（当前 {minName} = {min}、{maxName} = {max}）。", maxName);
        }
    }

    /// <summary>把堆叠模式显示长度百分比限制在 1 ~ <see cref="MaxStackedDisplayPercent"/>（非有限值按默认 100 处理）。</summary>
    public static double ClampDisplayPercent(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 1.0, MaxStackedDisplayPercent) : DefaultStackedDisplayPercent;

    /// <summary>把跟随模式窗口百分比限制在 0.1 ~ 100（非有限值按默认 30 处理）。</summary>
    public static double ClampWindowPercent(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 0.1, 100.0) : DefaultFollowWindowPercent;
}
