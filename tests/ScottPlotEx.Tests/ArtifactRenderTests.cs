using System.Drawing;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 渲染示例图片，用于人工检查两种模式的显示效果（输出到仓库的 artifacts 目录）。
/// </summary>
public class ArtifactRenderTests
{
    private const int Width = 1100;
    private const int Height = 620;

    [Fact(DisplayName = "渲染堆叠模式与跟随模式示例图片")]
    public void RenderModeScreenshots()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(Width, Height));
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        string stackedPath = Path.Combine(TestPaths.ArtifactsDirectory, "stacked-mode.png");
        string followPath = Path.Combine(TestPaths.ArtifactsDirectory, "follow-mode.png");

        host.Invoke(() =>
        {
            for (int i = 1; i <= 120; i++)
            {
                double wave = 50 + 25 * Math.Sin(i / 25.0) + 4 * Math.Sin(i / 3.0);
                plot.AddValue(wave);
            }

            plot.Plot.Title("ScottPlotEx 堆叠模式：X 轴 0 ~ 点数+2");
            plot.Mode = ScrollingMode.Stacked;
            plot.Plot.Save(stackedPath, Width, Height);

            plot.Plot.Title("ScottPlotEx 跟随模式：X 轴最新 30%，最大值 = 点数+2");
            plot.Mode = ScrollingMode.Follow;
            plot.Plot.Save(followPath, Width, Height);
        });

        Assert.True(File.Exists(stackedPath), "未生成堆叠模式示例图片。");
        Assert.True(File.Exists(followPath), "未生成跟随模式示例图片。");

        using Bitmap stacked = new(stackedPath);
        using Bitmap follow = new(followPath);

        Assert.Equal(Width, stacked.Width);
        Assert.Equal(Height, stacked.Height);
        Assert.Equal(Width, follow.Width);
        Assert.Equal(Height, follow.Height);

        Assert.True(new FileInfo(stackedPath).Length > 2048, "堆叠模式图片内容异常。");
        Assert.True(new FileInfo(followPath).Length > 2048, "跟随模式图片内容异常。");

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "渲染中文右键菜单示例图片（由控件自身的菜单构建代码绘制）")]
    public void RenderContextMenuImage()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        string artifactPath = Path.Combine(TestPaths.ArtifactsDirectory, "context-menu.png");
        string docsPath = Path.Combine(TestPaths.DocsDirectory, "demo-04-context-menu.png");

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 30);

            using ContextMenuStrip menu = plot.PlotMenu.BuildContextMenu(plot.Plot);
            menu.CreateControl();
            menu.PerformLayout();

            Assert.True(menu.Width > 0 && menu.Height > 0, "右键菜单尺寸异常。");

            using System.Drawing.Bitmap bitmap = new(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));

            // 放大 2 倍，方便在文档中查看
            using System.Drawing.Bitmap scaled = new(menu.Width * 2, menu.Height * 2);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(bitmap, 0, 0, scaled.Width, scaled.Height);
            }

            scaled.Save(artifactPath, System.Drawing.Imaging.ImageFormat.Png);
            scaled.Save(docsPath, System.Drawing.Imaging.ImageFormat.Png);
        });

        Assert.True(File.Exists(artifactPath), "未生成右键菜单示例图片。");
        Assert.True(File.Exists(docsPath), "未生成右键菜单文档截图。");

        host.AssertNoUnhandledExceptions();
    }
}
