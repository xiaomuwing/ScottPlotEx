using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace ScottPlotEx.Demo;

/// <summary>
/// 多曲线测试窗口：交互式验证控件的多曲线功能。
/// <list type="bullet">
/// <item>添加 / 删除 / 清空 / 显示隐藏曲线（列表勾选框 = <see cref="PlotSeries.IsVisible"/>）；</item>
/// <item>每条曲线独立的数据源：波形、幅值、偏移、速率，写入线程可选“UI 定时器”（UI 线程同步写入）或“后台线程”（跨线程按帧合并）；</item>
/// <item>清空 / 删除可以从后台线程发起，用来验证跨线程的顺序保证；</item>
/// <item>模式、X 轴单位（点数 / 相对时间 / 绝对时间）、显示百分比、跟随固定窗口（按当前单位：点数或秒）、帧率上限、图例等控件选项；</item>
/// <item>每条曲线的采样间隔由它的“点/秒”决定（<see cref="PlotSeries.SampleRate"/>），时间单位下各曲线在时间上对齐；</item>
/// <item>起始时间（控件的 <see cref="ScrollingPlotControl.DefaultStartTime"/>，可用“使用当前时间”）：绝对时间必须先设置，未设置时下拉框里的“绝对时间”灰显并说明原因；</item>
/// <item>预设场景：量级差别大的曲线、长短不一的曲线、10 条 × 1000 点/秒。</item>
/// </list>
/// </summary>
public sealed class MultiSeriesTestForm : Form
{
    private readonly ScrollingPlotControl _plot = new() { Dock = DockStyle.Fill };
    private readonly List<CurveFeeder> _feeders = [];
    private readonly System.Windows.Forms.Timer _uiFeedTimer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 500 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new(20261012);

