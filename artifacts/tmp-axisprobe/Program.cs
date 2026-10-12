using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace AxisProbe;

/// <summary>
/// 探针：逐场景打印 ScrollingPlotControl 的坐标轴状态，用来定位
/// “新建控件在写入数据前坐标为 ±∞ / 未设置” 的确切时机。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();

        Console.WriteLine("== 场景 1：刚构造（未加入窗体、未渲染、无数据） ==");
        ScrollingPlotControl plot = new();
        Report("构造后", plot);
        ProbeImage(plot, "构造后立即离屏渲染");

        Console.WriteLine();
        Console.WriteLine("== 场景 2：加入窗体并显示（触发首次真实渲染），仍无数据 ==");
        using Form form = CreateHost();
        plot.Dock = DockStyle.Fill;
        form.Controls.Add(plot);
        form.Show();
        Pump(400);
        Report("显示并渲染后", plot);

        Console.WriteLine();
        Console.WriteLine("== 场景 3：强制整帧绘制（Refresh） ==");
        plot.Refresh();
        Pump(100);
        Report("Refresh 后", plot);

        Console.WriteLine();
        Console.WriteLine("== 场景 4：写入第一个数据点 ==");
        plot.AddValue(1.0);
        Report("AddValue 后", plot);

        Console.WriteLine();
        Console.WriteLine("== 场景 5：清空数据（ClearData） ==");
        plot.ClearData();
        Report("ClearData 后", plot);

        Console.WriteLine();
        Console.WriteLine("== 场景 6：空控件上做“中键整体显示”（Autoscale） ==");
        MiddleClick(plot);
        Report("空图中键整体显示后", plot);

        Console.WriteLine();
        Console.WriteLine("== 场景 7：Reset() ==");
        plot.Reset();
        Report("Reset 后", plot);

        Console.WriteLine();
        Console.WriteLine("== 场景 8：释放并重建控件（结构变化阶段会反复做这件事） ==");
        form.Controls.Remove(plot);
        plot.Dispose();
        ScrollingPlotControl rebuilt = new() { Dock = DockStyle.Fill };
        form.Controls.Add(rebuilt);
        Report("重建后立刻（尚未渲染、未写数据）", rebuilt);
        Pump(400);
        Report("重建后渲染一次", rebuilt);

        Console.WriteLine();
        Console.WriteLine("== 场景 9：重建后不渲染、直接写数据 ==");
        ScrollingPlotControl rebuilt2 = new() { Dock = DockStyle.Fill };
        form.Controls.Remove(rebuilt);
        rebuilt.Dispose();
        form.Controls.Add(rebuilt2);
        Report("重建后立刻", rebuilt2);
        rebuilt2.AddValue(5.0);
        Report("写 1 个点后", rebuilt2);

        form.Close();
        Console.WriteLine();
        Console.WriteLine("探针结束。");
    }

    private static Form CreateHost() => new()
    {
        ClientSize = new Size(900, 600),
        StartPosition = FormStartPosition.Manual,
        Location = new Point(0, 0),
        Opacity = 0.01,
        ShowInTaskbar = false,
        FormBorderStyle = FormBorderStyle.FixedToolWindow,
    };

    private static void Pump(int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void Report(string label, ScrollingPlotControl plot)
    {
        ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();

        Console.WriteLine(
            $"{label,-22} IsReal={limits.IsReal,-5} " +
            $"X=[{Format(limits.Left)}, {Format(limits.Right)}] " +
            $"Y=[{Format(limits.Bottom)}, {Format(limits.Top)}] " +
            $"有句柄={plot.IsHandleCreated} 数据点={plot.DataCount}");
    }

    private static string Format(double value) =>
        double.IsPositiveInfinity(value) ? "+∞"
        : double.IsNegativeInfinity(value) ? "-∞"
        : double.IsNaN(value) ? "NaN"
        : value.ToString("F3");

    private static void ProbeImage(ScrollingPlotControl plot, string label)
    {
        try
        {
            using ScottPlot.Image image = plot.Plot.GetImage(400, 300);
            Console.WriteLine($"{label,-22} 离屏渲染成功（未抛异常）");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{label,-22} 离屏渲染抛异常：{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>通过反射注入中键按下 / 抬起（与压测工具、单元测试使用同一条输入管线）。</summary>
    private static void MiddleClick(ScrollingPlotControl plot)
    {
        Control? canvas = plot.FormsPlot.Controls
            .OfType<Control>()
            .FirstOrDefault(control => control.GetType().Name.Contains("SKControl", StringComparison.Ordinal));

        if (canvas is null)
        {
            Console.WriteLine("（找不到画布控件，跳过中键测试）");
            return;
        }

        ScottPlot.PixelRect rect = plot.Plot.RenderManager.LastRender.DataRect;
        Point point = new(
            (int)Math.Round(Math.Min(rect.Left, rect.Right) + (Math.Abs(rect.Right - rect.Left) / 2)),
            (int)Math.Round(Math.Min(rect.Top, rect.Bottom) + (Math.Abs(rect.Bottom - rect.Top) / 2)));

        Invoke(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Middle, 1, point.X, point.Y, 0));
        Invoke(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Middle, 1, point.X, point.Y, 0));
        Pump(150);
    }

    private static void Invoke(Control control, string methodName, EventArgs args)
    {
        for (Type? type = control.GetType(); type is not null; type = type.BaseType)
        {
            MethodInfo? method = type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            if (method is not null)
            {
                method.Invoke(control, [args]);
                return;
            }
        }

        throw new MissingMethodException(control.GetType().FullName, methodName);
    }
}
