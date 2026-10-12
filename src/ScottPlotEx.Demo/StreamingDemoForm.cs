using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace ScottPlotEx.Demo;

/// <summary>
/// 10 曲线流式演示：默认 10 条曲线 × 每条 1000 点/秒（合计 10,000 点/秒），全部通过控件的多曲线 API 完成。
/// <list type="number">
/// <item>每条曲线由 <see cref="ScrollingPlotControl.AddSeries"/> 创建；采集线程直接调用 <see cref="PlotSeries.AddValues(ReadOnlySpan{double})"/>，
/// 不碰 UI、不自建缓冲区——控件把各线程的写入按帧合并成一条窗口消息，不会淹没消息队列；</item>
/// <item>X 轴为相对时间（<see cref="ScrollingPlotControl.XAxisUnit"/> = <see cref="XAxisUnit.RelativeTime"/>）：
/// 每条曲线的采样率（<see cref="PlotSeries.SampleRate"/>）把采样序号换算成秒，刻度自动显示为 秒 / 分:秒；</item>
/// <item>跟随模式 + <see cref="ScrollingPlotControl.FollowWindowDuration"/>：始终显示最近 N 秒（示波器式滚动），
/// Y 轴取所有曲线可见范围的并集（极值金字塔，开销与窗口长度无关）；</item>
/// <item><see cref="ScrollingPlotControl.MaxFramesPerSecond"/> 限制重绘帧率（高精度定时，30 帧/秒就是 30 帧/秒，
/// 不会像 WinForms 定时器那样被 15.6 ms 的时钟精度拉长到约 21 帧/秒）。</item>
/// </list>
/// 说明：控件目前保留全部历史数据（尚无“只保留最近 N 点”的选项），内存随运行时间增长，状态栏会显示内存中的点数；
/// 需要长时间运行时可以定期点“清空”。
/// </summary>
public sealed class StreamingDemoForm : Form
{
    private const int DefaultCurves = 10;
    private const int DefaultRatePerCurve = 1000;
    private const int DefaultWindowSeconds = 60;

    /// <summary>默认帧率上限：30 帧/秒（实时曲线足够，CPU 约为 60 帧/秒的一半）。</summary>
    private const int DefaultMaxFps = 30;

    private readonly ScrollingPlotControl _plot = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 1000 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly NumericUpDown _curveCount = new() { Minimum = 1, Maximum = 20, Value = DefaultCurves, Width = 56 };
    private readonly NumericUpDown _ratePerCurve = new() { Minimum = 10, Maximum = 20000, Increment = 100, Value = DefaultRatePerCurve, Width = 80 };
    private readonly NumericUpDown _windowSeconds = new() { Minimum = 5, Maximum = 600, Increment = 5, Value = DefaultWindowSeconds, Width = 64 };
    private readonly NumericUpDown _maxFps = new() { Minimum = 0, Maximum = 240, Increment = 10, Value = DefaultMaxFps, Width = 56 };
    private readonly Button _restart = new() { Text = "应用并重启采集", AutoSize = true };
    private readonly Button _pause = new() { Text = "暂停", AutoSize = true };
    private readonly Button _clear = new() { Text = "清空", AutoSize = true };
    private readonly Button _snapshot = new() { Text = "截图", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(12, 6, 0, 0), Text = "正在启动……" };

    private readonly Random _seedSource = new(20260923);
    private readonly List<Thread> _writers = [];
    private readonly List<PlotSeries> _series = [];
    private readonly UiLatencyProbe _latency;

    private volatile bool _writersStop = true;
    private int _rate;
    private int _windowPoints;
    private long _written;
    private long _lastRenderCount;
    private long _lastWritten;
    private long _lastMetricsTicks;
    private double _lastCpuSeconds;

    /// <summary>窗体中使用的实时曲线控件（便于测试）。</summary>
    public ScrollingPlotControl PlotControl => _plot;

