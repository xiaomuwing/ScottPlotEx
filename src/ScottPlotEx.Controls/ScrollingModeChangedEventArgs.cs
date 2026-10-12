namespace ScottPlotEx.Controls;

/// <summary>
/// <see cref="ScrollingPlotControl.ModeChanged"/> 事件参数。
/// </summary>
public sealed class ScrollingModeChangedEventArgs : EventArgs
{
    internal ScrollingModeChangedEventArgs(ScrollingMode previousMode, ScrollingMode mode, ScrollingModeChangeReason reason)
    {
        PreviousMode = previousMode;
        Mode = mode;
        Reason = reason;
    }

    /// <summary>变化前的模式。</summary>
    public ScrollingMode PreviousMode { get; }

    /// <summary>变化后的模式。</summary>
    public ScrollingMode Mode { get; }

    /// <summary>变化原因。</summary>
    public ScrollingModeChangeReason Reason { get; }

    /// <inheritdoc />
    public override string ToString() => $"{PreviousMode} -> {Mode} ({Reason})";
}
