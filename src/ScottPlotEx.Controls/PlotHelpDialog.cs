using System.Drawing;
using System.Windows.Forms;

namespace ScottPlotEx.Controls;

/// <summary>
/// 中文帮助对话框。
/// </summary>
internal sealed class PlotHelpDialog : Form
{
    public PlotHelpDialog(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        Text = PlotHelpContent.Title;
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(660, 560);
        MinimumSize = new Size(420, 320);
        Padding = new Padding(8);

        Button closeButton = new()
        {
            Text = "关闭(&C)",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
        };

        FlowLayoutPanel bottomPanel = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        bottomPanel.Controls.Add(closeButton);

        TextBox textBox = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Window,
            Text = content,
            TabStop = false,
        };

        Controls.Add(textBox);
        Controls.Add(bottomPanel);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        _textBox = textBox;
    }

    private readonly TextBox _textBox;

    /// <summary>更新正文（帮助窗口已经打开、再次请求帮助时复用同一个窗口）。</summary>
    public void SetContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!string.Equals(_textBox.Text, content, StringComparison.Ordinal))
            _textBox.Text = content;
    }
}
