using System.Runtime.InteropServices;

namespace ScottPlotEx.Controls;

/// <summary>
/// 控件中的一条曲线（对应 imc 曲线窗口中的一个通道），由 <see cref="ScrollingPlotControl.AddSeries"/> 创建。
/// 各曲线的数据彼此独立。数据点的 X 坐标取决于控件的 <see cref="ScrollingPlotControl.XAxisUnit"/>：
/// 点数单位下第 n 个数据点 X = n（从 1 开始）；时间单位下第 i 个数据点（下标从 0 开始）X =（<see cref="StartTime"/> − 参考时刻）+ i × <see cref="SampleInterval"/>（秒）。
/// <list type="bullet">
/// <item>数据接口（<see cref="AddValue"/> / <see cref="AddValues(ReadOnlySpan{double})"/> / <see cref="Clear"/>）可在任意线程调用：
/// UI 线程上同步生效；非 UI 线程上先进入本曲线的待处理列表，由控件按帧整批写入
/// （所有曲线共用同一条合并消息，同一线程内的写入与清空顺序保持不变）。</item>
/// <item>外观与结构接口（<see cref="Name"/> / <see cref="Color"/> / <see cref="LineWidth"/> / <see cref="IsVisible"/> / <see cref="Remove"/>）
/// 在 UI 线程上执行，从其它线程调用时会同步切换到 UI 线程。</item>
/// </list>
/// </summary>
public sealed class PlotSeries
{
    private readonly ScrollingPlotControl _owner;
    private string _name;
    private ScottPlot.Color _color;
    private float _lineWidth = 1f;
    private bool _isVisible = true;

    /// <summary>用户通过 <see cref="Signal"/> 直接修改的线型 / 标记样式（Reset 重建图元时原样恢复）。</summary>
    private ScottPlot.LineStyle? _lineStyle;
    private ScottPlot.MarkerStyle? _markerStyle;

    /// <summary>本曲线自己的采样间隔（秒），0 表示使用控件的 <see cref="ScrollingPlotControl.DefaultSampleInterval"/>。</summary>
    private double _intervalSeconds;

    /// <summary>本曲线自己的起始时间，null 表示使用控件的 <see cref="ScrollingPlotControl.DefaultStartTime"/>。</summary>
    private DateTime? _startTime;

    internal PlotSeries(ScrollingPlotControl owner, string name, ScottPlot.Color color, bool isDefault)
    {
        _owner = owner;
        _name = name;
        _color = color;
        IsDefault = isDefault;
    }

    #region 内部状态（由控件管理）

    /// <summary>本曲线的数据（含极值金字塔），只在 UI 线程上读写。</summary>
    internal PlotDataBuffer Data { get; } = new();

    /// <summary>当前挂在 Plot 上的图元（Reset 替换 Plot 后会重新创建）。</summary>
    internal ScottPlot.Plottables.Signal? Plottable { get; set; }

    /// <summary>是否为默认曲线（单曲线接口 AddValue / AddValues 写入的曲线，不能删除）。</summary>
    internal bool IsDefault { get; }

    // 跨线程待处理数据：由控件的待处理锁保护；Draining* 只在 UI 线程的合并写入中使用
    internal List<double> PendingValues = [];
    internal List<double> DrainingValues = [];
    internal bool PendingClear;
    internal bool DrainClear;

    /// <summary>
    /// 第一个数据点写入控件的时刻（起始时间与默认起始时间都没设置时作为起始时间）。
    /// 清空本曲线后复位，下一批数据到达时重新记录。只在 UI 线程上读写。
    /// </summary>
    internal DateTime? ArrivalStartTime { get; set; }

    /// <summary>生效的采样间隔（秒）：本曲线设置的值，否则为控件的默认采样间隔。</summary>
    internal double IntervalSeconds => _intervalSeconds > 0 ? _intervalSeconds : _owner.DefaultSampleIntervalSeconds;

    /// <summary>本曲线自己设置的起始时间（null 表示没有设置）。只在 UI 线程上修改。</summary>
    internal DateTime? OwnStartTime
    {
        get => _startTime;
        set => _startTime = value;
    }

    /// <summary>明确设置的起始时间：本曲线设置的值 → 控件的默认起始时间；都没有时为 null（绝对时间单位只认它）。</summary>
    internal DateTime? ExplicitStartTime => _startTime ?? _owner.DefaultStartTime;

    /// <summary>
    /// 生效的起始时间：本曲线设置的值 → 控件的默认起始时间 → 第一个数据点写入的时刻；都没有时为 null。
    /// 绝对时间单位下不使用第一个数据点写入的时刻（时刻必须明确设置）。
    /// </summary>
    internal DateTime? EffectiveStartTime =>
        ExplicitStartTime ?? (_owner.XAxisUnit == XAxisUnit.AbsoluteTime ? null : ArrivalStartTime);

    /// <summary>起始时间是否由“第一个数据点写入的时刻”决定（本曲线与控件都没有设置起始时间）。</summary>
    internal bool UsesArrivalStartTime => _startTime is null && _owner.DefaultStartTime is null;

