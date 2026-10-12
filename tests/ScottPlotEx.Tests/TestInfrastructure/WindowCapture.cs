using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScottPlotEx.Tests.TestInfrastructure;

/// <summary>
/// 把真实窗口的内容渲染成图片（不截取桌面，因此不会拍到其它窗口）。
/// </summary>
internal static class WindowCapture
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

    /// <summary>渲染窗口并保存为 PNG。</summary>
    public static void Save(IntPtr windowHandle, string path)
    {
        Assert.True(GetWindowRect(windowHandle, out RECT rect), "无法获取窗口尺寸。");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        Assert.True(width > 0 && height > 0, "窗口尺寸无效。");

        using Bitmap bitmap = new(width, height);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            try
            {
                Assert.True(PrintWindow(windowHandle, hdc, PW_RENDERFULLCONTENT), "PrintWindow 调用失败。");
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
