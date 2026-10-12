using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace ScottPlotEx.Demo;

/// <summary>
/// 演示窗体：每秒向 <see cref="ScrollingPlotControl"/> 添加一个数据点，
/// 并可通过单选框切换堆叠 / 跟随 / 手动模式。
/// </summary>
public sealed class MainForm : Form
{
    private readonly ScrollingPlotControl _plot = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 20 };
    private readonly Random _random = new(20260920);

    private readonly RadioButton _stackedMode = new() { Text = "堆叠模式", AutoSize = true, Checked = true };
    private readonly RadioButton _followMode = new() { Text = "跟随模式", AutoSize = true };
    private readonly RadioButton _manualMode = new() { Text = "手动（停止滚动）", AutoSize = true };
    private readonly CheckBox _autoAdd = new() { Text = "每秒添加数据", AutoSize = true, Checked = true };
    private readonly Label _displayPercentLabel = new() { Text = "X 轴显示 %", AutoSize = true, Padding = new Padding(12, 6, 0, 0) };
    private readonly NumericUpDown _displayPercent = new()
    {
        Minimum = 0,
        Maximum = 100,
        DecimalPlaces = 1,
        Increment = 5,
        Value = 30,
        Width = 72,
        Margin = new Padding(3, 3, 3, 3),
    };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    private long _index;
    private bool _syncingMode;

    /// <summary>窗体中使用的实时曲线控件（便于测试与二次开发）。</summary>
    public ScrollingPlotControl PlotControl => _plot;

    public MainForm()
    {
        Text = "ScottPlot 实时曲线控件示例（堆叠 / 跟随 / 手动）";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(1100, 660);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 420);

        Controls.Add(_plot);
        Controls.Add(CreateToolbar());

        _plot.Plot.Title("实时数据（每秒 1 点）");
        _plot.Plot.XLabel("数据点序号");
        _plot.Plot.YLabel("数值");
        _plot.ModeChanged += (_, _) => UpdateModeUi();
        _plot.DataChanged += (_, _) => UpdateStatus();
        _plot.AutoScrollStopped += (_, _) => UpdateStatus();

        _timer.Tick += OnTimerTick;
        _timer.Start();

        UpdateModeUi();
        UpdateStatus();
    }

    private Control CreateToolbar()
    {
        Button addOnce = new() { Text = "立即添加 1 点", AutoSize = true };
        addOnce.Click += (_, _) => AddNextValue();

        Button addTen = new() { Text = "添加 10 点", AutoSize = true };
        addTen.Click += (_, _) =>
        {
            for (int i = 0; i < 10; i++)
                AddNextValue();
        };

        Button clear = new() { Text = "清空数据", AutoSize = true };
        clear.Click += (_, _) => _plot.ClearData();

        Button help = new() { Text = "显示帮助", AutoSize = true };
        help.Click += (_, _) => _plot.ShowHelp();

        // 多曲线流式演示：10 条曲线 × 1000 点/秒，采集线程直接调用 PlotSeries.AddValues（控件按帧合并、限制帧率）
        Button streaming = new() { Text = "10 曲线流式演示", AutoSize = true };
        streaming.Click += (_, _) =>
        {
            StreamingDemoForm form = new();
            form.Show(this);
        };

        // 多曲线测试：交互式添加 / 删除 / 隐藏曲线，各曲线独立的波形、速率与写入线程，以及预设场景
        Button multiSeries = new() { Text = "多曲线测试", AutoSize = true };
        multiSeries.Click += (_, _) =>
        {
            MultiSeriesTestForm form = new();
            form.Show(this);
        };

        // 堆叠堆积压力测试：20 次/秒、1 条曲线、只增不删（观察“这样堆叠能持续多久”）
        Button stacking = new() { Text = "堆叠堆积压测（20 次/秒）", AutoSize = true };
        stacking.Click += (_, _) =>
        {
            StackingStressForm form = new();
            form.Show(this);
        };

        _stackedMode.CheckedChanged += OnModeCheckedChanged;
        _followMode.CheckedChanged += OnModeCheckedChanged;
        _manualMode.CheckedChanged += OnModeCheckedChanged;
        _autoAdd.CheckedChanged += (_, _) =>
        {
            if (_autoAdd.Checked)
                _timer.Start();
            else
                _timer.Stop();
        };

        // X 轴显示长度百分比：堆叠模式=原点百分比；跟随模式=尾部窗口百分比
        _displayPercent.ValueChanged += (_, _) =>
        {
            if (_syncingMode)
                return;

            // 显式指定当前模式：避免把某个模式的百分比误写到另一个模式
            // 注意：这里只改百分比、不动模式；不要为了“让百分比生效”去写 _plot.Mode = ...
            _plot.SetXAxisDisplayPercent((double)_displayPercent.Value, _plot.Mode);
            
            SyncPercentUi(); // 回写被控件修正后的值（例如跟随模式的最小窗口）
            UpdateStatus();
        };

        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true,
        };

        toolbar.Controls.AddRange([
            _stackedMode, _followMode, _manualMode, _autoAdd,
            _displayPercentLabel, _displayPercent,
            addOnce, addTen, clear, help, streaming, multiSeries, stacking,
        ]);
        toolbar.Controls.Add(_status);

        return toolbar;
    }

    private void OnTimerTick(object? sender, EventArgs e) => AddNextValue();

    private void AddNextValue()
    {
        if (_plot.IsDisposed)
            return;

        _index++;
        double wave = 50 + 25 * Math.Sin(_index / 25.0) + 4 * Math.Sin(_index / 3.0);
        double value = wave + (_random.NextDouble() - 0.5) * 6;
        _plot.AddValue(value);
    }

    private void OnModeCheckedChanged(object? sender, EventArgs e)
    {
        if (_syncingMode)
            return;

        if (_stackedMode.Checked)
            _plot.Mode = ScrollingMode.Stacked;
        else if (_followMode.Checked)
            _plot.Mode = ScrollingMode.Follow;
        else if (_manualMode.Checked)
            _plot.Mode = ScrollingMode.Manual;
    }

    private void UpdateModeUi()
    {
        _syncingMode = true;
        try
        {
            _stackedMode.Checked = _plot.Mode == ScrollingMode.Stacked;
            _followMode.Checked = _plot.Mode == ScrollingMode.Follow;
            _manualMode.Checked = _plot.Mode == ScrollingMode.Manual;
        }
        finally
        {
            _syncingMode = false;
        }

        Text = $"ScottPlot 实时曲线控件示例（堆叠 / 跟随 / 手动）- 当前：{ModeText(_plot.Mode)}";
        _displayPercentLabel.Text = PercentLabelText();
        SyncPercentUi();
        UpdateStatus();
    }

    /// <summary>百分比输入框的标签：明确当前改的是哪个模式的百分比。</summary>
    private string PercentLabelText()
    {
        ScrollingMode mode = _plot.Mode == ScrollingMode.Manual ? _plot.LastAutoScrollMode : _plot.Mode;
        return mode == ScrollingMode.Follow ? "跟随窗口 %" : "堆叠显示 %";
    }

    private void SyncPercentUi()
    {
        _syncingMode = true;
        try
        {
            _displayPercent.Value = (decimal)Math.Round(_plot.CurrentXAxisDisplayPercent, 1);
        }
        finally
        {
            _syncingMode = false;
        }
    }

    private static string ModeText(ScrollingMode mode) => mode switch
    {
        ScrollingMode.Stacked => "堆叠模式",
        ScrollingMode.Follow => "跟随模式",
        _ => "手动（停止滚动）",
    };

    private void UpdateStatus()
    {
        string modeText = _plot.Mode switch
        {
            ScrollingMode.Stacked => $"堆叠（X 轴显示最新 {_plot.StackedDisplayPercent:0.#}%，最大值 = 点数+2）",
            ScrollingMode.Follow => $"跟随（X 轴最新 {_plot.FollowWindowPercent:0.#}%，最大值 = 点数+2）",
            _ => "手动（已停止滚动，重新选择模式或中键单击整体显示即可恢复）",
        };

        string limits;
        try
        {
            ScottPlot.AxisLimits axisLimits = _plot.Plot.Axes.GetLimits();
            limits = axisLimits.IsReal
                ? $"X: [{axisLimits.Left:F1}, {axisLimits.Right:F1}]    Y: [{axisLimits.Bottom:F2}, {axisLimits.Top:F2}]"
                : "X: [-], Y: [-]";
        }
        catch (Exception)
        {
            limits = "X: [-], Y: [-]";
        }

        _status.Text = $"点数：{_plot.DataCount}    最新值：{_plot.LatestValue:F2}    模式：{modeText}    {limits}";
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        // 
        // MainForm
        // 
        ClientSize = new Size(1063, 623);
        Name = "MainForm";
        ResumeLayout(false);

    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosing(e);
    }
}
