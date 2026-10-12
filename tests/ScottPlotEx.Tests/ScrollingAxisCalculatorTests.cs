using ScottPlotEx.Controls;

namespace ScottPlotEx.Tests;

/// <summary>需求 1、2 的取值计算验证。</summary>
public class ScrollingAxisCalculatorTests
{
    private readonly ScrollingAxisOptions _options = new();

    [Fact(DisplayName = "堆叠模式 X 轴范围为 0 ~ 数据点数量 + 2")]
    public void StackedMode_UsesZeroToCountPlusTwo()
    {
        bool ok = ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 100, _options, 0, 0, out double xMin, out double xMax);

        Assert.True(ok);
        Assert.Equal(0.0, xMin, 9);
        Assert.Equal(102.0, xMax, 9);
    }

    [Fact(DisplayName = "堆叠模式点数很少时范围依然有效")]
    public void StackedMode_WithSinglePoint_IsValid()
    {
        bool ok = ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 1, _options, 0, 0, out double xMin, out double xMax);

        Assert.True(ok);
        Assert.Equal(0.0, xMin, 9);
        Assert.Equal(3.0, xMax, 9);
    }

    [Fact(DisplayName = "需求6：堆叠模式下拖动只改变 X 轴最小值，最大值仍为 点数 + 2")]
    public void StackedMode_XOffsetOnlyMovesMinimum()
    {
        bool ok = ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 100, _options, 25, 0, out double xMin, out double xMax);

        Assert.True(ok);
        Assert.Equal(25.0, xMin, 9);
        Assert.Equal(102.0, xMax, 9);
    }

    [Fact(DisplayName = "堆叠模式右侧固定留白可通过参数调整")]
    public void StackedMode_RightMarginPointsIsConfigurable()
    {
        ScrollingAxisOptions options = new() { StackedRightMarginPoints = 5 };

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 10, options, 0, 0, out double xMin, out double xMax));

        Assert.Equal(0.0, xMin, 9);
        Assert.Equal(15.0, xMax, 9);
    }

    [Fact(DisplayName = "堆叠模式：设置百分比时把原点固定下来（换算一次）")]
    public void StackedMode_DisplayPercentControlsVisibleLength()
    {
        ScrollingAxisOptions options = new() { StackedDisplayPercent = 20 };
        double origin = ScrollingAxisCalculator.CalculateStackedOrigin(100, options, options.StackedDisplayPercent);

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 100, options, 0, origin, out double xMin, out double xMax));

        Assert.Equal(102.0, xMax, 9);            // 最大值仍是 点数 + 2
        Assert.Equal(102.0 * 0.8, xMin, 9);      // 只显示最后 20%
        Assert.Equal(102.0 * 0.2, xMax - xMin, 9);
    }

    [Fact(DisplayName = "堆叠模式：90% 显示 90% 的数据，且原点不随数据增长移动")]
    public void StackedMode_DisplayPercent90_ShowsMostOfData()
    {
        ScrollingAxisOptions options = new() { StackedDisplayPercent = 90 };
        double origin = ScrollingAxisCalculator.CalculateStackedOrigin(100, options, 90);

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 100, options, 0, origin, out double xMin, out double xMax));

        Assert.Equal(102.0, xMax, 9);
        Assert.Equal(10.2, xMin, 9);
        Assert.Equal(91.8, xMax - xMin, 9);      // 可见约 92 个数据点

        // 数据增长到 200 点后：原点保持 10.2 不动，右端扩展到 202
        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 200, options, 0, origin, out double laterMin, out double laterMax));

        Assert.Equal(10.2, laterMin, 9);          // 原点固定
        Assert.Equal(202.0, laterMax, 9);         // 右端随数据增长
    }

    [Fact(DisplayName = "堆叠模式：固定原点与拖动偏移可以叠加")]
    public void StackedMode_DisplayPercentWithOffset()
    {
        ScrollingAxisOptions options = new() { StackedDisplayPercent = 20 };
        double origin = ScrollingAxisCalculator.CalculateStackedOrigin(100, options, 20);

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 100, options, 5, origin, out double xMin, out double xMax));

        Assert.Equal((102.0 * 0.8) + 5, xMin, 9);
        Assert.Equal(102.0, xMax, 9);
    }

    [Fact(DisplayName = "堆叠模式：换算原点时百分比被限制在 1 ~ 100")]
    public void StackedMode_DisplayPercentIsClamped()
    {
        ScrollingAxisOptions tooSmall = new() { StackedDisplayPercent = -5 };
        ScrollingAxisOptions tooLarge = new() { StackedDisplayPercent = 150 };

        Assert.Equal(102.0 * 0.99, ScrollingAxisCalculator.CalculateStackedOrigin(100, tooSmall, tooSmall.StackedDisplayPercent), 9);
        Assert.Equal(0.0, ScrollingAxisCalculator.CalculateStackedOrigin(100, tooLarge, tooLarge.StackedDisplayPercent), 9);
    }

    [Fact(DisplayName = "跟随模式：窗口百分比 = 数据显示窗口长度")]
    public void FollowMode_WindowPercentControlsWindow()
    {
        ScrollingAxisOptions options = new() { FollowWindowPercent = 10 };

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, options, 0, 0, out double xMin, out double xMax));

        Assert.Equal(92.0, xMin, 9);  // 102 - 100×10%
        Assert.Equal(102.0, xMax, 9);
        Assert.Equal(10.0, xMax - xMin, 9);
    }

    [Fact(DisplayName = "跟随模式：固定窗口宽度（点数）优先于百分比，且宽度不随数据增长")]
    public void FollowMode_FixedWindowPointsOverridesPercent()
    {
        ScrollingAxisOptions options = new() { FollowWindowPercent = 10, FollowWindowPoints = 60 };

        foreach (int count in new[] { 10, 100, 1_000_000 })
        {
            Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
                ScrollingMode.Follow, count, options, 0, 0, out double xMin, out double xMax));

            Assert.Equal(count + 2.0, xMax, 9);
            Assert.Equal(60.0, xMax - xMin, 9); // 数据不足一个窗口时 xMin 为负（左侧留空）
        }

        options.FollowWindowPoints = 1; // 最窄 2 个数据点，保证最新数据可见
        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, options, 0, 0, out double narrowMin, out double narrowMax));
        Assert.Equal(2.0, narrowMax - narrowMin, 9);
    }

    [Fact(DisplayName = "跟随模式：窗口百分比被限制在 0.1 ~ 100，且至少 2 个数据点宽")]
    public void FollowMode_WindowPercentIsClamped()
    {
        ScrollingAxisOptions tooSmall = new() { FollowWindowPercent = 0 };
        ScrollingAxisOptions tooLarge = new() { FollowWindowPercent = 200 };

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, tooSmall, 0, 0, out double xMin1, out double xMax1));
        Assert.Equal(2.0, xMax1 - xMin1, 9); // 最窄 2 个数据点，保证最新数据可见

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, tooLarge, 0, 0, out double xMin2, out double xMax2));
        Assert.Equal(2.0, xMin2, 9);  // 100% 窗口
        Assert.Equal(102.0, xMax2, 9);
    }

    [Fact(DisplayName = "跟随模式：最新数据点始终落在显示窗口内")]
    public void FollowMode_LatestPointStaysInsideWindow()
    {
        foreach (double percent in new[] { 0.1, 1, 30, 100 })
        {
            ScrollingAxisOptions options = new() { FollowWindowPercent = percent };

            Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
                ScrollingMode.Follow, 10, options, 0, 0, out double xMin, out double xMax));

            Assert.True(10.0 >= xMin && 10.0 <= xMax, $"窗口百分比 {percent}% 时最新数据点（x=10）不在窗口 [{xMin}, {xMax}] 内。");
        }
    }

    [Fact(DisplayName = "需求2：跟随模式 X 轴为最新 30% 数据，最大值 = 数据点数量 + 2")]
    public void FollowMode_UsesTrailingThirtyPercent()
    {
        bool ok = ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, _options, 0, 0, out double xMin, out double xMax);

        Assert.True(ok);
        Assert.Equal(72.0, xMin, 9);
        Assert.Equal(102.0, xMax, 9);

        // 窗口跨度仍是数据点数量的 30%
        Assert.Equal(30.0, xMax - xMin, 9);
    }

    [Fact(DisplayName = "跟随模式：可配置右侧留白比例")]
    public void FollowMode_WithRightPadding()
    {
        ScrollingAxisOptions options = new() { FollowRightPaddingFraction = 0.10 };

        bool ok = ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, options, 0, 0, out double xMin, out double xMax);

        Assert.True(ok);
        Assert.Equal(82.0, xMin, 9);
        Assert.Equal(112.0, xMax, 9);
    }

    [Fact(DisplayName = "跟随模式：拖动只改变 X 轴最小值，最大值仍为 数据点数量 + 2")]
    public void FollowMode_XOffsetOnlyMovesMinimum()
    {
        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, _options, 5, 0, out double xMin, out double xMax));

        Assert.Equal(77.0, xMin, 9);
        Assert.Equal(102.0, xMax, 9);
    }

    [Fact(DisplayName = "跟随模式右侧固定留白可通过参数调整")]
    public void FollowMode_RightMarginPointsIsConfigurable()
    {
        ScrollingAxisOptions options = new() { FollowRightMarginPoints = 10 };

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 100, options, 0, 0, out double xMin, out double xMax));

        Assert.Equal(80.0, xMin, 9);
        Assert.Equal(110.0, xMax, 9);
    }

    [Fact(DisplayName = "手动模式：不计算 X 轴范围")]
    public void ManualMode_DoesNotCalculateWindow()
    {
        bool ok = ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Manual, 100, _options, 0, 0, out _, out _);

        Assert.False(ok);
    }

    [Fact(DisplayName = "没有数据时使用空视图范围")]
    public void EmptyData_UsesEmptyWindow()
    {
        bool ok = ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 0, _options, 0, 0, out double xMin, out double xMax);

        Assert.True(ok);
        Assert.Equal(_options.EmptyXMin, xMin, 9);
        Assert.Equal(_options.EmptyXMax, xMax, 9);
    }

    [Fact(DisplayName = "需求1、2：Y 轴范围上下各留 5%")]
    public void YWindow_AddsFivePercentPadding()
    {
        bool ok = ScrollingAxisCalculator.TryCalculateYWindow(0, 100, _options, out double yMin, out double yMax);

        Assert.True(ok);
        Assert.Equal(-5.0, yMin, 9);
        Assert.Equal(105.0, yMax, 9);
    }

    [Fact(DisplayName = "Y 轴范围：自动处理最大值/最小值顺序颠倒的情况")]
    public void YWindow_SwapsInvertedInput()
    {
        Assert.True(ScrollingAxisCalculator.TryCalculateYWindow(100, 0, _options, out double yMin, out double yMax));
        Assert.Equal(-5.0, yMin, 9);
        Assert.Equal(105.0, yMax, 9);
    }

    [Fact(DisplayName = "Y 轴范围：数据完全水平时不退化为一条线")]
    public void YWindow_FlatData_UsesMinimumHalfSpan()
    {
        Assert.True(ScrollingAxisCalculator.TryCalculateYWindow(7, 7, _options, out double yMin, out double yMax));
        Assert.Equal(7 - _options.FlatDataHalfSpan, yMin, 9);
        Assert.Equal(7 + _options.FlatDataHalfSpan, yMax, 9);
    }

    [Fact(DisplayName = "Y 轴范围：非有限数据被拒绝")]
    public void YWindow_NonFiniteData_IsRejected()
    {
        Assert.False(ScrollingAxisCalculator.TryCalculateYWindow(double.NaN, 1, _options, out _, out _));
        Assert.False(ScrollingAxisCalculator.TryCalculateYWindow(0, double.PositiveInfinity, _options, out _, out _));
    }

    [Fact(DisplayName = "非法参数下始终返回有限且非零的 X 轴范围")]
    public void XWindow_AlwaysReturnsUsableRange()
    {
        ScrollingAxisOptions options = new() { FollowWindowPercent = 0, FollowRightPaddingFraction = 0 };

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Follow, 10, options, 0, 0, out double xMin, out double xMax));

        Assert.True(double.IsFinite(xMin));
        Assert.True(double.IsFinite(xMax));
        Assert.True(xMax > xMin);
    }

    [Fact(DisplayName = "非有限的 X 轴偏移会被忽略")]
    public void XWindow_IgnoresNonFiniteOffset()
    {
        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(
            ScrollingMode.Stacked, 10, _options, double.NaN, 0, out double xMin, out double xMax));

        Assert.Equal(0.0, xMin, 9);
        Assert.Equal(12.0, xMax, 9);
    }
}
