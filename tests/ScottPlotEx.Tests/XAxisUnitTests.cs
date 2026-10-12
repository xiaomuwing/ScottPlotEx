using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ScottPlot;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// X 轴单位（点数 / 相对时间 / 绝对时间，对标 imc 曲线窗口）：每条曲线自己的采样间隔与起始时间、
/// 堆叠 / 跟随（点数窗口与时间窗口）/ 拖动后继续滚动 / 中键整体显示下的坐标轴范围与 Y 轴统计、
/// 采样间隔不同的多曲线在时间上对齐、运行中切换单位、刻度标签格式、最近点查询返回的 X。
/// </summary>
public class XAxisUnitTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 8, 0, 0);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    #region 计算器（纯函数）

    [Fact(DisplayName = "X 轴单位·计算器：时间单位下堆叠右端 = 最新 X 的最大值 + 2 个最大的采样间隔，原点按右端换算")]
    public void Calculator_StackedRightEdge_UsesNewestXAndLargestInterval()
    {
        ScrollingAxisOptions options = new();
        SeriesXExtent[] series = [new(0.995, 0.005), new(0.98, 0.02), new(0.9, 0.1)];

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Stacked, series, XAxisUnit.RelativeTime, options, 0, 0, out double xMin, out double xMax));
        Assert.Equal(0, xMin, 9);
        Assert.Equal(0.995 + 0.2, xMax, 9); // 最快曲线的最新 X + 2 × 最大间隔 0.1；慢曲线最后一点 0.9 仍在留白之内

        Assert.Equal(1.195, ScrollingAxisCalculator.CalculateStackedRightEdge(series, options), 9);
        Assert.Equal(1.195 * 0.5, ScrollingAxisCalculator.CalculateStackedOrigin(series, options, 50), 9);

        // 点数单位的单曲线：与原接口逐位一致（“点数 + 2”）
        SeriesXExtent[] samples = [new(100, 1)];
        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Follow, samples, XAxisUnit.Samples, options, 0, 0, out double sMin, out double sMax));
        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Follow, 100, options, 0, 0, out double lMin, out double lMax));
        Assert.Equal(lMin, sMin);
        Assert.Equal(lMax, sMax);
    }

    [Theory(DisplayName = "X 轴单位·计算器：跟随窗口——时间窗口 / 点数按各曲线采样间隔换算 / 百分比 / 最小宽度")]
    [InlineData(XAxisUnit.RelativeTime, 5.0, 0, 100.02 - 5)]        // 时间窗口 5 秒
    [InlineData(XAxisUnit.AbsoluteTime, 5.0, 0, 100.02 - 5)]        // 绝对时间同样按时长
    [InlineData(XAxisUnit.RelativeTime, 0.0, 1000, 100.02 - 10)]    // 1000 点：取最宽的数据点 0.01 → 10 秒
    [InlineData(XAxisUnit.RelativeTime, 0.0, 0, 100.02 - 30)]       // 百分比：最新 X 100 × 30%
    [InlineData(XAxisUnit.Samples, 5.0, 0, 100.02 - 30)]            // 点数单位不使用时间窗口
    [InlineData(XAxisUnit.RelativeTime, 0.001, 0, 100.02 - 0.03)]   // 时间单位最少（留白 2 + 1）个最宽的数据点：慢曲线最后一点仍在窗口内
    [InlineData(XAxisUnit.Samples, 0.0, 1, 100.02 - 0.02)]          // 点数单位最少 2 个数据点宽（与原来相同）
    public void Calculator_FollowWindow(XAxisUnit unit, double durationSeconds, int points, double expectedXMin)
    {
        ScrollingAxisOptions options = new()
        {
            FollowWindowDuration = TimeSpan.FromSeconds(durationSeconds),
            FollowWindowPoints = points,
        };

        SeriesXExtent[] series = [new(100.0, 0.001), new(99.99, 0.01)];

        Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Follow, series, unit, options, 0, 0, out double xMin, out double xMax));
        Assert.Equal(100.02, xMax, 9); // 最新 X 100 + 2 × 最大间隔 0.01
        Assert.Equal(expectedXMin, xMin, 9);

        // 时间单位：采样间隔大的曲线的最后一点始终在窗口内（点数单位保持原来的“最少 2 个点”，不受影响）
        if (unit != XAxisUnit.Samples)
            Assert.True(xMin <= 99.99, "采样间隔大的曲线的最后一点应在窗口内。");
    }

    [Fact(DisplayName = "X 轴单位·计算器：多条曲线交替写入时右端随最快的曲线平滑前进，不随慢曲线一格一格跳")]
    public void Calculator_RightEdge_AdvancesSmoothly()
    {
        ScrollingAxisOptions options = new() { FollowWindowDuration = TimeSpan.FromSeconds(1) };
        List<double> stacked = [];
        List<double> follow = [];

        // 实时交替写入：快曲线 200 点/秒，慢曲线 10 点/秒（每 20 个快点来 1 个慢点），共 2 秒
        for (int fast = 200; fast <= 600; fast++)
        {
            int slow = fast / 20;
            SeriesXExtent[] series = [new((fast - 1) * 0.005, 0.005), new((slow - 1) * 0.1, 0.1)];

            Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Stacked, series, XAxisUnit.RelativeTime, options, 0, 0, out _, out double stackedMax));
            Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Follow, series, XAxisUnit.RelativeTime, options, 0, 0, out double followMin, out double followMax));
            stacked.Add(stackedMax);
            follow.Add(followMax);

            // 慢曲线的最后一点始终在跟随窗口内
            Assert.InRange((slow - 1) * 0.1, followMin, followMax);
        }

        // 每来一个快点右端前进一个快曲线间隔（0.005 秒），慢点到达时不跳
        for (int i = 1; i < stacked.Count; i++)
        {
            Assert.Equal(0.005, stacked[i] - stacked[i - 1], 9);
            Assert.Equal(0.005, follow[i] - follow[i - 1], 9);
        }
    }

    [Fact(DisplayName = "X 轴单位·计算器：点数单位下多条曲线的结果与原来“最长曲线点数”的公式逐位相同")]
    public void Calculator_SamplesUnit_IsBitIdenticalToOriginalFormula()
    {
        Random random = new(7);
        ScrollingAxisOptions options = new() { StackedRightPaddingFraction = 0.013, FollowRightPaddingFraction = 0.007, FollowWindowPercent = 17 };

        for (int round = 0; round < 300; round++)
        {
            int[] counts = [.. Enumerable.Range(0, random.Next(1, 6)).Select(_ => random.Next(1, 2_000_000))];
            SeriesXExtent[] series = [.. counts.Select(c => new SeriesXExtent(c, 1.0))];
            options.FollowWindowPoints = round % 3 == 0 ? random.Next(1, 100_000) : 0;
            int count = counts.Max();
            double offset = random.NextDouble() * 100;
            const double Origin = 12.5;

            // 改动前的实现（按最长曲线的点数）原样写在这里作为对照
            double stackedMax = (count * (1 + options.StackedRightPaddingFraction)) + options.StackedRightMarginPoints;
            double followMax = (count * (1 + options.FollowRightPaddingFraction)) + options.FollowRightMarginPoints;
            double window = Math.Max(
                options.FollowWindowPoints > 0 ? options.FollowWindowPoints : count * (ScrollingAxisOptions.ClampWindowPercent(options.FollowWindowPercent) / 100.0),
                2.0);

            Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Stacked, series, XAxisUnit.Samples, options, offset, Origin, out double xMin, out double xMax));
            Assert.Equal(Origin + offset, xMin);
            Assert.Equal(stackedMax, xMax);

            Assert.True(ScrollingAxisCalculator.TryCalculateXWindow(ScrollingMode.Follow, series, XAxisUnit.Samples, options, offset, Origin, out xMin, out xMax));
            Assert.Equal(followMax - window + offset, xMin);
            Assert.Equal(followMax, xMax);
        }
    }

    [Fact(DisplayName = "X 轴单位·缓冲区：按曲线自己的 X 映射把 X 窗口换算成下标区间（含浮点误差容差）")]
    public void Buffer_ConvertsXWindowWithItsOwnMapping()
    {
        PlotDataBuffer buffer = new() { XStart = 2.0, XStep = 0.1 };
        buffer.AddRange([.. Enumerable.Range(0, 10).Select(i => (double)i)]);

        Assert.Equal(2.9, buffer.LatestX, 12);
        Assert.Equal(2.3, buffer.GetX(3), 12);
        Assert.Equal(5.0, buffer.GetIndex(2.5), 9);

        // X 2.3 ~ 2.5 恰好落在下标 3 ~ 5 上（2.3 / 0.1 之类的换算有舍入误差）
        Assert.True(buffer.TryGetIndexRange(2.3, 2.5, out int start, out int end));
        Assert.Equal((3, 5), (start, end));
        Assert.True(buffer.TryGetValueRange(2.3, 2.5, out double min, out double max));
        Assert.Equal((3.0, 5.0), (min, max));

        // 窗口落在两个数据点之间：没有数据点
        Assert.False(buffer.TryGetValueRange(2.31, 2.39, out _, out _));
    }

    #endregion

    #region 刻度标签格式

    [Theory(DisplayName = "X 轴单位·刻度：相对时间按时长显示为 秒 / 分:秒 / 时:分:秒 / 天")]
    [InlineData(12.5, 0.5, 30, "12.5 s")]
    [InlineData(-5, 1, 30, "-5 s")]
    [InlineData(0.0004, 0.001, 0.01, "0.000 s")]
    [InlineData(125, 5, 200, "2:05")]
    [InlineData(125.25, 0.05, 200, "2:05.25")]
    [InlineData(3725, 60, 4000, "1:02:05")]
    [InlineData((3 * 86400) + (4 * 3600), 3600, 4 * 86400, "3天 04:00")]
    [InlineData((3 * 86400) + (4 * 3600) + 5, 5, 4 * 86400, "3天 04:00:05")]
    [InlineData(2 * 86400, 86400, 4 * 86400, "2天")]
    public void RelativeLabels(double seconds, double spacing, double rangeMaxAbs, string expected) =>
        Assert.Equal(expected, TimeAxisTickGenerator.FormatRelative(seconds, spacing, rangeMaxAbs));

    [Theory(DisplayName = "X 轴单位·刻度：绝对时间按刻度间距显示 时:分:秒(.毫秒) / 时:分，第一个刻度与日期变化处显示日期")]
    [InlineData(5, false, "08:00:05")]
    [InlineData(5, true, "08:00:05\n2026-10-12")]
    [InlineData(0.5, false, "08:00:05.2")]
    [InlineData(0.001, false, "08:00:05.250")]
    [InlineData(600, true, "08:00\n2026-10-12")]
    [InlineData(2 * 86400, true, "2026-10-12")]
    public void AbsoluteLabels(double spacing, bool includeDate, string expected) =>
        Assert.Equal(expected, TimeAxisTickGenerator.FormatAbsolute(T0.AddSeconds(5.25), spacing, includeDate));

    [Fact(DisplayName = "X 轴单位·刻度：控件绝对时间刻度落在整秒上，跨过午夜时标出新日期；相对时间显示 分:秒")]
    public void RenderedTickLabels_AbsoluteAndRelative()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(1000, 500));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.AbsoluteTime;
            plot.DefaultStartTime = new DateTime(2026, 10, 12, 23, 59, 30);
            plot.DefaultSeries.SampleRate = 10;
            plot.AddValues(Enumerable.Range(0, 600).Select(i => Math.Sin(i / 20.0))); // 60 秒，跨过午夜

            Assert.IsType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);
            Tick[] ticks = RenderMajorTicks(plot);

            Assert.True(ticks.Length >= 3, $"刻度太少：{ticks.Length}");
            Assert.All(LabeledTicks(ticks), t => Assert.Matches(@"^\d\d:\d\d:\d\d(\n\d{4}-\d\d-\d\d)?$", t.Label));
            Assert.EndsWith("\n2026-10-12", ticks[0].Label);
            Assert.Contains(ticks, t => t.Label == "00:00:00\n2026-10-13");

            // 刻度落在整秒上（参考时刻 23:59:30 + X）
            Assert.All(ticks, t => Assert.Equal(Math.Round(t.Position), t.Position, 6));

            plot.XAxisUnit = XAxisUnit.RelativeTime; // X 范围 0 ~ 60.1 秒：分:秒
            ticks = RenderMajorTicks(plot);
            Assert.Contains(ticks, t => t.Label == "0:00");
            Assert.Contains(ticks, t => t.Label == "0:30");
            Assert.All(LabeledTicks(ticks), t => Assert.Matches(@"^\d+:\d\d$", t.Label));
        });

        host.AssertNoUnhandledExceptions();

        // 紧贴右端、标签会伸出绘图区的最后一个刻度只保留刻度线（标签为空），其余刻度都有标签
        static IEnumerable<Tick> LabeledTicks(Tick[] ticks)
        {
            Assert.All(ticks[..^1], t => Assert.NotEqual(string.Empty, t.Label));
            return ticks.Where(t => t.Label.Length > 0);
        }
    }

    [Fact(DisplayName = "X 轴单位·刻度：紧贴坐标轴末端、会被裁掉一半的标签不显示，离末端足够远时照常显示")]
    public void EdgeLabel_IsHiddenOnlyWhenClipped()
    {
        using Plot plot = new();
        TimeAxisTickGenerator generator = new(absolute: true, new DateTime(2026, 10, 12, 8, 0, 0));
        ScottPlot.LabelStyle style = plot.Axes.Bottom.TickLabelStyle;
        using ScottPlot.Paint paint = ScottPlot.Paint.NewDisposablePaint();

        // 0 ~ 40.1 秒、1000 像素：40 秒的刻度离右端只有 2.5 像素
        generator.Regenerate(new CoordinateRange(0, 40.1), Edge.Bottom, new PixelLength(1000), paint, style);
        Tick[] ticks = [.. generator.Ticks.Where(t => t.IsMajor)];
        Assert.Equal(40, ticks[^1].Position, 9);
        Assert.Equal(string.Empty, ticks[^1].Label);

        // 0 ~ 42 秒：离右端 2 秒（约 48 像素），标签照常显示
        generator.Regenerate(new CoordinateRange(0, 42), Edge.Bottom, new PixelLength(1000), paint, style);
        ticks = [.. generator.Ticks.Where(t => t.IsMajor)];
        Assert.Equal(40, ticks[^1].Position, 9);
        Assert.Equal("08:00:40", ticks[^1].Label);
    }

    #endregion

    #region 单条曲线：堆叠 / 跟随 / 百分比

    [Fact(DisplayName = "X 轴单位·默认点数单位：采样间隔不影响 X（第 n 个点 X = n），刻度生成器保持 ScottPlot 默认")]
    public void SamplesUnit_IgnoresSampleInterval()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            Assert.Equal(XAxisUnit.Samples, plot.XAxisUnit);
            Assert.Equal(TimeSpan.FromSeconds(1), plot.DefaultSampleInterval);
            Assert.Equal(TimeSpan.Zero, plot.FollowWindowDuration);
            Assert.IsNotType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);

            plot.DefaultSeries.SampleRate = 1000;
            plot.DefaultStartTime = T0;
            plot.AddValues(Enumerable.Range(0, 10).Select(i => (double)i));

            Assert.Equal(10, plot.LatestX);
            Assert.Equal(1, plot.Signal!.Data.Period);
            Assert.Equal(1, plot.Signal.Data.XOffset);
            Assert.Equal(12, PlotTestHelper.GetXRange(plot).Max, 9);
            Assert.Equal(TimeSpan.FromMilliseconds(1), plot.DefaultSeries.SampleInterval);

            Assert.Throws<ArgumentOutOfRangeException>(() => plot.DefaultSampleInterval = TimeSpan.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => plot.DefaultSeries.SampleInterval = TimeSpan.FromSeconds(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => plot.DefaultSeries.SampleRate = 0);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Theory(DisplayName = "X 轴单位·单曲线：堆叠 / 显示百分比 / 跟随时间窗口 / 跟随点数窗口的 X 范围与按窗口统计的 Y 轴")]
    [InlineData(XAxisUnit.RelativeTime)]
    [InlineData(XAxisUnit.AbsoluteTime)]
    public void SingleSeries_AxisRanges(XAxisUnit unit)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = unit;
            plot.DefaultSeries.StartTime = T0;
            plot.DefaultSeries.SampleRate = 100;

            // 前 5 秒 100 ~ 109，后 5 秒 0 ~ 9
            plot.AddValues(Enumerable.Range(0, 1000).Select(i => (i < 500 ? 100.0 : 0.0) + (i % 10)));

            Assert.Equal(T0, plot.TimeReference);
            Assert.Equal(9.99, plot.LatestX, 9);
            Assert.Equal(0.01, plot.Signal!.Data.Period, 12);
            Assert.Equal(0.0, plot.Signal.Data.XOffset, 12);

            // 堆叠：X [0, 最新 X + 2 × 采样间隔]，Y 统计全部数据
            AssertX(plot, 0, 9.99 + 0.02);
            AssertY(plot, 0, 109);

            // 堆叠显示 50%：原点固定在右端的一半，Y 只统计 X ≥ 5.005 的数据（下标 501 起，0 ~ 9）
            plot.SetXAxisDisplayPercent(50, ScrollingMode.Stacked);
            AssertX(plot, 10.01 * 0.5, 10.01);
            AssertY(plot, 0, 9);

            // 跟随 + 时间窗口 2 秒
            plot.FollowWindowDuration = TimeSpan.FromSeconds(2);
            plot.Mode = ScrollingMode.Follow;
            AssertX(plot, 10.01 - 2, 10.01);
            AssertY(plot, 0, 9);

            // 窗口 7 秒：覆盖到前 5 秒的数据
            plot.FollowWindowDuration = TimeSpan.FromSeconds(7);
            AssertX(plot, 10.01 - 7, 10.01);
            AssertY(plot, 0, 109);

            // 没有时间窗口时，点数窗口按采样间隔换算成时长：300 点 = 3 秒
            plot.FollowWindowDuration = TimeSpan.Zero;
            plot.FollowWindowPoints = 300;
            AssertX(plot, 10.01 - 3, 10.01);

            // 重新选择跟随模式不复位窗口
            plot.FollowWindowDuration = TimeSpan.FromSeconds(2);
            plot.Mode = ScrollingMode.Follow;
            Assert.Equal(TimeSpan.FromSeconds(2), plot.FollowWindowDuration);
            AssertX(plot, 10.01 - 2, 10.01);

            // 数据继续增长：窗口宽度不变，右端跟着最新数据
            plot.AddValues(Enumerable.Range(0, 100).Select(_ => 50.0));
            AssertX(plot, 11.01 - 2, 11.01);
            AssertY(plot, 0, 50);
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 多曲线：采样间隔不同、在时间上对齐

    [Fact(DisplayName = "X 轴单位·多曲线：采样间隔不同的曲线在时间单位下对齐（点数单位下按序号）")]
    public void SeriesWithDifferentIntervals_AlignInTime()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.DefaultStartTime = T0;
            PlotSeries slow = plot.AddSeries("慢");
            PlotSeries mid = plot.AddSeries("中");
            PlotSeries fast = plot.AddSeries("快");
            slow.SampleRate = 10;
            mid.SampleInterval = TimeSpan.FromMilliseconds(20);
            fast.SampleRate = 200;

            // 同样 1 秒的数据：10 / 50 / 200 个点
            slow.AddValues(Enumerable.Range(0, 10).Select(i => (double)i));
            mid.AddValues(Enumerable.Range(0, 50).Select(i => 20.0 + (i % 5)));
            fast.AddValues(Enumerable.Range(0, 200).Select(i => 40.0 + (i % 7)));

            // 点数单位：按序号，X 轴取最长的曲线
            AssertX(plot, 0, 202);
            Assert.Equal(10, slow.LatestX);

            plot.XAxisUnit = XAxisUnit.RelativeTime;

            Assert.Equal(0.9, slow.LatestX, 9);
            Assert.Equal(0.98, mid.LatestX, 9);
            Assert.Equal(0.995, fast.LatestX, 9);
            Assert.All([slow, mid, fast], s => Assert.Equal(0.0, s.GetX(0), 12));
            Assert.Equal(0.5, slow.GetX(5), 12);
            Assert.Equal(0.5, mid.GetX(25), 12);
            Assert.Equal(0.5, fast.GetX(100), 12);

            Assert.Equal(0.1, slow.Signal!.Data.Period, 12);
            Assert.Equal(0.02, mid.Signal!.Data.Period, 12);
            Assert.Equal(0.005, fast.Signal!.Data.Period, 12);

            // X 轴右端：最新 X 的最大值（快曲线 0.995）+ 2 个最大的采样间隔（慢曲线 0.1）
            AssertX(plot, 0, 0.995 + 0.2);
            AssertY(plot, 0, 46);

            // 隐藏慢曲线：最大间隔变为中速曲线的 0.02
            slow.IsVisible = false;
            AssertX(plot, 0, 0.995 + 0.04);

            // 改变采样间隔立即重算
            slow.IsVisible = true;
            slow.SampleRate = 5;
            Assert.Equal(1.8, slow.LatestX, 9);
            AssertX(plot, 0, 1.8 + 0.4);

            // 切回点数单位：映射与坐标轴恢复
            plot.XAxisUnit = XAxisUnit.Samples;
            Assert.Equal(1, slow.Signal.Data.Period);
            Assert.Equal(1, slow.Signal.Data.XOffset);
            AssertX(plot, 0, 202);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·多曲线：起始时间不同的曲线按时间错开；跟随窗口内每条曲线按自己的映射统计 Y")]
    public void SeriesWithDifferentStartTimes_AndPerSeriesYStatistics()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.RelativeTime;

            // 慢：1 点/秒，X 0 ~ 9，值 0 ~ 9；快：100 点/秒，X 0 ~ 9.99，前 7.5 秒 1000，之后 -5
            PlotSeries slow = plot.AddSeries("慢");
            PlotSeries fast = plot.AddSeries("快");
            slow.StartTime = T0;
            fast.StartTime = T0;
            slow.SampleRate = 1;
            fast.SampleRate = 100;
            slow.AddValues(Enumerable.Range(0, 10).Select(i => (double)i));
            fast.AddValues(Enumerable.Range(0, 1000).Select(i => i < 750 ? 1000.0 : -5.0));

            // 跟随 4 秒：右端 = 最新 X 9.99 + 2 × 最大间隔 1 = 11.99 → 窗口 [7.99, 11.99]：
            // 慢曲线 X = 8、9（值 8、9），快曲线下标 799 ~ 999（-5）
            plot.FollowWindowDuration = TimeSpan.FromSeconds(4);
            plot.Mode = ScrollingMode.Follow;
            AssertX(plot, 7.99, 11.99);
            AssertY(plot, -5, 9);

            // 窗口 1 秒比留白还窄：时间单位下至少（2 + 1）个最大间隔 = 3 秒，慢曲线的最后一点（X = 9）仍在窗口内
            plot.FollowWindowDuration = TimeSpan.FromSeconds(1);
            AssertX(plot, 8.99, 11.99);
            AssertY(plot, -5, 9);

            // 同样的数据在点数单位下：窗口 = 最长曲线 30% = 300 点 → [702, 1002]，慢曲线（X 1 ~ 10）整条落在窗口外，
            // 快曲线下标 701 ~ 999 含 1000 与 -5
            plot.XAxisUnit = XAxisUnit.Samples;
            AssertX(plot, 702, 1002);
            AssertY(plot, -5, 1000);

            // 起始时间错开 5 秒：参考时刻取最早的一条
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            plot.FollowWindowDuration = TimeSpan.FromSeconds(4);
            PlotSeries late = plot.AddSeries("晚 5 秒");
            late.StartTime = T0.AddSeconds(5);
            late.SampleRate = 1;
            late.AddValues(Enumerable.Range(0, 10).Select(_ => 77.0));

            Assert.Equal(T0, plot.TimeReference);
            Assert.Equal(5.0, late.GetX(0), 12);
            Assert.Equal(14.0, late.LatestX, 12);
            AssertX(plot, 16 - 4, 16); // 14 + 2 × 1：其它曲线（结束于 9 / 9.99）都落在窗口外
            AssertY(plot, 77, 77);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·起始时间：未设置时取第一个数据点写入的时刻，清空后重新记录；默认起始时间与曲线自己的设置优先")]
    public void StartTime_DefaultsToArrivalOfFirstPoint()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            PlotSeries a = plot.AddSeries("A");
            a.SampleRate = 10;

            Assert.Null(a.StartTime);
            Assert.Null(plot.TimeReference);

            DateTime before = DateTime.Now;
            a.AddValue(1);
            DateTime after = DateTime.Now;

            Assert.NotNull(a.StartTime);
            Assert.InRange(a.StartTime!.Value, before, after);
            Assert.Equal(a.StartTime, plot.TimeReference);
            Assert.Equal(0.0, a.LatestX, 12);

            a.AddValues(2, 3);
            Assert.Equal(0.2, a.LatestX, 12);
            AssertX(plot, 0, 0.4);

            // 清空后重新记录
            a.Clear();
            Assert.Null(a.StartTime);
            Assert.Null(plot.TimeReference);

            // 控件的默认起始时间优先于到达时刻；曲线自己的设置再优先
            plot.DefaultStartTime = T0;
            Assert.Equal(T0, a.StartTime);
            a.StartTime = T0.AddSeconds(1);
            Assert.Equal(T0.AddSeconds(1), a.StartTime);
            Assert.Equal(T0, plot.TimeReference); // 默认曲线（没有数据）的起始时间 = 默认起始时间，更早

            a.AddValues(5, 6);
            Assert.Equal(1.0, a.GetX(0), 12);
            Assert.Equal(1.1, a.LatestX, 12);

            a.StartTime = null; // 恢复默认
            Assert.Equal(T0, a.StartTime);
            Assert.Equal(0.1, a.LatestX, 12);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·采样率：按双精度保存，3000 点/秒运行 1 小时没有累积误差")]
    public void SampleRate_IsExact()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            plot.DefaultSeries.StartTime = T0;
            plot.DefaultSeries.SampleRate = 3000;

            Assert.Equal(3600.0, plot.DefaultSeries.GetX(3000 * 3600), 6);
            Assert.Equal(3000.0, plot.DefaultSeries.SampleRate, 9);

            // TimeSpan 只有 100 ns 精度：读取时取最近的刻度
            Assert.Equal(TimeSpan.FromTicks(3333), plot.DefaultSeries.SampleInterval);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·跨线程：后台线程写入时间单位的曲线，到达时刻与 X 坐标正确")]
    public void CrossThreadWrites_InTimeUnit()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries series = host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            PlotSeries created = plot.AddSeries("后台");
            created.SampleRate = 1000;
            return created;
        });

        DateTime before = DateTime.Now;
        Thread writer = new(() =>
        {
            double[] batch = new double[100];
            for (int i = 0; i < 50; i++)
            {
                for (int k = 0; k < batch.Length; k++)
                    batch[k] = (i * batch.Length) + k;

                series.AddValues(batch);
            }
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout));
        WaitUntil(host, () => series.DataCount == 5000);

        host.Invoke(() =>
        {
            Assert.InRange(series.StartTime!.Value, before, DateTime.Now);
            Assert.Equal(4.999, series.LatestX, 9);
            AssertX(plot, 0, 4.999 + 0.002);
            AssertY(plot, 0, 4999);
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 鼠标：拖动后继续滚动 / 停止滚动后中键整体显示

    [Fact(DisplayName = "X 轴单位·拖动：时间单位下拖动后仍能看到最新数据 → 保持 X 轴最小值继续滚动；切换单位时偏移按采样间隔换算")]
    public void LeftDrag_KeepsScrolling_InTimeUnit()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareTimeCanvas(host, plot); // 10 点 × 0.5 秒：X 0 ~ 4.5，堆叠 [0, 5.5]

        host.Invoke(() =>
        {
            AssertX(plot, 0, 5.5);
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(canvas, PlotTestHelper.PointInDataArea(rect, 0.60, 0.5), PlotTestHelper.PointInDataArea(rect, 0.20, 0.5), MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.XOffset > 0, $"X 轴偏移应为正值，实际为 {plot.XOffset}。");
            AssertX(plot, plot.XOffset, plot.XOffset + 5.5);

            // 新数据：X 轴最小值保持，最大值 = 最新 X + 2 × 0.5
            plot.AddValue(2.5);
            AssertX(plot, plot.XOffset, 5.0 + 1.0);

            // 切到点数单位：偏移 ÷ 0.5，仍看着同一段数据
            double offsetSeconds = plot.XOffset;
            plot.XAxisUnit = XAxisUnit.Samples;
            Assert.Equal(offsetSeconds / 0.5, plot.XOffset, 9);
            AssertX(plot, plot.XOffset, 11 + 2);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·拖动：时间单位下拖到看不到最新数据 → 停止滚动；中键单击整体显示 → 恢复堆叠、偏移归零")]
    public void LeftDragAway_ThenMiddleClick_InTimeUnit()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = PrepareTimeCanvas(host, plot);

        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(canvas, PlotTestHelper.PointInDataArea(rect, 0.30, 0.5), PlotTestHelper.PointInDataArea(rect, 0.80, 0.5), MouseButtons.Left);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Manual, plot.Mode);

            // 停止滚动期间新数据不移动视图
            ScottPlot.AxisLimits frozen = plot.Plot.Axes.GetLimits();
            plot.AddValues(1, 2, 3);
            Assert.Equal(frozen.Left, plot.Plot.Axes.GetLimits().Left, 9);

            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Middle);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(0.0, plot.XOffset, 9);

            plot.AddValue(0);
            AssertX(plot, 0, 6.5 + 1.0); // 14 点 × 0.5 秒：最新 X 6.5
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 绝对时间需要明确的起始时间

    [Fact(DisplayName = "X 轴单位·绝对时间：有曲线没有明确起始时间时拒绝切换（中文提示 DefaultStartTime / StartTime），单位保持不变")]
    public void AbsoluteTime_RequiresExplicitStartTime()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            PlotSeries a = plot.AddSeries("通道A");
            a.SampleRate = 10;
            a.AddValues(1, 2, 3);
            Assert.NotNull(a.StartTime); // 相对时间：第一个点到达的时刻

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => plot.XAxisUnit = XAxisUnit.AbsoluteTime);
            Assert.Contains("DefaultStartTime", error.Message);
            Assert.Contains("StartTime", error.Message);
            Assert.Contains("通道A", error.Message);
            Assert.Equal(XAxisUnit.RelativeTime, plot.XAxisUnit);
            Assert.False(Assert.IsType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator).Absolute);

            // 默认曲线一旦有了数据，同样需要起始时间
            a.StartTime = T0;
            plot.AddValue(5);
            error = Assert.Throws<InvalidOperationException>(() => plot.XAxisUnit = XAxisUnit.AbsoluteTime);
            Assert.Contains("默认曲线", error.Message);
            Assert.DoesNotContain("通道A", error.Message);

            // 默认起始时间对所有没有自己起始时间的曲线生效
            plot.DefaultStartTime = T0.AddSeconds(-10);
            plot.XAxisUnit = XAxisUnit.AbsoluteTime;
            Assert.Equal(T0.AddSeconds(-10), plot.TimeReference);
            Assert.Equal(10.0, a.GetX(0), 12);
            Assert.Equal(0.0, plot.DefaultSeries.GetX(0), 12);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·绝对时间：不使用到达时刻；没人用的默认曲线不妨碍切换，写给它的数据（含跨线程）被拒收而不抛异常")]
    public void AbsoluteTime_UnusedDefaultSeries_RejectsWrites()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries a = host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            PlotSeries series = plot.AddSeries("A", startTime: T0);
            series.SampleRate = 10;

            plot.XAxisUnit = XAxisUnit.AbsoluteTime; // 默认曲线没有数据、没有起始时间：不妨碍
            Assert.Equal(T0, plot.TimeReference);
            Assert.Null(plot.DefaultSeries.StartTime); // 绝对时间不使用到达时刻

            series.AddValues(1, 2, 3);
            Assert.Equal(0.2, series.LatestX, 12); // 从明确的起始时间 T0 算起，而不是数据到达的时刻

            // 写给没有起始时间的默认曲线：拒收并计数，不抛异常
            plot.AddValue(7);
            plot.AddValues(1, 2);
            Assert.Equal(0, plot.DataCount);
            Assert.Equal(3, plot.DefaultSeries.RejectedValueCount);
            return series;
        });

        Exception? writerError = null;
        Thread writer = new(() =>
        {
            try
            {
                plot.AddValues(1, 2, 3, 4);
                plot.AddValue(5);
                a.AddValues(4, 5);
            }
            catch (Exception exception)
            {
                writerError = exception;
            }
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout));
        Assert.Null(writerError);
        WaitUntil(host, () => plot.DefaultSeries.RejectedValueCount == 8 && a.DataCount == 5);

        host.Invoke(() =>
        {
            Assert.Equal(0, plot.DataCount);

            // 设置默认起始时间后默认曲线正常接收
            plot.DefaultStartTime = T0;
            plot.AddValue(9);
            Assert.Equal(1, plot.DataCount);

            plot.DefaultSeries.Clear();
            Assert.Equal(0, plot.DefaultSeries.RejectedValueCount);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·绝对时间：清除起始时间、不带起始时间地添加曲线都会被拒绝（含跨线程调用），相对时间下不受限制")]
    public void AbsoluteTime_BlocksLosingStartTimes()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries a = host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            plot.DefaultStartTime = T0;
            PlotSeries series = plot.AddSeries("A");
            plot.XAxisUnit = XAxisUnit.AbsoluteTime;

            // A 依赖默认起始时间：不能清除
            Assert.Throws<InvalidOperationException>(() => plot.DefaultStartTime = null);
            Assert.Equal(T0, plot.DefaultStartTime);

            // 有默认起始时间时，曲线清除自己的起始时间、不带起始时间地添加曲线都可以
            series.StartTime = T0.AddSeconds(1);
            series.StartTime = null;
            Assert.Equal(T0, series.StartTime);
            PlotSeries b = plot.AddSeries("B");

            // 每条曲线都有自己的起始时间后，可以清除默认起始时间（没有数据的默认曲线不受影响）
            series.StartTime = T0;
            b.StartTime = T0;
            plot.DefaultStartTime = null;
            Assert.Null(plot.DefaultStartTime);

            // 现在曲线不能再清除自己的起始时间，也不能不带起始时间地添加曲线
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => series.StartTime = null);
            Assert.Contains("DefaultStartTime", error.Message);
            Assert.Equal(T0, series.StartTime);

            error = Assert.Throws<InvalidOperationException>(() => plot.AddSeries("C"));
            Assert.Contains("startTime", error.Message);
            Assert.Equal(3, plot.Series.Count);

            PlotSeries c = plot.AddSeries("C", startTime: T0.AddSeconds(2));
            Assert.Equal(T0.AddSeconds(2), c.StartTime);
            Assert.Equal(2.0, c.GetX(0), 12);

            // 没有数据的默认曲线可以随意设置 / 清除自己的起始时间
            plot.DefaultSeries.StartTime = T0;
            plot.DefaultSeries.StartTime = null;
            return series;
        });

        // 跨线程调用同样把异常抛给调用方
        Exception? startTimeError = null;
        Exception? addSeriesError = null;
        Thread worker = new(() =>
        {
            try
            {
                a.StartTime = null;
            }
            catch (Exception exception)
            {
                startTimeError = exception;
            }

            try
            {
                plot.AddSeries("D");
            }
            catch (Exception exception)
            {
                addSeriesError = exception;
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();
        Assert.True(worker.Join(WaitTimeout));
        Assert.IsType<InvalidOperationException>(startTimeError);
        Assert.IsType<InvalidOperationException>(addSeriesError);

        host.Invoke(() =>
        {
            Assert.Equal(T0, a.StartTime);
            Assert.Equal(4, plot.Series.Count);

            // 相对时间下不受限制：清除后回到“第一个点到达的时刻”
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            a.StartTime = null;
            Assert.Null(a.StartTime); // 还没有数据
            a.AddValue(1);
            Assert.NotNull(a.StartTime);
            plot.AddSeries("D");
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 运行中切换单位 / Reset / 最近点查询

    [Fact(DisplayName = "X 轴单位·运行中切换：数据保留、坐标轴立即重算，手动模式视图换算到新单位，自定义刻度生成器在切回点数时恢复")]
    public void SwitchingUnitAtRuntime()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            ScottPlot.TickGenerators.NumericAutomatic custom = new() { LabelFormatter = x => $"#{x:0}" };
            plot.Plot.Axes.Bottom.TickGenerator = custom;

            plot.DefaultSeries.StartTime = T0;
            plot.DefaultSeries.SampleRate = 10;
            plot.AddValues(Enumerable.Range(0, 100).Select(i => (double)i));
            AssertX(plot, 0, 102);

            // 堆叠 → 相对时间：立即按时间重算
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            Assert.Equal(100, plot.DataCount);
            Assert.IsType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);
            AssertX(plot, 0, 9.9 + 0.2);

            // 绝对时间：X 不变，只换标签
            plot.XAxisUnit = XAxisUnit.AbsoluteTime;
            AssertX(plot, 0, 10.1);
            TimeAxisTickGenerator generator = Assert.IsType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);
            Assert.True(generator.Absolute);
            Assert.Equal(T0, generator.ReferenceTime);

            // 切回点数：恢复用户的刻度生成器
            plot.XAxisUnit = XAxisUnit.Samples;
            Assert.Same(custom, plot.Plot.Axes.Bottom.TickGenerator);
            AssertX(plot, 0, 102);

            // 手动模式：视图（下标 20 ~ 40）换算到时间单位后仍是同一段数据
            plot.StopScrolling();
            plot.Plot.Axes.SetLimits(21, 41, -1, 1);
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
            AssertX(plot, 2.0, 4.0);

            plot.XAxisUnit = XAxisUnit.Samples;
            AssertX(plot, 21, 41);

            // 重新选择模式恢复滚动
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            plot.Mode = ScrollingMode.Stacked;
            AssertX(plot, 0, 10.1);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·参考时刻移动：删除起始时间最早的曲线后 X 整体平移，手动模式仍看着同一段时间")]
    public void RemovingEarliestSeries_ShiftsReference()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            PlotSeries early = plot.AddSeries("早");
            PlotSeries late = plot.AddSeries("晚");
            early.StartTime = T0;
            late.StartTime = T0.AddSeconds(10);
            early.AddValues(Enumerable.Range(0, 20).Select(i => (double)i)); // 默认采样间隔 1 秒：X 0 ~ 19
            late.AddValues(Enumerable.Range(0, 20).Select(i => (double)i));  // X 10 ~ 29

            plot.StopScrolling();
            plot.Plot.Axes.SetLimits(15, 25, -1, 30);

            early.Remove();

            Assert.Equal(T0.AddSeconds(10), plot.TimeReference);
            Assert.Equal(0.0, late.GetX(0), 12);
            AssertX(plot, 5, 15); // 仍是“晚”曲线的下标 5 ~ 15
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·Reset：时间单位下替换绘图对象后重新安装时间刻度，曲线映射保留")]
    public void Reset_ReinstallsTimeTicks()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.DefaultStartTime = T0;
            plot.XAxisUnit = XAxisUnit.AbsoluteTime;
            plot.DefaultSeries.SampleRate = 4;
            plot.AddValues(1, 2, 3);

            plot.Reset();

            Assert.Equal(0, plot.DataCount);
            Assert.Equal(T0, plot.TimeReference); // 起始时间是明确设置的，Reset 不改变它
            Assert.Equal(XAxisUnit.AbsoluteTime, plot.XAxisUnit);
            Assert.IsType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);

            plot.AddValues(4, 5, 6, 7, 8);
            Assert.Equal(0.25, plot.Signal!.Data.Period, 12);
            Assert.Equal(1.0, plot.LatestX, 12);
            AssertX(plot, 0, 1.5);

            plot.XAxisUnit = XAxisUnit.Samples;
            Assert.IsNotType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·最近点查询：时间单位下 GetNearest / GetNearestX 返回按时间换算的 X")]
    public void NearestPointQuery_ReturnsTimeX()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(1000, 500));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            plot.DefaultSeries.StartTime = T0;
            PlotSeries series = plot.AddSeries("A");
            series.StartTime = T0.AddSeconds(2);
            series.SampleRate = 100;
            series.AddValues(Enumerable.Range(0, 500).Select(i => Math.Sin(i / 30.0)));

            PlotTestHelper.EnsureRendered(plot);
            plot.Refresh();
            RenderDetails render = plot.Plot.RenderManager.LastRender;

            Coordinates location = new(series.GetX(100) + 0.001, series.Values[100]);
            DataPoint nearest = series.Signal!.GetNearest(location, render, 15);
            Assert.Equal(100, nearest.Index);
            Assert.Equal(3.0, nearest.X, 9);
            Assert.Equal(series.Values[100], nearest.Y, 12);

            DataPoint nearestX = series.Signal.GetNearestX(location, render, 15);
            Assert.Equal(100, nearestX.Index);
            Assert.Equal(3.0, nearestX.X, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "X 轴单位·金字塔数据源：Period / XOffset 不为 1 时渲染与最近点查询仍与 ScottPlot 内置数据源一致")]
    public void PyramidSource_MatchesBuiltIn_WithTimeMapping()
    {
        const double Period = 0.004;
        const double XOffset = 2.5;
        List<double> values = [.. Enumerable.Range(0, 20_000).Select(i => (Math.Sin(i / 300.0) * 10) + ((i * 7919 % 13) / 13.0))];
        PlotDataBuffer buffer = new() { XStart = XOffset, XStep = Period };
        buffer.AddRange(values);

        (byte[] builtInImage, string builtInNearest) = Render(plot => plot.Add.Signal(values, Period));
        (byte[] pyramidImage, string pyramidNearest) = Render(plot => plot.Add.Signal(new PyramidSignalSource(buffer, Period)));

        Assert.True(builtInImage.AsSpan().SequenceEqual(pyramidImage), "渲染结果与内置数据源不一致。");
        Assert.Equal(builtInNearest, pyramidNearest);

        (byte[] Image, string Nearest) Render(Func<Plot, ScottPlot.Plottables.Signal> addSignal)
        {
            using Plot plot = new();
            ScottPlot.Plottables.Signal signal = addSignal(plot);
            signal.Data.XOffset = XOffset;
            signal.LineWidth = 1;
            signal.Color = Colors.Blue;
            plot.Axes.SetLimits(XOffset - 1, XOffset + (values.Count * Period) + 1, -12, 12);

            using ScottPlot.Image rendered = plot.GetImage(800, 500);
            string nearest = string.Empty;

            foreach (double fraction in new[] { 0.0, 0.31, 0.5, 0.97, 1.2 })
            {
                int index = Math.Min(values.Count - 1, (int)(values.Count * fraction));
                Coordinates location = new(XOffset + (values.Count * fraction * Period) + 0.0013, values[index] + 0.2);
                DataPoint point = signal.GetNearest(location, plot.RenderManager.LastRender, 15);
                DataPoint pointX = signal.GetNearestX(location, plot.RenderManager.LastRender, 15);
                nearest += $"{point.Index}/{point.X}/{point.Y}|{pointX.Index}/{pointX.X}/{pointX.Y};";
            }

            return (rendered.GetImageBytes(), nearest);
        }
    }

    [Fact(DisplayName = "X 轴单位·示例图片：三条采样率不同的曲线在点数 / 相对时间 / 绝对时间下的显示效果")]
    public void RenderArtifacts()
    {
        const int Width = 1100;
        const int Height = 560;
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(Width, Height));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        (XAxisUnit Unit, string File, string Title)[] cases =
        [
            (XAxisUnit.Samples, "x-axis-samples.png", "点数：第 n 个点 X = n，各曲线按序号对齐（快曲线最长）"),
            (XAxisUnit.RelativeTime, "x-axis-relative.png", "相对时间：起始时间 + 下标 × 采样间隔，三条曲线在时间上对齐"),
            (XAxisUnit.AbsoluteTime, "x-axis-absolute.png", "绝对时间：刻度显示时刻，第一个刻度与日期变化处标出日期"),
        ];

        host.Invoke(() =>
        {
            plot.ShowLegend = true;
            plot.DefaultStartTime = new DateTime(2026, 10, 12, 23, 59, 40);
            (string Name, int Rate, double Offset, ScottPlot.Color Color)[] curves =
            [
                ("慢 10 点/秒", 10, 0, Colors.Blue),
                ("中 50 点/秒", 50, 12, Colors.Red),
                ("快 200 点/秒", 200, 24, Colors.Green),
            ];

            foreach ((string name, int rate, double offset, ScottPlot.Color color) in curves)
            {
                PlotSeries series = plot.AddSeries(name, color);
                series.SampleRate = rate;
                series.AddValues(Enumerable.Range(0, rate * 40).Select(i => offset + (4 * Math.Sin(2 * Math.PI * 0.1 * i / rate))));
            }

            foreach ((XAxisUnit unit, string file, string title) in cases)
            {
                plot.XAxisUnit = unit;
                plot.Plot.Title(title);
                plot.Plot.Save(Path.Combine(TestPaths.ArtifactsDirectory, file), Width, Height);
            }
        });

        Assert.All(cases, c => Assert.True(new FileInfo(Path.Combine(TestPaths.ArtifactsDirectory, c.File)).Length > 2048));
        host.AssertNoUnhandledExceptions();
    }

    #endregion

    private static Control PrepareTimeCanvas(WinFormsTestHost host, ScrollingPlotControl plot) =>
        host.Invoke(() =>
        {
            plot.XAxisUnit = XAxisUnit.RelativeTime;
            plot.DefaultSeries.StartTime = T0;
            plot.DefaultSeries.SampleRate = 2;
            PlotTestHelper.AddWave(plot, 10);
            plot.Mode = ScrollingMode.Stacked;
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

    private static Tick[] RenderMajorTicks(ScrollingPlotControl plot)
    {
        PlotTestHelper.EnsureRendered(plot);
        plot.Refresh();
        return [.. plot.Plot.Axes.Bottom.TickGenerator.Ticks.Where(t => t.IsMajor)];
    }

    private static void AssertX(ScrollingPlotControl plot, double expectedMin, double expectedMax)
    {
        (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
        Assert.True(Math.Abs(xMin - expectedMin) < 1e-6 && Math.Abs(xMax - expectedMax) < 1e-6, $"X 轴应为 [{expectedMin}, {expectedMax}]，实际为 [{xMin}, {xMax}]。");
    }

    /// <summary>Y 轴应为数据范围 [min, max] 上下各留 5%（数据水平时为 max(|值| × 5%, 0.5)）。</summary>
    private static void AssertY(ScrollingPlotControl plot, double dataMin, double dataMax)
    {
        Assert.True(ScrollingAxisCalculator.TryCalculateYWindow(dataMin, dataMax, plot.AxisOptions, out double expectedMin, out double expectedMax));
        (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
        Assert.True(Math.Abs(yMin - expectedMin) < 1e-6 && Math.Abs(yMax - expectedMax) < 1e-6, $"Y 轴应为 [{expectedMin}, {expectedMax}]（数据 {dataMin} ~ {dataMax}），实际为 [{yMin}, {yMax}]。");
    }

    private static void WaitUntil(WinFormsTestHost host, Func<bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (host.Invoke(condition))
                return;

            host.Pump(20);
        }

        Assert.Fail("等待超时。");
    }
}
