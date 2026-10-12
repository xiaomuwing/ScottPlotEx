using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace ScottPlotEx.SoakTest;

/// <summary>
/// 向控件注入鼠标 / 键盘输入。既有与单元测试一致的“托管事件注入”（直接调用 OnMouseXxx），
/// 也有通过 PostMessage 投递的真实 Windows 鼠标消息（走窗口过程）。
/// </summary>
internal static class InputSimulator
{
    public static void Down(Control control, Point point, MouseButtons button) =>
        Raise(control, "OnMouseDown", new MouseEventArgs(button, 1, point.X, point.Y, 0));

    public static void Move(Control control, Point point, MouseButtons button = MouseButtons.None) =>
        Raise(control, "OnMouseMove", new MouseEventArgs(button, 0, point.X, point.Y, 0));

    public static void Up(Control control, Point point, MouseButtons button) =>
        Raise(control, "OnMouseUp", new MouseEventArgs(button, 1, point.X, point.Y, 0));

    public static void Wheel(Control control, Point point, int delta) =>
        Raise(control, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, delta));

    public static void KeyDown(Control control, Keys key) =>
        Raise(control, "OnKeyDown", new KeyEventArgs(key));

    public static void KeyUp(Control control, Keys key) =>
        Raise(control, "OnKeyUp", new KeyEventArgs(key));

    public static void Click(Control control, Point point, MouseButtons button)
    {
        Down(control, point, button);
        Up(control, point, button);
    }

    public static void Drag(Control control, Point from, Point to, MouseButtons button, int steps = 6)
    {
        Down(control, from, button);

        for (int i = 1; i <= steps; i++)
        {
            int x = from.X + ((to.X - from.X) * i / steps);
            int y = from.Y + ((to.Y - from.Y) * i / steps);
            Move(control, new Point(x, y), button);
        }

        Up(control, to, button);
    }

    /// <summary>通过真实 Windows 鼠标消息完成一次拖动（走 WndProc）。</summary>
    public static void PostDrag(IntPtr canvasHandle, Point from, Point to, MouseButtons button, int steps = 6)
    {
        (uint down, uint up, int modifier) = button switch
        {
            MouseButtons.Right => (NativeMethods.WM_RBUTTONDOWN, NativeMethods.WM_RBUTTONUP, NativeMethods.MK_RBUTTON),
            MouseButtons.Middle => (NativeMethods.WM_MBUTTONDOWN, NativeMethods.WM_MBUTTONUP, NativeMethods.MK_MBUTTON),
            _ => (NativeMethods.WM_LBUTTONDOWN, NativeMethods.WM_LBUTTONUP, NativeMethods.MK_LBUTTON),
        };

        _ = NativeMethods.PostMessage(
            canvasHandle, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, NativeMethods.MakeLParam(from.X, from.Y));
        _ = NativeMethods.PostMessage(canvasHandle, down, (IntPtr)modifier, NativeMethods.MakeLParam(from.X, from.Y));

        for (int i = 1; i <= steps; i++)
        {
            int x = from.X + ((to.X - from.X) * i / steps);
            int y = from.Y + ((to.Y - from.Y) * i / steps);
            _ = NativeMethods.PostMessage(canvasHandle, NativeMethods.WM_MOUSEMOVE, (IntPtr)modifier, NativeMethods.MakeLParam(x, y));
        }

        _ = NativeMethods.PostMessage(canvasHandle, up, IntPtr.Zero, NativeMethods.MakeLParam(to.X, to.Y));
    }

    /// <summary>通过真实 Windows 鼠标消息转动滚轮。</summary>
    public static void PostWheel(IntPtr canvasHandle, Point point, int delta)
    {
        IntPtr wParam = (IntPtr)((delta << 16) | NativeMethods.MK_LBUTTON);
        _ = NativeMethods.PostMessage(
            canvasHandle, NativeMethods.WM_MOUSEWHEEL, wParam, NativeMethods.MakeLParam(point.X, point.Y));
    }

    private static void Raise(Control control, string methodName, EventArgs args)
    {
        MethodInfo? method = FindMethod(control.GetType(), methodName)
            ?? throw new MissingMethodException(control.GetType().FullName, methodName);

        try
        {
            method.Invoke(control, [args]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static MethodInfo? FindMethod(Type? type, string methodName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            if (method is not null)
                return method;
        }

        return null;
    }
}
