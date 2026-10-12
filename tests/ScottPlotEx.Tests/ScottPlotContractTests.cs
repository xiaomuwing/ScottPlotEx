using System.Reflection;
using System.Windows.Forms;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 升级护栏：把本控件所依赖的 ScottPlot 行为/接口固化成断言。
/// <para>
/// 升级 ScottPlot 后如果这些测试失败，说明需要按提示同步调整本控件，
/// 而不是让功能静默失效。依赖点包括：<see cref="ScottPlot.IPlotControl"/> 接口成员、
/// 内置右键菜单项（本控件按其重写了中文菜单）、默认鼠标交互绑定、
/// 自绘画布依赖的 <see cref="ScottPlot.WinForms.FormsPlotExtensions"/> 输入转发方法，以及画布的绘制结构。
/// </para>
/// </summary>
public class ScottPlotContractTests
{
    /// <summary>本控件实现 <see cref="ScottPlot.IPlotControl"/> 时所依赖的接口成员快照。</summary>
    private static readonly string[] ExpectedPlotControlMembers =
    [
        "DetectDisplayScale() (Method)",
        "DisplayScale (Property, get/set)",
        "GRContext (Property, get)",
        "Menu (Property, get/set)",
        "Multiplot (Property, get/set)",
        "Plot (Property, get)",
        "Refresh() (Method)",
        "Reset() (Method)",
        "Reset(Plot) (Method)",
        "SetCursor(Cursor) (Method)",
        "ShowContextMenu(Pixel) (Method)",
        "UserInputProcessor (Property, get)",
    ];

    /// <summary>ScottPlot 自带的右键菜单项（本控件据此提供中文翻译与勾选项）。</summary>
    private static readonly string[] ExpectedBuiltInMenuLabels =
    [
        "Autoscale",
        "Copy to Clipboard",
        "Open in New Window",
        "Save Image",
    ];

    /// <summary>ScottPlot 默认鼠标/键盘交互响应（本控件的手势判定依赖这套绑定）。</summary>
    private static readonly string[] ExpectedDefaultUserResponses =
    [
        "DoubleClickBenchmark",
        "KeyboardAutoscale",
        "KeyboardPanAndZoom",
        "MouseDragPan",
        "MouseDragZoom",
        "MouseDragZoomRectangle",
        "MouseInteractWithPlottables",
        "MouseWheelZoom",
        "SingleClickAutoscale",
        "SingleClickContextMenu",
    ];