    // 曲线列表与所选曲线参数
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = true,
    };
    private readonly TextBox _name = new() { Width = 110 };
    private readonly ComboBox _waveform = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly NumericUpDown _amplitude = new() { Minimum = 0, Maximum = 1_000_000, DecimalPlaces = 2, Value = 10, Width = 110 };
    private readonly NumericUpDown _offset = new() { Minimum = -1_000_000, Maximum = 1_000_000, DecimalPlaces = 2, Width = 110 };
    private readonly NumericUpDown _rate = new() { Minimum = 1, Maximum = 100_000, Increment = 10, Value = 100, Width = 110 };
    private readonly ComboBox _writer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly NumericUpDown _lineWidth = new() { Minimum = 0.5m, Maximum = 10, DecimalPlaces = 1, Increment = 0.5m, Value = 1, Width = 110 };
    private readonly Button _color = new() { Text = "选择……", Width = 110, Height = 28 };
    private readonly CheckBox _backgroundOperations = new() { Text = "清空 / 删除从后台线程发起", AutoSize = true, Padding = new Padding(8, 4, 0, 0) };

    // 控件选项
    private readonly RadioButton _stackedMode = new() { Text = "堆叠", AutoSize = true, Checked = true };
    private readonly RadioButton _followMode = new() { Text = "跟随", AutoSize = true };
    private readonly RadioButton _manualMode = new() { Text = "手动", AutoSize = true };
    private readonly Label _percentLabel = new() { Text = "堆叠显示 %", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    private readonly NumericUpDown _percent = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Increment = 5, Value = 100, Width = 64 };
    private readonly ComboBox _xAxisUnit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, DropDownWidth = 230, DrawMode = DrawMode.OwnerDrawFixed };

    /// <summary>起始时间（= 控件的 DefaultStartTime，所有曲线第一个数据点的时刻）；不勾选表示没有设置。绝对时间必须设置。</summary>
    private readonly DateTimePicker _startTime = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm:ss",
        ShowCheckBox = true,
        Checked = false,
        Width = 210,
    };
    private readonly Button _useNow = new() { Text = "使用当前时间", AutoSize = true };
    private readonly ToolTip _toolbarTips = new();
    private string? _notice;
    private DateTime _noticeExpires;
    private readonly Label _followWindowLabel = new() { Text = "跟随窗口(点)", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };

    /// <summary>跟随固定窗口：点数单位下为点数（FollowWindowPoints），时间单位下为秒数（FollowWindowDuration）。</summary>
    private readonly NumericUpDown _followWindow = new() { Minimum = 0, Maximum = 100_000_000, Increment = 1000, Width = 90 };
    private readonly NumericUpDown _maxFps = new() { Minimum = 0, Maximum = 240, Increment = 10, Width = 56 };
    private readonly CheckBox _legend = new() { Text = "图例", AutoSize = true, Checked = true, Padding = new Padding(8, 3, 0, 0) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 26, Padding = new Padding(8, 6, 0, 0) };

    private bool _syncing;
    private int _curveSerial;
    private long _lastWritten;
    private long _lastRenderCount;
    private double _lastStatusSeconds;

    /// <summary>窗体中使用的实时曲线控件（便于测试与二次开发）。</summary>
    public ScrollingPlotControl PlotControl => _plot;

    /// <summary>当前全部曲线的数据源（便于测试）。</summary>
    public IReadOnlyList<CurveFeeder> Feeders => _feeders;

    /// <summary>预设场景。</summary>
    public enum Preset
    {
        /// <summary>量级差别大：温度（约 25）/ 压力（约 3000）/ 振动（±0.5），UI 定时器与后台线程混合写入。</summary>
        DifferentMagnitudes,

        /// <summary>长短不一：10 / 50 / 200 点/秒，X 轴取最长的可见曲线（隐藏最长的一条可以看到 X 轴变短）。</summary>
        DifferentLengths,

        /// <summary>10 条曲线 × 每条 1000 点/秒，全部后台线程写入。</summary>
        TenCurvesStress,
    }

    public MultiSeriesTestForm()
    {
        Text = "多曲线测试";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(1320, 780);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 560);

        _plot.Plot.Title("多曲线测试");
        _plot.Plot.YLabel("数值");
        _plot.ShowLegend = _legend.Checked;
        ApplyAxisLabel();

        _xAxisUnit.Items.AddRange(["点数", "相对时间", "绝对时间"]);
        _xAxisUnit.SelectedIndex = (int)_plot.XAxisUnit;
        _waveform.Items.AddRange([.. Enum.GetValues<Waveform>().Select(WaveformText)]);
        _waveform.SelectedIndex = 0;
        _writer.Items.AddRange(["UI 定时器", "后台线程"]);
        _writer.SelectedIndex = 1;

        _list.Columns.Add("名称", 84);
        _list.Columns.Add("波形", 58);
        _list.Columns.Add("点/秒", 54, HorizontalAlignment.Right);
        _list.Columns.Add("线程", 58);
        _list.Columns.Add("点数", 68, HorizontalAlignment.Right);
        _list.Columns.Add("最新值", 72, HorizontalAlignment.Right);

        Controls.Add(_plot);
        Controls.Add(CreateSidePanel());
        Controls.Add(CreateToolbar());
        Controls.Add(_status);

        _list.ItemChecked += OnItemChecked;
        _list.SelectedIndexChanged += (_, _) => LoadSelectedParameters();
        _plot.ModeChanged += (_, _) => SyncModeUi();

        _uiFeedTimer.Tick += (_, _) => FeedUiCurves();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _uiFeedTimer.Start();
        _statusTimer.Start();

        // 打开窗口时默认加载“量级差别大”场景（调用方已经先应用了别的场景时不覆盖）
        Load += (_, _) =>
        {
            if (_feeders.Count == 0)
                ApplyPreset(Preset.DifferentMagnitudes);
        };
    }

    #region 界面

    private Control CreateToolbar()
    {
        _stackedMode.CheckedChanged += OnModeRadioChanged;
        _followMode.CheckedChanged += OnModeRadioChanged;
        _manualMode.CheckedChanged += OnModeRadioChanged;

        _percent.ValueChanged += (_, _) =>
        {
            if (_syncing)
                return;

            _plot.SetXAxisDisplayPercent((double)_percent.Value, _plot.Mode);
            SyncModeUi(); // 回写被控件修正后的值
        };

        _xAxisUnit.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing)
                return;

            // 运行中切换单位：数据保留，控件立即重算坐标轴（拖动偏移 / 手动模式视图换算到新单位）。
            // 绝对时间只认明确设置的起始时间：没有设置时控件拒绝切换，这里提示原因并把下拉框恢复为当前单位
            try
            {
                _plot.XAxisUnit = (XAxisUnit)_xAxisUnit.SelectedIndex;
            }
            catch (InvalidOperationException)
            {
                ShowNotice("绝对时间需要起始时间：请先勾选并设置“起始时间”，或点“使用当前时间”。", _xAxisUnit);
            }

            ApplyAxisLabel();
            SyncModeUi(); // 跟随窗口输入框换成当前单位（点数或秒）；切换被拒绝时下拉框恢复为当前单位
        };

        // 绝对时间没有起始时间时不可用：下拉列表里灰显并说明原因
        _xAxisUnit.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
                return;

            bool unavailable = e.Index == (int)XAxisUnit.AbsoluteTime && _plot.DefaultStartTime is null;
            bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
            string text = (string)_xAxisUnit.Items[e.Index]! + (unavailable && inList ? "（需先设置起始时间）" : string.Empty);
            Color color = unavailable ? SystemColors.GrayText
                : (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText
                : _xAxisUnit.ForeColor;

            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font, e.Bounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        };

        // 起始时间：勾选 = 设置控件的 DefaultStartTime（所有曲线的第一个数据点），不勾选 = 不设置（相对时间按第一个点到达的时刻）
        _startTime.ValueChanged += (_, _) =>
        {
            if (!_syncing)
                SetStartTime(_startTime.Checked ? _startTime.Value : null);
        };
        _useNow.Click += (_, _) => UseCurrentTimeAsStartTime();
        _toolbarTips.SetToolTip(_startTime,
            "起始时间 = 所有曲线第一个数据点的时刻（控件的 DefaultStartTime）。\n" +
            "不勾选：相对时间按每条曲线第一个点到达的时刻；绝对时间必须设置。\n" +
            "已设置时，应用预设 / 全部清空会把它更新为当前时间（重新开始一次测量）。");
        _toolbarTips.SetToolTip(_xAxisUnit, "点数 / 相对时间 / 绝对时间（绝对时间需要先设置起始时间）");

        // 跟随固定窗口：点数单位写 FollowWindowPoints，时间单位写 FollowWindowDuration（两者各自保留）
        _followWindow.ValueChanged += (_, _) =>
        {
            if (_syncing)
                return;

            if (_plot.XAxisUnit == XAxisUnit.Samples)
                _plot.FollowWindowPoints = (int)_followWindow.Value;
            else
                _plot.FollowWindowDuration = TimeSpan.FromSeconds((double)_followWindow.Value);
        };
        _maxFps.ValueChanged += (_, _) => _plot.MaxFramesPerSecond = (int)_maxFps.Value;
        _legend.CheckedChanged += (_, _) => _plot.ShowLegend = _legend.Checked;

        Button startAll = new() { Text = "全部开始", AutoSize = true };
        startAll.Click += (_, _) => _feeders.ForEach(f => f.Start());

        Button stopAll = new() { Text = "全部停止", AutoSize = true };
        stopAll.Click += (_, _) => _feeders.ForEach(f => f.Stop());

        Button clearAll = new() { Text = "全部清空", AutoSize = true };
        clearAll.Click += (_, _) =>
        {
            RestartStartTimeIfSet(); // 重新开始一次测量：已设置的起始时间更新为当前时间
            RunOperation(_plot.ClearData);
        };

        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 6, 8, 2),
            WrapContents = true,
        };

        toolbar.Controls.AddRange([
            new Label { Text = "模式", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _stackedMode, _followMode, _manualMode,
            new Label { Text = "X 轴", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _xAxisUnit,
            new Label { Text = "起始时间", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _startTime, _useNow,
            _percentLabel, _percent,
            _followWindowLabel, _followWindow,
            new Label { Text = "帧率上限(0=不限)", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _maxFps,
            _legend, startAll, stopAll, clearAll,
        ]);

        return toolbar;
    }

    private Control CreateSidePanel()
    {
        Button add = new() { Text = "添加曲线", AutoSize = true };
        add.Click += (_, _) => AddCurve(NextCurveName(), ReadParameters());

        Button remove = new() { Text = "删除所选", AutoSize = true };
        remove.Click += (_, _) => RemoveSelected();

        Button clear = new() { Text = "清空所选", AutoSize = true };
        clear.Click += (_, _) => SelectedFeeders().ToList().ForEach(f => RunOperation(f.Series.Clear));

        Button start = new() { Text = "开始所选", AutoSize = true };
        start.Click += (_, _) => SelectedFeeders().ToList().ForEach(f => f.Start());

        Button stop = new() { Text = "停止所选", AutoSize = true };
        stop.Click += (_, _) => SelectedFeeders().ToList().ForEach(f => f.Stop());

        Button apply = new() { Text = "应用到所选", AutoSize = true };
        apply.Click += (_, _) => ApplyParametersToSelected();

        _color.Click += (_, _) => PickColor();

        FlowLayoutPanel listButtons = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
        listButtons.Controls.AddRange([add, remove, clear, start, stop]);

        GroupBox listGroup = new() { Text = "曲线（勾选 = 显示）", Dock = DockStyle.Fill, Padding = new Padding(6) };
        listGroup.Controls.Add(_list);
        listGroup.Controls.Add(listButtons);

        // 两列排布：标签 | 输入框 | 标签 | 输入框
        TableLayoutPanel parameters = new() { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        AddRow(parameters, ("名称", _name), ("波形", _waveform));
        AddRow(parameters, ("幅值", _amplitude), ("偏移", _offset));
        AddRow(parameters, ("点/秒", _rate), ("写入线程", _writer));
        AddRow(parameters, ("线宽", _lineWidth), ("颜色", _color));
        parameters.Controls.Add(apply, 1, parameters.RowCount);
        parameters.Controls.Add(_backgroundOperations, 2, parameters.RowCount);
        parameters.SetColumnSpan(_backgroundOperations, 2);
        parameters.RowCount++;

        GroupBox parameterGroup = new() { Text = "所选曲线参数（添加曲线也用这些参数）", Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        parameterGroup.Controls.Add(parameters);

        FlowLayoutPanel presets = new() { Dock = DockStyle.Fill, WrapContents = true };
        ToolTip tips = new();
        presets.Controls.Add(PresetButton("量级差别大", "温度（约 25）/ 压力（约 3000）/ 振动（±0.5），UI 定时器与后台线程混合写入", Preset.DifferentMagnitudes));
        presets.Controls.Add(PresetButton("长短不一", "10 / 50 / 200 点/秒：点数单位下 X 轴取最长的可见曲线（隐藏最长的一条可以看到 X 轴变短）；切到相对 / 绝对时间可以看到三条曲线在时间上对齐", Preset.DifferentLengths));
        presets.Controls.Add(PresetButton("10 条 × 1000 点/秒", "10 条曲线全部后台线程写入，跟随模式固定显示最近 60 秒", Preset.TenCurvesStress));

        GroupBox presetGroup = new() { Text = "预设场景（会替换现有曲线）", Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(6) };
        presetGroup.Controls.Add(presets);

        Panel side = new() { Dock = DockStyle.Left, Width = 440, Padding = new Padding(6, 0, 0, 0) };
        side.Controls.Add(listGroup);
        side.Controls.Add(parameterGroup);
        side.Controls.Add(presetGroup);
        return side;

        static void AddRow(TableLayoutPanel table, (string Label, Control Control) left, (string Label, Control Control) right)
        {
            table.Controls.Add(new Label { Text = left.Label, AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, 0, table.RowCount);
            table.Controls.Add(left.Control, 1, table.RowCount);
            table.Controls.Add(new Label { Text = right.Label, AutoSize = true, Padding = new Padding(8, 6, 4, 0) }, 2, table.RowCount);
            table.Controls.Add(right.Control, 3, table.RowCount);
            table.RowCount++;
        }

        Button PresetButton(string text, string tip, Preset preset)
        {
            Button button = new() { Text = text, AutoSize = true };
            button.Click += (_, _) => ApplyPreset(preset);
            tips.SetToolTip(button, tip);
            return button;
        }
    }

    #endregion

    #region 曲线操作

    /// <summary>添加一条曲线并按参数开始写入。</summary>
    public CurveFeeder AddCurve(string name, CurveParameters parameters, bool start = true)
    {
        PlotSeries series = _plot.AddSeries(name, parameters.Color);
        series.LineWidth = parameters.LineWidth;
        series.SampleRate = parameters.Rate; // 采样间隔 = 1 / 点每秒：时间单位下各曲线在时间上对齐

        CurveFeeder feeder = new(series, parameters, _random.Next());
        _feeders.Add(feeder);

        ListViewItem item = new([name, "", "", "", "0", ""]) { Checked = true, Tag = feeder, UseItemStyleForSubItems = false };
        _syncing = true;
        try
        {
            _list.Items.Add(item);
        }
        finally
        {
            _syncing = false;
        }

        UpdateRow(item);

        if (start)
            feeder.Start();

        return feeder;
    }

    /// <summary>删除曲线（停止写入并从控件中删除）。</summary>
    public void RemoveCurve(CurveFeeder feeder)
    {
        feeder.Stop();
        _feeders.Remove(feeder);

        foreach (ListViewItem item in _list.Items)
        {
            if (ReferenceEquals(item.Tag, feeder))
            {
                _list.Items.Remove(item);
                break;
            }
        }

        RunOperation(feeder.Series.Remove);
    }

    /// <summary>应用预设场景：删除现有曲线，清空数据，再按场景添加曲线并开始写入。</summary>
    public void ApplyPreset(Preset preset)
    {
        foreach (CurveFeeder feeder in _feeders.ToList())
            RemoveCurve(feeder);

        _plot.ClearData();
        RestartStartTimeIfSet(); // 重新开始一次测量：已设置的起始时间更新为当前时间
        _curveSerial = 0;

        // 默认按百分比；压测场景再改成固定 60 秒窗口（点数单位 60000 点，时间单位 60 秒）。X 轴单位保持用户当前的选择
        _plot.FollowWindowPoints = 0;
        _plot.FollowWindowDuration = TimeSpan.Zero;

        switch (preset)
        {
            case Preset.DifferentMagnitudes:
                _plot.Mode = ScrollingMode.Stacked;
                AddCurve("温度 (°C)", new CurveParameters(Waveform.Sine, 1.5, 25, 50, CurveWriter.UiTimer, ScottPlot.Colors.Blue));
                AddCurve("压力 (Pa)", new CurveParameters(Waveform.Sine, 40, 3000, 50, CurveWriter.BackgroundThread, ScottPlot.Colors.Orange));
                AddCurve("振动 (g)", new CurveParameters(Waveform.Noise, 0.5, 0, 200, CurveWriter.BackgroundThread, ScottPlot.Colors.Green));
                break;

            case Preset.DifferentLengths:
                _plot.Mode = ScrollingMode.Stacked;
                AddCurve("慢 10 点/秒", new CurveParameters(Waveform.Square, 5, 0, 10, CurveWriter.UiTimer, ScottPlot.Colors.Blue));
                AddCurve("中 50 点/秒", new CurveParameters(Waveform.Sawtooth, 5, 20, 50, CurveWriter.BackgroundThread, ScottPlot.Colors.Red));
                AddCurve("快 200 点/秒", new CurveParameters(Waveform.RandomWalk, 5, 40, 200, CurveWriter.BackgroundThread, ScottPlot.Colors.Green));
                break;

            case Preset.TenCurvesStress:
                _plot.Mode = ScrollingMode.Follow;
                _plot.FollowWindowPoints = 60_000; // 显示最近 60 秒（点数单位）
                _plot.FollowWindowDuration = TimeSpan.FromSeconds(60); // 显示最近 60 秒（时间单位）
                for (int i = 0; i < 10; i++)
                {
                    AddCurve(
                        $"曲线 {i + 1}",
                        new CurveParameters(Waveform.Sine, 8, (i % 5) * 6.0, 1000, CurveWriter.BackgroundThread, PaletteColor(i)));
                }

                break;
        }

        _curveSerial = _feeders.Count;
        SyncModeUi();
    }

    private void RemoveSelected()
    {
        foreach (CurveFeeder feeder in SelectedFeeders().ToList())
            RemoveCurve(feeder);
    }

    private void ApplyParametersToSelected()
    {
        CurveParameters parameters = ReadParameters();

        foreach (ListViewItem item in _list.SelectedItems)
        {
            CurveFeeder feeder = (CurveFeeder)item.Tag!;
            bool wasRunning = feeder.IsRunning;

            feeder.Stop();
            feeder.Parameters = parameters with { Color = feeder.Parameters.Color };

            if (_list.SelectedItems.Count == 1 && !string.IsNullOrWhiteSpace(_name.Text))
                feeder.Series.Name = _name.Text.Trim();

            feeder.Series.LineWidth = parameters.LineWidth;
            feeder.Series.SampleRate = parameters.Rate;

            if (wasRunning)
                feeder.Start();

            UpdateRow(item);
        }
    }

    private void PickColor()
    {
        using ColorDialog dialog = new() { Color = _color.BackColor };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _color.BackColor = dialog.Color;
        ScottPlot.Color color = ScottPlot.Color.FromColor(dialog.Color);

        foreach (CurveFeeder feeder in SelectedFeeders())
        {
            feeder.Parameters = feeder.Parameters with { Color = color };
            feeder.Series.Color = color;
        }

        UpdateRows();
    }

    /// <summary>清空 / 删除等操作：按勾选决定在 UI 线程直接执行，还是从后台线程发起（验证跨线程顺序）。</summary>
    private void RunOperation(Action operation)
    {
        if (_backgroundOperations.Checked)
            Task.Run(operation);
        else
            operation();
    }

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_syncing || e.Item.Tag is not CurveFeeder feeder)
            return;

        feeder.Series.IsVisible = e.Item.Checked;
    }

    private IEnumerable<CurveFeeder> SelectedFeeders() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(item => (CurveFeeder)item.Tag!);

    private string NextCurveName() => $"曲线 {++_curveSerial}";

    #endregion

    #region 参数与状态

    private CurveParameters ReadParameters() => new(
        (Waveform)_waveform.SelectedIndex,
        (double)_amplitude.Value,
        (double)_offset.Value,
        (int)_rate.Value,
        _writer.SelectedIndex == 0 ? CurveWriter.UiTimer : CurveWriter.BackgroundThread,
        _color.BackColor == SystemColors.Control ? PaletteColor(_feeders.Count) : ScottPlot.Color.FromColor(_color.BackColor),
        (float)_lineWidth.Value);

    private void LoadSelectedParameters()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not CurveFeeder feeder)
            return;

        CurveParameters p = feeder.Parameters;
        _name.Text = feeder.Series.Name;
        _waveform.SelectedIndex = (int)p.Waveform;
        _amplitude.Value = Clamp(_amplitude, p.Amplitude);
        _offset.Value = Clamp(_offset, p.Offset);
        _rate.Value = Clamp(_rate, p.Rate);
        _writer.SelectedIndex = p.Writer == CurveWriter.UiTimer ? 0 : 1;
        _lineWidth.Value = Clamp(_lineWidth, feeder.Series.LineWidth);
        _color.BackColor = ToSystemColor(feeder.Series.Color);

        static decimal Clamp(NumericUpDown box, double value) =>
            Math.Clamp((decimal)value, box.Minimum, box.Maximum);
    }

    private void OnModeRadioChanged(object? sender, EventArgs e)
    {
        if (_syncing || sender is not RadioButton { Checked: true } radio)
            return;

        _plot.Mode = radio == _followMode ? ScrollingMode.Follow : radio == _manualMode ? ScrollingMode.Manual : ScrollingMode.Stacked;
    }

    private void SyncModeUi()
    {
        _syncing = true;
        try
        {
            _stackedMode.Checked = _plot.Mode == ScrollingMode.Stacked;
            _followMode.Checked = _plot.Mode == ScrollingMode.Follow;
            _manualMode.Checked = _plot.Mode == ScrollingMode.Manual;

            ScrollingMode percentMode = _plot.Mode == ScrollingMode.Manual ? _plot.LastAutoScrollMode : _plot.Mode;
            _percentLabel.Text = percentMode == ScrollingMode.Follow ? "跟随窗口 %" : "堆叠显示 %";
            _percent.Value = (decimal)Math.Round(_plot.CurrentXAxisDisplayPercent, 1);

            // 跟随窗口按当前单位显示：点数单位为点数，时间单位为秒
            bool time = _plot.XAxisUnit != XAxisUnit.Samples;
            _xAxisUnit.SelectedIndex = (int)_plot.XAxisUnit;
            _followWindowLabel.Text = time ? "跟随窗口(秒)" : "跟随窗口(点)";
            _followWindow.DecimalPlaces = time ? 2 : 0;
            _followWindow.Increment = time ? 1 : 1000;
            _followWindow.Value = Math.Min(_followWindow.Maximum, time
                ? (decimal)Math.Round(_plot.FollowWindowDuration.TotalSeconds, 2)
                : _plot.FollowWindowPoints);

            // 起始时间：勾选状态与控件的 DefaultStartTime 一致；“绝对时间”是否可用随之重绘
            _startTime.Checked = _plot.DefaultStartTime is not null;
            if (_plot.DefaultStartTime is { } start && start >= _startTime.MinDate && start <= _startTime.MaxDate)
                _startTime.Value = start;

            _xAxisUnit.Invalidate();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 切换 X 轴单位（与工具栏“X 轴”下拉框相同：数据保留，坐标轴、轴标题与跟随窗口输入框随之切换）。
    /// 没有起始时间时选择绝对时间会被拒绝（保持当前单位并在状态栏说明原因，见 <see cref="Notice"/>）。
    /// </summary>
    public void SetXAxisUnit(XAxisUnit unit) => _xAxisUnit.SelectedIndex = (int)unit;

    /// <summary>把起始时间（控件的 DefaultStartTime）设为当前时间（与“使用当前时间”按钮相同）。</summary>
    public void UseCurrentTimeAsStartTime() => SetStartTime(DateTime.Now);

    /// <summary>
    /// 设置起始时间（与工具栏“起始时间”相同；null = 不勾选）。绝对时间单位下不能清除：保持原值并在状态栏说明原因。
    /// </summary>
    public void SetStartTime(DateTime? value)
    {
        try
        {
            _plot.DefaultStartTime = value;
        }
        catch (InvalidOperationException)
        {
            ShowNotice("当前为绝对时间，不能清除起始时间：请先切换到点数或相对时间。", _startTime);
        }

        SyncModeUi(); // 勾选状态与控件一致（清除被拒绝时恢复勾选）；“绝对时间”是否可用随之重绘
    }

    /// <summary>最近一次操作被拒绝的原因（显示在状态栏开头，几秒后消失；便于测试）。</summary>
    public string? Notice => DateTime.Now < _noticeExpires ? _notice : null;

    private void ShowNotice(string text, Control anchor)
    {
        _notice = text;
        _noticeExpires = DateTime.Now.AddSeconds(8);
        _toolbarTips.Show(text, anchor, 0, anchor.Height, 5000);
        UpdateStatus();
    }

    /// <summary>重新开始一次测量（应用预设 / 全部清空）：已设置的起始时间更新为当前时间，否则新数据会排在旧的起始时间上。</summary>
    private void RestartStartTimeIfSet()
    {
        if (_plot.DefaultStartTime is not null)
            UseCurrentTimeAsStartTime();
    }

    private void ApplyAxisLabel() => _plot.Plot.XLabel(_plot.XAxisUnit switch
    {
        XAxisUnit.RelativeTime => "相对时间（各曲线按 起始时间 + 下标 ÷ 点每秒 在时间上对齐）",
        XAxisUnit.AbsoluteTime => "绝对时间（各曲线按 起始时间 + 下标 ÷ 点每秒 在时间上对齐）",
        _ => "数据点序号（各曲线独立，X 轴取最长的可见曲线）",
    });

    /// <summary>状态栏中的 X 坐标：点数单位为序号，相对时间为秒，绝对时间为时刻。</summary>
    private string FormatX(double x) => _plot.XAxisUnit switch
    {
        XAxisUnit.RelativeTime => x.ToString("0.###", CultureInfo.InvariantCulture) + " s",
        XAxisUnit.AbsoluteTime when _plot.TimeReference is { } reference && Math.Abs(x) < 1e9 =>
            reference.AddSeconds(x).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        _ => x.ToString("0.#", CultureInfo.InvariantCulture),
    };

    /// <summary>UI 定时器：给“UI 定时器”写入方式的曲线按速率补齐到期的数据（在 UI 线程上同步写入）。</summary>
    private void FeedUiCurves()
    {
        double now = _clock.Elapsed.TotalSeconds;

        foreach (CurveFeeder feeder in _feeders)
            feeder.FeedFromUiTimer(now);
    }

    private void UpdateStatus()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double seconds = Math.Max(0.001, now - _lastStatusSeconds);
        long written = _feeders.Sum(f => f.Written);
        long renders = _plot.Plot.RenderManager.RenderCount;

        double writeRate = (written - _lastWritten) / seconds;
        double fps = (renders - _lastRenderCount) / seconds;

        _lastWritten = written;
        _lastRenderCount = renders;
        _lastStatusSeconds = now;

        UpdateRows();

        ScottPlot.AxisLimits limits = _plot.Plot.Axes.GetLimits();
        string axis = limits.IsReal
            ? string.Create(CultureInfo.InvariantCulture, $"X: [{FormatX(limits.Left)}, {FormatX(limits.Right)}]  Y: [{limits.Bottom:0.###}, {limits.Top:0.###}]")
            : "X / Y: -";

        long stored = _plot.Series.Sum(s => (long)s.DataCount);
        int visible = _plot.Series.Count(s => s.IsVisible && s.DataCount > 0);

        _status.Text = (Notice is { } notice ? "⚠ " + notice + " · " : string.Empty) +
            $"曲线 {_feeders.Count} 条（可见且有数据 {visible} 条）· 写入 {writeRate:N0} 点/秒 · 绘制 {fps:F1} FPS · " +
            $"内存中 {stored:N0} 点 · 模式 {ModeText(_plot.Mode)} · X 轴 {_xAxisUnit.Text} · {axis}";
    }

    private void UpdateRows()
    {
        _list.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _list.Items)
                UpdateRow(item);
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private static void UpdateRow(ListViewItem item)
    {
        if (item.Tag is not CurveFeeder feeder)
            return;

        CurveParameters p = feeder.Parameters;
        PlotSeries series = feeder.Series;

        item.Text = series.Name;
        item.ForeColor = ToSystemColor(series.Color);
        item.SubItems[1].Text = WaveformText(p.Waveform);
        item.SubItems[2].Text = p.Rate.ToString("N0", CultureInfo.CurrentCulture);
        item.SubItems[3].Text = (p.Writer == CurveWriter.UiTimer ? "UI" : "后台") + (feeder.IsRunning ? "" : "（停）");
        item.SubItems[4].Text = series.IsRemoved ? "-" : series.DataCount.ToString("N0", CultureInfo.CurrentCulture);
        item.SubItems[5].Text = double.IsNaN(series.LatestValue) ? "-" : series.LatestValue.ToString(Math.Abs(series.LatestValue) >= 1000 ? "0.#" : "0.###", CultureInfo.CurrentCulture);
    }

    private static string ModeText(ScrollingMode mode) => mode switch
    {
        ScrollingMode.Stacked => "堆叠",
        ScrollingMode.Follow => "跟随",
        _ => "手动",
    };

    private static string WaveformText(Waveform waveform) => waveform switch
    {
        Waveform.Sine => "正弦",
        Waveform.Square => "方波",
        Waveform.Sawtooth => "锯齿",
        Waveform.RandomWalk => "随机游走",
        _ => "噪声",
    };

    private static ScottPlot.Color PaletteColor(int index) => (index % 10) switch
    {
        0 => ScottPlot.Colors.Blue,
        1 => ScottPlot.Colors.Red,
        2 => ScottPlot.Colors.Green,
        3 => ScottPlot.Colors.Orange,
        4 => ScottPlot.Colors.Purple,
        5 => ScottPlot.Colors.Cyan,
        6 => ScottPlot.Colors.Magenta,
        7 => ScottPlot.Colors.Brown,
        8 => ScottPlot.Colors.Olive,
        _ => ScottPlot.Colors.Navy,
    };

    private static Color ToSystemColor(ScottPlot.Color color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    #endregion

    /// <summary>无人值守冒烟：运行 seconds 秒后自动截图并关闭（供自动化验证使用）。</summary>
    public void RunSmokeTest(int seconds, string snapshotPath)
    {
        System.Windows.Forms.Timer timer = new() { Interval = Math.Max(1, seconds) * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdateStatus();

            try
            {
                string fullPath = Path.GetFullPath(snapshotPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                WindowSnapshot.Save(this, fullPath);
            }
            catch (Exception exception)
            {
                _status.Text = "截图失败：" + exception.Message;
            }

            Close();
        };
        timer.Start();
    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _uiFeedTimer.Stop();
        _statusTimer.Stop();

        foreach (CurveFeeder feeder in _feeders)
            feeder.Stop();

        base.OnFormClosing(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (CurveFeeder feeder in _feeders)
                feeder.Stop();

            _uiFeedTimer.Dispose();
            _statusTimer.Dispose();
            _toolbarTips.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>波形。</summary>
public enum Waveform
{
    /// <summary>正弦（叠加少量噪声）。</summary>
    Sine,

    /// <summary>方波。</summary>
    Square,

    /// <summary>锯齿波。</summary>
    Sawtooth,

    /// <summary>随机游走（范围不断变化，用来观察 Y 轴自动缩放）。</summary>
    RandomWalk,

    /// <summary>均匀噪声。</summary>
    Noise,
}

/// <summary>写入方式。</summary>
public enum CurveWriter
{
    /// <summary>UI 线程定时器写入（控件同步生效）。</summary>
    UiTimer,

    /// <summary>后台线程写入（控件按帧合并）。</summary>
    BackgroundThread,
}

/// <summary>曲线数据源参数。</summary>
public sealed record CurveParameters(
    Waveform Waveform,
    double Amplitude,
    double Offset,
    int Rate,
    CurveWriter Writer,
    ScottPlot.Color Color,
    float LineWidth = 1f);

/// <summary>一条曲线的数据源：按参数生成波形，从 UI 定时器或后台线程写入 <see cref="PlotSeries"/>。</summary>
public sealed class CurveFeeder
{
    private const double Frequency = 0.2; // 每秒 0.2 个周期

    private readonly Random _random;
    private readonly double _phase;
    private volatile CurveParameters _parameters;
    private volatile bool _stop = true;
    private Thread? _thread;
    private long _sample;
    private long _written;
    private double _walk;
    private double _uiLastSeconds = double.NaN;
    private double _uiCarry;
    private double[] _uiBuffer = new double[64];

    internal CurveFeeder(PlotSeries series, CurveParameters parameters, int seed)
    {
        Series = series;
        _parameters = parameters;
        _random = new Random(seed);
        _phase = _random.NextDouble(); // 随机初相（单位：周期），同偏移的曲线不会完全重叠
    }

    /// <summary>写入的曲线。</summary>
    public PlotSeries Series { get; }

    /// <summary>当前参数（修改后下一批数据生效）。</summary>
    public CurveParameters Parameters
    {
        get => _parameters;
        set => _parameters = value;
    }

    /// <summary>是否正在写入。</summary>
    public bool IsRunning => !_stop;

    /// <summary>累计写入的点数。</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>开始写入（后台线程方式会创建线程；UI 定时器方式由窗体的定时器驱动）。</summary>
    public void Start()
    {
        if (!_stop || Series.IsRemoved)
            return;

        _stop = false;
        _uiLastSeconds = double.NaN;

        if (_parameters.Writer == CurveWriter.BackgroundThread)
        {
            _thread = new Thread(BackgroundLoop)
            {
                IsBackground = true,
                Name = $"多曲线测试：{Series.Name}",
            };
            _thread.Start();
        }
    }

    /// <summary>停止写入（后台线程方式会等待线程结束）。</summary>
    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }

    /// <summary>由窗体的 UI 定时器调用：UI 定时器写入方式的曲线在这里补齐到期的数据。</summary>
    internal void FeedFromUiTimer(double nowSeconds)
    {
        if (_stop || _parameters.Writer != CurveWriter.UiTimer)
            return;

        if (double.IsNaN(_uiLastSeconds))
        {
            _uiLastSeconds = nowSeconds;
            return;
        }

        int count = DueCount(nowSeconds - _uiLastSeconds, ref _uiCarry);
        _uiLastSeconds = nowSeconds;

        if (count <= 0)
            return;

        if (count > _uiBuffer.Length)
            _uiBuffer = new double[count];

        Write(count, _uiBuffer);
    }

    private void BackgroundLoop()
    {
        Stopwatch clock = Stopwatch.StartNew();
        double last = 0;
        double carry = 0;
        double[] batch = new double[512];

        while (!_stop)
        {
            double now = clock.Elapsed.TotalSeconds;
            int count = DueCount(now - last, ref carry);
            last = now;

            if (count > 0)
            {
                if (count > batch.Length)
                    batch = new double[count];

                Write(count, batch);
            }

            Thread.Sleep(1);
        }
    }

    private int DueCount(double elapsedSeconds, ref double carry)
    {
        double due = (_parameters.Rate * elapsedSeconds) + carry;
        int count = (int)Math.Min(due, 100_000);
        carry = due - Math.Floor(due);
        return count;
    }

    private void Write(int count, double[] buffer)
    {
        CurveParameters p = _parameters;

        for (int i = 0; i < count; i++)
            buffer[i] = NextValue(p);

        // UI 线程上同步写入；后台线程上进入待处理列表，由控件按帧合并
        Series.AddValues(buffer.AsSpan(0, count));
        Interlocked.Add(ref _written, count);
    }

    private double NextValue(CurveParameters p)
    {
        _sample++;
        double t = _sample / (double)Math.Max(1, p.Rate);
        double phase = (t * Frequency) + _phase;

        return p.Waveform switch
        {
            Waveform.Sine => p.Offset + (p.Amplitude * Math.Sin(phase * 2 * Math.PI)) + (p.Amplitude * 0.05 * (_random.NextDouble() - 0.5)),
            Waveform.Square => p.Offset + (p.Amplitude * Math.Sign(Math.Sin(phase * 2 * Math.PI))),
            Waveform.Sawtooth => p.Offset + (p.Amplitude * ((2 * (phase - Math.Floor(phase))) - 1)),
            Waveform.RandomWalk => p.Offset + (_walk += p.Amplitude * 0.05 * (_random.NextDouble() - 0.5)),
            _ => p.Offset + (p.Amplitude * 2 * (_random.NextDouble() - 0.5)),
        };
    }
}
