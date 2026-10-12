namespace ScottPlotEx.Controls;

/// <summary>
/// 计算自动滚动模式下坐标轴的取值范围（纯函数，便于单元测试）。
/// <para>
/// 计算在 X 坐标上进行，每条曲线用 <see cref="SeriesXExtent"/>（最新 X 与一个数据点的 X 宽度）描述：
/// 点数单位下最新 X = 点数、宽度 = 1；时间单位下最新 X = 距参考时刻的秒数、宽度 = 采样间隔。
/// “点数 + 2”因此推广为“各曲线最新 X 的最大值 + 2 个最大的数据点宽度”：右端跟着最快的曲线平滑前进，
/// 不会随采样间隔大的慢曲线一格一格跳；慢曲线的最后一个点（最多落后最新数据一个间隔）仍在留白之内。
/// 点数单位下所有宽度都是 1，结果与“最长曲线点数 + 2”逐位相同。
/// </para>
/// </summary>
public static class ScrollingAxisCalculator
{
    /// <summary>
    /// 计算 X 轴范围（单条曲线、点数单位：第 n 个点 X = n）。
    /// </summary>
    /// <param name="mode">滚动模式。</param>
    /// <param name="count">已添加的数据点数。</param>
    /// <param name="options">取值范围参数。</param>
    /// <param name="xOffset">X 轴偏移量（用户拖动后保留的 X 轴最小值偏移）。</param>
    /// <param name="stackedOriginX">
    /// 堆叠模式固定的 X 轴原点（由 <see cref="CalculateStackedOrigin(int, ScrollingAxisOptions, double)"/> 在设置百分比时换算一次）。
    /// 该值不随数据增长而移动，因此曲线是"从固定原点向右堆叠"。
    /// </param>
    /// <param name="xMin">计算得到的 X 轴最小值。</param>
    /// <param name="xMax">计算得到的 X 轴最大值。</param>
    /// <returns>手动模式返回 false（不修改坐标轴），其余情况返回 true。</returns>
    public static bool TryCalculateXWindow(
        ScrollingMode mode,
        int count,
        ScrollingAxisOptions options,
        double xOffset,
        double stackedOriginX,
        out double xMin,
        out double xMax) =>
        TryCalculateXWindow(mode, SamplesExtent(count), XAxisUnit.Samples, options, xOffset, stackedOriginX, out xMin, out xMax);

    /// <summary>
    /// 计算 X 轴范围（多条曲线、任意 X 轴单位）。
    /// </summary>
    /// <param name="mode">滚动模式。</param>
    /// <param name="series">参与计算的曲线（可见且有数据）；为空时返回空视图。</param>
    /// <param name="unit">X 轴单位：决定跟随模式使用点数窗口还是时间窗口（<see cref="ScrollingAxisOptions.FollowWindowDuration"/>）。</param>
    /// <param name="options">取值范围参数。</param>
    /// <param name="xOffset">X 轴偏移量（用户拖动后保留的 X 轴最小值偏移）。</param>
    /// <param name="stackedOriginX">堆叠模式固定的 X 轴原点（见 <see cref="CalculateStackedOrigin(ReadOnlySpan{SeriesXExtent}, ScrollingAxisOptions, double)"/>）。</param>
    /// <param name="xMin">计算得到的 X 轴最小值。</param>
    /// <param name="xMax">计算得到的 X 轴最大值。</param>
    /// <returns>手动模式返回 false（不修改坐标轴），其余情况返回 true。</returns>
    public static bool TryCalculateXWindow(
        ScrollingMode mode,
        ReadOnlySpan<SeriesXExtent> series,
        XAxisUnit unit,
        ScrollingAxisOptions options,
        double xOffset,
        double stackedOriginX,
        out double xMin,
        out double xMax)
    {
        ArgumentNullException.ThrowIfNull(options);

        xMin = 0;
        xMax = 0;

        if (mode == ScrollingMode.Manual)
            return false;

        if (!double.IsFinite(xOffset))
            xOffset = 0;

        if (!double.IsFinite(stackedOriginX))
            stackedOriginX = 0;

        if (series.IsEmpty)
        {
            xMin = options.EmptyXMin + xOffset;
            xMax = options.EmptyXMax + xOffset;
        }
        else
        {
            switch (mode)
            {
                case ScrollingMode.Stacked:
                    // 原点由调用方固定（设置百分比那一刻换算一次，之后不再随数据刷新移动）；
                    // 右端始终为“最新 X 的最大值 + 2 个最大的数据点宽度”（点数单位即“数据点数量 + 2”），因此曲线从固定原点向右堆叠。
                    // 用户拖动后（需求 6）只保持 X 轴最小值，最大值仍为右端。
                    xMax = CalculateStackedRightEdge(series, options);
                    xMin = stackedOriginX + xOffset;
                    break;

                case ScrollingMode.Follow:
                    // X 轴显示最后 N% 的数据窗口（默认 30%）、固定的最后 N 个数据点或最近一段时间，
                    // 最大值同样为“最新 X 的最大值 + 2 个最大的数据点宽度”；窗口最少 2 个数据点宽（时间单位见 CalculateFollowWindow），保证最新数据点始终落在窗口内
                    xMax = RightEdge(series, options.FollowRightPaddingFraction, options.FollowRightMarginPoints);
                    xMin = xMax - CalculateFollowWindow(series, unit, options) + xOffset;
                    break;

                default:
                    return false;
            }
        }

        // 兜底：保证范围始终有限且非零，避免渲染异常
        if (!double.IsFinite(xMin) || !double.IsFinite(xMax) || xMax <= xMin)
        {
            if (!double.IsFinite(xMin))
                xMin = 0;

            xMax = xMin + 1;
        }

        return true;
    }

