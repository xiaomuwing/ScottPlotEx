using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScottPlot;
using ScottPlot.WinForms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using Xunit;

namespace ScottPlotEx.Tests;

/// <summary>
/// 2026-11-07 覆盖矩阵审查（docs/code-review-2026-11-07-matrix.md）的回归测试：
/// 问题 M1–M8、P2、P3 与业务语义定稿 Q1–Q5、Q7。
/// </summary>
public class MatrixReviewFixesTests
{
    private const int PointCount = 100;

    private static WinFormsTestHost Start(out ScrollingPlotControl plot, out Control canvas, int points = PointCount)
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

    /// <summary>需求 1 的堆叠视图：X [0, 点数 + 2]，Y 为全部数据上下各外扩 5%。</summary>
    private static void AssertStackedRequirementView(ScrollingPlotControl plot)
    {
        (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
        Assert.Equal(0.0, xMin, 6);
        Assert.Equal(plot.DataCount + 2, xMax, 6);

        double[] finite = plot.Values.Where(double.IsFinite).ToArray();
        double span = finite.Max() - finite.Min();
        (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
        Assert.Equal(finite.Min() - (0.05 * span), yMin, 6);
        Assert.Equal(finite.Max() + (0.05 * span), yMax, 6);
    }

    private static void ClickMiddle(ScrollingPlotControl plot, Control canvas) =>
        MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(PlotTestHelper.EnsureRendered(plot), 0.5, 0.5), MouseButtons.Middle);

    private static void PressKeys(Control canvas, params Keys[] keys)
    {
        foreach (Keys key in keys)
            MouseInput.KeyDown(canvas, key);

        for (int i = keys.Length - 1; i >= 0; i--)
            MouseInput.KeyUp(canvas, keys[i]);
    }

    #region Q1 / M5 整体显示

    [Theory(DisplayName = "Q1/M5 堆叠模式整体显示（中键 / 菜单 / A 键）：立即显示需求 1 的视图，百分比一律恢复为 100%")]
    [InlineData("中键", 50.0)]
    [InlineData("中键", 240.0)]
    [InlineData("菜单", 50.0)]
    [InlineData("A 键", 240.0)]
    public void Autoscale_InStackedMode_ShowsRequirementViewImmediately(string trigger, double percent)
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            plot.SetXAxisDisplayPercent(percent, ScrollingMode.Stacked);

            switch (trigger)
            {
                case "中键":
                    ClickMiddle(plot, canvas);
                    break;
                case "菜单":
                    plot.PlotMenu.Autoscale(plot.Plot);
                    break;
                default:
                    PressKeys(canvas, Keys.A);
                    break;
            }

            // 不需要等下一个数据点：操作完成时就是需求 1 的视图
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);
            AssertStackedRequirementView(plot);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "Q1 跟随模式整体显示：换成堆叠模式并显示全部数据（堆叠百分比恢复为 100%）")]
    public void Autoscale_InFollowMode_SwitchesToFullStackedView()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            plot.SetXAxisDisplayPercent(240, ScrollingMode.Stacked);
            plot.Mode = ScrollingMode.Follow;
            plot.SetXAxisDisplayPercent(240, ScrollingMode.Stacked);
            ClickMiddle(plot, canvas);

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);
            AssertStackedRequirementView(plot);
        });
    }

    #endregion

    #region Q2 Y 锁定

    [Fact(DisplayName = "Q2 左键竖直拖动后锁定 Y：X 继续滚动，新数据不覆盖用户的 Y 范围；整体显示后解除")]
    public void VerticalDrag_LocksYAxis_UntilAutoscale()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.3), PlotTestHelper.PointInDataArea(rect, 0.5, 0.8), MouseButtons.Left);

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.IsYAxisLocked);
            (double lockedMin, double lockedMax) = PlotTestHelper.GetYRange(plot);

            for (int i = 0; i < 5; i++)
                plot.AddValue(100); // 超出锁定范围的新数据也不改变 Y

            Assert.Equal((lockedMin, lockedMax), PlotTestHelper.GetYRange(plot));
            Assert.Equal(plot.DataCount + 2, PlotTestHelper.GetXRange(plot).Max, 6); // X 照常滚动

            ClickMiddle(plot, canvas);
            Assert.False(plot.IsYAxisLocked);
            AssertStackedRequirementView(plot);
        });
    }

    [Fact(DisplayName = "Q2 横向拖动时竖直方向不足 5 像素的抖动不锁定 Y")]
    public void HorizontalDragWithJitter_DoesNotLockY()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point from = PlotTestHelper.PointInDataArea(rect, 0.5, 0.5);
            Point to = PlotTestHelper.PointInDataArea(rect, 0.42, 0.5);
            MouseInput.Drag(canvas, from, new Point(to.X, from.Y + 3), MouseButtons.Left);

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.False(plot.IsYAxisLocked);
        });
    }

    [Fact(DisplayName = "Q2 中键框选（含最新数据）锁定 Y；重新选择模式、清空数据、ResetYAxis 都会解除")]
    public void ZoomRectangle_LocksY_AndUnlockTriggers()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        void LockByZoomRectangle()
        {
            PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(canvas, PlotTestHelper.PointInDataArea(rect, 0.6, 0.1), PlotTestHelper.PointInDataArea(rect, 0.999, 0.9), MouseButtons.Middle);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.IsYAxisLocked);
        }

        host.Invoke(() =>
        {
            LockByZoomRectangle();
            plot.Mode = ScrollingMode.Stacked; // 选回当前模式
            Assert.False(plot.IsYAxisLocked);

            LockByZoomRectangle();
            plot.ResetYAxis();
            Assert.False(plot.IsYAxisLocked);

            // X 保持框选后的最小值（需求 7），Y 恢复为当前 X 窗口内数据上下各外扩 5%
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            double[] visible = plot.Values.Where((_, i) => i + 1 >= xMin && i + 1 <= xMax).ToArray();
            double span = visible.Max() - visible.Min();
            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.Equal(visible.Min() - (0.05 * span), yMin, 6);
            Assert.Equal(visible.Max() + (0.05 * span), yMax, 6);

            LockByZoomRectangle();
            plot.ClearData();
            Assert.False(plot.IsYAxisLocked);
        });
    }

    #endregion

    #region Q5 / M2 按按下鼠标时的最新数据判断

    [Fact(DisplayName = "Q5/M2 拖动期间到达多个数据点：按按下鼠标时的最新数据判断，继续滚动")]
    public void DataArrivingDuringDrag_DoesNotStopScrolling()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            Point a = PlotTestHelper.PointInDataArea(rect, 0.5, 0.3);
            Point b = PlotTestHelper.PointInDataArea(rect, 0.5, 0.6);
            MouseInput.Down(canvas, a, MouseButtons.Left);
            MouseInput.Move(canvas, b, MouseButtons.Left);

            for (int i = 0; i < 20; i++)
                plot.AddValue(10); // 手势期间坐标轴不动，这些点落在视图右侧之外

            MouseInput.Up(canvas, b, MouseButtons.Left);

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            plot.AddValue(10);
            Assert.Equal(plot.DataCount + 2, PlotTestHelper.GetXRange(plot).Max, 6);
        });
    }

    [Fact(DisplayName = "Q5/M2 后台 1 kHz 持续写入时轻拖 300 ms：继续滚动")]
    public void DragWhileStreamingAt1kHz_KeepsScrolling()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);
        bool stop = false;
        Thread producer = new(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                plot.AddValue(12);
                Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
        };

        producer.Start();

        try
        {
            host.Pump(200);
            PixelRect rect = host.Invoke(() => PlotTestHelper.EnsureRendered(plot));
            Point a = PlotTestHelper.PointInDataArea(rect, 0.5, 0.5);
            Point b = PlotTestHelper.PointInDataArea(rect, 0.42, 0.5);

            host.Invoke(() =>
            {
                MouseInput.Down(canvas, a, MouseButtons.Left);
                MouseInput.Move(canvas, b, MouseButtons.Left);
            });

            host.Pump(300);
            host.Invoke(() => MouseInput.Up(canvas, b, MouseButtons.Left));
            host.Invoke(() => Assert.Equal(ScrollingMode.Stacked, plot.Mode));
        }
        finally
        {
            Volatile.Write(ref stop, true);
            producer.Join(1000);
        }

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region M4 键盘

    [Fact(DisplayName = "M4 Ctrl+方向键缩放后停止滚动（需求 3）；方向键平移按左键拖动规则继续滚动")]
    public void Keyboard_ZoomStops_PanKeepsScrolling()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            // → 键：视图右移，仍能看到最新数据 → 继续滚动，X 轴最小值取平移后的当前值
            PressKeys(canvas, Keys.Right);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.XOffset > 0);

            PressKeys(canvas, Keys.Up);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.True(plot.IsYAxisLocked); // 竖直平移锁定 Y（Q2）

            PressKeys(canvas, Keys.ControlKey, Keys.Up);
            Assert.Equal(ScrollingMode.Manual, plot.Mode);

            // ← 键：视图左移，最新数据移出右边界（堆叠模式右侧只留 2 个点）→ 按左键拖动规则停止滚动
            plot.Mode = ScrollingMode.Stacked;
            PressKeys(canvas, Keys.Left);
            Assert.Equal(ScrollingMode.Manual, plot.Mode);
        });
    }

    [Fact(DisplayName = "M4 A 键：停止滚动时恢复滚动（补充 1），跟随模式换成堆叠模式（补充 2）")]
    public void Keyboard_A_ResumesAndSwitchesToStacked()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);

        host.Invoke(() =>
        {
            plot.StopScrolling();
            PressKeys(canvas, Keys.A);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            AssertStackedRequirementView(plot);

            plot.Mode = ScrollingMode.Follow;
            PressKeys(canvas, Keys.A);
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            AssertStackedRequirementView(plot);
        });
    }

    #endregion

    #region Q3 堆叠百分比超过 100%

    [Fact(DisplayName = "Q3 堆叠 240%：右侧预留空白，数据写满之前坐标轴不动，写满后按同一比例扩展")]
    public void StackedPercentAbove100_ExpandsInStages()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);

        host.Invoke(() =>
        {
            plot.SetXAxisDisplayPercent(240, ScrollingMode.Stacked);
            AssertXRange(0, 102 * 2.4);

            // 写到 242 点（右端需要 244 ≤ 244.8）：坐标轴保持不动
            while (plot.DataCount < 242)
                plot.AddValue(5);

            AssertXRange(0, 102 * 2.4);

            // 第 243 点：右侧留白不足 2 个点 → 扩展为 (243 + 2) × 2.4
            plot.AddValue(5);
            AssertXRange(0, 245 * 2.4);

            // 跟随模式最大 100%；重新选择堆叠模式复位为 100%
            plot.SetXAxisDisplayPercent(240, ScrollingMode.Follow);
            Assert.Equal(100.0, plot.FollowWindowPercent, 9);
            plot.Mode = ScrollingMode.Stacked;
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);
            AssertStackedRequirementView(plot);
        });

        void AssertXRange(double min, double max)
        {
            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(min, xMin, 6);
            Assert.Equal(max, xMax, 6);
        }
    }

    #endregion

    #region Q4 / M1 / M7 缺失值

    [Fact(DisplayName = "Q4/M1 缺失值占位：多条曲线按序号对齐，缺失之后的点 X 不错位；Y 轴跳过缺失值")]
    public void MissingValues_KeepAlignment()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotSeries withGap = plot.DefaultSeries;
            PlotSeries noGap = plot.AddSeries("b");
            double[] gapped = [1, 2, double.NaN, 4, 5];
            double[] full = [1, 2, 3, 4, 5];
            withGap.AddValues(gapped);
            noGap.AddValues(full);

            Assert.Equal(5, withGap.DataCount);
            Assert.Equal(1, withGap.SkippedValueCount);
            Assert.Equal(withGap.LatestX, noGap.LatestX);
            Assert.Equal(4, withGap.Values[3]); // X = 4 处两条曲线都是 4

            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.True(double.IsFinite(yMin) && double.IsFinite(yMax));
            Assert.Equal(1 - (0.05 * 4), yMin, 6);
            Assert.Equal(5 + (0.05 * 4), yMax, 6);
        });
    }

    [Fact(DisplayName = "Q4/M7 极大值（±1e308）按缺失值处理：不抛异常，坐标轴有效")]
    public void HugeValues_AreMissing_AndRenderSafely()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValue(1e308);
            plot.AddValue(-1e308);
            plot.AddValue(3);
            plot.Refresh();

            Assert.Equal(2, plot.SkippedValueCount);
            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            Assert.True(limits.IsReal);
        });

        host.Pump(100);
        host.AssertNoUnhandledExceptions();
        host.Invoke(() => Assert.Equal(0, ((PlotCanvas)PlotTestHelper.GetInputCanvas(plot)).RenderFailureCount));
    }

    [Theory(DisplayName = "Q4 断线绘制：缺失处没有连线，两侧的曲线照常绘制（低密度 / 高密度）")]
    [InlineData(20, 8, 9)]
    [InlineData(100_000, 40_000, 49_999)]
    public void MissingValues_RenderAsGap(int count, int gapStart, int gapEnd)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            double[] values = new double[count];
            for (int i = 0; i < count; i++)
                values[i] = i >= gapStart && i <= gapEnd ? double.NaN : (count <= 50 ? i % 5 : Math.Sin(i / 2000.0));

            plot.AddValues(values);
            plot.Signal!.LineWidth = 2;

            using ScottPlot.Image image = plot.Plot.GetImage(600, 300);
            using Bitmap bitmap = image.GetBitmap();

            // 点数单位：第 i 个点（下标从 0）X = i + 1
            double gapCenterX = ((gapStart + gapEnd) / 2.0) + 1;
            double leftX = (gapStart / 2.0) + 1;
            double rightX = ((gapEnd + count) / 2.0) + 1;

            Assert.Equal(0, CurvePixelsInColumn(plot, bitmap, gapCenterX));
            Assert.True(CurvePixelsInColumn(plot, bitmap, leftX) > 0, "缺失区间左侧的曲线没有画出来。");
            Assert.True(CurvePixelsInColumn(plot, bitmap, rightX) > 0, "缺失区间右侧的曲线没有画出来。");
        });
    }

    /// <summary>某个 X 坐标所在像素列中曲线颜色（默认蓝色）的像素数。</summary>
    private static int CurvePixelsInColumn(ScrollingPlotControl plot, Bitmap bitmap, double x)
    {
        PixelRect dataRect = plot.Plot.RenderManager.LastRender.DataRect;
        int column = (int)Math.Round(plot.Plot.GetPixel(new Coordinates(x, 0)).X);
        int count = 0;

        for (int y = (int)dataRect.Top + 1; y < (int)dataRect.Bottom - 1; y++)
        {
            System.Drawing.Color c = bitmap.GetPixel(column, y);
            if (c.B > 180 && c.R < 100 && c.G < 100)
                count++;
        }

        return count;
    }

    [Fact(DisplayName = "M7 渲染异常不冒到消息循环：画出错误提示，移除出错的图元后恢复正常")]
    public void RenderException_IsContained()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);
        PlotCanvas plotCanvas = (PlotCanvas)canvas;
        ThrowingPlottable bad = new();

        host.Invoke(() =>
        {
            plot.Plot.Add.Plottable(bad);
            plot.Refresh();
        });

        host.Pump(100);
        host.AssertNoUnhandledExceptions();
        host.Invoke(() => Assert.True(plotCanvas.RenderFailureCount > 0));

        int failures = host.Invoke(() => plotCanvas.RenderFailureCount);
        host.Invoke(() =>
        {
            plot.Plot.Remove(bad);
            plot.Refresh();
        });

        host.Invoke(() => Assert.Equal(failures, plotCanvas.RenderFailureCount));
    }

    private sealed class ThrowingPlottable : IPlottable
    {
        public bool IsVisible { get; set; } = true;

        public IAxes Axes { get; set; } = new ScottPlot.Axes();

        public IEnumerable<LegendItem> LegendItems => [];

        public AxisLimits GetAxisLimits() => AxisLimits.NoLimits;

        public void Render(RenderPack rp) => throw new InvalidOperationException("测试：图元渲染失败");
    }

    #endregion

    #region M3 / M8 线程

    [Fact(DisplayName = "M3 UI 线程等待采集线程时，采集线程添加曲线、修改曲线属性不会互相卡死")]
    public void BackgroundSeriesConfiguration_DoesNotDeadlockWhenUiWaits()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);
        PlotSeries removable = host.Invoke(() => plot.AddSeries("待删除"));
        PlotSeries? created = null;

        bool joined = host.Invoke(() =>
        {
            Thread worker = new(() =>
            {
                created = plot.AddSeries("采集");
                created.Color = ScottPlot.Colors.Red;
                created.SampleRate = 100;
                created.LineWidth = 3;
                created.IsVisible = false;
                created.AddValues(new double[] { 1, 2, 3 });
                removable.Remove();

                // 读取立即返回新值
                Assert.Equal(ScottPlot.Colors.Red, created.Color);
                Assert.Equal(3f, created.LineWidth);
                Assert.False(created.IsVisible);
            })
            {
                IsBackground = true,
            };

            worker.Start();
            return worker.Join(3000); // 关闭窗体时常见的写法：UI 线程等待采集线程退出
        });

        Assert.True(joined, "UI 线程等待期间，采集线程被同步切换到 UI 线程的调用卡住了。");

        host.Pump(200);
        host.Invoke(() =>
        {
            Assert.NotNull(created!.Signal);
            Assert.Equal(ScottPlot.Colors.Red, created.Signal!.Color);
            Assert.Equal(3f, created.Signal.LineWidth);
            Assert.False(created.Signal.IsVisible);
            Assert.Equal(3, created.DataCount);
            Assert.True(removable.IsRemoved);
            Assert.DoesNotContain(removable, plot.Series);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "M3 绝对时间下从后台线程清除起始时间：仍在调用线程上抛出异常")]
    public void BackgroundStartTimeValidation_StillThrowsToCaller()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        PlotSeries series = host.Invoke(() =>
        {
            PlotSeries s = plot.AddSeries("a", startTime: DateTime.Now);
            s.AddValue(1);
            plot.XAxisUnit = XAxisUnit.AbsoluteTime;
            return s;
        });

        Exception? thrown = null;
        Thread worker = new(() => thrown = Record.Exception(() => series.StartTime = null)) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(3000));
        Assert.IsType<InvalidOperationException>(thrown);
    }

    [Fact(DisplayName = "M8 控件配置接口在其它线程调用时立即抛出异常；RequestRender 可以在任意线程调用")]
    public void ConfigurationApis_RequireUiThread()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);
        PlotCanvas plotCanvas = (PlotCanvas)canvas;
        int framesBefore = host.Invoke(() => plotCanvas.RenderedFrameCount);

        Action[] configuration =
        [
            () => plot.Mode = ScrollingMode.Follow,
            () => plot.StopScrolling(),
            () => plot.SetXAxisDisplayPercent(50, ScrollingMode.Stacked),
            () => plot.XAxisUnit = XAxisUnit.RelativeTime,
            () => plot.ShowLegend = true,
            () => plot.Reset(),
            () => plot.ApplyAxisOptions(),
            () => plot.ResetYAxis(),
        ];

        List<Exception?> results = [];
        Exception? requestRenderError = null;

        Thread worker = new(() =>
        {
            foreach (Action action in configuration)
                results.Add(Record.Exception(action));

            requestRenderError = Record.Exception(plot.RequestRender);
        })
        {
            IsBackground = true,
        };

        worker.Start();
        Assert.True(worker.Join(3000));

        Assert.All(results, error => Assert.IsType<InvalidOperationException>(error));
        Assert.Null(requestRenderError);

        host.Pump(200);
        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode); // 没有被后台线程改动
            Assert.True(plotCanvas.RenderedFrameCount > framesBefore);
        });

        host.AssertNoUnhandledExceptions();
    }

    #endregion

    #region M6 剪贴板

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [Fact(DisplayName = "M6 剪贴板被占用时“复制到剪贴板”不抛异常，改为提示错误")]
    public void CopyToClipboard_WhenBusy_ReportsError()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);
        using ManualResetEventSlim opened = new();
        using ManualResetEventSlim release = new();
        List<string> errors = [];

        Thread holder = new(() =>
        {
            OpenClipboard(IntPtr.Zero);
            opened.Set();
            release.Wait(10_000);
            CloseClipboard();
        })
        {
            IsBackground = true,
        };

        holder.SetApartmentState(ApartmentState.STA);
        holder.Start();
        Assert.True(opened.Wait(3000));

        try
        {
            host.Invoke(() =>
            {
                plot.PlotMenu.ErrorReporter = errors.Add;
                Exception? error = Record.Exception(() => plot.PlotMenu.CopyImageToClipboard(plot.Plot));
                Assert.Null(error);
            });
        }
        finally
        {
            release.Set();
            holder.Join(3000);
        }

        Assert.Single(errors);
        Assert.Contains("剪贴板", errors[0]);
    }

    #endregion

    #region P2 / P3

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [Fact(DisplayName = "P2 画面内容没变的重绘（窗口重新露出）直接复用上一帧，不重新渲染；请求重绘后照常渲染")]
    public void ExposureRepaint_ReusesLastFrame()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out Control canvas);
        PlotCanvas plotCanvas = (PlotCanvas)canvas;

        host.Invoke(() =>
        {
            canvas.Update();
            int before = plotCanvas.RenderedFrameCount;

            // 系统发起的重绘（不经过 Control.Invalidate），相当于窗口被遮挡后重新露出
            InvalidateRect(canvas.Handle, IntPtr.Zero, false);
            canvas.Update();
            Assert.Equal(before, plotCanvas.RenderedFrameCount);

            plot.AddValue(3);
            canvas.Update();
            Assert.Equal(before + 1, plotCanvas.RenderedFrameCount);
        });
    }

    [Fact(DisplayName = "P3/Q7 默认帧率上限 60；每帧耗时超过帧间隔时，空闲保护让窗体上的 WinForms 定时器照常触发")]
    public void DefaultFrameCap_And_IdleGapKeepsWinFormsTimersAlive()
    {
        using WinFormsTestHost host = Start(out ScrollingPlotControl plot, out _);
        Assert.Equal(60, host.Invoke(() => plot.MaxFramesPerSecond));

        // 每帧渲染耗时约 40 ms（> 16.7 ms 帧间隔）：模拟曲线很多、窗口很大时的重负载
        host.Invoke(() => plot.Plot.Add.Plottable(new SlowPlottable(40)));

        int ticks = 0;
        using System.Windows.Forms.Timer timer = new() { Interval = 50 };
        timer.Tick += (_, _) => ticks++;

        bool stop = false;
        Thread producer = new(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                plot.AddValue(5);
                Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
        };

        host.Invoke(timer.Start);
        producer.Start();

        try
        {
            host.Pump(2000);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            producer.Join(1000);
            host.Invoke(timer.Stop);
        }

        // 正常约 32 次（50 ms 定时器受 15.6 ms 系统时钟影响，实际约 62.5 ms 一次）；没有空闲保护时为 0
        Assert.True(ticks >= 8, $"2 秒内 50 ms 定时器只触发了 {ticks} 次。");
        host.AssertNoUnhandledExceptions();
    }

    private sealed class SlowPlottable(int milliseconds) : IPlottable
    {
        public bool IsVisible { get; set; } = true;

        public IAxes Axes { get; set; } = new ScottPlot.Axes();

        public IEnumerable<LegendItem> LegendItems => [];

        public AxisLimits GetAxisLimits() => AxisLimits.NoLimits;

        public void Render(RenderPack rp) => Thread.Sleep(milliseconds);
    }

    #endregion
}
