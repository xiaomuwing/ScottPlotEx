using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Demo;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 复现“改过 X 轴显示百分比之后切不回堆叠模式”的问题。
/// </summary>
public class ReproFollowToStackedTests
{
    [Fact(DisplayName = "复现：演示窗体里改百分比后切回堆叠模式")]
    public void DemoForm_ChangePercentThenSwitchToStacked()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new MainForm(), new Size(1200, 700));
        MainForm form = host.ControlAs<MainForm>();
        ScrollingPlotControl plot = host.Invoke(() => form.PlotControl);
        List<string> log = [];

        host.Invoke(() =>
        {
            NumericUpDown percentBox = FindControl<NumericUpDown>(form)!;
            RadioButton stacked = FindControl<RadioButton>(form, r => r.Text == "堆叠模式")!;
            RadioButton follow = FindControl<RadioButton>(form, r => r.Text == "跟随模式")!;

            // 1) 切到跟随模式，并把百分比改成 50
            follow.Checked = true;
            log.Add($"跟随模式：百分比框={percentBox.Value}，堆叠显示={plot.StackedDisplayPercent}，跟随窗口={plot.FollowWindowPercent}");

            percentBox.Value = 50;
            log.Add($"改百分比后：堆叠显示={plot.StackedDisplayPercent}，跟随窗口={plot.FollowWindowPercent}");

            // 2) 切回堆叠模式
            stacked.Checked = true;
            log.Add($"切到堆叠后：模式={plot.Mode}，百分比框={percentBox.Value}，" +
                    $"堆叠显示={plot.StackedDisplayPercent}，跟随窗口={plot.FollowWindowPercent}");

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            log.Add($"X 轴范围=[{xMin:0.###}, {xMax:0.###}]，数据点数={plot.DataCount}");
        });

        host.Pump(200);
        host.Invoke(() =>
        {
            (double xMin, _) = PlotTestHelper.GetXRange(plot);

            Assert.True(
                Math.Abs(xMin) < 1e-6,
                "切换到堆叠模式后 X 轴原点应为 0（显示全部数据）。实际：" + string.Join(" | ", log));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "回归：改过跟随窗口百分比后，切回堆叠模式应显示全部数据")]
    public void AfterChangingFollowPercent_SwitchingToStackedShowsAllData()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        List<string> log = [];

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));

            // 用户自己的界面：模式变化时把当前百分比同步到输入框，输入框变化时写回控件
            plot.Mode = ScrollingMode.Follow;
            double percent = plot.CurrentXAxisDisplayPercent; // 读到 30（跟随窗口）
            log.Add($"跟随模式读到 {percent}");

            // 修改跟随窗口百分比（只影响跟随模式）
            plot.SetXAxisDisplayPercent(50, ScrollingMode.Follow);
            log.Add($"跟随窗口改为 {plot.FollowWindowPercent}，堆叠显示={plot.StackedDisplayPercent}");

            plot.Mode = ScrollingMode.Stacked;

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            log.Add($"切到堆叠后：模式={plot.Mode}，堆叠显示={plot.StackedDisplayPercent}，X=[{xMin:0.###}, {xMax:0.###}]");
        });

        host.Invoke(() =>
        {
            (double xMin, _) = PlotTestHelper.GetXRange(plot);

            Assert.True(
                Math.Abs(xMin) < 1e-6,
                "堆叠模式不应被跟随窗口百分比影响，X 轴原点应为 0。实际：" + string.Join(" | ", log));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "回归：显式把堆叠模式裁短后，可用 ShowAllData() 恢复显示全部数据")]
    public void ExplicitlyCroppedStackedView_CanBeRestored()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));

            // 显式把堆叠模式设成“只显示最新 30% 的数据”
            plot.SetXAxisDisplayPercent(30, ScrollingMode.Stacked);

            (double croppedMin, double croppedMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(102.0 * 0.7, croppedMin, 6);
            Assert.Equal(102.0, croppedMax, 6);

            // 恢复显示全部数据
            plot.ShowAllData();

            (double fullMin, double fullMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(0.0, fullMin, 6);
            Assert.Equal(102.0, fullMax, 6);
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "回归：调整百分比后重新选择堆叠模式，应回到“显示全部数据”")]
    public void AfterChangingStackedPercent_ReselectingStackedShowsAllData()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        List<string> log = [];

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));

            // 1) 把堆叠模式的显示长度调成“只显示最新 50% 的数据”
            plot.SetXAxisDisplayPercent(50, ScrollingMode.Stacked);

            (double croppedMin, double croppedMax) = PlotTestHelper.GetXRange(plot);
            log.Add($"调整后：X=[{croppedMin:0.###}, {croppedMax:0.###}]，显示百分比={plot.StackedDisplayPercent}");
            Assert.Equal(102.0 * 0.5, croppedMin, 6);
            Assert.Equal(102.0, croppedMax, 6);

            // 2) 重新选择堆叠模式（用户界面上再次点击“堆叠模式”）
            plot.Mode = ScrollingMode.Stacked;

            (double fullMin, double fullMax) = PlotTestHelper.GetXRange(plot);
            log.Add($"重新选择堆叠模式后：X=[{fullMin:0.###}, {fullMax:0.###}]，显示百分比={plot.StackedDisplayPercent}");

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(100.0, plot.StackedDisplayPercent, 9);
            Assert.Equal(0.0, fullMin, 6);
            Assert.Equal(102.0, fullMax, 6);
        });

        host.Invoke(() =>
        {
            (double xMin, _) = PlotTestHelper.GetXRange(plot);
            Assert.True(Math.Abs(xMin) < 1e-6, "重新选择堆叠模式后应显示全部数据。实际：" + string.Join(" | ", log));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "回归：调整跟随窗口百分比后重新选择跟随模式，应回到默认 30% 窗口")]
    public void AfterChangingFollowPercent_ReselectingFollowRestoresDefaultWindow()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            plot.Mode = ScrollingMode.Follow;

            plot.SetXAxisDisplayPercent(10, ScrollingMode.Follow);
            Assert.Equal(10.0, plot.FollowWindowPercent, 9);

            // 继续采集数据时，自定义窗口保持有效
            plot.AddValue(1);
            Assert.Equal(10.0, plot.FollowWindowPercent, 9);

            // 重新选择跟随模式 -> 回到默认窗口 30%
            plot.Mode = ScrollingMode.Follow;
            Assert.Equal(30.0, plot.FollowWindowPercent, 9);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(xMax - (101 * 0.3), xMin, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "百分比跨模式切换可以保持（关闭 ResetDisplayPercentOnModeChange）")]
    public void DisplayPercent_CanPersistAcrossModeChanges()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            plot.AxisOptions.ResetDisplayPercentOnModeChange = false;

            plot.SetXAxisDisplayPercent(40, ScrollingMode.Stacked);
            plot.Mode = ScrollingMode.Stacked; // 关闭复位选项后保持 40%

            Assert.Equal(40.0, plot.StackedDisplayPercent, 9);

            (double xMin, _) = PlotTestHelper.GetXRange(plot);
            Assert.Equal(102.0 * 0.6, xMin, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "错误回归：堆叠模式设 90% 应显示约 90% 数据，而不是变成跟随样子的窄窗口")]
    public void StackedMode_DisplayPercent90_StillLooksStacked()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            plot.SetXAxisDisplayPercent(90, ScrollingMode.Stacked);

            (double xMin, double xMax) = PlotTestHelper.GetXRange(plot);

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);   // 模式不能被改掉
            Assert.Equal(100.0 - 10.0, plot.StackedDisplayPercent, 9);
            Assert.Equal(102.0, xMax, 6);
            Assert.Equal(10.2, xMin, 6);                       // 只裁掉最早的 10%
            Assert.Equal(91.8, xMax - xMin, 6);                // 可见约 92 个数据点（≈ 90%）
            Assert.True(xMax - xMin > 0.8 * xMax, "90% 应当显示绝大部分数据，而不是窄窗口。");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "百分比接口不会改变当前模式（堆叠模式调百分比后仍是堆叠模式）")]
    public void PercentApis_NeverChangeMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        List<string> log = [];

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(1, 100).Select(i => (double)i));
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            // 堆叠模式下：所有百分比入口都不得切换模式
            plot.SetXAxisDisplayPercent(40, ScrollingMode.Stacked);
            log.Add($"SetXAxisDisplayPercent(40, Stacked) -> Mode={plot.Mode}");
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            plot.SetXAxisDisplayPercent(50, ScrollingMode.Follow); // 只配置跟随窗口，不应切换模式
            log.Add($"SetXAxisDisplayPercent(50, Follow) -> Mode={plot.Mode}，跟随窗口={plot.FollowWindowPercent}");
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            plot.SetXAxisDisplayPercent(0, ScrollingMode.Stacked);
            log.Add($"SetXAxisDisplayPercent(0, Stacked) -> Mode={plot.Mode}");
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            plot.FollowWindowPercent = 15;
            log.Add($"FollowWindowPercent=15 -> Mode={plot.Mode}");
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            plot.StackedDisplayPercent = 20;
            log.Add($"StackedDisplayPercent=20 -> Mode={plot.Mode}");
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            plot.ShowAllData();
            log.Add($"ShowAllData() -> Mode={plot.Mode}");
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);

            // 跟随模式下同样不会因为改百分比而切走
            plot.Mode = ScrollingMode.Follow;
            plot.SetXAxisDisplayPercent(25, ScrollingMode.Follow);
            plot.SetXAxisDisplayPercent(10, ScrollingMode.Stacked);
            log.Add($"跟随模式下改两个百分比 -> Mode={plot.Mode}");
            Assert.Equal(ScrollingMode.Follow, plot.Mode);
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Follow, plot.Mode);
            Assert.Equal(25.0, plot.FollowWindowPercent, 9);
            Assert.Equal(10.0, plot.StackedDisplayPercent, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    private static T? FindControl<T>(Control root, Func<T, bool>? predicate = null) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed && (predicate is null || predicate(typed)))
                return typed;

            T? nested = FindControl(child, predicate);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
