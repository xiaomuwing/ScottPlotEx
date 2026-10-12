using System.Diagnostics;
using System.Drawing;
using ScottPlotEx.Controls;
using ScottPlotEx.Demo;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 在测试进程内驱动演示程序的“多曲线测试”窗口：预设场景、UI 定时器与后台线程两种写入方式、
/// 显示隐藏 / 删除 / 停止，以及坐标轴按可见曲线计算。
/// </summary>
public class MultiSeriesDemoTests
{
    private static readonly Size FormSize = new(1320, 780);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "多曲线测试窗口：量级差别大的场景——两种写入方式都有数据，Y 轴取并集，隐藏 / 删除后重算")]
    public void DifferentMagnitudesPreset_Works()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MultiSeriesTestForm(), FormSize);
        MultiSeriesTestForm form = host.ControlAs<MultiSeriesTestForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);

        host.Invoke(() => form.ApplyPreset(MultiSeriesTestForm.Preset.DifferentMagnitudes));

        // 温度走 UI 定时器（UI 线程同步写入），压力和振动走后台线程（跨线程合并写入）
        WaitUntil(host, () => form.Feeders.Count == 3 && form.Feeders.All(f => f.Series.DataCount > 20), "三条曲线都应收到数据");

        host.Invoke(() =>
        {
            Assert.Equal(CurveWriter.UiTimer, form.Feeders[0].Parameters.Writer);
            Assert.Equal(CurveWriter.BackgroundThread, form.Feeders[1].Parameters.Writer);
            Assert.True(plot.ShowLegend);
            Assert.Equal(4, plot.Series.Count); // 默认曲线 + 三条

            // Y 轴取并集：覆盖振动（约 0）与压力（约 3000）
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.True(yMin < 0 && yMax > 2900, $"Y 轴 {yMin} ~ {yMax} 没有覆盖全部曲线。");

            // 隐藏压力：Y 轴只剩温度（约 25）与振动（约 0）
            form.Feeders[1].Series.IsVisible = false;
            Assert.True(PlotTestHelper.GetYRange(plot).Max < 100);

            // 删除振动：曲线集合与图元同步减少
            form.RemoveCurve(form.Feeders[2]);
            Assert.Equal(2, form.Feeders.Count);
            Assert.Equal(3, plot.Series.Count);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线测试窗口：长短不一的场景——X 轴取最长的可见曲线，停止后不再写入")]
    public void DifferentLengthsPreset_UsesLongestVisibleCurve()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MultiSeriesTestForm(), FormSize);
        MultiSeriesTestForm form = host.ControlAs<MultiSeriesTestForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);

        host.Invoke(() => form.ApplyPreset(MultiSeriesTestForm.Preset.DifferentLengths));
        WaitUntil(host, () => form.Feeders.All(f => f.Series.DataCount > 5) && form.Feeders[2].Series.DataCount > 200, "三条曲线都应收到数据");

        host.Invoke(() =>
        {
            foreach (CurveFeeder feeder in form.Feeders)
                feeder.Stop();
        });

        host.Pump(300); // 让停止前排队的数据全部写完

        host.Invoke(() =>
        {
            int[] counts = [.. form.Feeders.Select(f => f.Series.DataCount)];
            Assert.True(counts[2] > counts[1] && counts[1] > counts[0], $"点数应为 慢 < 中 < 快：{string.Join(", ", counts)}");

            Assert.Equal(counts[2] + 2, PlotTestHelper.GetXRange(plot).Max, 6);

            // 隐藏最长的一条：X 轴缩短到第二长的曲线
            form.Feeders[2].Series.IsVisible = false;
            Assert.Equal(counts[1] + 2, PlotTestHelper.GetXRange(plot).Max, 6);
        });

        int[] before = host.Invoke(() => form.Feeders.Select(f => f.Series.DataCount).ToArray());
        host.Pump(300);
        int[] after = host.Invoke(() => form.Feeders.Select(f => f.Series.DataCount).ToArray());
        Assert.Equal(before, after); // 已停止：不再增长

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线测试窗口：10 条 × 1000 点/秒——跟随模式固定 60 秒窗口，全部曲线持续接收数据")]
    public void StressPreset_AllCurvesReceiveData()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MultiSeriesTestForm(), FormSize);
        MultiSeriesTestForm form = host.ControlAs<MultiSeriesTestForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);

        host.Invoke(() => form.ApplyPreset(MultiSeriesTestForm.Preset.TenCurvesStress));
        WaitUntil(host, () => form.Feeders.Count == 10 && form.Feeders.All(f => f.Series.DataCount > 500), "十条曲线都应收到数据");

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Follow, plot.Mode);
            Assert.Equal(60_000, plot.FollowWindowPoints);

            int longest = form.Feeders.Max(f => f.Series.DataCount);
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(longest + 2, xMax, 6);
            Assert.Equal(60_000, xMax - xMin, 6);

            foreach (CurveFeeder feeder in form.Feeders)
                feeder.Stop();
        });

        host.AssertNoUnhandledExceptions();
    }

    private static void WaitUntil(WinFormsTestHost host, Func<bool> condition, string message)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (host.Invoke(condition))
                return;

            host.Pump(50);
        }

        Assert.Fail(message);
    }
}
