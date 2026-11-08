using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ScottPlot;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using Xunit;

namespace ScottPlotEx.Tests;

/// <summary>
/// 2026-11-08 第三轮复审（docs/fix-plan-2026-11-08.md）的回归测试：
/// 第 1 项后台 AddSeries 竞争、第 2 项空曲线让自动缩放得到非法 Y、第 3 项清空后 Y 锁定（含后台“清空后马上写入”）、
/// 第 4 项没有数据时的堆叠百分比、第 5 项 YScale / YOffset 参与自动 Y。
/// </summary>
public class FixPlan20261108Tests
{
    private static WinFormsTestHost Start(out ScrollingPlotControl plot, out Control canvas, int points = 100)
    {
        WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(900, 500));
        ScrollingPlotControl control = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            for (int i = 0; i < points; i++)
                control.AddValue(10 + (10 * Math.Sin(i / 7.0)));

            PlotTestHelper.EnsureRendered(control);
        });

        host.Pump(50);
        plot = control;
        canvas = PlotTestHelper.GetInputCanvas(control);
        return host;
    }

    /// <summary>Y 轴自动范围：数据最小 / 最大值各向外 5%。</summary>
    private static void AssertAutoY(ScrollingPlotControl plot, double dataMin, double dataMax)
    {
        double span = dataMax - dataMin;
        (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
        Assert.Equal(dataMin - (0.05 * span), yMin, 6);
        Assert.Equal(dataMax + (0.05 * span), yMax, 6);
    }

    /// <summary>左键从中间偏右上拖到偏左下：仍能看到最新数据 → 继续滚动，记下 X 偏移并锁定 Y。</summary>
    private static void DragToLockY(ScrollingPlotControl plot, Control canvas)
    {
        PixelRect rect = PlotTestHelper.EnsureRendered(plot);
        MouseInput.Drag(canvas, PlotTestHelper.PointInDataArea(rect, 0.6, 0.3), PlotTestHelper.PointInDataArea(rect, 0.4, 0.8), MouseButtons.Left);

        Assert.Equal(ScrollingMode.Stacked, plot.Mode);
        Assert.True(plot.IsYAxisLocked);
        Assert.True(PlotTestHelper.GetXRange(plot).Min > 1, "拖动后应留下 X 偏移。");
    }

    #region 第 1 项：后台 AddSeries 与 UI 线程遍历曲线集合

    [Fact(DisplayName = "1 后台线程连续添加曲线时 UI 线程照常写入数据：不抛“集合已修改”，曲线全部挂接")]
    public void AddSeries_FromBackgroundThread_DoesNotBreakUiIteration()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);
        const int SeriesCount = 2000;
        int created = 0;

        Thread creator = new(() =>
        {
            for (int i = 0; i < SeriesCount; i++)
            {
                plot.AddSeries("s" + i);
                Interlocked.Increment(ref created);
            }
        })
        { IsBackground = true };

        int failures = 0;
        string? firstFailure = null;

        creator.Start();

        // UI 线程上持续写入：每次写入都会遍历曲线集合（坐标轴计算、合并更新判断），与后台的 AddSeries 并发
        host.Invoke(() =>
        {
            Stopwatch clock = Stopwatch.StartNew();

            while (creator.IsAlive && clock.ElapsedMilliseconds < 10_000)
            {
                try
                {
                    plot.AddValue(1);
                }
                catch (Exception exception)
                {
                    failures++;
                    firstFailure ??= exception.ToString();
                }
            }
        });

        Assert.True(creator.Join(TimeSpan.FromSeconds(30)), "后台线程没有结束。");
        Assert.True(failures == 0, $"UI 线程写入失败 {failures} 次：{firstFailure}");
        Assert.Equal(SeriesCount, created);

        host.Pump(300);
        host.Invoke(() =>
        {
            Assert.Equal(SeriesCount + 1, plot.Series.Count);
            Assert.All(plot.Series, series => Assert.NotNull(series.Signal));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "1 后台线程添加曲线并立即写入、清空：数据与清空都按顺序生效")]
    public void AddSeries_FromBackgroundThread_ThenWriteAndClear()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);
        PlotSeries? series = null;

        Thread worker = new(() =>
        {
            series = plot.AddSeries("后台");
            series.AddValues(1, 2, 3);
            series.Clear();
            series.AddValues(7, 8);
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        host.Pump(300);

        host.Invoke(() =>
        {
            Assert.Contains(series!, plot.Series);
            Assert.Equal(new double[] { 7, 8 }, series!.Values);
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 第 2 项：空曲线 / 全缺失曲线与自动缩放

    [Theory(DisplayName = "2 有空曲线或全缺失曲线时整体显示（菜单 / 中键 / A 键）：Y 有限且为需求 1 的范围")]
    [InlineData("菜单", "空曲线")]
    [InlineData("菜单", "全缺失")]
    [InlineData("中键", "空曲线")]
    [InlineData("中键", "全缺失")]
    [InlineData("A 键", "空曲线")]
    [InlineData("A 键", "全缺失")]
    public void Autoscale_WithEmptyOrAllMissingSeries_KeepsFiniteRequirementView(string trigger, string kind)
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            PlotSeries extra = plot.AddSeries(kind);
            if (kind == "全缺失")
                extra.AddValues(double.NaN, double.NaN, double.PositiveInfinity);

            plot.StackedDisplayPercent = 50; // 从非默认视图开始，确认整体显示真的执行了
            PlotTestHelper.EnsureRendered(plot);

            switch (trigger)
            {
                case "菜单":
                    plot.PlotMenu.Autoscale(plot.Plot);
                    break;
                case "中键":
                    MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(PlotTestHelper.EnsureRendered(plot), 0.5, 0.5), MouseButtons.Middle);
                    break;
                default:
                    MouseInput.KeyDown(canvas, Keys.A);
                    MouseInput.KeyUp(canvas, Keys.A);
                    break;
            }

            Assert.True(plot.Plot.Axes.GetLimits().IsReal);
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(plot.DataCount + 2, xMax, 6);

            double[] values = plot.Values.ToArray();
            AssertAutoY(plot, values.Min(), values.Max());
        });

        host.Pump(100);
        host.Invoke(() => Assert.True(plot.Plot.Axes.GetLimits().IsReal, "绘制之后坐标轴仍应有效。"));
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "2 新建的空控件菜单“自动缩放”：保持稳定空视图")]
    public void MenuAutoscale_OnEmptyControl_KeepsEmptyView()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _, points: 0);

        host.Invoke(() =>
        {
            plot.PlotMenu.Autoscale(plot.Plot);

            ScrollingAxisOptions options = plot.AxisOptions;
            Assert.True(plot.Plot.Axes.GetLimits().IsReal);
            Assert.Equal((options.EmptyXMin, options.EmptyXMax), PlotTestHelper.GetXRange(plot));
            Assert.Equal((options.EmptyYMin, options.EmptyYMax), PlotTestHelper.GetYRange(plot));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "2 兼容 ScottPlot：直接调用 Plot.Axes.AutoScale() 时空曲线不产生 ±∞")]
    public void ScottPlotAutoScale_IgnoresEmptySeries()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);

        host.Invoke(() =>
        {
            plot.AddSeries("空");
            plot.AddSeries("全缺失").AddValues(double.NaN, double.NaN);

            plot.Plot.Axes.AutoScale();
            AxisLimits limits = plot.Plot.Axes.GetLimits();

            Assert.True(limits.IsReal, $"自动缩放结果：{limits}");
            Assert.True(limits.Bottom < 0 && limits.Top > 20, "范围应覆盖默认曲线的数据（0 ~ 20）。");
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 第 3 项：清空后的 Y 锁定

    [Fact(DisplayName = "3 Y 锁定后清空唯一曲线：解除锁定，写入不同量级的数据 Y 自动适配")]
    public void ClearingOnlySeries_UnlocksY()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            DragToLockY(plot, canvas);

            plot.DefaultSeries.Clear();
            Assert.False(plot.IsYAxisLocked);

            for (int i = 0; i < 50; i++)
                plot.AddValue(1000 + i);

            Assert.False(plot.IsYAxisLocked);
            Assert.Equal((0.0, 52.0), PlotTestHelper.GetXRange(plot));
            AssertAutoY(plot, 1000, 1049);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "3 清空其中一条曲线、其它可见曲线仍有数据：保留 Y 锁定")]
    public void ClearingOneOfSeveralSeries_KeepsYLock()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            PlotSeries other = plot.AddSeries("其它");
            other.AddValues(Enumerable.Range(0, 100).Select(i => 10.0 + Math.Cos(i / 5.0)).ToArray());
            PlotTestHelper.EnsureRendered(plot);

            DragToLockY(plot, canvas);
            (double, double) locked = PlotTestHelper.GetYRange(plot);

            other.Clear();

            Assert.True(plot.IsYAxisLocked);
            Assert.Equal(locked, PlotTestHelper.GetYRange(plot));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Theory(DisplayName = "3 “清空后马上写入”：在 UI 线程上与从其它线程（同一帧合并）结果相同——X 偏移复位、Y 解除锁定")]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearThenWrite_SameResultOnUiAndBackgroundThread(bool fromBackground)
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);
        host.Invoke(() => DragToLockY(plot, canvas));

        double[] fresh = [.. Enumerable.Range(0, 50).Select(i => 1000.0 + i)];

        if (fromBackground)
        {
            Thread worker = new(() =>
            {
                plot.DefaultSeries.Clear();
                plot.DefaultSeries.AddValues(fresh);
            });

            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            host.Pump(300);
        }
        else
        {
            host.Invoke(() =>
            {
                plot.DefaultSeries.Clear();
                plot.DefaultSeries.AddValues(fresh);
            });
        }

        host.Invoke(() =>
        {
            Assert.Equal(50, plot.DataCount);
            Assert.False(plot.IsYAxisLocked);
            Assert.Equal((0.0, 52.0), PlotTestHelper.GetXRange(plot));
            AssertAutoY(plot, 1000, 1049);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "3 其它线程 ClearData 后马上写入（同一帧合并）：堆叠原点从 0 开始，与 UI 线程一致")]
    public void BackgroundClearDataThenWrite_StartsStackedFromZero()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);
        host.Invoke(() => plot.StackedDisplayPercent = 50);

        Thread worker = new(() =>
        {
            plot.ClearData();
            plot.AddValues(Enumerable.Range(0, 80).Select(i => (double)i).ToArray());
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        host.Pump(300);

        host.Invoke(() =>
        {
            Assert.Equal(80, plot.DataCount);
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);
            Assert.Equal((0.0, 82.0), PlotTestHelper.GetXRange(plot));
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 第 4 项：没有数据时的堆叠显示百分比

    [Theory(DisplayName = "4 没有数据时设置小于 100 的堆叠百分比不生效（保持 100）；大于 100 照常生效")]
    [InlineData(50.0, 100.0)]
    [InlineData(1.0, 100.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(240.0, 240.0)]
    public void StackedPercent_WithoutData(double requested, double expected)
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _, points: 0);

        host.Invoke(() =>
        {
            plot.StackedDisplayPercent = requested;
            Assert.Equal(expected, plot.StackedDisplayPercent, 9);

            plot.SetXAxisDisplayPercent(requested, ScrollingMode.Stacked);
            Assert.Equal(expected, plot.StackedDisplayPercent, 9);

            plot.AxisOptions.StackedDisplayPercent = requested;
            plot.ApplyAxisOptions();
            Assert.Equal(expected, plot.StackedDisplayPercent, 9);

            for (int i = 0; i < 100; i++)
                plot.AddValue(i);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);

            if (expected <= 100)
                Assert.Equal(102.0, xMax, 6); // 显示全部数据，读出的百分比与画面一致
            else
                Assert.True(xMax > 102.0, "大于 100 时右侧预留空白。");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Theory(DisplayName = "4 清空数据后堆叠百分比恢复为 100（ClearData / Reset / 清空唯一曲线）")]
    [InlineData("ClearData", 50.0)]
    [InlineData("ClearData", 240.0)]
    [InlineData("Reset", 50.0)]
    [InlineData("PlotSeries.Clear", 50.0)]
    public void StackedPercent_ResetsAfterClearingAllData(string how, double percent)
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);

        host.Invoke(() =>
        {
            plot.StackedDisplayPercent = percent;
            Assert.Equal(percent, plot.StackedDisplayPercent, 9);

            switch (how)
            {
                case "ClearData":
                    plot.ClearData();
                    break;
                case "Reset":
                    plot.Reset();
                    break;
                default:
                    plot.DefaultSeries.Clear();
                    break;
            }

            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);

            for (int i = 0; i < 100; i++)
                plot.AddValue(i);

            Assert.Equal((0.0, 102.0), PlotTestHelper.GetXRange(plot));

            // 有数据之后照常设置
            plot.StackedDisplayPercent = 50;
            Assert.Equal(50.0, plot.StackedDisplayPercent, 9);
            Assert.Equal(51.0, PlotTestHelper.GetXRange(plot).Min, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "4 只清空其中一条曲线、其它可见曲线仍有数据：堆叠百分比保留")]
    public void StackedPercent_KeptWhenOtherSeriesStillHaveData()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);

        host.Invoke(() =>
        {
            PlotSeries other = plot.AddSeries("其它");
            other.AddValues(Enumerable.Range(0, 50).Select(i => (double)i).ToArray());

            plot.StackedDisplayPercent = 50;
            other.Clear();

            Assert.Equal(50.0, plot.StackedDisplayPercent, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region 第 5 项：YScale / YOffset

    [Theory(DisplayName = "5 Signal.Data.YScale / YOffset 参与自动 Y 轴：按换算后的值统计（含负的缩放）")]
    [InlineData(100.0, 5.0)]
    [InlineData(-2.0, 0.0)]
    [InlineData(1.0, 1000.0)]
    public void YScaleAndOffset_AreIncludedInAutoY(double scale, double offset)
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _, points: 0);

        host.Invoke(() =>
        {
            for (int i = 0; i < 100; i++)
                plot.AddValue(i);

            plot.Signal!.Data.YScale = scale;
            plot.Signal.Data.YOffset = offset;
            plot.AddValue(100); // 下一次写入时按新的换算重算

            double a = offset;
            double b = (100 * scale) + offset;
            AssertAutoY(plot, Math.Min(a, b), Math.Max(a, b));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "5 跟随模式按窗口统计时同样换算；修改后调用 ApplyAxisOptions() 立即重算")]
    public void YScale_InFollowMode_AndApplyAxisOptions()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _, points: 0);

        host.Invoke(() =>
        {
            for (int i = 1; i <= 100; i++)
                plot.AddValue(i);

            plot.Mode = ScrollingMode.Follow; // 可见第 72 ~ 100 个点
            plot.Signal!.Data.YScale = 10;
            plot.ApplyAxisOptions();

            AssertAutoY(plot, 720, 1000);
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion
}
