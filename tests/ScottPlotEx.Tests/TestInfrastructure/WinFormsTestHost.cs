using System.Drawing;
using System.Windows.Forms;

namespace ScottPlotEx.Tests.TestInfrastructure;

/// <summary>
/// 在独立的 STA 线程上创建真实窗体（屏幕外）与待测控件，并运行消息循环，
/// 使鼠标事件、SkiaSharp 绘制和右键菜单等行为都走真实路径。
/// </summary>
internal sealed class WinFormsTestHost : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly Form _form;
    private readonly Thread _thread;
    private readonly List<Exception> _unhandled;
    private readonly object _unhandledGate;
    private bool _disposed;

    private WinFormsTestHost(Form form, Thread thread, Control control, List<Exception> unhandled, object unhandledGate)
    {
        _form = form;
        _thread = thread;
        _unhandled = unhandled;
        _unhandledGate = unhandledGate;
        Control = control;
    }

    /// <summary>宿主窗体中的待测控件。</summary>
    public Control Control { get; }

    /// <summary>宿主窗体（真实顶层窗口，可用于窗口级截图）。</summary>
    public Form Form => _form;

    /// <summary>UI 线程上记录的未处理异常。</summary>
    public IReadOnlyList<Exception> UnhandledExceptions
    {
        get
        {
            lock (_unhandledGate)
            {
                return [.. _unhandled];
            }
        }
    }

    /// <summary>以待测控件的实际类型访问控件。</summary>
    public T ControlAs<T>() where T : Control => (T)Control;

    /// <summary>启动测试宿主。</summary>
    /// <param name="factory">创建待测控件。</param>
    /// <param name="clientSize">窗体客户区尺寸。</param>
    /// <param name="opacity">
    /// 窗体不透明度。默认 0.01（接近全透明，避免干扰用户）；这会让窗体成为分层窗口，绘制要经过额外的合成，
    /// 大尺寸下明显变慢，因此性能基准应传 1.0，使测得的帧耗时与真实窗口一致。
    /// </param>
    public static WinFormsTestHost Start<TControl>(Func<TControl> factory, Size? clientSize = null, double opacity = 0.01)
        where TControl : Control
    {
        ArgumentNullException.ThrowIfNull(factory);

        Form? form = null;
        Control? control = null;
        List<Exception> unhandled = [];
        object unhandledGate = new();
        using ManualResetEventSlim ready = new(false);

        Thread thread = new(() =>
        {
            void Record(Exception exception)
            {
                lock (unhandledGate)
                {
                    unhandled.Add(exception);
                }
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Record(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception exception)
                    Record(exception);
            };

            try
            {
                // 窗口需要位于屏幕内才会收到真实的 WM_PAINT；用接近全透明的方式避免干扰用户
                form = new TransparentTestForm
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(0, 0),
                    Opacity = opacity,
                    ClientSize = clientSize ?? new Size(1000, 600),
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    TopMost = true,
                    Text = "ScottPlotEx 测试宿主",
                };

                control = factory();
                control.Dock = DockStyle.Fill;

                // 允许把 Form 作为子控件嵌入测试宿主窗口
                if (control is Form childForm)
                    childForm.TopLevel = false;

                form.Controls.Add(control);
                control.Visible = true; // 嵌入的 Form 需要显式置为可见
                form.Shown += (_, _) => ready.Set();

                using (form)
                {
                    Application.Run(form);
                }
            }
            catch (Exception exception)
            {
                Record(exception);
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "ScottPlotEx 测试 UI 线程",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        bool started = ready.Wait(StartTimeout);

        if (!started || form is null || control is null)
        {
            thread.Join(StopTimeout);
            throw new InvalidOperationException("无法启动 WinForms 测试宿主（窗体或控件创建失败）。");
        }

        IReadOnlyList<Exception> startupExceptions;
        lock (unhandledGate)
        {
            startupExceptions = [.. unhandled];
        }

        if (startupExceptions.Count > 0)
        {
            thread.Join(StopTimeout);
            throw new InvalidOperationException(
                "测试宿主启动过程中出现异常：" + Environment.NewLine + string.Join(Environment.NewLine, startupExceptions));
        }

        return new WinFormsTestHost(form, thread, control, unhandled, unhandledGate);
    }

    /// <summary>在 UI 线程上同步执行。</summary>
    public void Invoke(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _form.Invoke(action);
    }

    /// <summary>在 UI 线程上同步执行并返回结果。</summary>
    public T Invoke<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (T)_form.Invoke(func);
    }

    /// <summary>让 UI 线程处理消息队列，用于等待绘制、菜单等异步行为完成。</summary>
    public void Pump(int milliseconds = 150)
    {
        Invoke(() =>
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            do
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            while (DateTime.UtcNow < deadline);
        });
    }

    /// <summary>断言 UI 线程上没有出现过未处理异常。</summary>
    public void AssertNoUnhandledExceptions()
    {
        IReadOnlyList<Exception> exceptions = UnhandledExceptions;

        Assert.True(
            exceptions.Count == 0,
            "UI 线程出现未处理异常：" + Environment.NewLine + string.Join(Environment.NewLine, exceptions.Select(x => x.ToString())));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _form.Invoke(() => _form.Close());
        }
        catch (Exception)
        {
            // 窗体可能已经关闭或句柄已销毁
        }

        _thread.Join(StopTimeout);
    }

    /// <summary>接近全透明的测试窗体：仍会收到真实 WM_PAINT，但不会抢占焦点。</summary>
    private sealed class TransparentTestForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
