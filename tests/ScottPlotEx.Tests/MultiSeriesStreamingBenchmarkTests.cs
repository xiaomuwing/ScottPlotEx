using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace ScottPlotEx.Tests;

/// <summary>
/// 性能基准：10 条曲线 × 每条 1000 点/秒，对比两种写法——
/// 旧演示的“自建定长缓冲 + 原生 Signal + WinForms 定时器按帧刷新”，与控件多曲线 API（采集线程直接 AddValues）。
/// 结果写入 artifacts/multi-series-streaming-report.md。
/// </summary>
[Trait("Category", "Performance")]
public class MultiSeriesStreamingBenchmarkTests
{
    private const int Curves = 10;
    private const int RatePerCurve = 1000;
    private const int WindowSeconds = 60;
    private const int Width = 1100;
    private const int Height = 660;
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Measure = TimeSpan.FromSeconds(8);
    private const int Rounds = 2;

    /// <summary>
    /// 开始测量前先给每条曲线灌满一个窗口（60 秒 × 1000 点）的数据：测的是长时间运行后的稳定状态
    /// （屏上每条曲线 60,000 点），而不是刚启动、屏上只有几千点的状态。
    /// </summary>
    private const int PrefillPoints = RatePerCurve * WindowSeconds;

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private readonly ITestOutputHelper _output;

