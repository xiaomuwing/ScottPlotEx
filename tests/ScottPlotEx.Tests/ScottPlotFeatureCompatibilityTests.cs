using ScottPlot;
using Color = ScottPlot.Color;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 与 ScottPlot 原有功能的兼容性：通过 <see cref="ScrollingPlotControl.Plot"/> 直接使用 ScottPlot 的外观、图元、刻度、
/// 数据游标等功能，在数据刷新 / 模式切换 / 清空数据之后仍然有效；并固化控件“接管”的部分（自动滚动维护坐标轴范围）。
/// 第一个测试另外生成 artifacts/scottplot-features.png 供人工核对。
/// </summary>
public class ScottPlotFeatureCompatibilityTests
{
    [Fact(DisplayName = "ScottPlot 外观设置（标题、坐标轴标签、颜色、网格、线型、标记、图例）在数据刷新、模式切换、清空后保持")]
    public void AppearanceSettings_SurviveDataUpdatesModesAndClear()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            Plot p = plot.Plot;

            // 标题与坐标轴标签（含字号、颜色）
            p.Title("ScottPlot 原有功能兼容性", size: 18);
            p.XLabel("时间（秒）", size: 14);
            p.YLabel("温度（°C）", size: 14);

            // 背景、网格、坐标轴颜色
            p.FigureBackground.Color = Color.FromHex("#F4F6FA");
            p.DataBackground.Color = Color.FromHex("#FFFFF0");
            p.Grid.MajorLineColor = Colors.LightGray.WithAlpha(0.6);
            p.Axes.Color(Color.FromHex("#334455"));       // 坐标轴、刻度与标签统一着色
            p.Axes.Bottom.Label.ForeColor = Colors.DarkBlue; // 再单独改 X 轴标签颜色

            // 曲线样式：颜色、线宽、虚线、数据点标记
            PlotSeries temperature = plot.AddSeries("温度", Colors.OrangeRed);
            temperature.LineWidth = 2;
            PlotSeries setpoint = plot.AddSeries("设定值", Colors.SeaGreen);
            setpoint.Signal!.LinePattern = LinePattern.Dashed;
            setpoint.Signal.MarkerShape = MarkerShape.FilledCircle;
            setpoint.Signal.MarkerSize = 4;
            plot.ShowLegend = true;

            temperature.AddValues(Enumerable.Range(0, 60).Select(i => 25 + (3 * Math.Sin(i / 6.0))));
            setpoint.AddValues(Enumerable.Range(0, 60).Select(i => i < 30 ? 24.0 : 26.0));

            // 模式切换、清空再写入
            plot.Mode = ScrollingMode.Follow;
            plot.Mode = ScrollingMode.Stacked;
            plot.ClearData();
            temperature.AddValues(Enumerable.Range(0, 80).Select(i => 25 + (3 * Math.Sin(i / 6.0))));
            setpoint.AddValues(Enumerable.Range(0, 80).Select(i => i < 40 ? 24.0 : 26.0));
            PlotTestHelper.EnsureRendered(plot);

            Assert.Equal("ScottPlot 原有功能兼容性", p.Axes.Title.Label.Text);
            Assert.Equal("时间（秒）", p.Axes.Bottom.Label.Text);
            Assert.Equal("温度（°C）", p.Axes.Left.Label.Text);
            Assert.Equal(Colors.DarkBlue, p.Axes.Bottom.Label.ForeColor);
            Assert.Equal(Color.FromHex("#FFFFF0"), p.DataBackground.Color);
            Assert.Equal(Colors.OrangeRed, temperature.Signal!.Color);
            Assert.Equal(2, temperature.Signal.LineWidth);
            Assert.Equal(LinePattern.Dashed, setpoint.Signal.LinePattern);
            Assert.Equal(MarkerShape.FilledCircle, setpoint.Signal.MarkerShape);
            Assert.True(p.Legend.IsVisible);

