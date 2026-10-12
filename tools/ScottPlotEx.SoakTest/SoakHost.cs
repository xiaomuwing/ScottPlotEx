using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace ScottPlotEx.SoakTest;

/// <summary>
/// 在独立的 STA 线程上创建真实窗体 + 待测控件并运行消息循环。
/// 默认使用“近透明 + 鼠标穿透 + 不抢焦点”的窗口：仍会走真实 WM_PAINT / SkiaSharp 绘制路径，
/// 但不会遮挡或拦截用户的鼠标操作，适合无人值守的长时压测。
/// </summary>
internal sealed class SoakHost : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private readonly SoakForm _form;
    private readonly Thread _thread;
    private bool _disposed;

    private SoakHost(SoakForm form, Thread thread, SoakLogger logger)
    {
        _form = form;
        _thread = thread;
        Logger = logger;
    }

    public SoakLogger Logger { get; }

    public SoakForm Form => _form;

    public bool IsAlive => _thread.IsAlive;

    public static SoakHost Start(SoakOptions options, SoakLogger logger, Size clientSize)
    {
        SoakForm? form = null;
        using ManualResetEventSlim ready = new(false);

        Thread thread = new(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                form = new SoakForm(options);
                form.ClientSize = clientSize;
                form.Shown += (_, _) => ready.Set();
                Application.Run(form);
            }
            catch (Exception exception)
            {
                logger.Event("FATAL", "UI 线程启动失败：" + exception);
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "ScottPlotEx 压测 UI 线程",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(StartTimeout) || form is null)
        {
            thread.Join(StopTimeout);
            throw new InvalidOperationException("无法启动压测宿主窗口。");
        }

        return new SoakHost(form, thread, logger);
    }

    /// <summary>在 UI 线程上同步执行。</summary>
    public void Invoke(Action action) => _form.Invoke(action);

    /// <summary>在 UI 线程上同步执行并返回结果。</summary>
    public T Invoke<T>(Func<T> func) => (T)_form.Invoke(func);

    /// <summary>让消息循环处理一小段时间（用于等待绘制 / 菜单完成）。</summary>
    public void Pump(int milliseconds)
    {
        Invoke(() =>
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            do
            {
                Application.DoEvents();
                Thread.Sleep(2);
            }
            while (DateTime.UtcNow < deadline);
        });
    }

    public void Close()
    {
        try
        {
            _form.Invoke(() => _form.Close());
        }
        catch (Exception)
        {
            // 窗口可能已经关闭
        }

        _thread.Join(StopTimeout);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
    }

    /// <summary>压测宿主窗体：默认近透明、鼠标穿透、非激活。</summary>
    internal sealed class SoakForm : Form
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly bool _ghost;

        public SoakForm(SoakOptions options)
        {
            _ghost = options.GhostWindow;

            Text = "ScottPlotEx 时间压力测试";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MinimumSize = new Size(320, 240);

            if (_ghost)
            {
                // 透明到几乎看不见，但仍会被 DWM 合成、并真实收到 WM_PAINT
                Opacity = 0.01;
                TopMost = true;
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
                ShowInTaskbar = true;
            }
        }

        public ScrollingPlotControl? CurrentPlot =>
            Controls.OfType<ScrollingPlotControl>().FirstOrDefault();

        protected override bool ShowWithoutActivation => _ghost;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;

                if (_ghost)
                {
                    // 鼠标穿透 + 不抢焦点：长时压测期间完全不干扰用户
                    parameters.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                }

                return parameters;
            }
        }
    }
}
