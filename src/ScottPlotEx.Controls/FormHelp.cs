namespace ScottPlot
{
    /// <summary>
    /// 中文帮助对话框。
    /// </summary>
    public partial class FormHelp : Form
    {
        /// <summary>
        /// 中文帮助对话框。
        /// </summary>
        public FormHelp()
        {
            InitializeComponent();
            //Text = $"ScottPlot {Plot.Version} Help";
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            lblMessage.Text = GetHelpMessage();
        }
        private string GetHelpMessage()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("鼠标左键拖拽: 平移");
            sb.AppendLine("鼠标右键拖拽: 缩放");
            sb.AppendLine("鼠标中键拖拽: 选择区域缩放");
            sb.AppendLine("ALT+鼠标左键拖拽: 选择区域缩放");
            sb.AppendLine("滚轮: 以光标为中心缩放");
            sb.AppendLine("");
            sb.AppendLine("鼠标右键点击: 显示菜单");
            sb.AppendLine("鼠标中键点击: 自动坐标系");
            //sb.AppendLine("Double-click: show benchmark");
            sb.AppendLine("");
            sb.AppendLine("CTRL+鼠标左键拖拽以水平移动");
            sb.AppendLine("SHIFT+鼠标左键拖拽以垂直移动");
            sb.AppendLine("CTRL+鼠标右键拖拽缩放X轴");
            sb.AppendLine("SHIFT+鼠标右键拖拽缩放Y轴");
            sb.AppendLine("CTRL+SHIFT+鼠标右键拖拽均匀缩放");
            return sb.ToString();
        }
    }
}