    public StreamingDemoForm()
    {
        Text = "10 曲线流式演示：多曲线控件 API（跟随模式固定窗口 + 帧率上限）";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 520);

        _plot.Plot.YLabel("数值");

        // 10 条曲线的图例放在数据区右侧外面，不遮挡曲线
        _plot.Plot.ShowLegend(ScottPlot.Edge.Right);

        Controls.Add(_plot);
        Controls.Add(CreateToolbar());

        _latency = new UiLatencyProbe(this);
        CpuMeter.Warmup();

        _restart.Click += (_, _) => Restart();
        _pause.Click += (_, _) => TogglePause();
        _clear.Click += (_, _) => _plot.ClearData();
        _snapshot.Click += (_, _) => SaveSnapshot(Path.Combine("artifacts", $"streaming-demo-{DateTime.Now:HHmmss}.png"));
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

        toolbar.Controls.AddRange([
            new Label { Text = "曲线数", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _curveCount,
            new Label { Text = "每条点/秒", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _ratePerCurve,
            new Label { Text = "窗口(秒)", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _windowSeconds,
            new Label { Text = "帧率上限(0=不限)", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _maxFps,
            _restart, _pause, _clear, _snapshot,
        ]);
        toolbar.Controls.Add(_status);

        return toolbar;
    }

    /// <summary>按当前参数重建曲线并重启采集（曲线数 / 速率 / 窗口长度 / 帧率上限）。</summary>
    private void Restart()
    {
        StopWriters();

        foreach (PlotSeries series in _series)
            series.Remove();

        _series.Clear();
        _plot.ClearData();

        _rate = (int)_ratePerCurve.Value;
        _windowPoints = (int)Math.Round(_rate * (double)_windowSeconds.Value);
        int curveCount = (int)_curveCount.Value;

        _plot.Plot.Title($"{curveCount} 条曲线 × {_rate:N0} 点/秒（控件多曲线 API：采集线程直接 AddValues）");
        _plot.Plot.XLabel("相对时间（距第一个数据点）");

        // 相对时间单位：每条曲线按自己的采样率把采样序号换算成秒（X = 下标 ÷ 点每秒），刻度自动显示为 秒 / 分:秒
        _plot.XAxisUnit = XAxisUnit.RelativeTime;

        // 跟随模式 + 时间窗口：始终显示最近 N 秒；帧率上限由控件用高精度定时器控制
        _plot.Mode = ScrollingMode.Follow;
        _plot.FollowWindowDuration = TimeSpan.FromSeconds((double)_windowSeconds.Value);
        _plot.MaxFramesPerSecond = (int)_maxFps.Value;

        for (int i = 0; i < curveCount; i++)
        {
            PlotSeries series = _plot.AddSeries($"曲线 {i + 1}", Palette(i));
            series.SampleRate = _rate;
            _series.Add(series);
        }

        Interlocked.Exchange(ref _written, 0);
        _lastWritten = 0;
        _lastRenderCount = _plot.Plot.RenderManager.RenderCount;

        StartWriters();

        _metricsTimer.Start();
        _status.Text = $"采集中：{curveCount} 条曲线 × {_rate:N0} 点/秒 = {curveCount * (long)_rate:N0} 点/秒，窗口 {_windowSeconds.Value} 秒（每条 {_windowPoints:N0} 点）";
    }

    private void StartWriters()
    {
        _writersStop = false;

        for (int i = 0; i < _series.Count; i++)
        {
            PlotSeries series = _series[i];
            int index = i;
            Thread writer = new(() => WriterLoop(series, index))
            {
                IsBackground = true,
                Name = $"采集线程 {index + 1}",
            };
            _writers.Add(writer);
            writer.Start();
        }

        _pause.Text = "暂停";
    }

    private void StopWriters()
    {
        _writersStop = true;

        foreach (Thread writer in _writers)
            writer.Join(2000);

        _writers.Clear();
    }

    private void TogglePause()
    {
        if (_writersStop)
        {
            StartWriters(); // 继续采集：保留已有数据
            return;
        }

        StopWriters();
        _pause.Text = "继续";
    }

    /// <summary>采集线程：按目标速率产生数据，直接调用曲线的 AddValues（控件负责跨线程合并，不碰 UI）。</summary>
    private void WriterLoop(PlotSeries series, int index)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Random random;

        lock (_seedSource)
            random = new Random(_seedSource.Next());

        double phase = index * 0.7;
        double center = (index % 5) * 6.0;
        double[] batch = new double[512];
        double last = 0;
        double carry = 0;
        long sample = 0;

        while (!_writersStop)
        {
            double now = clock.Elapsed.TotalSeconds;
            double delta = now - last;
            last = now;

            double due = (_rate * delta) + carry;
            int count = (int)due;
            carry = due - count;

            if (count <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            if (count > batch.Length)
                batch = new double[count];

            for (int i = 0; i < count; i++)
            {
                sample++;
                double t = (sample / (double)_rate) + phase;
                batch[i] = center
                    + (Math.Sin(t * 2.1) * 8)
                    + (Math.Sin(t * 11.0) * 1.5)
                    + ((random.NextDouble() - 0.5) * 0.8);
            }

            // 非 UI 线程调用：控件先复制到该曲线的待处理列表，返回后即可复用 batch
            series.AddValues(batch.AsSpan(0, count));
            Interlocked.Add(ref _written, count);
            Thread.Sleep(1);
        }
    }

    private void UpdateMetrics()
    {
        long written = Interlocked.Read(ref _written);
        long renders = _plot.Plot.RenderManager.RenderCount;
        long nowTicks = _clock.ElapsedMilliseconds;
        double seconds = Math.Max(0.001, (nowTicks - _lastMetricsTicks) / 1000.0);

        double pointsPerSecond = (written - _lastWritten) / seconds;
        double fps = (renders - _lastRenderCount) / seconds;
        double heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        double workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
        double cpuSeconds = CpuMeter.ProcessCpuSeconds();
        string cpu = _lastCpuSeconds > 0 && cpuSeconds > 0 ? $"{(cpuSeconds - _lastCpuSeconds) / seconds * 100.0:F0}%" : "—";
        long storedPoints = _series.Sum(s => (long)s.DataCount);
        (double p50, double p95) = _latency.TakePercentiles();

        _lastWritten = written;
        _lastRenderCount = renders;
        _lastMetricsTicks = nowTicks;
        _lastCpuSeconds = cpuSeconds;
        _status.Text =
            $"写入 {pointsPerSecond:N0} 点/秒 · 绘制 {fps:F1} FPS · CPU {cpu}（单核基准）· " +
            $"UI 延迟 p50 {p50:F1} / p95 {p95:F1} ms · 内存中 {storedPoints / 10_000.0:F0} 万点 · " +
            $"托管堆 {heapMb:F0} MB · 工作集 {workingSetMb:F0} MB";
    }

    /// <summary>无人值守冒烟：运行 seconds 秒后自动截图并关闭（供自动化验证使用）。</summary>
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

    /// <summary>无人值守验证用：直接指定曲线数 / 每条速率 / 窗口秒数 / 帧率上限（必须在窗体加载前调用；maxFps 小于 0 表示保持默认）。</summary>
    public void Configure(int curves, int ratePerCurve, int windowSeconds, int maxFps = -1)
    {
        _curveCount.Value = Math.Clamp(curves, (int)_curveCount.Minimum, (int)_curveCount.Maximum);
        _ratePerCurve.Value = Math.Clamp(ratePerCurve, (int)_ratePerCurve.Minimum, (int)_ratePerCurve.Maximum);
        _windowSeconds.Value = Math.Clamp(windowSeconds, (int)_windowSeconds.Minimum, (int)_windowSeconds.Maximum);

        if (maxFps >= 0)
            _maxFps.Value = Math.Clamp(maxFps, (int)_maxFps.Minimum, (int)_maxFps.Maximum);
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
            StopWriters();
            _latency.Dispose();
            _metricsTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private static ScottPlot.Color Palette(int index) => (index % 10) switch
    {
        0 => ScottPlot.Colors.Blue,
        1 => ScottPlot.Colors.Red,
        2 => ScottPlot.Colors.Green,
        3 => ScottPlot.Colors.Orange,
        4 => ScottPlot.Colors.Purple,
        5 => ScottPlot.Colors.Cyan,
        6 => ScottPlot.Colors.Magenta,
        7 => ScottPlot.Colors.Brown,
        8 => ScottPlot.Colors.Olive,
        _ => ScottPlot.Colors.Navy,
    };

    /// <summary>
    /// 进程 CPU 计量（周期精确）。Process.TotalProcessorTime 按约 15.6 ms 的时钟中断采样记账，
    /// 每帧只忙几毫秒的场景会被严重多算或少算；这里用 QueryProcessCycleTime，按忙等校准的每秒周期数换算成秒。
    /// </summary>
    private static class CpuMeter
    {
        private static readonly Lazy<double> CyclesPerSecond = new(Calibrate);

        /// <summary>提前在后台线程上完成校准（忙等约 0.6 秒），避免第一次读数时卡住 UI 线程。</summary>
        public static void Warmup() => ThreadPool.QueueUserWorkItem(_ => _ = CyclesPerSecond.Value);

        public static double ProcessCpuSeconds()
        {
            if (!CyclesPerSecond.IsValueCreated)
                return 0; // 校准尚未完成：本秒不计

            QueryProcessCycleTime(GetCurrentProcess(), out ulong cycles);
            return cycles / CyclesPerSecond.Value;
        }

        private static double Calibrate()
        {
            double best = 0;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                QueryThreadCycleTime(GetCurrentThread(), out ulong start);
                Stopwatch stopwatch = Stopwatch.StartNew();

                while (stopwatch.ElapsedMilliseconds < 200)
                    Thread.SpinWait(100);

                QueryThreadCycleTime(GetCurrentThread(), out ulong end);
                best = Math.Max(best, (end - start) / stopwatch.Elapsed.TotalSeconds);
            }

            return best;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    }

    /// <summary>UI 线程响应延迟探针：后台线程每 10 ms 投递一次 BeginInvoke，记录从投递到执行的耗时。</summary>
    private sealed class UiLatencyProbe : IDisposable
    {
        private readonly Control _owner;
        private readonly Thread _thread;
        private readonly List<double> _samples = [];
        private volatile bool _stop;

        public UiLatencyProbe(Control owner)
        {
            _owner = owner;
            _thread = new Thread(Run) { IsBackground = true, Name = "UI 延迟探针" };
            _owner.HandleCreated += (_, _) => _thread.Start();
        }

        private void Run()
        {
            while (!_stop)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                try
                {
                    _owner.BeginInvoke(() =>
                    {
                        double ms = stopwatch.Elapsed.TotalMilliseconds;
                        lock (_samples)
                            _samples.Add(ms);
                    });
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    return; // 窗口已关闭
                }

                Thread.Sleep(10);
            }
        }

        /// <summary>取出自上次调用以来的样本，返回 p50 / p95（毫秒）。</summary>
        public (double P50, double P95) TakePercentiles()
        {
            double[] samples;

            lock (_samples)
            {
                samples = [.. _samples];
                _samples.Clear();
            }

            if (samples.Length == 0)
                return (0, 0);

            Array.Sort(samples);
            return (samples[samples.Length / 2], samples[Math.Min(samples.Length - 1, (int)(samples.Length * 0.95))]);
        }

        public void Dispose() => _stop = true;
    }
}