    /// <summary>堆叠模式 X 轴右端位置：数据点数量 + 2（默认参数下）。</summary>
    public static double CalculateStackedRightEdge(int count, ScrollingAxisOptions options) =>
        CalculateStackedRightEdge(SamplesExtent(count), options);

    /// <summary>
    /// 堆叠模式 X 轴右端位置：各曲线最新 X 的最大值 + 2 个最大的数据点宽度（默认参数下）。
    /// 点数单位即“数据点数量 + 2”，时间单位即“最新 X + 2 个最大的采样间隔”。
    /// </summary>
    public static double CalculateStackedRightEdge(ReadOnlySpan<SeriesXExtent> series, ScrollingAxisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (series.IsEmpty)
            return options.StackedRightMarginPoints;

        return RightEdge(series, options.StackedRightPaddingFraction, options.StackedRightMarginPoints);
    }

    /// <summary>
    /// 把“X 轴显示长度百分比”换算成堆叠模式固定的原点位置。
    /// 该换算只应在设置百分比的那一刻执行一次：之后原点固定不动、曲线向右堆叠
    /// （若每次数据刷新都重算，原点会随数据增长而移动，看起来就像跟随模式）。
    /// </summary>
    /// <param name="count">执行换算时的数据点数量。</param>
    /// <param name="options">取值范围参数。</param>
    /// <param name="displayPercent">显示长度百分比（1 ~ 100，100 = 从 0 开始显示全部数据）。</param>
    public static double CalculateStackedOrigin(int count, ScrollingAxisOptions options, double displayPercent) =>
        CalculateStackedOrigin(SamplesExtent(count), options, displayPercent);

    /// <summary>
    /// 把“X 轴显示长度百分比”换算成堆叠模式固定的原点位置（多条曲线、任意单位）：原点 = 右端 ×（1 − 百分比）。
    /// </summary>
    /// <param name="series">执行换算时参与计算的曲线。</param>
    /// <param name="options">取值范围参数。</param>
    /// <param name="displayPercent">显示长度百分比（1 ~ 100，100 = 从 0 开始显示全部数据）。</param>
    public static double CalculateStackedOrigin(ReadOnlySpan<SeriesXExtent> series, ScrollingAxisOptions options, double displayPercent)
    {
        double right = CalculateStackedRightEdge(series, options);
        return right * (1.0 - (ScrollingAxisOptions.ClampDisplayPercent(displayPercent) / 100.0));
    }

