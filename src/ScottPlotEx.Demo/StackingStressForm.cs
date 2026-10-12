using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace ScottPlotEx.Demo;

/// <summary>
/// 堆叠 + 堆积压力测试窗体（**控件内置堆叠模式**）：每秒 20 次采样、1 条曲线、数据只增不删，
/// 用来观察“这样堆叠能持续多久”（随点数增长，帧耗时与内存如何变化）。
///
/// 关键点：
/// <list type="bullet">
/// <item>使用控件**真实的堆叠模式**：<c>Mode = ScrollingMode.Stacked</c>，X 轴由控件维护为
/// <c>[0, 当前点数 + 2]</c>，Y 轴为全部数据的 min/max 各向外 5%，原点固定不随数据移动；</item>
/// <item>数据通过控件的 <c>AddValue</c> 进入控件内部缓冲（真实堆叠模式的前提），采样在
/// **UI 线程定时器**里进行（20 次/秒，无跨线程编组、不投递窗口消息）；</item>
/// <item>本窗体**不做 FIFO、不清理**：每帧绘制的点数随运行时间线性增长，这正是要观察的“堆积代价”。</item>
/// </list>
///
/// 提示：Visual Studio 里默认是 Debug 配置（JIT 优化关闭），帧耗时会明显高于 Release；
/// 比较“能持续多久”时请使用同一配置，或改用 Release。
/// </summary>
public sealed class StackingStressForm : Form
{
    /// <summary>采样率（次/秒）。</summary>
    private const double SampleRateHz = 20;

    /// <summary>采样定时器间隔（毫秒）：50 ms = 20 次/秒。</summary>
    private const int SampleIntervalMs = 50;

    private readonly ScrollingPlotControl _plot = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _sampleTimer = new() { Interval = SampleIntervalMs };
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 1000 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _sampleClock = Stopwatch.StartNew();
    private readonly Random _random = new(20260923);
    private readonly List<string> _csvHistory = [];

    private readonly Button _pause = new() { Text = "暂停", AutoSize = true };
    private readonly Button _reset = new() { Text = "清空重来", AutoSize = true };
    private readonly Button _export = new() { Text = "导出报告", AutoSize = true };
    private readonly Button _snapshot = new() { Text = "截图", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(12, 6, 0, 0), Text = "正在启动……" };

    private long _sampleIndex;
    private double _lastSampleTickSeconds;
    private double _sampleCarry;
    private long _lastPoints;
    private long _lastRenderCount;
    private long _lastMetricsTicks;
    private double _lastCpuSeconds;
    private double _lastFrameProbeMs = double.NaN;
    private double _nextFrameProbeSecond = 5;
    private double _nextCsvSecond = 60;
    private long _nextMilestone = 100_000;
    private string _csvPath = string.Empty;

    public StackingStressForm()
    {
        Text = "堆叠压力测试：20 次/秒 · 1 条曲线 · 控件堆叠模式（X = 0 ~ 点数+2）· 数据只增不删";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 520);

        _plot.Mode = ScrollingMode.Stacked;          // 控件内置堆叠模式：X 轴 [0, 点数+2]，原点固定
        _plot.Plot.Title("堆叠模式 + 数据只增不删：20 次/秒 · 1 条曲线");
        _plot.Plot.XLabel("数据点序号");
        _plot.Plot.YLabel("数值");

        Controls.Add(_plot);
        Controls.Add(CreateToolbar());

        _pause.Click += (_, _) => TogglePause();
        _reset.Click += (_, _) => Restart();
        _export.Click += (_, _) => ExportReport();
        _snapshot.Click += (_, _) => SaveSnapshot(Path.Combine("artifacts", $"stacking-stress-{DateTime.Now:HHmmss}.png"));

        _sampleTimer.Tick += (_, _) => AddNextSample();
        _metricsTimer.Tick += (_, _) => UpdateMetrics();

