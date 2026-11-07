using System.Diagnostics;
using ScottPlot;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 2026-11-07 代码复审（docs/code-review-2026-11-07.md）各项修复的回归测试。
/// </summary>
public class ReviewFixes20261107Tests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    #region 1.1 UI 线程清空 / Reset 丢弃清空之前排队的跨线程数据

    public static TheoryData<string> ClearKinds => ["ClearData", "Series.Clear", "Reset"];

    [Theory(DisplayName = "1.1 UI 线程清空 / Reset：清空之前从后台线程写入、尚未合并的数据被丢弃，之后的写入照常到达")]
    [MemberData(nameof(ClearKinds))]
    public void UiThreadClear_DiscardsValuesQueuedBeforeClear(string kind)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(1.0, 2, 3);

            // 在 UI 线程被占用期间让后台线程写入：数据只能进入待处理队列，合并写入要等这段代码返回后才执行
            Thread writer = new(() =>
            {
                plot.AddValue(10);
                plot.AddValue(11);
                plot.AddValue(12);
            });
            writer.Start();
            Assert.True(writer.Join(WaitTimeout));

            switch (kind)
            {
                case "ClearData":
                    plot.ClearData();
                    break;
                case "Series.Clear":
                    plot.DefaultSeries.Clear();
                    break;
                default:
                    plot.Reset();
                    break;
            }
        });

        host.Pump(300);
        host.Invoke(() => Assert.Equal(0, plot.DataCount));

        // 清空之后的写入照常合并到达
        Thread after = new(() => plot.AddValues(20.0, 21));
        after.Start();
        Assert.True(after.Join(WaitTimeout));

        WaitUntil(host, () => plot.DataCount == 2, () => $"清空之后的数据未到达：{host.Invoke(() => plot.DataCount)} / 2");
        host.Invoke(() => Assert.Equal([20.0, 21], plot.Values));
        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 1.2 UI 线程逐点写入的坐标轴合并更新

    [Fact(DisplayName = "1.2 合并更新：同一段代码中写入不立即重算坐标轴，UpdateAxesNow / 消息循环 / 绘制之前完成，结果与立即更新一致")]
    public void DeferredAxisUpdate_IsAppliedOnDemand_AndMatchesImmediate()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        double[] data = [.. Enumerable.Range(0, 500).Select(i => 10 * Math.Sin(i / 7.0))];

        // 立即更新：写完马上读到新范围
        AxisLimits immediate = host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Follow;
            plot.AxisUpdateMode = AxisUpdateMode.Immediate;

            foreach (double value in data)
                plot.AddValue(value);

            return plot.Plot.Axes.GetLimits();
        });

        Assert.Equal(500 + 2, immediate.Right, 9);

        // 合并更新：同一段代码中读到的是写入之前的范围；UpdateAxesNow 之后与立即更新完全一致
        host.Invoke(() =>
        {
            plot.ClearData();
            plot.AxisUpdateMode = AxisUpdateMode.Deferred;
            AxisLimits before = plot.Plot.Axes.GetLimits();

            foreach (double value in data)
                plot.AddValue(value);

            Assert.Equal(before, plot.Plot.Axes.GetLimits());

            plot.UpdateAxesNow();
            Assert.Equal(immediate, plot.Plot.Axes.GetLimits());
        });

        // 不手动调用：本轮消息处理结束后自动完成
        host.Invoke(() =>
        {
            plot.ClearData();

            foreach (double value in data)
                plot.AddValue(value);
        });

        host.Invoke(() => Assert.Equal(immediate, plot.Plot.Axes.GetLimits()));

        // 同步绘制（Refresh）之前也会完成
        host.Invoke(() =>
        {
            plot.ClearData();

            foreach (double value in data)
                plot.AddValue(value);

            plot.Refresh();
            Assert.Equal(immediate, plot.Plot.Axes.GetLimits());
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "1.2 自动模式：可见曲线不超过阈值时立即更新，超过阈值时合并更新且最终范围正确")]
    public void AutoAxisUpdate_DefersOnlyAboveThreshold()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        List<PlotSeries> series = host.Invoke(() =>
        {
            Assert.Equal(AxisUpdateMode.Auto, plot.AxisUpdateMode);
            List<PlotSeries> list = [plot.DefaultSeries];

            for (int i = 1; i <= ScrollingPlotControl.AutoDeferVisibleSeriesThreshold; i++)
                list.Add(plot.AddSeries("S" + i));

            // 阈值以内（8 条有数据）：立即更新
            for (int i = 0; i < ScrollingPlotControl.AutoDeferVisibleSeriesThreshold; i++)
                list[i].AddValues(1.0, 2, 3);

            Assert.Equal(3 + 2, plot.Plot.Axes.GetLimits().Right, 9);
            return list;
        });

        // 第 9 条曲线也有了数据：超过阈值，同一段代码中不立即更新，消息处理结束后补上
        host.Invoke(() =>
        {
            for (int i = 0; i < 10; i++)
                series[^1].AddValue(i);

            Assert.Equal(3 + 2, plot.Plot.Axes.GetLimits().Right, 9);
        });

        host.Invoke(() =>
        {
            AxisLimits limits = plot.Plot.Axes.GetLimits();
            Assert.Equal(10 + 2, limits.Right, 9);
            Assert.True(limits.Bottom < 0 && limits.Top > 9, $"Y 轴未覆盖第 9 条曲线的数据：{limits.Bottom} ~ {limits.Top}");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "1.2 性能护栏：UI 线程逐点写入的开销不再随曲线数线性增长（100 条曲线 vs 1 条，跟随模式）")]
    public void UiThreadPerPointCost_DoesNotScaleWithSeriesCount()
    {
        double single = MeasurePerPointNanoseconds(1);
        double many = MeasurePerPointNanoseconds(100);

        // 修复前约为 30 倍（147 ns → 4456 ns / 点，Release）；合并更新后与曲线数基本无关，留出充足余量防止计时抖动
        Assert.True(many < single * 5, $"100 条曲线 {many:F0} ns/点，1 条曲线 {single:F0} ns/点，比值 {many / single:F1}（期望 < 5）。");
    }

    private static double MeasurePerPointNanoseconds(int seriesCount)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        return host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Follow;
            List<PlotSeries> series = [plot.DefaultSeries];

            for (int i = 1; i < seriesCount; i++)
                series.Add(plot.AddSeries("S" + i));

            double[] preload = [.. Enumerable.Range(0, 10_000).Select(i => Math.Sin(i * 0.01))];
            foreach (PlotSeries s in series)
                s.AddValues(preload);

            double best = double.MaxValue;

            for (int round = 0; round < 3; round++)
            {
                const int Points = 20_000;
                long start = Stopwatch.GetTimestamp();

                for (int k = 0; k < Points; k++)
                    series[k % series.Count].AddValue(Math.Cos(k * 0.01));

                double ns = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1e6 / Points;
                best = Math.Min(best, ns);
                plot.UpdateAxesNow();
            }

            return best;
        });
    }

    #endregion

    #region 1.3 / 2.4 帧率定时器

    [Fact(DisplayName = "1.3 帧率定时器：与释放并发调度不抛异常；释放后调度返回 false")]
    public void HighResolutionTimer_ScheduleRacingDispose_DoesNotThrow()
    {
        for (int round = 0; round < 100; round++)
        {
            HighResolutionTimer timer = new(() => { });
            Exception? failure = null;
            using ManualResetEventSlim started = new();

            Thread scheduler = new(() =>
            {
                try
                {
                    started.Set();
                    for (int i = 0; i < 2_000; i++)
                        timer.Schedule(Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 1000));
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });

            scheduler.Start();
            started.Wait();
            timer.Dispose();
            Assert.True(scheduler.Join(WaitTimeout));
            Assert.Null(failure);
            Assert.False(timer.Schedule(Stopwatch.GetTimestamp()));
        }
    }

    [Fact(DisplayName = "2.4 帧率上限改回 0：释放定时线程，之后的跨线程写入照常到达")]
    public void MaxFramesPerSecondBackToZero_ReleasesTimer_AndDataStillFlows()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            plot.MaxFramesPerSecond = 30;
            Assert.NotNull(plot.FrameTimerIsHighResolution);
        });

        Thread writer = new(() =>
        {
            for (int i = 0; i < 1_000; i++)
                plot.AddValue(i);
        });
        writer.Start();
        Assert.True(writer.Join(WaitTimeout));

        host.Invoke(() =>
        {
            plot.MaxFramesPerSecond = 0;
            Assert.Null(plot.FrameTimerIsHighResolution);
        });

        Thread more = new(() =>
        {
            for (int i = 0; i < 1_000; i++)
                plot.AddValue(i);
        });
        more.Start();
        Assert.True(more.Join(WaitTimeout));

        WaitUntil(host, () => plot.DataCount == 2_000, () => $"数据未全部到达：{host.Invoke(() => plot.DataCount)} / 2000");
        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 2.2 不会绘制时合并写入节流

    [Fact(DisplayName = "2.2 窗口最小化时跨线程写入按 100 ms 节流合并，停止写入后最后一批数据仍会到达")]
    public void Minimized_FlushesAreThrottled_AndTrailingDataArrives()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.EnsureRendered(plot);
            host.Form.WindowState = FormWindowState.Minimized;
        });
        host.Pump(100);

        int flushesBefore = host.Invoke(() => plot.PendingFlushCount);
        long written = 0;

        // 约 1 kHz 逐点写入 1 秒
        Thread writer = new(() =>
        {
            Stopwatch clock = Stopwatch.StartNew();
            long next = 0;

            while (clock.ElapsedMilliseconds < 1_000)
            {
                plot.AddValue(written % 100);
                written++;
                next += 1;

                while (clock.ElapsedMilliseconds < next)
                    Thread.SpinWait(200);
            }
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout));

        WaitUntil(host, () => plot.DataCount == written, () => $"停止写入后数据未全部到达：{host.Invoke(() => plot.DataCount)} / {written}");
        int flushes = host.Invoke(() => plot.PendingFlushCount) - flushesBefore;

        // 节流前每批数据一到就合并一次（约 1000 次）；节流后约 1 秒 / 100 ms ≈ 10 次，加上收尾
        Assert.True(flushes <= 30, $"最小化期间 1 秒内合并写入 {flushes} 次，未按 100 ms 节流。");
        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 2.3 待处理列表收缩

    [Fact(DisplayName = "2.3 突发积压之后，待处理列表在后续小批量写入时收缩")]
    public void PendingBuffers_ShrinkAfterBurst()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        PlotSeries series = host.Invoke(() => plot.DefaultSeries);

        // UI 线程被占用期间积压 30 万点
        host.Invoke(() =>
        {
            Thread burst = new(() =>
            {
                double[] chunk = new double[1_000];
                for (int i = 0; i < 300; i++)
                    series.AddValues(chunk);
            });
            burst.Start();
            Assert.True(burst.Join(WaitTimeout));
        });

        WaitUntil(host, () => series.DataCount == 300_000, () => $"积压数据未到达：{host.Invoke(() => series.DataCount)}");

        for (int round = 1; round <= 4; round++)
        {
            Thread small = new(() => series.AddValues(new double[10]));
            small.Start();
            Assert.True(small.Join(WaitTimeout));
            int expected = 300_000 + (round * 10);
            WaitUntil(host, () => series.DataCount == expected, () => $"小批量数据未到达：{host.Invoke(() => series.DataCount)} / {expected}");
        }

        host.Invoke(() =>
        {
            Assert.True(series.PendingValues.Capacity <= 64 * 1024, $"待处理列表容量 {series.PendingValues.Capacity} 没有收缩。");
            Assert.True(series.DrainingValues.Capacity <= 64 * 1024, $"取走列表容量 {series.DrainingValues.Capacity} 没有收缩。");
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 3.1 ApplyAxisOptions

    [Fact(DisplayName = "3.1 直接修改 AxisOptions 后调用 ApplyAxisOptions 立即生效（含重新固定堆叠原点），无效参数抛异常")]
    public void ApplyAxisOptions_AppliesDirectEdits_AndValidates()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            double[] data = [.. Enumerable.Range(0, 100).Select(i => (double)i)];
            plot.AddValues(data);
            Assert.Equal(0.0, PlotTestHelper.GetXRange(plot).Min, 9);

            plot.AxisOptions.StackedDisplayPercent = 50;
            plot.AxisOptions.YPaddingFraction = 0.25;

            // 直接修改字段不会自己生效
            Assert.Equal(0.0, PlotTestHelper.GetXRange(plot).Min, 9);

            plot.ApplyAxisOptions();

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(102.0, xMax, 9);
            Assert.Equal(51.0, xMin, 9);
            Assert.Equal(51.0, plot.StackedOriginX, 9);

            // Y 轴按可见数据（X 51 ~ 100 → 值 50 ~ 99）统计，新的留白比例 25% 生效：50 − 12.25 ~ 99 + 12.25
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(37.75, yMin, 9);
            Assert.Equal(111.25, yMax, 9);

            // 越界百分比被限制在有效范围内
            plot.AxisOptions.FollowWindowPercent = 500;
            plot.ApplyAxisOptions();
            Assert.Equal(100.0, plot.FollowWindowPercent, 9);

            plot.AxisOptions.EmptyXMax = plot.AxisOptions.EmptyXMin;
            ArgumentException error = Assert.Throws<ArgumentException>(plot.ApplyAxisOptions);
            Assert.Equal(nameof(ScrollingAxisOptions.EmptyXMax), error.ParamName);
            plot.AxisOptions.EmptyXMax = plot.AxisOptions.EmptyXMin + 1;

            plot.AxisOptions.YPaddingFraction = -0.1;
            Assert.Throws<ArgumentException>(plot.ApplyAxisOptions);
            plot.AxisOptions.YPaddingFraction = 0.05;
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 3.2 / 3.3 数据接口

    [Fact(DisplayName = "3.2 PlotDataBuffer.AddRange 有数组重载（C# 12 下不再二义），也能写入自身的 Values")]
    public void PlotDataBuffer_AddRangeArray_AndSelfView()
    {
        PlotDataBuffer buffer = new();
        double[] values = [1, double.NaN, 3];

        // 缺失值也占一个位置（2026-11-07 矩阵审查 Q4）
        Assert.Equal(3, buffer.AddRange(values));
        Assert.Equal(3, buffer.AddRange(buffer.Values));
        Assert.Equal([1.0, double.NaN, 3, 1, double.NaN, 3], buffer.Values);
    }

    [Fact(DisplayName = "3.3 Values 是只读视图：不能转换回 List 修改；快照可以在后台线程读取")]
    public void Values_IsReadOnlyView_AndSnapshotWorksFromBackgroundThread()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries series = host.Invoke(() =>
        {
            PlotSeries added = plot.AddSeries("A");
            added.AddValues(1.0, 2, 3);
            plot.AddValues(7.0);

            Assert.IsNotType<List<double>>(added.Values);
            Assert.IsNotType<List<double>>(plot.Values);
            Assert.Equal([1.0, 2, 3], added.Values);
            return added;
        });

        double[] snapshot = [];
        double[] defaultSnapshot = [];
        Thread reader = new(() =>
        {
            snapshot = series.GetValuesSnapshot();
            defaultSnapshot = plot.GetValuesSnapshot();
        });
        reader.Start();
        Assert.True(reader.Join(WaitTimeout));

        Assert.Equal([1.0, 2, 3], snapshot);
        Assert.Equal([7.0], defaultSnapshot);

        // 快照是副本：之后的写入不影响它
        host.Invoke(() => series.AddValue(4));
        Assert.Equal([1.0, 2, 3], snapshot);
        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 3.5 帮助窗口复用

    [Fact(DisplayName = "3.5 多次显示帮助只打开一个窗口（复用已打开的窗口）")]
    public void ShowHelp_Twice_ReusesSingleDialog()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.ShowHelp();
            plot.ShowHelp();
            plot.ShowHelp();

            PlotHelpDialog dialog = Application.OpenForms.OfType<PlotHelpDialog>().Single();
            try
            {
                Assert.Equal(PlotHelpContent.Text, dialog.Controls.OfType<TextBox>().Single().Text);
            }
            finally
            {
                dialog.Close();
            }

            // 关闭后再次显示会打开新的窗口
            plot.ShowHelp();
            PlotHelpDialog reopened = Application.OpenForms.OfType<PlotHelpDialog>().Single();
            Assert.NotSame(dialog, reopened);
            reopened.Close();
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 3.6 菜单模式项按动作识别

    [Fact(DisplayName = "3.6 菜单：与模式项同名的自定义项不会被当成模式项；模式项改名后仍是勾选项")]
    public void ModeMenuItems_AreIdentifiedByAction_NotByLabel()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        bool customInvoked = false;

        host.Invoke(() =>
        {
            ChinesePlotMenu menu = plot.PlotMenu;

            // 把“跟随模式”改名
            int followIndex = menu.ContextMenuItems.FindIndex(item => item.Label == "跟随模式");
            menu.ContextMenuItems[followIndex] = menu.ContextMenuItems[followIndex] with { Label = "Follow" };

            // 与“堆叠模式”同名的自定义项
            menu.Add("堆叠模式", _ => customInvoked = true);

            plot.Mode = ScrollingMode.Follow;

            using ContextMenuStrip strip = menu.BuildContextMenu(plot.Plot);
            ToolStripMenuItem[] items = [.. strip.Items.OfType<ToolStripMenuItem>()];

            ToolStripMenuItem renamed = items.Single(item => item.Text == "Follow");
            Assert.Equal(ScrollingMode.Follow, renamed.Tag);
            Assert.True(renamed.Checked);

            ToolStripMenuItem[] stacked = [.. items.Where(item => item.Text == "堆叠模式")];
            Assert.Equal(2, stacked.Length);
            ToolStripMenuItem custom = stacked.Single(item => item.Tag is null);
            Assert.False(custom.Checked);

            custom.PerformClick();
            Assert.True(customInvoked);
            Assert.Equal(ScrollingMode.Follow, plot.Mode); // 自定义项不切换模式

            stacked.Single(item => item.Tag is ScrollingMode).PerformClick();
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            menu.SyncModeChecks(strip);
            Assert.False(renamed.Checked);
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 遗留：单遍渲染的坐标轴比较

    [Fact(DisplayName = "ScottPlot 契约：GetAxes 只包含 X / Y 坐标轴（单遍渲染按 X + Y 计数比较坐标轴是否变化）")]
    public void GetAxes_IsExactlyXAndYAxes()
    {
        Plot plot = new();
        plot.Axes.AddRightAxis();
        plot.Axes.AddTopAxis();

        IAxis[] all = [.. plot.Axes.GetAxes()];
        IAxis[] xy = [.. plot.Axes.GetXAxes(), .. plot.Axes.GetYAxes()];

        Assert.Equal(xy.Length, all.Length);
        Assert.All(xy, axis => Assert.Contains(axis, all));
        Assert.IsAssignableFrom<IReadOnlyList<IXAxis>>(plot.Axes.GetXAxes());
        Assert.IsAssignableFrom<IReadOnlyList<IYAxis>>(plot.Axes.GetYAxes());
    }

    #endregion

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
