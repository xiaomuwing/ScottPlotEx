using System.ComponentModel;
using System.Diagnostics;

namespace ScottPlotEx.Controls;

/// <summary>
/// 设计器 / 运行时环境探测与降级支持。
/// <para>
/// ScottPlot 的 <c>Plot</c> 构造函数会触发 SkiaSharp 的静态初始化（默认字体），而 Visual Studio 的设计器进程
/// 不一定能解析到 <c>libSkiaSharp.dll</c>；此时在 Form 设计器中拖放本控件会抛出
/// <see cref="DllNotFoundException"/> / <see cref="TypeInitializationException"/>，导致“只能手写代码添加控件”。
/// 这里在运行时资源不可用时降级为一个设计时占位控件；运行时（非设计器）若确实缺少原生库也会显示同样的提示，
/// 而不是让窗体构造直接崩溃。
/// </para>
/// </summary>
internal static class DesignerSupport
{
    /// <summary>测试用：强制按设计时处理（即使当前进程是测试宿主）。</summary>
    internal static bool ForceDesignTime { get; set; }

    /// <summary>测试用：不做任何 ScottPlot / SkiaSharp 初始化，直接进入占位模式。</summary>
    internal static bool ForceDesignerFallback { get; set; }

    /// <summary>当前进程是否运行在 Visual Studio 设计器（含 .NET 的 out-of-proc DesignToolsServer）中。</summary>
    internal static bool IsDesignTime
    {
        get
        {
            if (ForceDesignTime)
                return true;

            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
                return true;

            string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
            string friendlyName = AppDomain.CurrentDomain.FriendlyName;

            return IsDesignerProcess(processName) || IsDesignerProcess(friendlyName);
        }
    }

    /// <summary>
    /// 是否是 SkiaSharp 原生库加载失败（包括被包在 <see cref="TypeInitializationException"/> 里的情况）。
    /// 这类失败在设计器里很常见，运行时降级比直接抛异常更可控。
    /// </summary>
    internal static bool IsNativeLibraryFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
                return true;
        }

        return false;
    }

    private static bool IsDesignerProcess(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name.Contains("devenv", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DesignTools", StringComparison.OrdinalIgnoreCase)
            || name.Contains("XDesProc", StringComparison.OrdinalIgnoreCase);
    }
}