    [Fact(DisplayName = "升级护栏：IPlotControl 接口成员未发生变化（否则需要同步实现）")]
    public void PlotControlInterface_HasExpectedMembers()
    {
        string[] actual =
        [
            .. typeof(ScottPlot.IPlotControl)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(member => !(member is MethodInfo method && method.IsSpecialName))
                .Select(Describe)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(ExpectedPlotControlMembers.Order(StringComparer.Ordinal), actual);

        // 控件必须实现全部成员（编译期已保证，这里再兜一层，防止接口默认实现导致漏转发）
        foreach (MemberInfo interfaceMember in typeof(ScottPlot.IPlotControl).GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            if (interfaceMember is MethodInfo interfaceMethod && interfaceMethod.IsSpecialName)
                continue;

            MemberInfo? implemented = interfaceMember switch
            {
                PropertyInfo property => typeof(ScrollingPlotControl).GetProperty(property.Name),
                MethodInfo method => typeof(ScrollingPlotControl).GetMethod(
                    method.Name,
                    [.. method.GetParameters().Select(parameter => parameter.ParameterType)]),
                _ => null,
            };

            Assert.True(implemented is not null, $"ScrollingPlotControl 未实现 IPlotControl 成员：{Describe(interfaceMember)}");
        }
    }

    [Fact(DisplayName = "升级护栏：ScottPlot 内置右键菜单项未发生变化（否则中文翻译会缺失）")]
    public void BuiltInMenuLabels_AreUnchanged()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            // 控件不再内嵌 FormsPlot：单独创建一个仅用于读取 ScottPlot 自带菜单项
            using ScottPlot.WinForms.FormsPlot reference = new();
            string[] actual =
            [
                .. new ScottPlot.WinForms.FormsPlotMenu(reference)
                    .StandardContextMenuItems()
                    .Select(item => item.Label)
                    .Order(StringComparer.Ordinal),
            ];

            Assert.Equal(ExpectedBuiltInMenuLabels.Order(StringComparer.Ordinal), actual);

            // 所有内置项都必须有中文翻译（新增/改名的菜单项会在这里暴露）
            Assert.All(
                plot.PlotMenu.StandardContextMenuItems(),
                item => Assert.False(item.Label.Any(char.IsAsciiLetter), $"菜单项“{item.Label}”缺少中文翻译，请在 ChinesePlotMenu.ChineseLabels 中补充。"));
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "升级护栏：ScottPlot 默认鼠标交互绑定未发生变化（否则手势判定会失配）")]
    public void DefaultUserResponses_AreUnchanged()
    {
        List<ScottPlot.Interactivity.IUserActionResponse> responses = ScottPlot.Interactivity.UserInputProcessor.DefaultUserResponses();

        string[] actual = [.. responses.Select(response => response.GetType().Name).Order(StringComparer.Ordinal)];
        Assert.Equal(ExpectedDefaultUserResponses.Order(StringComparer.Ordinal), actual);

        // 本控件依赖的按钮绑定：左键拖动平移、中键框选放大、中键单击自动缩放、右键单击菜单
        Assert.Equal(
            ScottPlot.Interactivity.StandardMouseButtons.Left,
            GetProperty(responses, "MouseDragPan", "MouseButton"));

        Assert.Equal(
            ScottPlot.Interactivity.StandardMouseButtons.Middle,
            GetProperty(responses, "SingleClickAutoscale", "MouseButton"));

        Assert.Equal(
            ScottPlot.Interactivity.StandardMouseButtons.Right,
            GetProperty(responses, "SingleClickContextMenu", "MouseButton"));

        Assert.Equal(
            ScottPlot.Interactivity.StandardMouseButtons.Left,
            GetProperty(responses, "MouseDragZoomRectangle", "SecondaryMouseButton"));
    }

    [Fact(DisplayName = "升级护栏：自绘画布结构可用（直接子控件、实现 IPlotControl、经 Multiplot 渲染）")]
    public void Canvas_Structure_IsAvailable()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            PlotTestHelper.AddWave(plot, 50);
            ScottPlot.PixelRect rect = PlotTestHelper.EnsureRendered(plot);

            Control canvas = plot.Canvas;
            Assert.Same(plot, canvas.Parent);
            Assert.Equal(DockStyle.Fill, canvas.Dock);
            Assert.IsAssignableFrom<ScottPlot.IPlotControl>(canvas);

            // 画布的绘制必须经过 ScottPlot 的渲染管线（LastRender 由 Multiplot.Render 写入）
            Assert.True(rect.Width > 0 && rect.Height > 0);
            Assert.True(plot.Plot.RenderManager.RenderCount > 0);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "升级护栏：自绘画布依赖的 FormsPlotExtensions 输入转发方法仍然存在")]
    public void FormsPlotExtensions_InputForwarders_AreAvailable()
    {
        // 画布把 WinForms 输入转发给 ScottPlot 的方式与 FormsPlotBase 相同；这些公开方法改名或删除时需要同步调整画布
        string[] expected =
        [
            "ProcessKeyDown(UserInputProcessor, KeyEventArgs)",
            "ProcessKeyDown(UserInputProcessor, PreviewKeyDownEventArgs)",
            "ProcessKeyUp(UserInputProcessor, KeyEventArgs)",
            "ProcessLostFocus(UserInputProcessor)",
            "ProcessMouseDown(UserInputProcessor, MouseEventArgs)",
            "ProcessMouseMove(UserInputProcessor, MouseEventArgs)",
            "ProcessMouseUp(UserInputProcessor, MouseEventArgs)",
            "ProcessMouseWheel(UserInputProcessor, MouseEventArgs)",
        ];

        string[] actual =
        [
            .. typeof(ScottPlot.WinForms.FormsPlotExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name.StartsWith("Process", StringComparison.Ordinal))
                .Select(method => $"{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})")
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static object? GetProperty(IEnumerable<object> instances, string typeName, string propertyName)
    {
        object instance = instances.Single(x => x.GetType().Name == typeName);
        PropertyInfo? property = instance.GetType().GetProperty(propertyName);

        Assert.True(property is not null, $"{typeName}.{propertyName} 不存在，ScottPlot 默认交互可能已变更。");
        return property!.GetValue(instance);
    }

    private static string Describe(MemberInfo member) => member switch
    {
        PropertyInfo property =>
            $"{property.Name} (Property, {(property.CanRead ? "get" : string.Empty)}{(property.CanWrite ? "/set" : string.Empty)})",
        MethodInfo method =>
            $"{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))}) (Method)",
        EventInfo @event => $"{@event.Name} (Event)",
        _ => member.Name,
    };
}