            p.SavePng(Path.Combine(TestPaths.ArtifactsDirectory, "scottplot-features.png"), 900, 500);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "ScottPlot 附加图元（参考线、区间、文字、标记、散点）与自动滚动共存，坐标轴只按曲线计算")]
    public void ExtraPlottables_CoexistWithAutoScroll()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            Plot p = plot.Plot;
            var limit = p.Add.HorizontalLine(1000, color: Colors.Red, pattern: LinePattern.Dashed);
            var marker = p.Add.VerticalLine(10, color: Colors.Gray);
            var band = p.Add.HorizontalSpan(5, 8, Colors.Green.WithAlpha(0.2));
            var label = p.Add.Text("报警线", 2, 9);
            var point = p.Add.Marker(5, 5, MarkerShape.FilledDiamond, 10, Colors.Purple);
            var scatter = p.Add.Scatter(new double[] { 1, 2, 3 }, new double[] { 1, 4, 9 });

            plot.AddValues(Enumerable.Range(0, 50).Select(i => (double)(i % 10)));
            plot.Mode = ScrollingMode.Follow;
            plot.AddValues(Enumerable.Range(0, 50).Select(i => (double)(i % 10)));
            PlotTestHelper.EnsureRendered(plot);

            IPlottable[] all = [.. p.GetPlottables()];
            Assert.Contains(limit, all);
            Assert.Contains(marker, all);
            Assert.Contains(band, all);
            Assert.Contains(label, all);
            Assert.Contains(point, all);
            Assert.Contains(scatter, all);

            // 自动滚动的 Y 轴只按曲线数据（0 ~ 9）计算，不会为了 y = 1000 的参考线把曲线压扁
            Assert.True(PlotTestHelper.GetYRange(plot).Max < 20);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "ScottPlot 刻度：自定义刻度格式（例如把采样序号显示成秒）与刻度生成器可用")]
    public void CustomTickFormatter_IsUsed()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            const double SamplesPerSecond = 100;
            plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
            {
                LabelFormatter = x => $"{x / SamplesPerSecond:0.#} s",
            };

            plot.AddValues(Enumerable.Range(0, 1000).Select(i => Math.Sin(i / 50.0)));
            PlotTestHelper.EnsureRendered(plot);
            plot.Refresh(); // 同步绘制一帧：刻度在绘制时由新的刻度生成器生成

            Tick[] ticks = [.. plot.Plot.Axes.Bottom.TickGenerator.Ticks.Where(t => t.IsMajor)];
            Assert.NotEmpty(ticks);
            Assert.All(ticks, t => Assert.EndsWith(" s", t.Label));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "坐标轴范围：堆叠 / 跟随模式由控件维护（手动设置会被下一次数据覆盖），手动模式保持用户设置")]
    public void AxisLimits_AreOwnedByAutoScrollUnlessManual()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(0, 100).Select(i => (double)i));

            // 自动模式：直接调用 ScottPlot 设置范围，下一次数据到达时被自动滚动覆盖
            plot.Plot.Axes.SetLimits(10, 20, -1, 1);
            plot.AddValue(50);
            Assert.Equal(101 + 2, PlotTestHelper.GetXRange(plot).Max, 6);

            // 手动模式：用户设置的范围保持不变
            plot.Mode = ScrollingMode.Manual;
            plot.Plot.Axes.SetLimits(10, 20, -1, 1);
            plot.AddValue(60);
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(10, xMin, 6);
            Assert.Equal(20, xMax, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "ScottPlot 数据游标：GetNearest / GetNearestX 对每条曲线可用")]
    public void NearestPointQueries_WorkForEachSeries()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries low = plot.AddSeries("低");
            PlotSeries high = plot.AddSeries("高");
            low.AddValues(Enumerable.Range(0, 100).Select(i => (double)i));
            high.AddValues(Enumerable.Range(0, 100).Select(i => 1000.0 + i));
            PlotTestHelper.EnsureRendered(plot);

            RenderDetails render = plot.Plot.RenderManager.LastRender;

            DataPoint nearestLow = low.Signal!.GetNearestX(new Coordinates(42, 0), render, 50);
            DataPoint nearestHigh = high.Signal!.GetNearestX(new Coordinates(42, 0), render, 50);

            Assert.Equal(41, nearestLow.Index);   // X = 下标 + 1
            Assert.Equal(42, nearestLow.X, 6);
            Assert.Equal(41, nearestLow.Y, 6);
            Assert.Equal(1041, nearestHigh.Y, 6);
        });

        host.AssertNoUnhandledExceptions();
    }
}
