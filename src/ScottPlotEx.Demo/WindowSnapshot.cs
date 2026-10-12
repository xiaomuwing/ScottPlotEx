using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScottPlotEx.Demo;

/// <summary>用 PrintWindow 抓取窗口内容（不截取桌面，避免拍到其它窗口）。</summary>
internal static class WindowSnapshot
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
