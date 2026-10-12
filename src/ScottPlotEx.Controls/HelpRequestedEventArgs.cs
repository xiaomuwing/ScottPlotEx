namespace ScottPlotEx.Controls;

/// <summary>
/// <see cref="ScrollingPlotControl.ShowHelpRequested"/> 事件参数，
/// 用户可通过设置 <see cref="Handled"/> 阻止弹出内置的帮助对话框（例如换成自己的帮助窗口）。
/// </summary>
public sealed class HelpRequestedEventArgs : EventArgs
{
    internal HelpRequestedEventArgs(string content)
    {
        Content = content;
    }

    /// <summary>帮助正文。</summary>
    public string Content { get; }

    /// <summary>是否已处理。设置为 true 时不再弹出内置帮助对话框。</summary>
    public bool Handled { get; set; }
}
