using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>需求 4：右键菜单内容（ScottPlot 自带内容的中文版 + 显示帮助）。</summary>
public class ChinesePlotMenuTests
{
    [Fact(DisplayName = "需求4：菜单项与 ScottPlot 自带内容一一对应且全部为中文")]
    public void Menu_MatchesScottPlotDefaultsInChinese()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            // 控件不再内嵌 FormsPlot：单独创建一个仅用于读取 ScottPlot 自带菜单项
            using ScottPlot.WinForms.FormsPlot reference = new();
            List<ScottPlot.ContextMenuItem> builtin = new ScottPlot.WinForms.FormsPlotMenu(reference).StandardContextMenuItems();
            List<ScottPlot.ContextMenuItem> ours = plot.PlotMenu.StandardContextMenuItems();

            Assert.Equal(builtin.Count, ours.Count);

            for (int i = 0; i < builtin.Count; i++)
            {
                Assert.Equal(ChinesePlotMenu.TranslateLabel(builtin[i].Label), ours[i].Label);
                Assert.NotNull(ours[i].OnInvoke);
            }

            Assert.All(ours, item => Assert.False(item.Label.Any(char.IsAsciiLetter), $"菜单项“{item.Label}”没有翻译成中文。"));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求4：菜单顺序为 保存图像 / 复制到剪贴板 / 自动缩放 / 在新窗口中打开 / 分隔线 / 模式勾选项 / 分隔线 / 显示帮助")]
    public void Menu_BuildsExpectedItems()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            using ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot);

            Assert.Equal(10, menu.Items.Count);
            Assert.Equal("保存图像", menu.Items[0].Text);
            Assert.Equal("复制到剪贴板", menu.Items[1].Text);
            Assert.Equal("自动缩放", menu.Items[2].Text);
            Assert.Equal("在新窗口中打开", menu.Items[3].Text);
            Assert.IsType<ToolStripSeparator>(menu.Items[4]);
            Assert.Equal("堆叠模式", menu.Items[5].Text);
            Assert.Equal("跟随模式", menu.Items[6].Text);
            Assert.Equal("手动模式（停止滚动）", menu.Items[7].Text);
            Assert.IsType<ToolStripSeparator>(menu.Items[8]);
            Assert.Equal("显示帮助", menu.Items[9].Text);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "模式菜单项为勾选项，且勾选状态与当前模式一致")]
    public void ModeItems_AreCheckableAndFollowCurrentMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        void AssertChecks(ScrollingMode expected)
        {
            using ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot);
            ToolStripMenuItem[] modeItems =
            [
                .. menu.Items.OfType<ToolStripMenuItem>()
                    .Where(item => ChinesePlotMenu.TryGetMode(item.Text ?? string.Empty, out _)),
            ];

            Assert.Equal(3, modeItems.Length);

            foreach (ToolStripMenuItem item in modeItems)
            {
                Assert.True(ChinesePlotMenu.TryGetMode(item.Text ?? string.Empty, out ScrollingMode mode));

                // 勾选状态完全由当前模式推导
                Assert.Equal(mode == expected, item.Checked);

                // 点击不会自行切换勾选状态（否则会与模式不一致）
                Assert.False(item.CheckOnClick);
            }

            // 同一时刻只勾选一项
            Assert.Equal(1, modeItems.Count(item => item.Checked));
        }

        host.Invoke(() =>
        {
            plot.AddValues([1, 2, 3]);

            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            AssertChecks(ScrollingMode.Stacked);

            plot.Mode = ScrollingMode.Follow;
            AssertChecks(ScrollingMode.Follow);

            plot.Mode = ScrollingMode.Manual;
            AssertChecks(ScrollingMode.Manual);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "点击模式勾选项：切换模式；再次点击已勾选项不会取消勾选")]
    public void ModeItems_Click_SwitchesModeWithoutUnchecking()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);

            // 点击“跟随模式”
            using (ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot))
            {
                menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "跟随模式").PerformClick();
            }

            Assert.Equal(ScrollingMode.Follow, plot.Mode);

            // 重新打开菜单：跟随模式应处于勾选状态
            using (ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot))
            {
                Assert.True(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "跟随模式").Checked);
                Assert.False(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "堆叠模式").Checked);

                // 再次点击已经勾选的项：不会取消勾选，模式保持不变
                menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "跟随模式").PerformClick();
            }

            Assert.Equal(ScrollingMode.Follow, plot.Mode);

            using (ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot))
            {
                Assert.True(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "跟随模式").Checked);
            }
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "自动停止滚动后打开菜单：勾选自动落到“手动模式”")]
    public void ModeItems_ReflectAutoStoppedState()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        // 滚轮缩放 -> 自动停止滚动
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Wheel(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), 120);
        });

        host.Invoke(() =>
        {
            Assert.True(plot.IsScrollingStopped);

            using ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot);

            Assert.True(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "手动模式（停止滚动）").Checked);
            Assert.False(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "堆叠模式").Checked);
            Assert.False(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "跟随模式").Checked);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "菜单打开期间模式变化（例如中键整体显示）会同步勾选状态")]
    public void ModeItems_SyncWhileMenuIsOpen()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            plot.Mode = ScrollingMode.Follow;
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        // 打开右键菜单
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Click(canvas, PlotTestHelper.PointInDataArea(rect, 0.5, 0.5), MouseButtons.Right);
        });

        host.Pump(200);

        host.Invoke(() =>
        {
            ContextMenuStrip menu = plot.PlotMenu.LastMenuShown!;
            Assert.NotNull(menu);
            Assert.True(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "跟随模式").Checked);

            // 菜单仍然打开，此时模式被改变（模拟整体显示 / 外部代码设置模式）
            plot.Mode = ScrollingMode.Stacked;

            Assert.True(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "堆叠模式").Checked);
            Assert.False(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "跟随模式").Checked);

            menu.Close();
        });

        host.Pump(100);
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "需求4：点击“显示帮助”触发帮助事件（可自定义帮助窗口）")]
    public void HelpItem_RaisesShowHelpRequested()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        bool raised = false;

        host.Invoke(() =>
        {
            plot.ShowHelpRequested += (_, e) =>
            {
                raised = true;
                e.Handled = true; // 测试中不弹出模态窗口
            };

            using ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot);
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "显示帮助").PerformClick();
        });

        Assert.True(raised);
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "内置帮助窗口：标题与正文都为中文说明")]
    public void BuiltInHelpDialog_ContainsChineseContent()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());

        host.Invoke(() =>
        {
            using PlotHelpDialog dialog = new(PlotHelpContent.Text);

            Assert.Equal(PlotHelpContent.Title, dialog.Text);
            Assert.Contains("堆叠模式", PlotHelpContent.Text);
            Assert.Contains("跟随模式", PlotHelpContent.Text);
            Assert.Contains("鼠标操作", PlotHelpContent.Text);

            TextBox textBox = dialog.Controls.OfType<TextBox>().Single();
            Assert.Equal(PlotHelpContent.Text, textBox.Text);
            Assert.True(textBox.ReadOnly);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "菜单“自动缩放”会复位 X 轴偏移并保持滚动模式")]
    public void AutoscaleItem_ResetsOffsetAndKeepsMode()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        Control canvas = host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 20);
            PlotTestHelper.EnsureRendered(plot);
            return PlotTestHelper.GetInputCanvas(plot);
        });

        // 先制造偏移
        host.Invoke(() =>
        {
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);
            MouseInput.Drag(
                canvas,
                PlotTestHelper.PointInDataArea(rect, 0.6, 0.5),
                PlotTestHelper.PointInDataArea(rect, 0.2, 0.5),
                MouseButtons.Left);
        });

        host.Invoke(() => Assert.True(plot.XOffset > 0));

        // 点击菜单中的“自动缩放”
        host.Invoke(() =>
        {
            using ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot);
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "自动缩放").PerformClick();
        });

        host.Invoke(() =>
        {
            Assert.Equal(ScrollingMode.Stacked, plot.Mode);
            Assert.Equal(0.0, plot.XOffset, 9);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "模式勾选项默认显示，也可以通过 ShowModeMenuItems 关闭")]
    public void ModeItems_AreShownByDefaultAndCanBeDisabled()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            // 默认显示
            Assert.True(plot.ShowModeMenuItems);
            Assert.Contains(plot.PlotMenu.ContextMenuItems, item => item.Label == "堆叠模式");
            Assert.Contains(plot.PlotMenu.ContextMenuItems, item => item.Label == "跟随模式");
            Assert.Contains(plot.PlotMenu.ContextMenuItems, item => item.Label == "手动模式（停止滚动）");

            using (ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot))
            {
                Assert.Contains(menu.Items.OfType<ToolStripMenuItem>(), item => item.Text == "跟随模式");
            }

            // 关闭后不再显示
            plot.ShowModeMenuItems = false;
        });

        host.Invoke(() =>
        {
            List<string> labels = [.. plot.PlotMenu.ContextMenuItems
                .Where(item => !item.IsSeparator)
                .Select(item => item.Label)];

            Assert.DoesNotContain("堆叠模式", labels);
            Assert.DoesNotContain("跟随模式", labels);
            Assert.DoesNotContain("手动模式（停止滚动）", labels);
            Assert.Contains("显示帮助", labels);
        });
        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "菜单支持自定义项：Add / AddSeparator / Clear / Reset")]
    public void Menu_SupportsCustomization()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            ChinesePlotMenu menu = plot.PlotMenu;
            int standardCount = menu.ContextMenuItems.Count;

            menu.Add("自定义项", _ => { });
            Assert.Equal(standardCount + 1, menu.ContextMenuItems.Count);

            menu.AddSeparator();
            Assert.Equal(standardCount + 2, menu.ContextMenuItems.Count);

            menu.Clear();
            Assert.Empty(menu.ContextMenuItems);

            menu.Reset();
            Assert.Equal(standardCount, menu.ContextMenuItems.Count);
            Assert.Equal("显示帮助", menu.ContextMenuItems[^1].Label);
        });

        host.AssertNoUnhandledExceptions();
    }
}
