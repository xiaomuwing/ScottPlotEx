using System.Diagnostics;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 多曲线：各曲线数据独立、坐标轴按所有可见曲线计算、跨线程写入按帧合并、单曲线接口兼容。
/// </summary>
public class MultiSeriesTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "多曲线：各曲线数据独立，单曲线接口读写默认曲线")]
    public void Series_HaveIndependentData_AndLegacyApiUsesDefaultSeries()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries a = plot.AddSeries("A");
            PlotSeries b = plot.AddSeries("B", ScottPlot.Colors.Red);

            a.AddValues(1, 2, 3);
            b.AddValues([10.0, 20]);
            plot.AddValue(7);

            Assert.Equal([1.0, 2, 3], a.Values);
            Assert.Equal([10.0, 20], b.Values);
            Assert.Equal([7.0], plot.Values);
            Assert.Equal(1, plot.DataCount);
            Assert.Equal(3, a.DataCount);
            Assert.Equal(3, a.LatestX);
            Assert.Equal(20, b.LatestValue);

            Assert.Equal(3, plot.Series.Count);
            Assert.Same(plot.DefaultSeries, plot.Series[0]);
            Assert.Same(plot.DefaultSeries.Signal, plot.Signal);
            Assert.Equal(3, plot.Plot.GetPlottables<ScottPlot.Plottables.Signal>().Count());

            Assert.Equal(ScottPlot.Colors.Red, b.Color);
            Assert.Equal("B", b.Signal!.LegendText);
            Assert.NotEqual(a.Color, b.Color);
            Assert.Equal(1f, a.LineWidth);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线：堆叠模式 X 取最长的可见曲线，Y 取各曲线极值的并集")]
    public void StackedMode_UsesLongestSeries_AndUnionOfRanges()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries a = plot.AddSeries("A");
            PlotSeries b = plot.AddSeries("B");

            a.AddValues(Enumerable.Range(0, 100).Select(i => (double)(i % 10)));   // 0 ~ 9，100 点
            b.AddValues(Enumerable.Range(0, 50).Select(i => 100.0 + (i % 5)));     // 100 ~ 104，50 点

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0, xMin, 6);
            Assert.Equal(100 + 2, xMax, 6);

            // 并集 0 ~ 104，上下各留 5%（104 × 0.05 = 5.2）
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(-5.2, yMin, 6);
            Assert.Equal(109.2, yMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线：隐藏的曲线不绘制，也不参与 X 轴长度与 Y 轴范围")]
    public void HiddenSeries_IsExcludedFromAutoScaling()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries a = plot.AddSeries("A");
            PlotSeries b = plot.AddSeries("B");

            a.AddValues(Enumerable.Range(0, 100).Select(i => (double)(i % 10)));   // 0 ~ 9，100 点
            b.AddValues(Enumerable.Range(0, 300).Select(_ => 1000.0));             // 1000，300 点

            Assert.Equal(300 + 2, PlotTestHelper.GetXRange(plot).Max, 6);

            b.IsVisible = false;

            Assert.False(b.Signal!.IsVisible);
            Assert.Equal(100 + 2, PlotTestHelper.GetXRange(plot).Max, 6);
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(-0.45, yMin, 6);
            Assert.Equal(9.45, yMax, 6);

            b.IsVisible = true;
            Assert.Equal(300 + 2, PlotTestHelper.GetXRange(plot).Max, 6);
            Assert.True(PlotTestHelper.GetYRange(plot).Max > 1000);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线：跟随模式按最长曲线计算窗口，Y 轴只统计窗口内的数据")]
    public void FollowMode_UsesLongestSeries_AndOnlyDataInsideWindow()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Follow;

            PlotSeries shortSeries = plot.AddSeries("短");
            PlotSeries longSeries = plot.AddSeries("长");

            // 短曲线（X 1~100）完全落在跟随窗口之外；长曲线前 700 点为 0，最后 300 点为 50 ~ 59
            shortSeries.AddValues(Enumerable.Range(0, 100).Select(_ => -1000.0));
            longSeries.AddValues(Enumerable.Range(0, 1000).Select(i => i < 700 ? 0.0 : 50.0 + (i % 10)));

            // 窗口 = 最长曲线的 30% = 300 点，右端 = 1000 + 2
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(1002, xMax, 6);
            Assert.Equal(702, xMin, 6);

            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(50 - 0.45, yMin, 6);
            Assert.Equal(59 + 0.45, yMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "跟随模式固定窗口宽度（点数）：窗口不随数据增长，重新选择跟随模式也保留")]
    public void FollowWindowPoints_KeepsFixedWidth()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Follow;
            plot.FollowWindowPoints = 100;

            plot.AddValues(Enumerable.Range(0, 50).Select(i => (double)i));
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(52, xMax, 6);
            Assert.Equal(-48, xMin, 6); // 数据不足一个窗口：左侧留空

            plot.AddValues(Enumerable.Range(0, 950).Select(i => (double)i));
            (xMin, xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(1002, xMax, 6);
            Assert.Equal(902, xMin, 6);

            plot.Mode = ScrollingMode.Follow;
            Assert.Equal(100, plot.FollowWindowPoints);
            Assert.Equal(902, PlotTestHelper.GetXRange(plot).Min, 6);

            plot.FollowWindowPoints = 0; // 回到按百分比（默认 30%）
            Assert.Equal(1002 - 300, PlotTestHelper.GetXRange(plot).Min, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "跨线程多曲线写入：全部到达、顺序不变，所有曲线共用合并消息")]
    public void CrossThreadWrites_ToManySeries_AllArrive_AndAreCoalesced()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        const int SeriesCount = 10;
        const int ValuesPerSeries = 5_000;

        PlotSeries[] series = host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            return Enumerable.Range(0, SeriesCount).Select(i => plot.AddSeries($"曲线 {i + 1}")).ToArray();
        });

        Thread[] writers =
        [
            .. series.Select((s, index) => new Thread(() =>
            {
                // 单点与小批量交替写入，值在每条曲线内连续递增
                for (int i = 0; i < ValuesPerSeries; i += 3)
                {
                    double first = (index * 100_000.0) + i;
                    s.AddValue(first);
                    s.AddValues(first + 1, first + 2);
                }
            })
            {
                IsBackground = true,
            }),
        ];

        foreach (Thread writer in writers)
            writer.Start();

        foreach (Thread writer in writers)
            Assert.True(writer.Join(WaitTimeout), "采集线程未能按时结束。");

        // 每条曲线：i = 0, 3, 6, …, 4998 各写 3 个点 → 1667 × 3 = 5001 点，值连续递增
        int expected = ((ValuesPerSeries + 2) / 3) * 3;
        WaitUntil(host, () => series.All(s => s.DataCount == expected), () => "数据未全部到达：" + string.Join(", ", host.Invoke(() => series.Select(s => s.DataCount).ToArray())));

        host.Invoke(() =>
        {
            for (int index = 0; index < SeriesCount; index++)
                Assert.Equal(Enumerable.Range(0, expected).Select(i => (index * 100_000.0) + i), series[index].Values);

            Assert.True(plot.PendingFlushCount < SeriesCount * ValuesPerSeries / 10, $"合并写入执行了 {plot.PendingFlushCount} 次，未达到合并效果。");
            Assert.Equal(expected + 2, PlotTestHelper.GetXRange(plot).Max, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "跨线程清空单条曲线：按调用顺序生效，其它曲线不受影响")]
    public void CrossThreadClear_OfOneSeries_KeepsOrdering()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        (PlotSeries a, PlotSeries b) = host.Invoke(() => (plot.AddSeries("A"), plot.AddSeries("B")));

        Thread writer = new(() =>
        {
            for (int i = 0; i < 1_000; i++)
            {
                a.AddValue(-1 - i);
                b.AddValue(i);
            }

            a.Clear();

            for (int i = 0; i < 100; i++)
                a.AddValue(i);
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout), "采集线程未能按时结束。");

        WaitUntil(host, () => a.DataCount == 100 && a.LatestValue == 99 && b.DataCount == 1_000, () => $"A = {host.Invoke(() => a.DataCount)}, B = {host.Invoke(() => b.DataCount)}");

        host.Invoke(() =>
        {
            Assert.Equal(Enumerable.Range(0, 100).Select(i => (double)i), a.Values);
            Assert.Equal(Enumerable.Range(0, 1_000).Select(i => (double)i), b.Values);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "ClearData 清空全部曲线的数据（曲线本身保留），UI 线程与跨线程一致")]
    public void ClearData_ClearsAllSeries()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries a = host.Invoke(() =>
        {
            PlotSeries series = plot.AddSeries("A");
            series.AddValues(1, 2, 3);
            plot.AddValues(4, 5);

            plot.ClearData();

            Assert.Equal(0, series.DataCount);
            Assert.Equal(0, plot.DataCount);
            Assert.Equal(2, plot.Series.Count);
            Assert.Equal(1, PlotTestHelper.GetXRange(plot).Max, 6); // 空视图

            series.AddValues(1, 2, 3);
            return series;
        });

        Thread writer = new(() =>
        {
            a.AddValues(4, 5);
            plot.ClearData();
            a.AddValues(10, 11);
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout));

        WaitUntil(host, () => a.DataCount == 2 && a.LatestValue == 11, () => $"A = {host.Invoke(() => a.DataCount)}");
        host.Invoke(() => Assert.Equal([10.0, 11], a.Values));
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "删除曲线：图元移除、坐标轴重算、之后的写入被忽略；默认曲线不能删除")]
    public void RemoveSeries_DetachesAndRecalculates()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries a = plot.AddSeries("A");
            PlotSeries b = plot.AddSeries("B");
            a.AddValues(Enumerable.Range(0, 100).Select(i => (double)i));
            b.AddValues(Enumerable.Range(0, 500).Select(_ => 1000.0));

            Assert.Equal(502, PlotTestHelper.GetXRange(plot).Max, 6);

            b.Remove();

            Assert.True(b.IsRemoved);
            Assert.Null(b.Signal);
            Assert.DoesNotContain(b, plot.Series);
            Assert.Equal(2, plot.Plot.GetPlottables<ScottPlot.Plottables.Signal>().Count());
            Assert.Equal(102, PlotTestHelper.GetXRange(plot).Max, 6);
            Assert.True(PlotTestHelper.GetYRange(plot).Max < 1000);

            b.AddValue(5);
            b.Remove(); // 重复删除无副作用
            Assert.Equal(500, b.DataCount);

            Assert.Throws<InvalidOperationException>(() => plot.DefaultSeries.Remove());
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "Reset：保留曲线与样式（含直接修改的图元样式），清空数据并挂到新的绘图对象上")]
    public void Reset_KeepsSeriesAndStyle()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.ShowLegend = true;
            PlotSeries a = plot.AddSeries("A", ScottPlot.Colors.Red);
            a.Signal!.LineWidth = 3; // 直接改图元
            a.AddValues(1, 2, 3);
            ScottPlot.Plottables.Signal before = a.Signal;

            plot.Reset();

            Assert.Equal(0, a.DataCount);
            Assert.NotSame(before, a.Signal);
            Assert.Equal(ScottPlot.Colors.Red, a.Signal!.Color);
            Assert.Equal(3, a.Signal.LineWidth);
            Assert.Equal("A", a.Signal.LegendText);
            Assert.Equal(2, plot.Plot.GetPlottables<ScottPlot.Plottables.Signal>().Count());
            Assert.True(plot.Plot.Legend.IsVisible);

            a.AddValues(4, 5);
            Assert.Equal(4, PlotTestHelper.GetXRange(plot).Max, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线：非 UI 线程可以创建曲线并修改样式（同步切换到 UI 线程）")]
    public void AddSeriesAndStyle_FromBackgroundThread()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries series = plot.AddSeries("后台");
        series.Color = ScottPlot.Colors.Green;
        series.LineWidth = 2;
        series.Name = "后台曲线";
        series.AddValues(1, 2, 3);

        WaitUntil(host, () => series.DataCount == 3, () => "数据未到达");

        host.Invoke(() =>
        {
            Assert.Contains(series, plot.Series);
            Assert.Equal(ScottPlot.Colors.Green, series.Signal!.Color);
            Assert.Equal(2, series.Signal.LineWidth);
            Assert.Equal("后台曲线", series.Signal.LegendText);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "帧率上限：跨线程持续写入时按设定帧率合并（高精度定时，不受 15.6 ms 时钟精度影响）")]
    public void MaxFramesPerSecond_LimitsFlushRate_Precisely()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), opacity: 1.0);
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        const int Fps = 30;
        const double Seconds = 2.0;

        PlotSeries series = host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            plot.MaxFramesPerSecond = Fps;
            return plot.AddSeries("A");
        });

        bool highResolution = host.Invoke(() => plot.FrameTimerIsHighResolution) == true;
        int flushesBefore = host.Invoke(() => plot.PendingFlushCount);
        long written = 0;

        Thread writer = new(() =>
        {
            Stopwatch clock = Stopwatch.StartNew();
            double[] batch = new double[10];

            while (clock.Elapsed.TotalSeconds < Seconds)
            {
                series.AddValues(batch);
                written += batch.Length;
                Thread.SpinWait(2_000);
            }
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout));
        int flushes = host.Invoke(() => plot.PendingFlushCount) - flushesBefore;

        WaitUntil(host, () => series.DataCount == written, () => $"数据未全部到达：{host.Invoke(() => series.DataCount)} / {written}");

        // 上限：不超过设定帧率（留出启动 / 收尾的余量）
        Assert.True(flushes <= (Fps * Seconds) + 5, $"{Seconds} 秒内合并写入 {flushes} 次，超过了 {Fps} 帧/秒的上限。");

        // 精度：高精度定时器下应接近设定帧率；若退化为 15.6 ms 时钟精度，33 ms 会被拉长到约 47 ms（约 21 帧/秒）
        if (highResolution)
            Assert.True(flushes >= Fps * Seconds * 0.8, $"{Seconds} 秒内只合并写入 {flushes} 次，明显低于 {Fps} 帧/秒（定时精度不足）。");

        host.AssertNoUnhandledExceptions();
    }

    private static void WaitUntil(WinFormsTestHost host, Func<bool> condition, Func<string> failureMessage)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (host.Invoke(condition))
                return;

            host.Pump(20);
        }

        Assert.Fail(failureMessage());
    }
}