    public MultiSeriesStreamingBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "性能基准：10 条曲线 × 每条 1000 点/秒（自建缓冲写法 vs 控件多曲线 API）")]
    public void CompareStreamingApproaches()
    {
        Func<Result>[] scenarios =
        [
            () => RunSelfBuffered(timerIntervalMs: 33, "A1 自建缓冲 + 原生 Signal（旧演示写法）", "WinForms 定时器 33 ms"),
            () => RunSelfBuffered(timerIntervalMs: 16, "A2 自建缓冲 + 原生 Signal（旧演示写法）", "WinForms 定时器 16 ms"),
            () => RunControlApi(maxFps: 30, "B 控件多曲线 API（采集线程直接 AddValues）", "MaxFramesPerSecond = 30"),
            () => RunControlApi(maxFps: 0, "C 控件多曲线 API（采集线程直接 AddValues）", "不限帧率"),
            () => RunControlApi(maxFps: 0, "D 控件多曲线 API + 相对时间单位", "不限帧率", XAxisUnit.RelativeTime),
        ];

        // 各写法交替执行两轮，减小系统负载漂移的影响；每项指标取两轮的较小值（耗时类）或均值（速率类）
        List<Result>[] rounds = [.. Enumerable.Range(0, Rounds).Select(_ => new List<Result>())];

        for (int round = 0; round < Rounds; round++)
        {
            foreach (Func<Result> scenario in scenarios)
                rounds[round].Add(scenario());
        }

        List<Result> results = [.. Enumerable.Range(0, scenarios.Length).Select(i => Combine([.. rounds.Select(r => r[i])]))];

        StringBuilder report = new();
        report.AppendLine("# 10 条曲线 × 每条 1000 点/秒：自建缓冲写法 vs 控件多曲线 API");
        report.AppendLine();
        report.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"- 操作系统：{RuntimeInformation.OSDescription}；处理器：{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}（逻辑核心数 {Environment.ProcessorCount}）");
        report.AppendLine($"- 控件 {Width}×{Height}，普通不透明窗口；每条曲线显示最近 {WindowSeconds} 秒；开始前先给每条曲线灌满一个窗口（{PrefillPoints:N0} 点），测的是长时间运行后的稳定状态");
        report.AppendLine($"- 每种写法预热 {Warmup.TotalSeconds:F0} 秒后测量 {Measure.TotalSeconds:F0} 秒");
        report.AppendLine($"- 构建配置：{BuildConfiguration}（Debug 构建下控件代码不经 JIT 优化，而 ScottPlot 来自 Release 包，两种写法不可比，请用 Release 运行）");
        report.AppendLine($"- 各写法交替执行 {Rounds} 轮：UI 线程 CPU 取较小值，其余取均值");
        report.AppendLine("- CPU 计量为周期精确（QueryThreadCycleTime / QueryProcessCycleTime，按忙等校准换算成时间），不受 15.6 ms 时钟采样记账的影响");
        report.AppendLine("- **UI 线程 CPU**：UI 线程自身的 CPU 时间（GetThreadTimes），即“接收数据 + 计算坐标轴 + 绘制”的全部开销，是比较两种写法的主要指标；");
        report.AppendLine("  进程 CPU 还包括 10 个采集线程（每毫秒唤醒一次）与 GC / JIT 后台线程，波动大，仅供参考");
        report.AppendLine("- UI 延迟：后台线程每 5 ms 投递一次 BeginInvoke，到 UI 线程执行的耗时");
        report.AppendLine();
        report.AppendLine("| 写法 | 帧率控制 | 达成写入(点/秒) | 实测 FPS | **UI 线程 CPU(单核%)** | **UI 线程每帧(ms)** | 进程 CPU(单核%) | UI 延迟 p50 / p95 / max (ms) | 内存中点数 |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (Result r in results)
        {
            report.AppendLine(
                $"| {r.Name} | {r.FrameControl} | {r.PointsPerSecond:N0} | {r.Fps:F1} | **{r.UiCpuPercent:F1}%** | **{r.UiCpuPercent * 10 / Math.Max(r.Fps, 0.1):F2}** | {r.CpuPercent:F0}% | " +
                $"{r.LatencyP50:F1} / {r.LatencyP95:F1} / {r.LatencyMax:F0} | {r.StoredPoints:N0} |");
        }

        report.AppendLine();
        report.AppendLine("说明：");
        report.AppendLine("- A1 是旧演示的默认设置：33 ms 的 WinForms 定时器受系统时钟约 15.6 ms 精度影响，实际约 47 ms 一帧（约 21 FPS）；");
        report.AppendLine("  A2 把定时器设为 16 ms（实际帧率取决于系统时钟精度），用来和 B 在相近帧率下比较。");
        report.AppendLine("- B / C 由控件按帧合并跨线程写入，B 的帧率上限由高精度定时器控制；C 不限帧率，每帧画完立即处理下一批数据。");
        report.AppendLine("- D 与 C 相同，只是 X 轴单位为相对时间（每条曲线按采样率换算成秒、跟随窗口 60 秒、时间刻度生成器），用来确认时间单位没有额外开销。");
        report.AppendLine("- 自建缓冲写法只保留最近 60 秒（内存恒定）；控件目前保留全部历史数据，内存中点数随运行时间增长（每条每小时约 360 万点、约 32 MB）。");
        report.AppendLine();
        report.AppendLine("重新运行：`dotnet test ScottPlotEx.sln -c Release --filter \"FullyQualifiedName~MultiSeriesStreamingBenchmarkTests\"`（结果写入此文件）。");

        string markdown = report.ToString();
        File.WriteAllText(Path.Combine(TestPaths.ArtifactsDirectory, "multi-series-streaming-report.md"), markdown, Encoding.UTF8);
        _output.WriteLine(markdown);

        Assert.All(results, r => Assert.True(r.PointsPerSecond > Curves * RatePerCurve * 0.9, $"{r.Name}（{r.FrameControl}）只写入了 {r.PointsPerSecond:N0} 点/秒。"));
        Assert.All(results.Where(r => r.Lost.HasValue), r => Assert.Equal(0, r.Lost!.Value));
    }

    /// <summary>旧演示写法：采集线程写自建定长缓冲；UI 线程用 WinForms 定时器按帧设置 MinRenderIndex/MaxRenderIndex 与坐标轴。</summary>
    private static Result RunSelfBuffered(int timerIntervalMs, string name, string frameControl)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(Width, Height), opacity: 1.0);
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        int windowPoints = RatePerCurve * WindowSeconds;
        double period = 1.0 / RatePerCurve;
        List<CurveBuffer> curves = [];
        System.Windows.Forms.Timer? timer = null;
        double yMin = -15, yMax = 15;
        Stopwatch clock = Stopwatch.StartNew();
        int nextYSecond = 1;

        host.Invoke(() =>
        {
            plot.Mode = ScrollingMode.Manual;

            for (int i = 0; i < Curves; i++)
            {
                double[] buffer = new double[windowPoints * 2];
                ScottPlot.Plottables.Signal signal = plot.Plot.Add.Signal(buffer, period);
                signal.LineWidth = 1;
                curves.Add(new CurveBuffer(buffer, signal));
            }

            double[] prefill = PrefillValues();
            foreach (CurveBuffer curve in curves)
                curve.Append(prefill);

            timer = new System.Windows.Forms.Timer { Interval = timerIntervalMs };
            timer.Tick += (_, _) =>
            {
                int reference = 0;

                foreach (CurveBuffer curve in curves)
                {
                    int written = curve.Written;
                    reference = Math.Max(reference, written);
                    curve.Signal.MinRenderIndex = Math.Max(0, written - windowPoints);
                    curve.Signal.MaxRenderIndex = Math.Max(0, written - 1);
                }

                double now = reference * period;
                plot.Plot.Axes.SetLimits(now - WindowSeconds, now, yMin, yMax);
                plot.RequestRender();

                int second = (int)clock.Elapsed.TotalSeconds;
                if (second >= nextYSecond)
                {
                    nextYSecond = second + 1;
                    double min = double.MaxValue, max = double.MinValue;

                    foreach (CurveBuffer curve in curves)
                    {
                        int written = curve.Written;
                        for (int i = Math.Max(0, written - windowPoints); i < written; i++)
                        {
                            double value = curve.Buffer[i];
                            if (value < min) min = value;
                            if (value > max) max = value;
                        }
                    }

                    if (min <= max)
                    {
                        double padding = Math.Max(0.5, (max - min) * 0.05);
                        yMin = min - padding;
                        yMax = max + padding;
                    }
                }
            };
            timer.Start();
        });

        Result result = Run(host, plot, name, frameControl, index => (values => curves[index].Append(values)),
            storedPoints: () => curves.Sum(c => (long)Math.Min(windowPoints, c.Written)), lost: null);

        host.Invoke(() => timer!.Dispose());
        return result;
    }

    /// <summary>
    /// 控件多曲线 API：采集线程直接调用 PlotSeries.AddValues，控件按帧合并并维护坐标轴。
    /// 时间单位下每条曲线按采样率换算成秒、跟随窗口按时长（刻度由 TimeAxisTickGenerator 生成）。
    /// </summary>
    private static Result RunControlApi(int maxFps, string name, string frameControl, XAxisUnit unit = XAxisUnit.Samples)
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl(), new Size(Width, Height), opacity: 1.0);
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        PlotSeries[] series = host.Invoke(() =>
        {
            plot.XAxisUnit = unit;
            plot.Mode = ScrollingMode.Follow;
            plot.FollowWindowPoints = RatePerCurve * WindowSeconds;
            plot.FollowWindowDuration = TimeSpan.FromSeconds(WindowSeconds);
            plot.MaxFramesPerSecond = maxFps;
            PlotSeries[] created = [.. Enumerable.Range(0, Curves).Select(i => plot.AddSeries($"曲线 {i + 1}"))];

            foreach (PlotSeries s in created)
                s.SampleRate = RatePerCurve;

            double[] prefill = PrefillValues();
            foreach (PlotSeries s in created)
                s.AddValues(prefill);

            return created;
        });

        long totalWritten = (long)Curves * PrefillPoints; // 预灌的数据也计入，最后与控件中的点数核对

        Result result = Run(host, plot, name, frameControl, index => (values =>
        {
            series[index].AddValues(values);
            Interlocked.Add(ref totalWritten, values.Length);
        }),
        storedPoints: () => series.Sum(s => (long)s.DataCount),
        lost: () =>
        {
            // 采集停止后等合并写入全部完成，再核对点数：控件写法不应丢任何点
            Stopwatch wait = Stopwatch.StartNew();
            long stored = 0;

            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                stored = host.Invoke(() => series.Sum(s => (long)s.DataCount));
                if (stored == Interlocked.Read(ref totalWritten))
                    break;

                host.Pump(20);
            }

            return Interlocked.Read(ref totalWritten) - stored;
        });

        return result;
    }

    private delegate void ValueSink(ReadOnlySpan<double> values);

    /// <summary>预灌数据：与采集线程相同形状的波形（慢变正弦 + 噪声）。</summary>
    private static double[] PrefillValues()
    {
        Random random = new(42);
        double[] values = new double[PrefillPoints];

        for (int i = 0; i < values.Length; i++)
        {
            double t = i / (double)RatePerCurve;
            values[i] = (Math.Sin(t * 2.1) * 8) + (Math.Sin(t * 11.0) * 1.5) + ((random.NextDouble() - 0.5) * 0.8);
        }

        return values;
    }

    private static Result Run(
        WinFormsTestHost host,
        ScrollingPlotControl plot,
        string name,
        string frameControl,
        Func<int, ValueSink> sinkFor,
        Func<long> storedPoints,
        Func<long>? lost)
    {
        bool stop = false;
        long written = 0;
        List<double> latencies = [];
        bool measuring = false;

        Thread[] writers =
        [
            .. Enumerable.Range(0, Curves).Select(index => new Thread(() =>
            {
                ValueSink sink = sinkFor(index);
                Stopwatch clock = Stopwatch.StartNew();
                Random random = new(index + 1);
                double[] batch = new double[512];
                double last = 0, carry = 0;
                long sample = 0;

                while (!Volatile.Read(ref stop))
                {
                    double now = clock.Elapsed.TotalSeconds;
                    double due = (RatePerCurve * (now - last)) + carry;
                    last = now;
                    int count = (int)due;
                    carry = due - count;

                    if (count > 0)
                    {
                        if (count > batch.Length)
                            batch = new double[count];

                        for (int i = 0; i < count; i++)
                        {
                            sample++;
                            double t = (sample / (double)RatePerCurve) + (index * 0.7);
                            batch[i] = ((index % 5) * 6.0) + (Math.Sin(t * 2.1) * 8) + (Math.Sin(t * 11.0) * 1.5) + ((random.NextDouble() - 0.5) * 0.8);
                        }

                        sink(batch.AsSpan(0, count));
                        Interlocked.Add(ref written, count);
                    }

                    Thread.Sleep(1);
                }
            })
            {
                IsBackground = true,
            }),
        ];

        Thread probe = new(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    host.Form.BeginInvoke(() =>
                    {
                        if (Volatile.Read(ref measuring))
                        {
                            lock (latencies)
                                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                        }
                    });
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                Thread.Sleep(5);
            }
        })
        {
            IsBackground = true,
        };

        foreach (Thread writer in writers)
            writer.Start();
        probe.Start();

        Thread.Sleep(Warmup);

        double cpuStart = CycleCpu.ProcessSeconds();
        long writtenStart = Interlocked.Read(ref written);
        long rendersStart = host.Invoke(() => plot.Plot.RenderManager.RenderCount);
        double uiCpuStart = host.Invoke(CycleCpu.CurrentThreadSeconds);
        Stopwatch window = Stopwatch.StartNew();
        Volatile.Write(ref measuring, true);

        Thread.Sleep(Measure);

        Volatile.Write(ref measuring, false);
        double seconds = window.Elapsed.TotalSeconds;
        long rendersEnd = host.Invoke(() => plot.Plot.RenderManager.RenderCount);
        double uiCpuEnd = host.Invoke(CycleCpu.CurrentThreadSeconds);
        long writtenEnd = Interlocked.Read(ref written);
        double cpuEnd = CycleCpu.ProcessSeconds();
        double heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        Volatile.Write(ref stop, true);
        foreach (Thread writer in writers)
            writer.Join(5000);
        probe.Join(2000);

        long? lostPoints = lost?.Invoke();
        long stored = host.Invoke(storedPoints);

        double[] samples;
        lock (latencies)
            samples = [.. latencies.Order()];

        host.AssertNoUnhandledExceptions();

        return new Result(
            name,
            frameControl,
            (writtenEnd - writtenStart) / seconds,
            (rendersEnd - rendersStart) / seconds,
            (cpuEnd - cpuStart) / seconds * 100,
            (uiCpuEnd - uiCpuStart) / seconds * 100,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples.Length > 0 ? samples[^1] : 0,
            heapMb,
            stored,
            lostPoints);
    }

    /// <summary>合并多轮结果：UI 线程 CPU 与延迟取较小值（排除偶发干扰），速率取均值，丢点取最大值。</summary>
    private static Result Combine(Result[] runs) => runs[0] with
    {
        PointsPerSecond = runs.Average(r => r.PointsPerSecond),
        Fps = runs.Average(r => r.Fps),
        CpuPercent = runs.Average(r => r.CpuPercent),
        UiCpuPercent = runs.Min(r => r.UiCpuPercent),
        LatencyP50 = runs.Min(r => r.LatencyP50),
        LatencyP95 = runs.Min(r => r.LatencyP95),
        LatencyMax = runs.Min(r => r.LatencyMax),
        HeapMb = runs.Average(r => r.HeapMb),
        StoredPoints = runs.Max(r => r.StoredPoints),
        Lost = runs.Any(r => r.Lost.HasValue) ? runs.Max(r => r.Lost ?? 0) : null,
    };

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];

    private sealed record Result(
        string Name,
        string FrameControl,
        double PointsPerSecond,
        double Fps,
        double CpuPercent,
        double UiCpuPercent,
        double LatencyP50,
        double LatencyP95,
        double LatencyMax,
        double HeapMb,
        long StoredPoints,
        long? Lost);

    /// <summary>旧演示的定长滑动窗口（2 倍窗口，写满后整体前移半个缓冲）。</summary>
    private sealed class CurveBuffer(double[] buffer, ScottPlot.Plottables.Signal signal)
    {
        private readonly object _sync = new();
        private int _written;

        public double[] Buffer { get; } = buffer;

        public ScottPlot.Plottables.Signal Signal { get; } = signal;

        public int Written
        {
            get
            {
                lock (_sync)
                    return _written;
            }
        }

        public void Append(ReadOnlySpan<double> values)
        {
            lock (_sync)
            {
                int written = _written;

                foreach (double value in values)
                {
                    if (written >= Buffer.Length)
                    {
                        int keep = Buffer.Length / 2;
                        Array.Copy(Buffer, Buffer.Length - keep, Buffer, 0, keep);
                        written = keep;
                    }

                    Buffer[written++] = value;
                }

                _written = written;
            }
        }
    }
}
