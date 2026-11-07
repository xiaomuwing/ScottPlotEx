using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScottPlotEx.Controls;

/// <summary>
/// 高精度单次定时：到达指定的 <see cref="Stopwatch"/> 时间戳时，在后台线程上调用回调。
/// <para>
/// WinForms 定时器与 Thread.Sleep 的精度受系统时钟中断限制（默认约 15.6 ms），33 ms 的帧间隔会被拉长到约 47 ms。
/// 这里使用高精度可等待定时器（CREATE_WAITABLE_TIMER_HIGH_RESOLUTION，Windows 10 1803 起支持，误差约 1 ms 以内），
/// 不支持时退回普通可等待定时器。
/// </para>
/// 同一时刻只保留一个截止时间（再次调度会覆盖上一次）；回调在后台线程执行，必须是线程安全的。
/// </summary>
internal sealed class HighResolutionTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    private readonly Action _callback;
    private readonly AutoResetEvent _wake = new(false);
    private readonly TimerWaitHandle _timer;
    private readonly Thread _thread;

    /// <summary>截止时间（Stopwatch 时间戳），0 表示没有待触发的定时。</summary>
    private long _due;
    private volatile bool _disposed;

    /// <summary>创建定时器并启动后台等待线程。</summary>
    /// <param name="callback">到点时在后台线程上调用的回调。</param>
    public HighResolutionTimer(Action callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        IntPtr handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        IsHighResolution = handle != IntPtr.Zero;

        if (handle == IntPtr.Zero)
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);

        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建可等待定时器。");

        _timer = new TimerWaitHandle(handle);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ScottPlotEx 帧率限制",
        };
        _thread.Start();
    }

    /// <summary>是否使用了高精度可等待定时器（旧系统上为 false，精度退回系统时钟中断间隔）。</summary>
    public bool IsHighResolution { get; }

    /// <summary>在指定时刻触发一次回调（覆盖之前尚未触发的定时）。可以在任意线程调用，与 <see cref="Dispose"/> 并发也安全。</summary>
    /// <param name="dueTimestamp">截止时间（<see cref="Stopwatch.GetTimestamp"/>）；已经过去时会尽快触发。</param>
    /// <returns>定时器已释放（调度没有生效）时返回 false，调用方需要自行处理这一次触发。</returns>
    public bool Schedule(long dueTimestamp)
    {
        if (_disposed)
            return false;

        Volatile.Write(ref _due, Math.Max(dueTimestamp, 1));

        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 并发：事件已经释放，这次调度不会触发
            return false;
        }

        return !_disposed;
    }

    private void Run()
    {
        WaitHandle[] handles = [_wake, _timer];

        try
        {
            while (!_disposed)
            {
                long due = Volatile.Read(ref _due);

                if (due == 0)
                {
                    _wake.WaitOne();
                    continue;
                }

                long remaining = due - Stopwatch.GetTimestamp();

                if (remaining <= 0)
                {
                    // 只有抢到这一次截止时间的线程才触发：期间被重新调度则按新的截止时间等待
                    if (Interlocked.CompareExchange(ref _due, 0, due) == due)
                        InvokeCallback();

                    continue;
                }

                // 负值表示相对时间，单位 100 ns
                long relative = -Math.Max(1, (long)(remaining * 10_000_000.0 / Stopwatch.Frequency));

                if (!SetWaitableTimer(_timer.SafeWaitHandle, ref relative, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    // 设置失败（极少见）：退回按毫秒等待，保证不会忙等
                    _wake.WaitOne(Math.Max(1, (int)(remaining * 1000 / Stopwatch.Frequency)));
                    continue;
                }

                WaitHandle.WaitAny(handles);
            }
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // 释放时等待线程退出超时、句柄已被回收：线程直接结束，不能让未处理异常终止进程
        }
    }

    private void InvokeCallback()
    {
        try
        {
            _callback();
        }
        catch (Exception)
        {
            // 回调异常不能终止定时线程（回调内部负责自身的错误处理）
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _wake.Set();

        // 只有确认等待线程已经退出才释放句柄：线程还在 WaitAny / SetWaitableTimer 中时释放会让它在已释放的句柄上抛异常。
        // 超时（回调长时间不返回）时把句柄留给终结器回收，线程醒来后看到 _disposed 自行退出。
        if (!_thread.Join(1000))
            return;

        CancelWaitableTimer(_timer.SafeWaitHandle);
        _timer.Dispose();
        _wake.Dispose();
    }

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