    /// <summary>
    /// 跟随模式的窗口宽度（X 单位）：
    /// 时间单位且 <see cref="ScrollingAxisOptions.FollowWindowDuration"/> 大于 0 → 该时长（秒）；
    /// 否则 <see cref="ScrollingAxisOptions.FollowWindowPoints"/> 大于 0 → 点数 × 各曲线的数据点宽度（取最大）；
    /// 否则 → 最新 X × <see cref="ScrollingAxisOptions.FollowWindowPercent"/>。
    /// 结果至少为 2 个数据点宽（取各曲线中最宽的数据点），保证最新数据点始终可见；
    /// 时间单位下至少为（右侧留白点数 + 1）个最宽的数据点：右端只按最宽的数据点留白，
    /// 采样间隔最大的曲线的最后一个点最多落后最新数据一个间隔，这样它也始终在窗口内。
    /// </summary>
    public static double CalculateFollowWindow(ReadOnlySpan<SeriesXExtent> series, XAxisUnit unit, ScrollingAxisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        double latestX = double.NegativeInfinity;
        double maxStep = 0;

        foreach (SeriesXExtent extent in series)
        {
            latestX = Math.Max(latestX, extent.LatestX);
            maxStep = Math.Max(maxStep, extent.StepX);
        }

        if (series.IsEmpty)
        {
            latestX = 0;
            maxStep = 1;
        }

        double window;

        if (unit != XAxisUnit.Samples && options.FollowWindowDuration > TimeSpan.Zero)
            window = options.FollowWindowDuration.TotalSeconds;
        else if (options.FollowWindowPoints > 0)
            window = options.FollowWindowPoints * maxStep;
        else
            window = latestX * (ScrollingAxisOptions.ClampWindowPercent(options.FollowWindowPercent) / 100.0);

        double minimumSteps = unit == XAxisUnit.Samples ? 2.0 : Math.Max(2.0, options.FollowRightMarginPoints + 1);
        return Math.Max(window, minimumSteps * maxStep);
    }

    /// <summary>
    /// 计算 Y 轴范围：以数据最小值为下界、最大值为上界，各向外让出
    /// <see cref="ScrollingAxisOptions.YPaddingFraction"/>（默认 5%）。
    /// </summary>
    /// <returns>数据不是有限值时返回 false。</returns>
    public static bool TryCalculateYWindow(
        double dataMin,
        double dataMax,
        ScrollingAxisOptions options,
        out double yMin,
        out double yMax)
    {
        ArgumentNullException.ThrowIfNull(options);

        yMin = 0;
        yMax = 0;

        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
            return false;

        if (dataMax < dataMin)
            (dataMin, dataMax) = (dataMax, dataMin);

        double span = dataMax - dataMin;
        double padding = span > 0
            ? span * options.YPaddingFraction
            : Math.Max(Math.Abs(dataMin) * options.YPaddingFraction, options.FlatDataHalfSpan);

        if (!double.IsFinite(padding) || padding < 0)
            padding = 0;

        yMin = dataMin - padding;
        yMax = dataMax + padding;

        if (!double.IsFinite(yMin) || !double.IsFinite(yMax) || yMax <= yMin)
            return false;

        return true;
    }

    /// <summary>
    /// 右端 = 最新 X 的最大值 ×（1 + 留白比例）+ 留白点数 × 最大的数据点宽度。
    /// 留白按最宽的数据点而不是按“最新的那条曲线”计算：多条曲线交替成为最新时，右端只随最新 X 连续前进，不会来回跳。
    /// </summary>
    private static double RightEdge(ReadOnlySpan<SeriesXExtent> series, double paddingFraction, double marginPoints)
    {
        double latestX = double.NegativeInfinity;
        double maxStep = 0;

        foreach (SeriesXExtent extent in series)
        {
            latestX = Math.Max(latestX, extent.LatestX);
            maxStep = Math.Max(maxStep, extent.StepX);
        }

        return (latestX * (1 + paddingFraction)) + (marginPoints * maxStep);
    }

    /// <summary>点数单位的单条曲线：最新 X = 点数，数据点宽度 = 1。</summary>
    private static SeriesXExtent[] SamplesExtent(int count) => count > 0 ? [new SeriesXExtent(count, 1.0)] : [];
}
