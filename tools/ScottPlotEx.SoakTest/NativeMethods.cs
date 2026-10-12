using System.Runtime.InteropServices;

namespace ScottPlotEx.SoakTest;

/// <summary>压测工具需要的少量 Win32 接口。</summary>
internal static class NativeMethods
{
    internal const int GR_GDIOBJECTS = 0;
    internal const int GR_USEROBJECTS = 1;

    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MBUTTONDOWN = 0x0207;
    internal const uint WM_MBUTTONUP = 0x0208;
    internal const uint WM_MOUSEWHEEL = 0x020A;

    internal const int MK_LBUTTON = 0x0001;
    internal const int MK_RBUTTON = 0x0002;
    internal const int MK_MBUTTON = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>读取进程的 GDI / User 对象数量（句柄泄漏的经典指标）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

    /// <summary>把真实 Windows 鼠标消息投递到窗口过程。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    /// <summary>取当前控制台窗口句柄（没有控制台时返回 IntPtr.Zero）。</summary>
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    /// <summary>显示 / 隐藏窗口（SW_HIDE = 0）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const int SW_HIDE = 0;

    internal static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
}
