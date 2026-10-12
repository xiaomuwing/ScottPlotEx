using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScottPlotEx.Tests.TestInfrastructure;

/// <summary>
/// 周期精确的 CPU 计量（QueryThreadCycleTime / QueryProcessCycleTime）。
/// <para>
/// GetThreadTimes / Process.TotalProcessorTime 是按约 15.6 ms 的时钟中断采样记账的：
/// 每帧只忙 1~3 ms 的 UI 线程会因为与时钟中断的相位关系被严重多算或少算（实测同一场景 2% ~ 12% 来回跳）。
/// 周期数按处理器时间戳计数，换算成秒时用忙等校准得到的每秒周期数。
/// </para>
/// </summary>
internal static class CycleCpu
{
    private static readonly Lazy<double> CyclesPerSecond = new(Calibrate);

    /// <summary>调用线程自身消耗的 CPU 时间（秒）。</summary>
    public static double CurrentThreadSeconds()
    {
        QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
        return cycles / CyclesPerSecond.Value;
    }

    /// <summary>整个进程消耗的 CPU 时间（秒）。</summary>
    public static double ProcessSeconds()
    {
        QueryProcessCycleTime(GetCurrentProcess(), out ulong cycles);
        return cycles / CyclesPerSecond.Value;
    }

    /// <summary>在当前线程上忙等 200 ms，测出每秒的计量周期数（取 3 次中的最大值，排除被抢占的情况）。</summary>
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
