using System.Diagnostics;
using System.Drawing;
using ScottPlotEx.Controls;
using ScottPlotEx.Demo;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 在测试进程内驱动演示程序验证 X 轴单位：“多曲线测试”窗口的单位选择与“长短不一”预设在时间上对齐、
/// 跟随窗口按当前单位生效，“10 曲线流式演示”使用相对时间单位。
/// </summary>
public class XAxisUnitDemoTests
{
    private static readonly Size FormSize = new(1320, 780);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "多曲线测试窗口·X 轴单位：“长短不一”在相对时间下三条曲线的最新数据在时间上对齐，切回点数后按序号")]
    public void DifferentLengthsPreset_AlignsInTime()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MultiSeriesTestForm(), FormSize);
        MultiSeriesTestForm form = host.ControlAs<MultiSeriesTestForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);

        host.Invoke(() =>
        {
            form.ApplyPreset(MultiSeriesTestForm.Preset.DifferentLengths);
            form.SetXAxisUnit(XAxisUnit.RelativeTime);
            Assert.Equal(XAxisUnit.RelativeTime, plot.XAxisUnit);
            Assert.IsType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);

            // 每条曲线的采样间隔由它的“点/秒”决定
            Assert.Equal([10.0, 50, 200], form.Feeders.Select(f => f.Series.SampleRate));
        });

        WaitUntil(host, () => form.Feeders.All(f => f.Series.DataCount > 10) && form.Feeders[2].Series.DataCount > 400, "三条曲线都应收到数据");

        host.Invoke(() =>
        {
            foreach (CurveFeeder feeder in form.Feeders)
                feeder.Stop();
        });

        host.Pump(300); // 让停止前排队的数据全部写完

        host.Invoke(() =>
        {
            PlotSeries[] series = [.. form.Feeders.Select(f => f.Series)];
            int[] counts = [.. series.Select(s => s.DataCount)];
            double[] latest = [.. series.Select(s => s.LatestX)];

            // 点数相差约 20 倍，但最新数据点的时间几乎相同（误差约一个慢曲线采样间隔 + 定时器抖动）
            Assert.True(counts[2] > counts[0] * 8, $"点数应相差悬殊：{string.Join(", ", counts)}");
            Assert.True(latest.Max() - latest.Min() < 0.5, $"三条曲线的最新 X 应在时间上对齐：{string.Join(", ", latest.Select(x => x.ToString("0.###")))}");
            Assert.All(series, s => Assert.InRange(s.GetX(0), 0, 0.5)); // 起始时间都在开始写入后不久

            // X 轴右端 = 最新 X 的最大值 + 2 个最大的采样间隔（慢曲线 0.1 秒）：跟着最快的曲线平滑前进
            double expectedRight = series.Max(s => s.LatestX) + (2 * series.Max(s => 1 / s.SampleRate));
            Assert.Equal(expectedRight, PlotTestHelper.GetXRange(plot).Max, 6);

            // 切回点数单位：X 轴取最长曲线的点数 + 2
            form.SetXAxisUnit(XAxisUnit.Samples);
            Assert.Equal(counts.Max() + 2, PlotTestHelper.GetXRange(plot).Max, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线测试窗口·X 轴单位：10 条 × 1000 点/秒预设在时间单位下跟随窗口为 60 秒")]
    public void StressPreset_UsesTimeWindowInTimeUnits()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MultiSeriesTestForm(), FormSize);
        MultiSeriesTestForm form = host.ControlAs<MultiSeriesTestForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);

        host.Invoke(() =>
        {
            form.UseCurrentTimeAsStartTime(); // 绝对时间需要先设置起始时间
            form.SetXAxisUnit(XAxisUnit.AbsoluteTime);
            DateTime before = DateTime.Now;
            form.ApplyPreset(MultiSeriesTestForm.Preset.TenCurvesStress);
            Assert.Equal(XAxisUnit.AbsoluteTime, plot.XAxisUnit); // 预设不改变用户选择的单位
            Assert.True(plot.DefaultStartTime >= before, "应用预设 = 重新开始测量：已设置的起始时间更新为当前时间。");
        });

        WaitUntil(host, () => form.Feeders.Count == 10 && form.Feeders.All(f => f.Series.DataCount > 500), "十条曲线都应收到数据");

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Follow, plot.Mode);
            Assert.Equal(TimeSpan.FromSeconds(60), plot.FollowWindowDuration);
            Assert.Equal(60_000, plot.FollowWindowPoints);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(60, xMax - xMin, 6);

            // 切到点数单位：同一个预设按 60000 点
            form.SetXAxisUnit(XAxisUnit.Samples);
            (xMin, xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(60_000, xMax - xMin, 6);

            foreach (CurveFeeder feeder in form.Feeders)
                feeder.Stop();
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "多曲线测试窗口·绝对时间：没有起始时间时不能选择（保持原单位并说明原因），设置后可选；绝对时间下不能清除起始时间")]
    public void AbsoluteTime_RequiresStartTimeInDemo()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MultiSeriesTestForm(), FormSize);
        MultiSeriesTestForm form = host.ControlAs<MultiSeriesTestForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);

        WaitUntil(host, () => form.Feeders.Count == 3 && form.Feeders.All(f => f.Series.DataCount > 0), "默认场景的曲线应收到数据");

        host.Invoke(() =>
        {
            form.SetXAxisUnit(XAxisUnit.RelativeTime);
            Assert.Null(plot.DefaultStartTime);

            // 没有起始时间：选择绝对时间被拒绝，保持相对时间，并提示原因
            form.SetXAxisUnit(XAxisUnit.AbsoluteTime);
            Assert.Equal(XAxisUnit.RelativeTime, plot.XAxisUnit);
            Assert.Contains("起始时间", form.Notice);

            // 使用当前时间后可以选择
            DateTime before = DateTime.Now;
            form.UseCurrentTimeAsStartTime();
            Assert.InRange(plot.DefaultStartTime!.Value, before, DateTime.Now);
            form.SetXAxisUnit(XAxisUnit.AbsoluteTime);
            Assert.Equal(XAxisUnit.AbsoluteTime, plot.XAxisUnit);
            Assert.Equal(plot.DefaultStartTime, plot.TimeReference);

            // 绝对时间下不能清除起始时间
            form.SetStartTime(null);
            Assert.NotNull(plot.DefaultStartTime);
            Assert.Equal(XAxisUnit.AbsoluteTime, plot.XAxisUnit);
            Assert.Contains("不能清除起始时间", form.Notice);

            // 绝对时间下添加曲线使用起始时间
            CurveFeeder added = form.AddCurve("新曲线", new CurveParameters(Waveform.Sine, 1, 0, 50, CurveWriter.UiTimer, ScottPlot.Colors.Black));
            Assert.Equal(plot.DefaultStartTime, added.Series.StartTime);

            // 切回相对时间后可以清除
            form.SetXAxisUnit(XAxisUnit.RelativeTime);
            form.SetStartTime(null);
            Assert.Null(plot.DefaultStartTime);

            foreach (CurveFeeder feeder in form.Feeders)
                feeder.Stop();
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "10 曲线流式演示：使用相对时间单位与 60 秒时间窗口（不再自定义刻度格式）")]
    public void StreamingDemo_UsesRelativeTime()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new StreamingDemoForm(), new Size(1180, 720));
        StreamingDemoForm form = host.ControlAs<StreamingDemoForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);

        WaitUntil(host, () => plot.Series.Count == 11 && plot.Series.Skip(1).All(s => s.DataCount > 200), "十条曲线都应收到数据");

        host.Invoke(() =>
        {
            Assert.Equal(XAxisUnit.RelativeTime, plot.XAxisUnit);
            Assert.Equal(ScrollingMode.Follow, plot.Mode);
            Assert.Equal(TimeSpan.FromSeconds(60), plot.FollowWindowDuration);
            Assert.IsType<TimeAxisTickGenerator>(plot.Plot.Axes.Bottom.TickGenerator);
            Assert.All(plot.Series.Skip(1), s => Assert.Equal(1000, s.SampleRate, 9));

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(60, xMax - xMin, 6);
            Assert.InRange(xMax, 0.1, 30); // 刚开始采集：右端是距第一个数据点的秒数，而不是点数
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
