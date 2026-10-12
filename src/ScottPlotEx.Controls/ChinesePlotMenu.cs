using System.Drawing;
using System.Windows.Forms;
using ScottPlot;
using ScottPlot.WinForms;

namespace ScottPlotEx.Controls;

/// <summary>
/// ScottPlot 自带右键菜单（<see cref="FormsPlotMenu"/>）的中文版本：
/// 菜单项与动作和 ScottPlot 自带内容完全一致，只把文字换成中文，并在末尾增加“显示帮助”。
/// 控件已不再内嵌 FormsPlot，因此四个标准项的动作在这里按 FormsPlotMenu 的实现重写（行为保持一致）。
/// </summary>
public sealed class ChinesePlotMenu : IPlotMenu
{
    private const string SaveImageLabel = "保存图像";
    private const string CopyToClipboardLabel = "复制到剪贴板";
    private const string AutoscaleLabel = "自动缩放";
    private const string OpenInNewWindowLabel = "在新窗口中打开";

    /// <summary>ScottPlot 自带菜单项（英文）与中文菜单项的对应关系。</summary>
    private static readonly Dictionary<string, string> ChineseLabels = new(StringComparer.Ordinal)
    {
        ["Save Image"] = SaveImageLabel,
        ["Copy to Clipboard"] = CopyToClipboardLabel,
        ["Autoscale"] = AutoscaleLabel,
        ["Open in New Window"] = OpenInNewWindowLabel,
    };

    /// <summary>滚动模式菜单项（按顺序显示，渲染为勾选项）。</summary>
    private static readonly (string Label, ScrollingMode Mode)[] ModeItems =
    [
        ("堆叠模式", ScrollingMode.Stacked),
        ("跟随模式", ScrollingMode.Follow),
        ("手动模式（停止滚动）", ScrollingMode.Manual),
    ];

    private readonly ScrollingPlotControl _control;
    private bool _showModeItems = true;

    internal ChinesePlotMenu(ScrollingPlotControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        Reset();
    }

    /// <summary>当前菜单项列表。</summary>
    public List<ContextMenuItem> ContextMenuItems { get; set; } = [];

    /// <summary>保存图像时的默认文件名。</summary>
    public string DefaultSaveImageFilename { get; set; } = "Plot.png";

    /// <summary>
    /// 是否在菜单中显示“滚动模式”勾选项（堆叠模式 / 跟随模式 / 手动模式）。默认 true。
    /// </summary>
    public bool ShowModeItems
    {
        get => _showModeItems;
        set
        {
            if (_showModeItems == value)
                return;

            _showModeItems = value;
            Reset();
        }
    }

    /// <summary>最近一次显示的菜单，便于自动化测试与诊断。</summary>
    public ContextMenuStrip? LastMenuShown { get; private set; }

    /// <summary>菜单弹出后触发。</summary>
    public event EventHandler? ContextMenuOpened;

    /// <summary>
    /// 生成中文的 ScottPlot 标准菜单项（与 <see cref="FormsPlotMenu.StandardContextMenuItems"/> 一一对应，顺序相同）。
    /// </summary>
    public List<ContextMenuItem> StandardContextMenuItems() =>
    [
        new ContextMenuItem { Label = SaveImageLabel, OnInvoke = OpenSaveImageDialog },
        new ContextMenuItem { Label = CopyToClipboardLabel, OnInvoke = CopyImageToClipboard },
        new ContextMenuItem { Label = AutoscaleLabel, OnInvoke = Autoscale },
        new ContextMenuItem { Label = OpenInNewWindowLabel, OnInvoke = OpenInNewWindow },
    ];

