using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;

namespace ScottPlotEx.Controls;

/// <summary>
/// WinForms 无闪烁渲染辅助工具。
/// </summary>
public static class FlickerFreeRendering
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_COMPOSITED = 0x02000000;

    /// <summary>
    /// 为窗体启用 <c>WS_EX_COMPOSITED</c>：由 DWM 统一合成窗体与全部子控件，
    /// 从而消除子控件（例如大幅面曲线控件）快速重绘时的闪烁。
    /// 适用于控件尺寸较大、刷新较快，或同一窗体上有多个自绘子控件的场景。
    /// </summary>
    /// <param name="form">目标窗体（会自动创建窗口句柄）。</param>
    public static void EnableComposited(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        IntPtr handle = form.Handle; // 确保句柄已创建
        long style = GetExtendedStyle(handle);
        SetExtendedStyle(handle, style | WS_EX_COMPOSITED);
    }

    /// <summary>关闭窗体的 <c>WS_EX_COMPOSITED</c>。</summary>
    public static void DisableComposited(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        IntPtr handle = form.Handle;
        long style = GetExtendedStyle(handle);
        SetExtendedStyle(handle, style & ~(long)WS_EX_COMPOSITED);
    }

    /// <summary>查询窗体是否已启用 <c>WS_EX_COMPOSITED</c>。</summary>
    public static bool IsComposited(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return (GetExtendedStyle(form.Handle) & WS_EX_COMPOSITED) != 0;
    }

    /// <summary>
    /// 调整 WinForms 双缓冲上限（<see cref="BufferedGraphicsManager.Current"/> 的 <c>MaximumBuffer</c>）。
    /// 该上限默认约等于主显示器尺寸；控件尺寸超过上限时，<c>DoubleBuffered</c> 会静默退化为直接绘制，
    /// 于是背景擦除会直接显示在屏幕上 —— 这正是“控件变大以后才闪”的原因。
    /// 把上限调到不小于最大控件尺寸即可恢复真正的双缓冲（代价：每个活动缓冲区多占 4 字节 × 宽 × 高）。
    /// </summary>
    /// <param name="size">新的上限尺寸，例如 <c>new Size(4096, 2160)</c>。</param>
    public static void SetMaximumBufferSize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "双缓冲上限必须为正数。");

        BufferedGraphicsManager.Current.MaximumBuffer = size;
    }

    /// <summary>读取当前 WinForms 双缓冲上限。</summary>
    public static Size GetMaximumBufferSize() => BufferedGraphicsManager.Current.MaximumBuffer;

    /// <summary>按控件当前尺寸把双缓冲上限调大（只调大、不调小）。</summary>
    /// <param name="control">需要双缓冲的控件（通常会取窗体上尺寸最大的自绘控件）。</param>
    public static void EnsureMaximumBufferFor(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);

        Size current = GetMaximumBufferSize();
        Size required = control.Size;

        if (required.Width > current.Width || required.Height > current.Height)
            SetMaximumBufferSize(new Size(Math.Max(required.Width, current.Width), Math.Max(required.Height, current.Height)));
    }

    private static long GetExtendedStyle(IntPtr handle) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(handle, GWL_EXSTYLE);

    private static void SetExtendedStyle(IntPtr handle, long style)
    {
        if (IntPtr.Size == 8)
            _ = SetWindowLongPtr64(handle, GWL_EXSTYLE, new IntPtr(style));
        else
            _ = SetWindowLong32(handle, GWL_EXSTYLE, unchecked((int)style));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