        Load += (_, _) => Restart();
    }

    private Control CreateToolbar()
    {
        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true,
        };

        toolbar.Controls.AddRange([_pause, _reset, _export, _snapshot]);
        toolbar.Controls.Add(_status);

        return toolbar;
    }

    /// <summary>开始 / 重新开始：清空数据、复位统计、启动采样。</summary>
    private void Restart()
    {
        _sampleTimer.Stop();
        _plot.ClearData();
        _plot.Mode = ScrollingMode.Stacked;   // 重新选择堆叠模式（同时复位显示百分比与偏移）

        _sampleIndex = 0;
        _clock.Restart();
        _sampleClock.Restart();
        _lastSampleTickSeconds = 0;
        _sampleCarry = 0;
        _lastPoints = 0;
        _lastRenderCount = _plot.Plot.RenderManager.RenderCount;
        _lastMetricsTicks = 0;
        _nextFrameProbeSecond = 5;
        _nextCsvSecond = 60;
        _nextMilestone = 100_000;
        _csvHistory.Clear();

        using (Process process = Process.GetCurrentProcess())
            _lastCpuSeconds = process.TotalProcessorTime.TotalSeconds;

        StartCsvLog();

        _sampleTimer.Start();
        _metricsTimer.Start();
        _pause.Text = "暂停";
        _status.Text = $"采集中：{SampleRateHz:F0} 次/秒（UI 线程定时器，{SampleIntervalMs} ms）· 控件堆叠模式：X = [0, 点数+2] · 数据只增不删";
    }

    private void TogglePause()
    {
        if (_sampleTimer.Enabled)
        {
            _sampleTimer.Stop();
            _pause.Text = "继续";
            _status.Text = "已暂停（数据保留，可继续）";
            return;
        }

        _sampleTimer.Start();
        _pause.Text = "暂停";
    }

    /// <summary>
    /// 按真实时间追赶采样：WinForms 定时器 50 ms 的实际触发间隔约 62.5 ms（系统计时器精度 15.6 ms），
    /// 因此这里用"距上次触发的真实耗时 × 采样率"补齐该有的样本数，保证平均正好 20.0 点/秒。
    /// 样本通过控件的 <c>AddValues</c> 批量写入（UI 线程，真实堆叠模式的数据入口）。
    /// </summary>
    private void AddNextSample()
    {
        if (_plot.IsDisposed)
            return;

        double now = _sampleClock.Elapsed.TotalSeconds;
        double delta = now - _lastSampleTickSeconds;
        _lastSampleTickSeconds = now;

        if (delta <= 0 || delta > 1)
            delta = SampleIntervalMs / 1000.0;

        double due = (SampleRateHz * delta) + _sampleCarry;
        int count = (int)due;
        _sampleCarry = due - count;

        if (count <= 0)
            return;

        double[] values = new double[count];

        for (int i = 0; i < count; i++)
        {
            _sampleIndex++;
            double t = _sampleIndex / SampleRateHz;
            values[i] = 50
                + (Math.Sin(t * 0.35) * 25)
                + (Math.Sin(t * 6.5) * 4)
                + ((_random.NextDouble() - 0.5) * 2);
        }

        // 控件内部：追加数据 → 按堆叠模式重算坐标轴（X = [0, 点数+2]，Y = 全量 min/max ±5%）→ 请求合并重绘
        _plot.AddValues(values);
    }

    private void UpdateMetrics()
    {
        int points = _plot.DataCount;
        ScottPlot.AxisLimits limits = _plot.Plot.Axes.GetLimits();
        long renders = _plot.Plot.RenderManager.RenderCount;
        long nowTicks = _clock.ElapsedMilliseconds;
        double seconds = Math.Max(0.001, (nowTicks - _lastMetricsTicks) / 1000.0);
        double pointsPerSecond = (points - _lastPoints) / seconds;
        double fps = (renders - _lastRenderCount) / seconds;

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        double heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        double workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
        double cpuPercent = (process.TotalProcessorTime.TotalSeconds - _lastCpuSeconds) / seconds * 100.0;

        _lastPoints = points;
        _lastRenderCount = renders;
        _lastMetricsTicks = nowTicks;
        _lastCpuSeconds = process.TotalProcessorTime.TotalSeconds;

        double elapsed = _clock.Elapsed.TotalSeconds;

        // 帧耗时探针：每 5 秒做 3 次强制整帧绘制取中位数（不改变堆叠语义）
        if (elapsed >= _nextFrameProbeSecond)
        {
            _nextFrameProbeSecond = elapsed + 5;
            _lastFrameProbeMs = MeasureForcedRefresh();
        }

        TimeSpan runtime = TimeSpan.FromMilliseconds(nowTicks);
        _status.Text =
            $"运行 {runtime:d\\.hh\\:mm\\:ss} · 点数 {points:N0}（{pointsPerSecond:F1} 点/秒）· " +
            $"X 轴 [{Math.Min(limits.Left, limits.Right):F0}, {Math.Max(limits.Left, limits.Right):F0}] · " +
            $"绘制 {fps:F1} FPS · 帧耗时 {(double.IsNaN(_lastFrameProbeMs) ? "—" : _lastFrameProbeMs.ToString("F1") + " ms")} · " +
            $"CPU {cpuPercent:F0}%（单核）· 托管堆 {heapMb:F0} MB · 工作集 {workingSetMb:F0} MB";

        // 每 60 秒写一行明细；跨过整十万点再写一行里程碑，便于事后看“拐点”
        if (elapsed >= _nextCsvSecond)
        {
            _nextCsvSecond = elapsed + 60;
            AppendCsv($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},定时,{points},{(double.IsNaN(_lastFrameProbeMs) ? 0 : _lastFrameProbeMs):F1},{heapMb:F0},{workingSetMb:F0},{fps:F1}");
        }

        if (points >= _nextMilestone)
        {
            AppendCsv($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},里程碑,{points},{(double.IsNaN(_lastFrameProbeMs) ? 0 : _lastFrameProbeMs):F1},{heapMb:F0},{workingSetMb:F0},{fps:F1}");
            _nextMilestone = ((points / 100_000) + 1) * 100_000;
        }
    }

    /// <summary>强制整帧绘制的耗时（毫秒，3 次中位数）。</summary>
    private double MeasureForcedRefresh()
    {
        try
        {
            _plot.Refresh();
            List<double> samples = [];

            for (int i = 0; i < 3; i++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                _plot.Refresh();
                stopwatch.Stop();
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            samples.Sort();
            return samples[1];
        }
        catch (Exception)
        {
            return double.NaN;
        }
    }

    private void StartCsvLog()
    {
        try
        {
            string directory = Path.GetFullPath("artifacts");
            Directory.CreateDirectory(directory);
            _csvPath = Path.Combine(directory, $"stacking-stress-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(_csvPath, "时间,类型,点数,帧耗时ms,托管堆MB,工作集MB,FPS" + Environment.NewLine, Encoding.UTF8);
            AppendCsv($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},开始,0,0,0,0,0");
        }
        catch (Exception exception)
        {
            _csvPath = string.Empty;
            _status.Text = "日志文件创建失败：" + exception.Message;
        }
    }

    private void AppendCsv(string line)
    {
        if (string.IsNullOrEmpty(_csvPath))
            return;

        try
        {
            File.AppendAllText(_csvPath, line + Environment.NewLine, Encoding.UTF8);
            _csvHistory.Add(line);
        }
        catch (Exception)
        {
            // 记录失败不影响压测
        }
    }

    /// <summary>导出 Markdown 报告（运行时长、点数、X 轴范围、内存、帧耗时、日志路径）。</summary>
    private void ExportReport()
    {
        try
        {
            int points = _plot.DataCount;
            ScottPlot.AxisLimits limits = _plot.Plot.Axes.GetLimits();

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan runtime = TimeSpan.FromMilliseconds(_clock.ElapsedMilliseconds);
            string path = Path.GetFullPath(Path.Combine("artifacts", $"stacking-stress-report-{DateTime.Now:yyyyMMdd-HHmmss}.md"));

            StringBuilder report = new();
            report.AppendLine("# 堆叠 + 堆积压力测试报告");
            report.AppendLine();
            report.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"- 运行时长：{runtime:dd\\.hh\\:mm\\:ss}");
            report.AppendLine($"- 采样率：{SampleRateHz:F0} 次/秒；曲线数：1；模式：**控件堆叠模式**（X = [0, 点数+2]）；策略：数据只增不删");
            report.AppendLine($"- 当前点数：{points:N0}（约 {points / SampleRateHz / 3600.0:F2} 小时的数据）");
            report.AppendLine($"- 当前 X 轴范围：[{Math.Min(limits.Left, limits.Right):F0}, {Math.Max(limits.Left, limits.Right):F0}]（期望 0 ~ 点数+2）");
            report.AppendLine($"- 当前强制整帧耗时：{(double.IsNaN(_lastFrameProbeMs) ? "—" : _lastFrameProbeMs.ToString("F1") + " ms")}");
            report.AppendLine($"- 内存：托管堆 {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F0} MB，工作集 {process.WorkingSet64 / 1024.0 / 1024.0:F0} MB");
            report.AppendLine($"- 明细日志：`{_csvPath}`");
            report.AppendLine();
            report.AppendLine("> 堆积模式下每帧绘制的点数随运行时间线性增长，帧耗时会持续上升；");
            report.AppendLine("> 若需要长期运行，请改用滚动窗口（FIFO）或按像素抽稀。");

            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            _status.Text = "报告已导出：" + path;
        }
        catch (Exception exception)
        {
            _status.Text = "导出失败：" + exception.Message;
        }
    }

    /// <summary>无人值守验证：运行 seconds 秒后自动截图并关闭（供自动化使用，VS 里不需要）。</summary>
    public void RunSmokeTest(int seconds, string snapshotPath)
    {
        System.Windows.Forms.Timer timer = new() { Interval = Math.Max(1, seconds) * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveSnapshot(snapshotPath);
            Close();
        };
        timer.Start();
    }

    private void SaveSnapshot(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            WindowSnapshot.Save(this, fullPath);
            _status.Text = "截图已保存：" + fullPath;
        }
        catch (Exception exception)
        {
            _status.Text = "截图失败：" + exception.Message;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sampleTimer.Dispose();
            _metricsTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>用 PrintWindow 抓取窗口内容（不截取桌面）。</summary>
    private static class WindowSnapshot
    {
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static void Save(Form form, string path)
        {
            if (!GetWindowRect(form.Handle, out RECT rect))
                throw new InvalidOperationException("无法获取窗口尺寸。");

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("窗口尺寸无效。");

            using Bitmap bitmap = new(width, height);

            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr hdc = graphics.GetHdc();

                try
                {
                    if (!PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT))
                        throw new InvalidOperationException("PrintWindow 调用失败。");
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
