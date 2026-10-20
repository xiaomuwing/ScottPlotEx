using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace ScottPlotEx.Tests;

/// <summary>
/// 边界行为回归：Reset 后的堆叠原点、隐藏最长曲线、同一 Plot 的 Reset、句柄创建前的跨线程写入、
/// 曲线样式在 Reset 后保留、内置帮助窗口内容。
/// </summary>
public class EdgeCaseRegressionTests
{
    private readonly ITestOutputHelper _output;

    public EdgeCaseRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Reset：显示百分比 < 100 时重新从 0 开始堆叠，新数据不会被旧原点裁掉")]
    public void Reset_AfterStackedPercent_ReestablishesZeroOrigin()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(0, 1000).Select(i => (double)i));
            plot.SetXAxisDisplayPercent(90, ScrollingMode.Stacked);
            Assert.Equal(0.1 * 1002, plot.StackedOriginX, 6);

            plot.Reset();
            Assert.Equal(0, plot.StackedOriginX, 6);
            Assert.Equal(0, plot.DataCount);

            plot.AddValues(Enumerable.Range(0, 10).Select(i => (double)i));

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0, xMin, 6);
            Assert.Equal(12, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "隐藏最长曲线：堆叠百分比按剩余可见曲线重新固定原点，剩余数据不会整段不可见")]
    public void HidingLongestSeries_WithStackedPercent_KeepsRemainingDataVisible()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries shortSeries = plot.AddSeries("short");
            shortSeries.AddValues(Enumerable.Range(0, 10).Select(i => (double)i));

            plot.AddValues(Enumerable.Range(0, 1000).Select(i => (double)i));
            plot.SetXAxisDisplayPercent(90, ScrollingMode.Stacked);

            plot.DefaultSeries.IsVisible = false;

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            _output.WriteLine($"after hide: origin={plot.StackedOriginX}, x={xMin}..{xMax}");

            Assert.True(xMax > xMin, "X 轴退化");
            Assert.Equal(12, xMax, 6);
            Assert.Equal(1.2, plot.StackedOriginX, 6); // 12 × (1 − 90%)
            Assert.True(xMin < 2, "剩余曲线的数据被整段裁掉");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "Reset(当前 Plot)：不会重复挂接曲线图元（同一条曲线不会被画两次）")]
    public void Reset_WithSamePlot_DoesNotDuplicateSignals()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddSeries("extra");
            int before = plot.Plot.GetPlottables<ScottPlot.Plottables.Signal>().Count();
            plot.Reset(plot.Plot, disposeOldPlot: false);
            int after = plot.Plot.GetPlottables<ScottPlot.Plottables.Signal>().Count();

            Assert.Equal(before, after);
            Assert.All(plot.Series, series => Assert.NotNull(series.Signal));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "句柄创建前：后台线程写入进入待处理列表，句柄创建后全部到达")]
    public void BackgroundWrites_BeforeHandleCreation_AreQueuedAndFlushed()
    {
        ScrollingPlotControl? plot = null;
        using ManualResetEventSlim created = new(false);
        using ManualResetEventSlim go = new(false);
        using ManualResetEventSlim wrote = new(false);
        using ManualResetEventSlim dataArrived = new(false);
        int observedCount = -1;
        Exception? failure = null;

        Thread ui = new(() =>
        {
            try
            {
                plot = new ScrollingPlotControl();
                created.Set();
                go.Wait();

                using Form form = new() { ClientSize = new Size(600, 400), ShowInTaskbar = false };
                form.Controls.Add(plot);

                using System.Windows.Forms.Timer checkTimer = new() { Interval = 20 };
                checkTimer.Tick += (_, _) =>
                {
                    if (plot.DataCount != 1)
                        return;

                    observedCount = plot.DataCount;
                    dataArrived.Set();
                    checkTimer.Stop();
                    form.Close();
                };
                checkTimer.Start();

                using System.Windows.Forms.Timer fallbackTimer = new() { Interval = 5000 };
                fallbackTimer.Tick += (_, _) => form.Close();
                fallbackTimer.Start();

                Application.Run(form);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();

        Assert.True(created.Wait(TimeSpan.FromSeconds(10)));

        Thread writer = new(() =>
        {
            try
            {
                plot!.AddValue(42);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                wrote.Set();
            }
        });
        writer.Start();
        Assert.True(wrote.Wait(TimeSpan.FromSeconds(10)));

        // 句柄创建前不能在后台线程直接写 List；数据应停在待处理列表里
        Assert.Equal(0, plot!.DataCount);
        Assert.Single(plot.DefaultSeries.PendingValues);

        go.Set();
        Assert.True(dataArrived.Wait(TimeSpan.FromSeconds(10)), "句柄创建后排队的数据没有到达");
        Assert.Equal(1, observedCount);
        Assert.True(ui.Join(TimeSpan.FromSeconds(10)), "UI 线程未退出");
        Assert.Null(failure);
    }

    [Fact(DisplayName = "句柄创建前：后台线程 AddSeries 推迟到句柄创建后再挂接 Plot")]
    public void AddSeries_BeforeHandleCreation_IsDeferredUntilHandleCreated()
    {
        ScrollingPlotControl? plot = null;
        PlotSeries? series = null;
        using ManualResetEventSlim created = new(false);
        using ManualResetEventSlim go = new(false);
        using ManualResetEventSlim attached = new(false);
        Exception? failure = null;

        Thread ui = new(() =>
        {
            try
            {
                plot = new ScrollingPlotControl();
                created.Set();
                go.Wait();

                using Form form = new() { ClientSize = new Size(600, 400), ShowInTaskbar = false };
                form.Controls.Add(plot);

                using System.Windows.Forms.Timer checkTimer = new() { Interval = 20 };
                checkTimer.Tick += (_, _) =>
                {
                    if (series?.Signal is null)
                        return;

                    attached.Set();
                    checkTimer.Stop();
                    form.Close();
                };
                checkTimer.Start();

                using System.Windows.Forms.Timer fallbackTimer = new() { Interval = 5000 };
                fallbackTimer.Tick += (_, _) => form.Close();
                fallbackTimer.Start();

                Application.Run(form);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();

        Assert.True(created.Wait(TimeSpan.FromSeconds(10)));

        PlotSeries? added = null;
        Thread addThread = new(() => added = plot!.AddSeries("bg"));
        addThread.Start();
        Assert.True(addThread.Join(TimeSpan.FromSeconds(10)), "AddSeries 在句柄创建前卡住");
        series = added;

        // 句柄创建前不碰 Plot，挂接推迟到 OnHandleCreated
        Assert.Null(series!.Signal);

        go.Set();
        Assert.True(attached.Wait(TimeSpan.FromSeconds(10)), "句柄创建后曲线没有挂接到 Plot");
        Assert.True(ui.Join(TimeSpan.FromSeconds(10)), "UI 线程未退出");
        Assert.Null(failure);
    }

    [Fact(DisplayName = "Reset：通过 Signal 直接设置的线型与标记样式会保留")]
    public void SeriesStyles_SurviveReset()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries series = plot.AddSeries("styled");
            series.Signal!.LinePattern = ScottPlot.LinePattern.Dashed;
            series.Signal.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
            series.Signal.MarkerSize = 4;

            plot.Reset();

            Assert.Equal(ScottPlot.LinePattern.Dashed, series.Signal!.LinePattern);
            Assert.Equal(ScottPlot.MarkerShape.FilledCircle, series.Signal.MarkerShape);
            Assert.Equal(4, series.Signal.MarkerSize);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "坐标轴缓存：用户替换默认 X 轴后，自动滚动会重新解析并作用到新坐标轴")]
    public void ReplacingDefaultXAxis_RefreshesCachedAxis()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues([1.0, 2, 3]);

            plot.Plot.Axes.Remove(ScottPlot.Edge.Bottom);
            ScottPlot.AxisPanels.BottomAxis replacement = plot.Plot.Axes.AddBottomAxis();

            plot.AddValue(4);

            Assert.True(replacement.Max > replacement.Min, "自动滚动没有作用到替换后的 X 轴");
            Assert.Equal(0, replacement.Min, 6);
            Assert.Equal(6, replacement.Max, 6); // 4 个点 + 2
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "内置帮助：使用 PlotHelpContent 的完整中文说明窗口，而不是旧版鼠标提示")]
    public void BuiltInHelp_UsesRichDialog()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.ShowHelp();

            PlotHelpDialog dialog = Application.OpenForms.OfType<PlotHelpDialog>().Single();
            try
            {
                TextBox textBox = dialog.Controls.OfType<TextBox>().Single();
                Assert.Equal(PlotHelpContent.Text, textBox.Text);
                Assert.Contains("X 轴单位", textBox.Text);
            }
            finally
            {
                dialog.Close();
            }
        });

        host.AssertNoUnhandledExceptions();
    }
}
