using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace ScottPlotEx.Tests.TestInfrastructure;

/// <summary>
/// 向控件注入鼠标 / 键盘消息。注入点与真实鼠标一致（ScottPlot 的画布控件），
/// 因此会完整经过 ScottPlot 自带的输入处理管线。
/// </summary>
internal static class MouseInput
{
    public static void Down(Control control, Point point, MouseButtons button) =>
        Raise(control, "OnMouseDown", new MouseEventArgs(button, 1, point.X, point.Y, 0));

    public static void Move(Control control, Point point, MouseButtons button = MouseButtons.None) =>
        Raise(control, "OnMouseMove", new MouseEventArgs(button, 0, point.X, point.Y, 0));

    public static void Up(Control control, Point point, MouseButtons button) =>
        Raise(control, "OnMouseUp", new MouseEventArgs(button, 1, point.X, point.Y, 0));

    public static void Wheel(Control control, Point point, int delta) =>
        Raise(control, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, delta));

    public static void DoubleClick(Control control) =>
        Raise(control, "OnDoubleClick", EventArgs.Empty);

    public static void KeyDown(Control control, Keys key) =>
        Raise(control, "OnKeyDown", new KeyEventArgs(key));

    public static void KeyUp(Control control, Keys key) =>
        Raise(control, "OnKeyUp", new KeyEventArgs(key));

    /// <summary>单击（按下后立即抬起）。</summary>
    public static void Click(Control control, Point point, MouseButtons button)
    {
        Down(control, point, button);
        Up(control, point, button);
    }

    /// <summary>按住某个键拖动鼠标。</summary>
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

    private static void Raise(Control control, string methodName, EventArgs args)
    {
        MethodInfo? method = FindMethod(control.GetType(), methodName);

        if (method is null)
            throw new MissingMethodException(control.GetType().FullName, methodName);

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