    /// <summary>
    /// 绝对时间单位下是否必须有明确的起始时间：除了“还没有数据的默认曲线”（没人用的默认曲线不妨碍切换到绝对时间）之外都必须有。
    /// </summary>
    internal bool RequiresAbsoluteStartTime => !(IsDefault && Data.Count == 0);

    #endregion

    /// <summary>所属控件。</summary>
    public ScrollingPlotControl Owner => _owner;

    /// <summary>曲线是否已从控件中删除（删除后写入的数据会被忽略）。</summary>
    public bool IsRemoved { get; internal set; }

    /// <summary>曲线名称（显示在图例中；为空时不出现在图例里）。</summary>
    public string Name
    {
        get => Plottable?.LegendText ?? _name;
        set => _owner.RunOnUiThread(() =>
        {
            _name = value ?? string.Empty;
            ApplyStyle();
            _owner.RequestRender();
        });
    }

    /// <summary>线条颜色。</summary>
    public ScottPlot.Color Color
    {
        get => Plottable?.Color ?? _color;
        set => _owner.RunOnUiThread(() =>
        {
            _color = value;
            ApplyStyle();
            _owner.RequestRender();
        });
    }

    /// <summary>线宽（默认 1：SkiaSharp 对 1 像素线走快速光栅化，数据密集时明显更快）。</summary>
    public float LineWidth
    {
        get => Plottable?.LineWidth ?? _lineWidth;
        set => _owner.RunOnUiThread(() =>
        {
            _lineWidth = value;
            ApplyStyle();
            _owner.RequestRender();
        });
    }

    /// <summary>是否显示。隐藏的曲线不绘制，也不参与坐标轴的自动计算（X 轴长度与 Y 轴范围）。</summary>
    public bool IsVisible
    {
        get => Plottable?.IsVisible ?? _isVisible;
        set => _owner.RunOnUiThread(() =>
        {
            _isVisible = value;
            ApplyStyle();
            _owner.OnSeriesLayoutChanged();
        });
    }

    /// <summary>
    /// 采样间隔（相邻两个数据点的时间间隔），在相对时间 / 绝对时间单位下决定数据点的 X 坐标。
    /// 默认取控件的 <see cref="ScrollingPlotControl.DefaultSampleInterval"/>（读取时返回生效的值）；
    /// 设为 <see cref="TimeSpan.Zero"/> 恢复为跟随控件的默认值。点数单位下不影响 X 坐标。
    /// <see cref="TimeSpan"/> 的精度为 100 ns：间隔不是 100 ns 整数倍时（例如 1/3 ms）请改用 <see cref="SampleRate"/>，避免长时间运行后累积误差。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">设置为负值时抛出。</exception>
    public TimeSpan SampleInterval
    {
        get => TimeSpan.FromTicks((long)Math.Round(IntervalSeconds * TimeSpan.TicksPerSecond));
        set
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "采样间隔不能为负；设为 TimeSpan.Zero 表示使用控件的默认采样间隔。");

