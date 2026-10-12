using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>需求 8：高强度操作下的稳定性验证（不允许出现未处理异常 / 非法坐标）。</summary>
public class StressAndRobustnessTests
{
    [Fact(DisplayName = "压力测试：随机鼠标操作 + 数据更新，无未处理异常且坐标始终有效")]
    public void RandomMouseAndDataUpdates_DoNotCrash()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Random random = new(20260920);
        Control canvas = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 50);
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        for (int iteration = 0; iteration < 150; iteration++)
        {
            host.Invoke(() =>
            {
                ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);

                Point from = PlotTestHelper.PointInDataArea(rect, 0.05 + (random.NextDouble() * 0.9), 0.05 + (random.NextDouble() * 0.9));
                Point to = PlotTestHelper.PointInDataArea(rect, 0.05 + (random.NextDouble() * 0.9), 0.05 + (random.NextDouble() * 0.9));

                MouseButtons button = random.Next(10) switch
                {
                    0 => MouseButtons.Right,
                    1 or 2 or 3 => MouseButtons.Middle,
                    _ => MouseButtons.Left,
                };

                switch (random.Next(6))
                {
                    case 0:
                        MouseInput.Drag(canvas, from, to, button);
                        break;
                    case 1:
                        MouseInput.Wheel(canvas, from, random.Next(2) == 0 ? 120 : -120);
                        break;
                    case 2:
                        MouseInput.Click(canvas, from, button);
                        break;
                    case 3:
                        MouseInput.Move(canvas, to);
                        break;
                    case 4:
                        MouseInput.Down(canvas, from, button);
                        MouseInput.Move(canvas, to, button);
                        MouseInput.Up(canvas, to, button);
                        break;
                    default:
                        // 模拟用户重新选择堆叠 / 跟随模式
                        plot.Mode = random.Next(2) == 0 ? ScrollingMode.Stacked : ScrollingMode.Follow;
                        break;
                }

                plot.AddValue((random.NextDouble() * 100) - 50);

                if (random.Next(25) == 0)
                    plot.Mode = random.Next(2) == 0 ? ScrollingMode.Stacked : ScrollingMode.Follow;
            });

            host.Invoke(() =>
            {
                ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();

                Assert.True(limits.IsReal, $"第 {iteration} 次操作后坐标轴无效。");
                Assert.True(double.IsFinite(limits.Left) && double.IsFinite(limits.Right), $"第 {iteration} 次操作后 X 轴非法。");
                Assert.True(double.IsFinite(limits.Bottom) && double.IsFinite(limits.Top), $"第 {iteration} 次操作后 Y 轴非法。");
            });
        }

        host.AssertNoUnhandledExceptions();
    }

    private const int LargePointCount = 5000;

    [Fact(DisplayName = "压力测试：连续添加大量数据后坐标轴仍然有效")]
    public void ManyDataPoints_KeepAxesValid()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Random random = new(7);

        host.Invoke(() =>
        {
            for (int i = 0; i < LargePointCount; i++)
                plot.AddValue(random.NextDouble() * 1000);
        });

        host.Invoke(() =>
        {
            Assert.Equal(LargePointCount, plot.DataCount);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, xMin, 6);
            Assert.Equal(LargePointCount + 2, xMax, 6);

            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.True(double.IsFinite(yMin) && double.IsFinite(yMax));
            Assert.True(yMax > yMin);
        });

        // 让消息循环完成一次合并绘制
        host.Pump(300);

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "压力测试：频繁切换模式不会出现异常")]
    public void RapidModeSwitching_DoesNotThrow()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 100);

            for (int i = 0; i < 200; i++)
            {
                plot.Mode = (i % 3) switch
                {
                    0 => ScrollingMode.Stacked,
                    1 => ScrollingMode.Follow,
                    _ => ScrollingMode.Manual,
                };
                plot.AddValue(i);
            }
        });

        host.Invoke(() => Assert.Equal(300, plot.DataCount));
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "窗体尺寸变化不会导致异常")]
    public void ResizingForm_DoesNotThrow()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() => PlotTestHelper.AddWave(plot, 30));

        foreach (Size size in new[] { new Size(400, 300), new Size(1600, 900), new Size(120, 100) })
        {
            host.Invoke(() =>
            {
                Form? form = plot.FindForm();
                Assert.NotNull(form);
                form!.ClientSize = size;
            });

            host.Pump(80);

            host.Invoke(() =>
            {
                plot.AddValue(1.5);
                ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
                Assert.True(double.IsFinite(limits.Left) && double.IsFinite(limits.Right));
            });
        }

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "控件释放后再调用数据接口不会崩溃")]
    public void AfterDispose_DataCallsAreSafe()
    {
        WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() => PlotTestHelper.AddWave(plot, 10));
        host.Dispose();

        plot.AddValue(1.0);
        plot.AddValues([1, 2, 3]);
        plot.ClearData();
        plot.Mode = ScrollingMode.Follow;

        Assert.True(plot.IsDisposed);
    }
}