    /// <summary>“保存图像”：选择文件后按最近一次渲染的尺寸保存（格式由扩展名决定）。</summary>
    public void OpenSaveImageDialog(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        using SaveFileDialog dialog = new()
        {
            FileName = DefaultSaveImageFilename,
            Filter = "PNG 图像 (*.png)|*.png" +
                     "|JPEG 图像 (*.jpg, *.jpeg)|*.jpg;*.jpeg" +
                     "|BMP 图像 (*.bmp)|*.bmp" +
                     "|WebP 图像 (*.webp)|*.webp" +
                     "|SVG 图像 (*.svg)|*.svg" +
                     "|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(dialog.FileName))
            return;

        ImageFormat format;

        try
        {
            format = ImageFormats.FromFilename(dialog.FileName);
        }
        catch (ArgumentException)
        {
            MessageBox.Show("不支持该图像格式。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
            plot.Save(dialog.FileName, (int)size.Width, (int)size.Height, format);
        }
        catch (Exception)
        {
            MessageBox.Show("保存图像失败。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>“复制到剪贴板”：按最近一次渲染的尺寸生成位图并放入剪贴板。</summary>
    public void CopyImageToClipboard(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
        using ScottPlot.Image image = plot.GetImage((int)size.Width, (int)size.Height);
        using Bitmap bitmap = image.GetBitmap();
        Clipboard.SetImage(bitmap);
    }

    /// <summary>“自动缩放”：缩放到全部数据，再同步滚动状态（复位 X 轴偏移，必要时恢复 / 切换滚动模式）。</summary>
    public void Autoscale(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        plot.Axes.AutoScale();
        _control.Refresh();
        _control.HandleAutoscaleRequested();
    }

    /// <summary>“在新窗口中打开”：在模态窗口中交互式查看同一个 Plot，关闭后归还给本控件。</summary>
    public void OpenInNewWindow(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        FormsPlotViewer.Launch(plot, "Interactive Plot");
        _control.Refresh();
    }

    /// <summary>把 ScottPlot 自带的英文菜单文字翻译成中文。</summary>
    public static string TranslateLabel(string label) =>
        ChineseLabels.TryGetValue(label, out string? chinese) ? chinese : label;

    /// <summary>恢复为默认菜单：ScottPlot 自带菜单项的中文版本 + “显示帮助”。</summary>
    public void Reset()
    {
        ContextMenuItems.Clear();
        ContextMenuItems.AddRange(StandardContextMenuItems());
        ContextMenuItems.Add(new ContextMenuItem { IsSeparator = true });

        if (_showModeItems)
        {
            foreach ((string label, ScrollingMode mode) in ModeItems)
            {
                ContextMenuItems.Add(new ContextMenuItem
                {
                    Label = label,
                    OnInvoke = _ => _control.Mode = mode,
                });
            }

            ContextMenuItems.Add(new ContextMenuItem { IsSeparator = true });
        }

        ContextMenuItems.Add(new ContextMenuItem
        {
            Label = "显示帮助",
            OnInvoke = _ => _control.ShowHelp(),
        });
    }

    /// <summary>清空全部菜单项。</summary>
    public void Clear() => ContextMenuItems.Clear();

    /// <summary>添加一个自定义菜单项。</summary>
    public void Add(string Label, Action<Plot> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ContextMenuItems.Add(new ContextMenuItem { Label = Label, OnInvoke = action });
    }

    /// <summary>添加一条分隔线。</summary>
    public void AddSeparator() => ContextMenuItems.Add(new ContextMenuItem { IsSeparator = true });

    /// <summary>根据菜单项列表创建 <see cref="ContextMenuStrip"/>。</summary>
    public ContextMenuStrip BuildContextMenu(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        ContextMenuStrip menu = new();

        foreach (ContextMenuItem item in ContextMenuItems)
        {
            if (item.IsSeparator)
            {
                menu.Items.Add(new ToolStripSeparator());
                continue;
            }

            // 滚动模式渲染为勾选项：勾选状态完全由控件的当前模式推导
            if (TryGetMode(item.Label, out ScrollingMode mode))
            {
                menu.Items.Add(CreateModeMenuItem(item, mode, plot));
                continue;
            }

            ToolStripMenuItem menuItem = new(item.Label);
            Action<Plot> action = item.OnInvoke;
            menuItem.Click += (_, _) => action?.Invoke(plot);
            menu.Items.Add(menuItem);
        }

        return menu;
    }

    /// <summary>
    /// 把菜单中的模式勾选状态同步为控件当前的模式。
    /// 模式在菜单打开期间发生变化（例如自动停止滚动）时使用。
    /// </summary>
    public void SyncModeChecks(ContextMenuStrip menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem menuItem && TryGetMode(menuItem.Text ?? string.Empty, out ScrollingMode mode))
                menuItem.Checked = _control.Mode == mode;
        }
    }

    /// <summary>菜单项文字对应的滚动模式。</summary>
    public static bool TryGetMode(string label, out ScrollingMode mode)
    {
        foreach ((string itemLabel, ScrollingMode itemMode) in ModeItems)
        {
            if (string.Equals(itemLabel, label, StringComparison.Ordinal))
            {
                mode = itemMode;
                return true;
            }
        }

        mode = default;
        return false;
    }

    private ToolStripMenuItem CreateModeMenuItem(ContextMenuItem item, ScrollingMode mode, Plot plot)
    {
        ToolStripMenuItem menuItem = new(item.Label)
        {
            // 只在打开菜单时按当前模式勾选；点击不会自行切换勾选状态（避免勾选与模式不一致）
            Checked = _control.Mode == mode,
            CheckOnClick = false,
        };

        Action<Plot> action = item.OnInvoke;
        menuItem.Click += (_, _) => action?.Invoke(plot);

        return menuItem;
    }

    /// <summary>在指定位置弹出菜单。</summary>
    public void ShowContextMenu(Pixel pixel)
    {
        Plot? plot = _control.GetPlotAtPixel(pixel);
        if (plot is null || ContextMenuItems.Count == 0)
            return;

        ContextMenuStrip menu = BuildContextMenu(plot);
        LastMenuShown = menu;
        _control.TrackContextMenu(menu);

        // 菜单打开期间模式若发生变化（例如自动停止滚动），同步勾选状态
        EventHandler<ScrollingModeChangedEventArgs> modeChanged = (_, _) => SyncModeChecks(menu);
        _control.ModeChanged += modeChanged;
        menu.Closed += (_, _) => _control.ModeChanged -= modeChanged;

        menu.Show(_control.Canvas, new Point((int)pixel.X, (int)pixel.Y));
        ContextMenuOpened?.Invoke(this, EventArgs.Empty);
    }
}