            SetIntervalSeconds(value.Ticks / (double)TimeSpan.TicksPerSecond);
        }
    }

    /// <summary>
    /// 采样率（点/秒）= 1 / <see cref="SampleInterval"/>。按双精度保存，不受 <see cref="TimeSpan"/> 100 ns 精度的限制
    /// （例如 3000 点/秒的间隔 333.33… µs 不是 100 ns 的整数倍）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">设置为非正数或非有限值时抛出。</exception>
    public double SampleRate
    {
        get => 1.0 / IntervalSeconds;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "采样率必须是正的有限值。");

            SetIntervalSeconds(1.0 / value);
        }
    }

    /// <summary>
    /// 起始时间（第一个数据点的时刻），在相对时间 / 绝对时间单位下决定数据点的 X 坐标。
    /// 读取时返回生效的值：本曲线设置的值 → 控件的 <see cref="ScrollingPlotControl.DefaultStartTime"/> →
    /// 第一个数据点写入控件的时刻（清空后重新记录；绝对时间单位下不使用）；都没有时为 null。设为 null 恢复默认。
    /// 时间按原样比较与显示（不做时区转换），请统一使用本地时间或 UTC。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 绝对时间单位下设为 null、而控件又没有 <see cref="ScrollingPlotControl.DefaultStartTime"/> 时抛出（曲线不能没有起始时间）。
    /// 从其它线程设置时同样抛给调用方。
    /// </exception>
    public DateTime? StartTime
    {
        get => EffectiveStartTime;
        set => _owner.RunOnUiThread(() => _owner.SetSeriesStartTime(this, value));
    }

    /// <summary>
    /// 绝对时间单位下因为没有起始时间而被拒收的数据点数量。只可能出现在“控件与它自己都没有设置起始时间”的默认曲线上：
    /// 没有数据的默认曲线不妨碍切换到绝对时间，但之后写给它的数据无法换算成时刻，控件不接收（不抛异常，跨线程写入也安全）。
    /// 清空本曲线时归零。
    /// </summary>
    public int RejectedValueCount { get; internal set; }

    /// <summary>已添加的数据点数量。</summary>
    public int DataCount => Data.Count;

    /// <summary>
    /// 最新数据点的 X 坐标：点数单位下从 1 开始、等于数据点数量；时间单位下为距参考时刻的秒数。
    /// 没有数据时为第一个点之前一个数据点宽度的位置（点数单位下为 0）。
    /// </summary>
    public double LatestX => Data.LatestX;

    /// <summary>第 <paramref name="index"/> 个数据点（下标从 0 开始）在当前 X 轴单位下的 X 坐标。</summary>
    public double GetX(int index) => Data.GetX(index);

    /// <summary>最新数据点的 Y 值，没有数据时为 <see cref="double.NaN"/>。</summary>
    public double LatestValue => Data.LatestValue;

    /// <summary>被忽略的非有限值数量（NaN / Infinity）。</summary>
    public int SkippedValueCount => Data.SkippedCount;

    /// <summary>全部数据的 Y 值（内部实时列表，只应在 UI 线程上读取）。</summary>
    public IReadOnlyList<double> Values => Data.Values;

    /// <summary>用于绘制本曲线的 ScottPlot 图元（可修改更多样式；删除曲线后为 null）。</summary>
    public ScottPlot.Plottables.Signal? Signal => Plottable;

    /// <summary>添加一个数据点。可以在任意线程调用。</summary>
    /// <param name="y">Y 值，非有限值会被忽略。</param>
    public void AddValue(double y) => _owner.AppendValue(this, y);

    /// <summary>批量添加数据点。可以在任意线程调用。</summary>
    /// <param name="values">Y 值集合，非有限值会被忽略。</param>
    public void AddValues(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        switch (values)
        {
            // 数组与 List 直接按 Span 处理，不再先复制一份
            case double[] array:
                AddValues(new ReadOnlySpan<double>(array));
                return;
            case List<double> list:
                AddValues(CollectionsMarshal.AsSpan(list));
                return;
        }

        // 其它序列（例如 LINQ 延迟序列）先在调用线程上物化一次：
        // 跨线程时不在 UI 线程上枚举调用方的数据，也避免序列在枚举期间引用控件的数据而失效
        double[] buffer = [.. values];
        AddValues(new ReadOnlySpan<double>(buffer));
    }

    /// <summary>批量添加数据点。可以在任意线程调用。</summary>
    /// <param name="values">Y 值数组。</param>
    public void AddValues(params double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        AddValues(new ReadOnlySpan<double>(values));
    }

    /// <summary>
    /// 批量添加数据点。可以在任意线程调用：UI 线程上直接写入、不复制调用方的数据；
    /// 非 UI 线程上会先复制到待处理列表（调用返回后即可复用传入的缓冲区）。
    /// </summary>
    /// <param name="values">Y 值，非有限值会被忽略。</param>
    public void AddValues(ReadOnlySpan<double> values) => _owner.AppendValues(this, values);

    /// <summary>清空本曲线的数据（其它曲线不受影响）。可以在任意线程调用。</summary>
    public void Clear() => _owner.ClearSeries(this);

    /// <summary>从控件中删除本曲线。默认曲线不能删除（不需要时可设置 <see cref="IsVisible"/> = false）。</summary>
    /// <exception cref="InvalidOperationException">删除默认曲线时抛出。</exception>
    public void Remove() => _owner.RemoveSeries(this);

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "(默认曲线)" : Name;

    private void SetIntervalSeconds(double seconds) => _owner.RunOnUiThread(() =>
    {
        _intervalSeconds = seconds;
        _owner.OnSeriesXMappingChanged();
    });

    /// <summary>
    /// 重新挂接到新 Plot 之前调用：用户可能直接通过 <see cref="Signal"/> 修改过样式，
    /// 先把它们记下来，挂接到新图元后原样恢复。
    /// </summary>
    internal void CaptureStyle()
    {
        if (Plottable is null)
            return;

        _name = Plottable.LegendText ?? string.Empty;
        _color = Plottable.Color;
        _lineWidth = Plottable.LineWidth;
        _isVisible = Plottable.IsVisible;

        // 线型（实线 / 虚线 / 点线……）与标记样式保存在独立的样式对象里，
        // 只记录颜色 / 线宽会丢失它们；直接保存引用，重建图元时原样挂接。
        _lineStyle = Plottable.LineStyle;
        _markerStyle = Plottable.MarkerStyle;
    }

    /// <summary>把样式应用到当前图元。</summary>
    internal void ApplyStyle()
    {
        if (Plottable is null)
            return;

        if (_lineStyle is not null)
            Plottable.LineStyle = _lineStyle;

        if (_markerStyle is not null)
            Plottable.MarkerStyle = _markerStyle;

        Plottable.LegendText = _name;
        Plottable.Color = _color;
        Plottable.LineWidth = _lineWidth;
        Plottable.IsVisible = _isVisible;
    }
}
