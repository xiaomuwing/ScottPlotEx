using ScottPlot;
using ScottPlot.Interactivity;
using ScottPlot.WinForms;
using SkiaSharp;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using PlotCursor = ScottPlot.Cursor;

namespace ScottPlotEx.Controls;

/// <summary>
/// 用户自定义控件：内部嵌入自绘画布（SkiaSharp 直接渲染进 GDI DIB，BitBlt 到窗口），并实现 <see cref="IPlotControl"/> 的全部接口。
/// 在此基础上提供“堆叠 / 跟随 / 手动”三种坐标轴滚动模式：
/// <list type="number">
/// <item>堆叠模式（默认）：X 轴 0 ~ 数据点数量 + 2，Y 轴为全部数据的最大值/最小值各向外 5%。</item>
/// <item>跟随模式：X 轴为最新 30% 的数据（最大值同样为 数据点数量 + 2），Y 轴为当前显示数据的最大值/最小值各向外 5%。</item>
/// <item>放大局部数据后曲线停止滚动，重新选择堆叠/跟随模式即可恢复。</item>
/// <item>鼠标手势（拖动 / 框选 / 缩放）进行中暂停自动滚动：新数据不会把视图拉回自动窗口，
/// 因此不会出现“鼠标一操作就卡一下”的来回抖动，也不会丢掉用户的拖动结果。</item>
/// </list>
/// 支持多条曲线：<see cref="AddSeries"/> 添加曲线（<see cref="PlotSeries"/>），各曲线数据独立；
/// 坐标轴按所有可见曲线计算（X 轴取最新 X 最大的曲线，Y 轴取各曲线可见范围极值的并集）。
/// X 轴单位（<see cref="XAxisUnit"/>）可选点数（默认，第 n 个点 X = n）、相对时间、绝对时间：
/// 时间单位下各曲线按“起始时间 + 下标 × 采样间隔”在时间上对齐（每条曲线可以有自己的采样间隔）。
/// 单曲线接口（<see cref="AddValue"/> / <see cref="AddValues(ReadOnlySpan{double})"/> / <see cref="Values"/> / <see cref="DataCount"/> 等）
/// 读写的是始终存在的 <see cref="DefaultSeries"/>（默认曲线）。
/// </summary>
[ToolboxItem(true)]
[DefaultEvent(nameof(ModeChanged))]
[DefaultProperty(nameof(Mode))]
[Description("嵌入 ScottPlot 的实时曲线控件，支持“堆叠 / 跟随 / 手动”三种滚动模式。")]
public partial class ScrollingPlotControl : UserControl, IPlotControl
{
    /// <summary>判定“拖动”的最小像素距离，与 ScottPlot 内部阈值保持一致。</summary>
    public const int DragThresholdPixels = 5;

    /// <summary>默认绘图字体（支持中文，避免中文显示为方块）。</summary>
    public const string DefaultFontName = "Microsoft YaHei UI";

    /// <summary>新曲线未指定颜色时依次使用的调色板（默认曲线固定为蓝色）。</summary>
    private static readonly ScottPlot.Palettes.Category10 SeriesPalette = new();

    private readonly ScrollingAxisOptions _axisOptions = new();
    private readonly PlotCanvas _canvas;
    private readonly ChinesePlotMenu _plotMenu;
    private readonly List<ContextMenuStrip> _contextMenus = [];

    /// <summary>
    /// 创建控件时的线程。句柄创建之前 <see cref="Control.InvokeRequired"/> 总是返回 false，
    /// 采集线程会被误判成 UI 线程而直接写 List / Plot；这里用它判断“句柄创建前是否在 UI 线程上”，
    /// 非 UI 线程的写入先进入待处理列表，句柄创建时再统一处理。
    /// </summary>
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    /// <summary>句柄创建前从其它线程排队、创建后要在 UI 线程上执行的界面操作（样式、删除等）。</summary>
    private readonly Queue<Action> _pendingUiActions = new();

    /// <summary>句柄创建前从其它线程添加的曲线：句柄创建后再挂接到 Plot（避免后台线程操作 Plot）。</summary>
    private readonly List<PlotSeries> _pendingAttach = [];

    private readonly Action _attachPendingAction;
    private readonly Action _runPendingUiActionsAction;

    /// <summary>全部曲线（含默认曲线，默认曲线始终在第一位）。只在 UI 线程上修改，修改时持有待处理锁。</summary>
    private readonly List<PlotSeries> _series = [];
    private readonly PlotSeries _defaultSeries;
    private int _seriesCreated;
    private bool _showLegend;

    /// <summary>坐标轴计算用的可见曲线 X 范围（复用，避免每帧分配）。只在 UI 线程上使用。</summary>
    private readonly List<SeriesXExtent> _visibleExtents = [];

    // X 轴单位与时间映射：时间单位下 X = 距参考时刻（全部曲线中最早的起始时间）的秒数
    private XAxisUnit _xAxisUnit = XAxisUnit.Samples;
    private TimeSpan _defaultSampleInterval = TimeSpan.FromSeconds(1);
    private double _defaultSampleIntervalSeconds = 1.0;
    private DateTime? _defaultStartTime;
    private DateTime? _timeReference;
    private readonly DateTime _createdAt = DateTime.Now;
    private TimeAxisTickGenerator? _timeTickGenerator;

    /// <summary>切换到时间单位之前 X 轴的刻度生成器（切回点数单位时恢复）。</summary>
    private ITickGenerator? _tickGeneratorBeforeTimeUnit;

    private ScrollingMode _mode = ScrollingMode.Stacked;
    private ScrollingMode _lastAutoScrollMode = ScrollingMode.Stacked;

    /// <summary>
    /// 堆叠模式固定的 X 轴原点。设置显示百分比时换算一次，之后不随数据刷新移动，
    /// 因此曲线是“从固定原点向右堆叠”，而不是像跟随模式那样跟着最新数据滑动。
    /// </summary>
    private double _stackedOriginX;
    private double _xOffset;
    private bool _applyingLimits;

    private MouseButtons _gestureButton = MouseButtons.None;
    private Point _gestureStartPoint;
    private bool _gestureMoved;
    private AxisLimits? _limitsAtGestureStart;
    private AxisLimits? _lastKnownLimits;

    /// <summary>
    /// 排队中的合并写入在这么久之后仍未完成（控件不可见、渲染被关闭等情况下等不到渲染完成通知），
    /// 采集线程的下一次写入会重新投递，保证数据不会滞留。
    /// </summary>
    private static readonly TimeSpan PendingFlushTimeout = TimeSpan.FromMilliseconds(100);

    // 跨线程写入：采集线程只往各曲线的待处理列表里追加，UI 线程按帧整批取走（见 PostPendingFlush）
    private readonly object _pendingLock = new();
    private readonly Action _flushPendingAction;

    /// <summary>0 表示没有排队中的合并写入；否则为投递时刻（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
    private long _flushPostedAt;

    /// <summary>句柄创建前的合并写入投递失败了：句柄创建时要补投一次（见 <see cref="OnHandleCreated"/>）。</summary>
    private int _flushRetryOnHandleCreated;

    /// <summary>合并写入已执行、正在等待这一帧画完（仅 UI 线程访问）。</summary>
    private bool _flushAwaitingRender;

    // 帧率上限（只限制跨线程写入触发的合并重绘）：两次合并写入之间至少间隔这么多 Stopwatch 刻度，0 表示不限制
    private int _maxFramesPerSecond;
    private long _minFlushIntervalTicks;
    private long _lastFlushTimestamp;
    private HighResolutionTimer? _frameTimer;

    private Plot? _renderHookedPlot;

    /// <summary>
    /// 自动滚动使用的默认 X / Y 轴（缓存引用，避免每次读写坐标轴都调用
    /// <see cref="ScottPlot.AxisManager.Bottom"/> / <see cref="ScottPlot.AxisManager.Left"/> 的 LINQ 查找：
    /// 这两个属性每次调用都会分配约 40 字节，逐点写入时是主要的 GC 来源）。
    /// 用户替换 / 删除默认坐标轴时由 <see cref="EnsureAxesCurrent"/> 自动重新解析。
    /// </summary>
    private IXAxis? _xAxis;
    private IYAxis? _yAxis;

    /// <summary>
    /// 设计器兜底模式：SkiaSharp / ScottPlot 初始化失败时不创建 Plot，只显示占位说明。
    /// 此时 <see cref="_canvas"/> / <see cref="_plotMenu"/> / <see cref="_defaultSeries"/> 为 null，
    /// 所有公开成员都必须先判断这个标志。
    /// </summary>
    private bool _designerFallback;

    /// <summary>设计器兜底模式下保存 <see cref="ShowModeMenuItems"/> 的值（没有菜单对象可写入）。</summary>
    private bool _showModeItemsFallback = true;

    /// <summary>当前控件是否处于设计器兜底（占位）模式。</summary>
    internal bool IsDesignerFallback => _designerFallback;

    /// <summary>
    /// 创建控件：内嵌 ScottPlot 控件、挂接中文右键菜单与鼠标观察器。
    /// <para>
    /// 在 Visual Studio 设计器中，SkiaSharp 原生库可能无法加载；此时会降级为占位控件，
    /// 保证控件可以被拖放到 Form 上并正常生成设计器代码。运行时资源正常时行为不变。
    /// </para>
    /// </summary>
    public ScrollingPlotControl()
    {
        _flushPendingAction = FlushPendingValues;
        _attachPendingAction = AttachPendingSeries;
        _runPendingUiActionsAction = RunPendingUiActions;

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;
        BackColor = SystemColors.Control;

        _defaultSeries = null!;
        _canvas = null!;
        _plotMenu = null!;

        if (DesignerSupport.ForceDesignerFallback)
        {
            InitializeDesignerFallback(null);
            return;
        }

        try
        {
            // 默认曲线：单曲线接口（AddValue / AddValues / Values / DataCount ……）读写的就是它；名称为空，不出现在图例中
            _defaultSeries = new PlotSeries(this, string.Empty, Colors.Blue, isDefault: true);
            _series.Add(_defaultSeries);
            _seriesCreated = 1;

            // 自绘画布：SkiaSharp 直接渲染进 GDI DIB，WM_PAINT 中一次 BitBlt 到窗口。
            // 画布自身已是 Opaque + AllPaintingInWmPaint（不擦背景），DIB 就是后台缓冲，
            // 因此不再需要原先对 FormsPlot 的双缓冲 / 跳过背景擦除处理。
            _canvas = new PlotCanvas
            {
                Dock = DockStyle.Fill,
                Name = "ScottPlot",
            };
            Controls.Add(_canvas);

            // 中文右键菜单：菜单项与 ScottPlot 自带内容完全一致，并增加“显示帮助”
            _plotMenu = new ChinesePlotMenu(this);
            _canvas.Menu = _plotMenu;

            // 焦点丢失：ScottPlot 默认会立刻同步重绘整帧（Refresh()），而焦点变化往往正好发生在
            // 用户刚开始操作鼠标的那一刻（例如单击窗口把它激活），同步整帧重绘会造成一次明显卡顿。
            // 这里保留“焦点丢失即终止拖动”的语义，只把同步重绘改成合并重绘（不阻塞消息处理）。
            _canvas.UserInputProcessor.LostFocusAction = control =>
            {
                ScottPlot.Interactivity.UserInputProcessor.ResetState(control);
                RequestRender();
            };

            // 只观察鼠标事件：ScottPlot 自身先处理输入（画布在触发事件前已转发给 UserInputProcessor），
            // 我们随后判断视图是否发生变化
            _canvas.MouseDown += OnPlotMouseDown;
            _canvas.MouseMove += OnPlotMouseMove;
            _canvas.MouseUp += OnPlotMouseUp;
            _canvas.MouseWheel += OnPlotMouseWheel;

            ApplyDefaultFont();
            AttachDataToCurrentPlot();

            // 构造完成时就建立“稳定空视图”（X 0~1、Y -0.5~0.5）：
            // ScottPlot 的坐标轴在首次渲染或显式设置范围之前处于“未设置”状态，此时
            // Plot.Axes.GetLimits() 返回 (Left=+∞, Right=-∞)（IsReal = false）。
            // 如果只依赖 AddValue / ClearData / Reset 去建立空视图，那么新建（或释放后重建）的控件
            // 在第一个数据点到达、或首次绘制完成之前会暴露一个非法的坐标轴范围；
            // 这里与 AddValue / ClearData / Reset 保持一致，让控件从构造完成那一刻起就同时拥有
            // “有效的空视图”和“可用的回滚基准”（_lastKnownLimits）。
            ApplyAutoScroll();
            RememberLimits();
        }
        catch (Exception exception) when (DesignerSupport.IsNativeLibraryFailure(exception) || DesignerSupport.IsDesignTime)
        {
            // 设计器 / 缺少 SkiaSharp 原生库：清掉半初始化状态，显示占位控件，保证拖放不会报错。
            _series.Clear();
            _seriesCreated = 0;

            if (_canvas is not null)
            {
                try
                {
                    Controls.Remove(_canvas);
                    _canvas.Dispose();
                }
                catch
                {
                    // 半初始化状态，清理失败也不能影响设计器
                }
            }

            _defaultSeries = null!;
            _canvas = null!;
            _plotMenu = null!;

            InitializeDesignerFallback(exception);
        }
    }

    /// <summary>设计器 / 缺少原生 SkiaSharp 时使用的占位视图。</summary>
    private void InitializeDesignerFallback(Exception? exception)
    {
        _designerFallback = true;

        SuspendLayout();

        try
        {
            foreach (Control child in Controls.Cast<Control>().ToArray())
            {
                Controls.Remove(child);
                child.Dispose();
            }

            string detail = exception is null
                ? "SkiaSharp / ScottPlot 运行库仅在设计器外部加载。"
                : "SkiaSharp 原生库加载失败，设计器只显示占位视图；运行时请确保输出目录包含 runtimes/win-*/native/libSkiaSharp.dll。";

            System.Windows.Forms.Label placeholder = new()
            {
                Dock = DockStyle.Fill,
                Name = "ScottPlotDesignerPlaceholder",
                Text = "ScrollingPlotControl\r\n\r\n" + detail + "\r\n\r\n控件可以正常拖放和保存，运行时会显示实时曲线。",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = SystemColors.GrayText,
                BackColor = SystemColors.Control,
                Padding = new Padding(12),
            };

            Controls.Add(placeholder);
        }
        finally
        {
            ResumeLayout(true);
        }
    }

    /// <summary>
    /// 当前调用是否在控件的 UI 线程上。句柄创建之前 <see cref="Control.InvokeRequired"/> 总是 false，
    /// 不能用来判断线程，因此这里改用创建控件时记录的线程 ID。
    /// </summary>
    private bool IsOnUiThread =>
        IsHandleCreated ? !InvokeRequired : Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>
    /// 句柄创建后：在 UI 线程上执行句柄创建前排队的界面操作、挂接排队添加的曲线，
    /// 并投递句柄创建前收到的数据（见 <see cref="IsOnUiThread"/>）。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (IsDisposed || Disposing || _designerFallback)
            return;

        RunPendingUiActions();
        AttachPendingSeries();

        // 句柄创建前投递失败的合并写入 / 尚未处理的数据：句柄已存在，可以正常投递了。
        // 没有待处理数据时不投递，保持“UI 线程写入不产生合并写入”的语义。
        if (HasPendingWrites() || Interlocked.Exchange(ref _flushRetryOnHandleCreated, 0) != 0)
            PostPendingFlush();
    }

    /// <summary>是否有等待合并写入的数据或清空请求（只在持锁时读取各曲线的待处理列表）。</summary>
    private bool HasPendingWrites()
    {
        lock (_pendingLock)
        {
            foreach (PlotSeries series in _series)
            {
                if (series.PendingValues.Count > 0 || series.PendingClear)
                    return true;
            }
        }

        return false;
    }

    /// <summary>在 UI 线程上执行句柄创建前排队的界面操作（样式、删除等）。</summary>
    private void RunPendingUiActions()
    {
        if (IsDisposed || Disposing)
            return;

        Action[] actions;

        lock (_pendingLock)
        {
            if (_pendingUiActions.Count == 0)
                return;

            actions = [.. _pendingUiActions];
            _pendingUiActions.Clear();
        }

        foreach (Action action in actions)
            action();
    }

    /// <summary>在 UI 线程上挂接句柄创建前从其它线程添加的曲线。</summary>
    private void AttachPendingSeries()
    {
        if (IsDisposed || Disposing || _designerFallback || _canvas is null)
            return;

        PlotSeries[] attach;

        lock (_pendingLock)
        {
            if (_pendingAttach.Count == 0)
                return;

            attach = [.. _pendingAttach];
            _pendingAttach.Clear();
        }

        Plot plot = _canvas.Plot;

        foreach (PlotSeries series in attach)
        {
            if (series.IsRemoved || series.Plottable is not null)
                continue;

            AttachSeries(series, plot);
        }

        RefreshXMapping();
        OnSeriesLayoutChanged();
    }

    /// <summary>句柄创建后补投一次“挂接排队曲线”的消息（处理与 <see cref="OnHandleCreated"/> 的竞争）。</summary>
    private void PostPendingAttach()
    {
        if (!IsHandleCreated)
            return;

        try
        {
            BeginInvoke(_attachPendingAction);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // 句柄正在销毁：数据/曲线已无意义
        }
    }

    /// <summary>句柄创建后补投一次“执行排队界面操作”的消息（处理与 <see cref="OnHandleCreated"/> 的竞争）。</summary>
    private void PostPendingUiActions()
    {
        if (!IsHandleCreated)
            return;

        try
        {
            BeginInvoke(_runPendingUiActionsAction);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // 句柄正在销毁：操作已无意义
        }
    }

    #region 事件

    /// <summary>滚动模式发生变化时触发。</summary>
    [Category("ScottPlotEx")]
    [Description("滚动模式发生变化时触发。")]
    public event EventHandler<ScrollingModeChangedEventArgs>? ModeChanged;

    /// <summary>因为用户操作而停止滚动（进入手动模式）时触发。</summary>
    [Category("ScottPlotEx")]
    [Description("因为用户操作而停止滚动时触发。")]
    public event EventHandler? AutoScrollStopped;

    /// <summary>
    /// 点击“显示帮助”时触发。设置 <see cref="HelpRequestedEventArgs.Handled"/> 可以改用自定义帮助窗口。
    /// （该事件与 WinForms 框架自身的 <c>Control.HelpRequested</c> 无关。）
    /// </summary>
    [Category("ScottPlotEx")]
    [Description("请求显示帮助时触发。")]
    public event EventHandler<HelpRequestedEventArgs>? ShowHelpRequested;

    /// <summary>数据发生变化（添加或清空）时触发。</summary>
    [Category("ScottPlotEx")]
    [Description("数据发生变化时触发。")]
    public event EventHandler? DataChanged;

    #endregion

    #region 滚动模式

    /// <summary>
    /// 当前滚动模式。默认 <see cref="ScrollingMode.Stacked"/>（堆叠模式）。
    /// 重新设置该属性（包括设置成当前模式）会复位 X 轴偏移并立即恢复滚动。
    /// </summary>
    [Category("ScottPlotEx")]
    [DefaultValue(ScrollingMode.Stacked)]
    [Description("堆叠：X 轴 0 ~ 点数+2；跟随：X 轴为最新 30% 数据；手动：停止滚动。")]
    public ScrollingMode Mode
    {
        get => _mode;
        set => SetMode(value, ScrollingModeChangeReason.UserRequested);
    }

    /// <summary>当前是否已停止滚动（手动模式）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsScrollingStopped => _mode == ScrollingMode.Manual;

    /// <summary>用户拖动后保留的 X 轴偏移量。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double XOffset => _xOffset;

    /// <summary>自动滚动所使用的取值范围参数。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScrollingAxisOptions AxisOptions => _axisOptions;

    /// <summary>
    /// 最近一次的自动滚动模式（堆叠或跟随）。停止滚动后中键单击（整体显示）会恢复到该模式。
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScrollingMode LastAutoScrollMode => _lastAutoScrollMode;

    /// <summary>
    /// 当前模式的 X 轴显示长度百分比（只读）。
    /// 两种模式都表示“显示多少数据”：数值越大看到的数据越多（堆叠默认 100%，跟随默认 30%）。
    /// 需要修改时请使用 <see cref="SetXAxisDisplayPercent"/>（必须明确模式，避免把某个模式的数值误写到另一个模式）。
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double CurrentXAxisDisplayPercent => GetXAxisDisplayPercent();

    /// <summary>
    /// 读取指定模式的 X 轴显示长度百分比（数值越大看到的数据越多）。
    /// </summary>
    /// <param name="mode">目标模式；传 null（默认）表示当前模式，手动模式下自动取暂停之前的模式。</param>
    public double GetXAxisDisplayPercent(ScrollingMode? mode = null) =>
        ResolvePercentMode(mode) == ScrollingMode.Follow
            ? _axisOptions.FollowWindowPercent
            : _axisOptions.StackedDisplayPercent;

    /// <summary>
    /// 设置指定模式的 X 轴显示长度百分比，并立即按该模式重算坐标轴。
    /// 两种模式的百分比方向一致：**数值越大，看到的数据越多**。
    /// <list type="bullet">
    /// <item>堆叠模式：显示最新“百分比”的数据（默认 100% 即显示全部数据；90% 只裁掉最早的 10%，不会退化成跟随模式的窄窗口）。</item>
    /// <item>跟随模式：显示最后“百分比”的数据窗口（默认 30%，最小 0.1% 且至少 2 个数据点宽）。</item>
    /// </list>
    /// 明确指定 <paramref name="mode"/> 可以避免“把跟随模式的窗口百分比误写到堆叠模式”这类问题。
    /// </summary>
    /// <param name="percent">百分比（越界值自动限制：堆叠 1 ~ 100，跟随 0.1 ~ 100）。</param>
    /// <param name="mode">
    /// 明确指定要修改哪个模式的显示长度（手动模式会落到暂停之前的模式）。
    /// 该参数是必填的：把“读出来的百分比再写回去”这种写法固定成显式操作，避免误改另一个模式。
    /// </param>
    public void SetXAxisDisplayPercent(double percent, ScrollingMode mode)
    {
        if (ResolvePercentMode(mode) == ScrollingMode.Follow)
        {
            _axisOptions.FollowWindowPercent = ScrollingAxisOptions.ClampWindowPercent(percent);
        }
        else
        {
            _axisOptions.StackedDisplayPercent = ScrollingAxisOptions.ClampDisplayPercent(percent);

            // 关键：在此刻把原点固定下来（换算一次），之后数据刷新不再移动它
            PinStackedOrigin();
        }

        ReapplyAxes();
    }

    /// <summary>
    /// 恢复“显示全部数据”：把堆叠模式的显示长度百分比设为 100%（X 轴 0 ~ 数据点数量 + 2）。
    /// 用于从“只显示后一段数据”的视图回到完整视图。
    /// </summary>
    public void ShowAllData() => SetXAxisDisplayPercent(100.0, ScrollingMode.Stacked);

    /// <summary>
    /// 堆叠模式的 X 轴显示长度百分比（1 ~ 100）。默认 100，即从 0 开始显示全部数据；
    /// 数值越大看到的数据越多（90 只裁掉最早的 10%）。
    /// </summary>
    [Category("ScottPlotEx")]
    [DefaultValue(100.0)]
    [Description("堆叠模式显示长度百分比：显示最新该百分比的数据（100 = 显示全部数据）。")]
    public double StackedDisplayPercent
    {
        get => _axisOptions.StackedDisplayPercent;
        set
        {
            _axisOptions.StackedDisplayPercent = ScrollingAxisOptions.ClampDisplayPercent(value);
            PinStackedOrigin();
            ReapplyAxes();
        }
    }

    /// <summary>
    /// 按当前数据点数量与显示百分比固定堆叠模式的 X 轴原点。
    /// 只在“设置百分比 / 重新选择堆叠模式 / 清空数据”时调用，数据刷新时不调用，
    /// 这样原点就不会随数据增长而移动。
    /// </summary>
    private void PinStackedOrigin()
    {
        ReadOnlySpan<SeriesXExtent> extents = VisibleExtents();
        _stackedOriginX = extents.IsEmpty
            ? 0.0
            : ScrollingAxisCalculator.CalculateStackedOrigin(extents, _axisOptions, _axisOptions.StackedDisplayPercent);
    }

    /// <summary>当前固定的堆叠原点位置（可读取用于诊断）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double StackedOriginX => _stackedOriginX;

    /// <summary>跟随模式的 X 轴显示窗口百分比（0.1 ~ 100）。默认 30，即显示最后 30% 的数据。</summary>
    [Category("ScottPlotEx")]
    [DefaultValue(30.0)]
    [Description("跟随模式 X 轴窗口百分比：显示最后 该百分比 的数据。")]
    public double FollowWindowPercent
    {
        get => _axisOptions.FollowWindowPercent;
        set
        {
            _axisOptions.FollowWindowPercent = ScrollingAxisOptions.ClampWindowPercent(value);
            ReapplyAxes();
        }
    }

    /// <summary>
    /// 跟随模式固定窗口宽度（单位：数据点）。默认 0，即按 <see cref="FollowWindowPercent"/> 显示最后若干百分比的数据；
    /// 大于 0 时始终显示最后这么多个数据点（示波器式滚动，窗口宽度不随数据增长），例如每秒 1000 点、显示最近 60 秒即设为 60000。
    /// 点数单位下使用；时间单位下若没有设置 <see cref="FollowWindowDuration"/>，按各曲线的采样间隔换算成时长（取最长的一条）。
    /// 重新选择跟随模式不会复位该值。
    /// </summary>
    [Category("ScottPlotEx")]
    [DefaultValue(0)]
    [Description("跟随模式固定窗口宽度（数据点）：大于 0 时始终显示最后这么多个点，0 表示按百分比。")]
    public int FollowWindowPoints
    {
        get => _axisOptions.FollowWindowPoints;
        set
        {
            _axisOptions.FollowWindowPoints = Math.Max(0, value);
            ReapplyAxes();
        }
    }

    /// <summary>
    /// 跟随模式固定窗口时长（相对时间 / 绝对时间单位下使用）。默认 <see cref="TimeSpan.Zero"/>，即不按时长
    /// （此时按 <see cref="FollowWindowPoints"/> 换算或按 <see cref="FollowWindowPercent"/>）；
    /// 大于 0 时始终显示最近这么长时间的数据（示波器式滚动），例如显示最近 60 秒即设为 <c>TimeSpan.FromSeconds(60)</c>。
    /// 点数单位下不使用。重新选择跟随模式不会复位该值。
    /// </summary>
    [Category("ScottPlotEx")]
    [DefaultValue(typeof(TimeSpan), "00:00:00")]
    [Description("跟随模式固定窗口时长（时间单位下使用）：大于 0 时始终显示最近这么长时间，0 表示按点数或百分比。")]
    public TimeSpan FollowWindowDuration
    {
        get => _axisOptions.FollowWindowDuration;
        set
        {
            _axisOptions.FollowWindowDuration = value > TimeSpan.Zero ? value : TimeSpan.Zero;
            ReapplyAxes();
        }
    }

    /// <summary>
    /// X 轴单位（对标 imc 曲线窗口）：<see cref="XAxisUnit.Samples"/>（点数，默认：第 n 个点 X = n，各曲线按序号对齐）、
    /// <see cref="XAxisUnit.RelativeTime"/>（相对时间：X 为距 <see cref="TimeReference"/> 的秒数，刻度显示 秒 / 分:秒 / 时:分:秒 / 天）、
    /// <see cref="XAxisUnit.AbsoluteTime"/>（绝对时间：X 同相对时间，刻度显示日期时间）。
    /// 时间单位下各曲线按“起始时间 + 下标 × 采样间隔”在时间上对齐（见 <see cref="PlotSeries.SampleInterval"/> / <see cref="PlotSeries.StartTime"/>）。
    /// 运行中切换时数据保留、立即重算坐标轴；拖动偏移与手动模式下的视图按最长的可见曲线换算，仍显示同一段数据。
    /// 切换到时间单位时控件为 X 轴安装 <see cref="TimeAxisTickGenerator"/>，切回点数单位时恢复原来的刻度生成器。
    /// <para>
    /// 绝对时间要求起始时间是明确设置的（<see cref="DefaultStartTime"/> 或各曲线的 <see cref="PlotSeries.StartTime"/>），
    /// 不使用“第一个数据点写入的时刻”。规则：除了还没有数据的默认曲线（没人用的默认曲线不妨碍切换），每条曲线都必须有明确的起始时间；
    /// 处于绝对时间时，任何会让曲线失去起始时间的配置操作（清除 <see cref="DefaultStartTime"/> / <see cref="PlotSeries.StartTime"/>、
    /// 没有默认起始时间时不带起始时间地 <see cref="AddSeries"/>）都抛出 <see cref="InvalidOperationException"/>；
    /// 写给没有起始时间的默认曲线的数据不抛异常，而是拒收并计入 <see cref="PlotSeries.RejectedValueCount"/>。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">切换到绝对时间、但有曲线没有明确的起始时间时抛出（单位保持不变）。</exception>
    [Category("ScottPlotEx")]
    [DefaultValue(XAxisUnit.Samples)]
    [Description("X 轴单位：点数（第 n 个点 X = n）/ 相对时间 / 绝对时间。")]
    public XAxisUnit XAxisUnit
    {
        get => _xAxisUnit;
        set => SetXAxisUnit(value);
    }

    /// <summary>
    /// 曲线默认的采样间隔（没有设置 <see cref="PlotSeries.SampleInterval"/> 的曲线使用），默认 1 秒。
    /// 只在时间单位下影响 X 坐标。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">设置为非正值时抛出。</exception>
    [Category("ScottPlotEx")]
    [DefaultValue(typeof(TimeSpan), "00:00:01")]
    [Description("曲线默认的采样间隔（时间单位下决定数据点的 X 坐标）。")]
    public TimeSpan DefaultSampleInterval
    {
        get => _defaultSampleInterval;
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "采样间隔必须大于 0。");

            _defaultSampleInterval = value;
            _defaultSampleIntervalSeconds = value.Ticks / (double)TimeSpan.TicksPerSecond;
            OnSeriesXMappingChanged();
        }
    }

    /// <summary>
    /// 曲线默认的起始时间（没有设置 <see cref="PlotSeries.StartTime"/> 的曲线使用），默认 null：
    /// 此时相对时间单位下每条曲线以自己的第一个数据点写入控件的时刻为起始时间（清空后重新记录）；
    /// 绝对时间单位不使用该时刻，必须设置本属性或各曲线的 <see cref="PlotSeries.StartTime"/>。只在时间单位下影响 X 坐标。
    /// 设置后清空数据不会改变它：重新开始一次测量时请同时更新起始时间。
    /// </summary>
    /// <exception cref="InvalidOperationException">绝对时间单位下设为 null、而有曲线依赖它作为起始时间时抛出。</exception>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime? DefaultStartTime
    {
        get => _defaultStartTime;
        set
        {
            if (_designerFallback)
            {
                _defaultStartTime = value;
                return;
            }

            if (value is null && _xAxisUnit == XAxisUnit.AbsoluteTime)
                EnsureAbsoluteStartTimes(defaultStartTime: null);

            _defaultStartTime = value;
            OnSeriesXMappingChanged();
        }
    }

    /// <summary>
    /// 时间单位下 X = 0 对应的时刻：全部曲线生效起始时间中最早的一个（绝对时间刻度 = 该时刻 + X 秒）。
    /// 还没有任何曲线有起始时间（没有数据、也没有设置起始时间）时为 null。
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime? TimeReference => _timeReference;

    /// <summary>默认采样间隔（秒）。</summary>
    internal double DefaultSampleIntervalSeconds => _defaultSampleIntervalSeconds;

    /// <summary>
    /// 跨线程写入触发的重绘帧率上限（帧/秒）。默认 0，即不限制（每帧画完立即处理下一批数据）。
    /// 设为 30 / 60 等值后，两次合并写入之间至少间隔一帧时间，数据照常全部接收（只是按帧合并），CPU 占用随之下降。
    /// 间隔由高精度定时器控制，不受 WinForms 定时器约 15.6 ms 精度的影响（33 ms 不会被拉长到约 47 ms）。
    /// UI 线程上的写入仍然同步生效、由消息循环合并重绘，不受此限制。
    /// </summary>
    [Category("ScottPlotEx")]
    [DefaultValue(0)]
    [Description("跨线程写入触发的重绘帧率上限（帧/秒），0 表示不限制。")]
    public int MaxFramesPerSecond
    {
        get => _maxFramesPerSecond;
        set
        {
            _maxFramesPerSecond = Math.Max(0, value);
            Interlocked.Exchange(ref _minFlushIntervalTicks, _maxFramesPerSecond > 0 ? Stopwatch.Frequency / _maxFramesPerSecond : 0);

            if (_designerFallback)
                return;

            if (_maxFramesPerSecond > 0 && _frameTimer is null && !IsDisposed && !DesignMode)
                _frameTimer = new HighResolutionTimer(OnFrameTimerDue);
        }
    }

    /// <summary>是否显示图例（列出名称不为空的曲线）。默认 false。</summary>
    [Category("ScottPlotEx")]
    [DefaultValue(false)]
    [Description("是否显示图例（列出名称不为空的曲线）。")]
    public bool ShowLegend
    {
        get => _showLegend;
        set
        {
            _showLegend = value;

            if (_designerFallback)
                return;

            ApplyLegend();
            RequestRender();
        }
    }

    private void ApplyLegend()
    {
        if (_designerFallback || _canvas is null)
            return;

        _canvas.Plot.Legend.IsVisible = _showLegend;
    }

    /// <summary>把百分比接口解析为目标模式：显式指定优先；手动模式统一落到暂停之前的自动滚动模式。</summary>
    private ScrollingMode ResolvePercentMode(ScrollingMode? mode)
    {
        ScrollingMode target = mode ?? _mode;
        return target == ScrollingMode.Manual ? _lastAutoScrollMode : target;
    }

    /// <summary>按当前模式与参数重新计算坐标轴（手动模式只重绘，不改变视图）。</summary>
    private void ReapplyAxes()
    {
        if (IsDisposed || Disposing || _designerFallback)
            return;

        ApplyAutoScroll();
        RequestRender();
    }

    /// <summary>
    /// 是否在右键菜单中显示“堆叠模式 / 跟随模式 / 手动模式”勾选项，默认 true。
    /// 勾选状态始终与控件的当前模式保持一致（含自动停止滚动的情况）。
    /// </summary>
    [Category("ScottPlotEx")]
    [DefaultValue(true)]
    [Description("是否在右键菜单中显示滚动模式勾选项。")]
    public bool ShowModeMenuItems
    {
        get => _plotMenu?.ShowModeItems ?? _showModeItemsFallback;
        set
        {
            _showModeItemsFallback = value;

            if (_plotMenu is not null)
                _plotMenu.ShowModeItems = value;
        }
    }

    /// <summary>停止滚动（切换到手动模式），保持当前视图。</summary>
    public void StopScrolling() => SetMode(ScrollingMode.Manual, ScrollingModeChangeReason.UserRequested);

    private void SetMode(ScrollingMode mode, ScrollingModeChangeReason reason)
    {
        ScrollingMode previous = _mode;
        _mode = mode;

        if (mode != ScrollingMode.Manual)
            _lastAutoScrollMode = mode;

        if (_designerFallback)
        {
            _xOffset = 0;
            return;
        }

        // 重新选择模式 = 回到该模式的默认视图：
        // 选择“堆叠模式”即恢复 0%（显示全部数据），选择“跟随模式”即恢复默认窗口 30%，
        // 否则之前调整过的百分比会让视图一直停留在被裁短的状态（看起来像没有切回堆叠模式）。
        if (reason == ScrollingModeChangeReason.UserRequested && _axisOptions.ResetDisplayPercentOnModeChange)
        {
            if (mode == ScrollingMode.Stacked)
                _axisOptions.StackedDisplayPercent = ScrollingAxisOptions.DefaultStackedDisplayPercent;
            else if (mode == ScrollingMode.Follow)
                _axisOptions.FollowWindowPercent = ScrollingAxisOptions.DefaultFollowWindowPercent;
        }

        // 重新选择堆叠模式时按当前数据重新固定原点（默认 100% → 原点 0）
        if (mode == ScrollingMode.Stacked)
            PinStackedOrigin();

        // 重新选择模式（需求 3、6）：复位偏移量
        _xOffset = 0;

        if (mode != ScrollingMode.Manual)
            ApplyAutoScroll();

        if (previous != mode || reason == ScrollingModeChangeReason.UserRequested)
        {
            ModeChanged?.Invoke(this, new ScrollingModeChangedEventArgs(previous, mode, reason));

            if (mode == ScrollingMode.Manual)
                AutoScrollStopped?.Invoke(this, EventArgs.Empty);
        }

        RememberLimits();
        RequestRender();
    }

    #endregion

    #region 数据接口（单曲线接口：读写默认曲线）

    /// <summary>默认曲线的数据点数量（单曲线接口；多曲线时请使用各 <see cref="PlotSeries.DataCount"/>）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int DataCount => _defaultSeries?.DataCount ?? 0;

    /// <summary>默认曲线最新数据点的 X 坐标（点数单位下从 1 开始、等于点数；时间单位下为距 <see cref="TimeReference"/> 的秒数）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double LatestX => _defaultSeries?.LatestX ?? 0;

    /// <summary>默认曲线最新数据点的 Y 值。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double LatestValue => _defaultSeries?.LatestValue ?? double.NaN;

    /// <summary>默认曲线中被忽略的非有限值数量（NaN / Infinity）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SkippedValueCount => _defaultSeries?.SkippedValueCount ?? 0;

    /// <summary>默认曲线全部数据的 Y 值。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<double> Values => _defaultSeries?.Values ?? Array.Empty<double>();

    /// <summary>
    /// 向默认曲线添加一个数据点。X 坐标自动递增（第 1 个点为 1，第 n 个点为 n）。
    /// 可以在任意线程调用：UI 线程上同步生效；非 UI 线程上先进入待处理列表，
    /// 由 UI 线程按帧整批写入（无论调用多频繁，窗口消息队列里最多只有一条合并写入消息）。
    /// </summary>
    /// <param name="y">Y 值，非有限值会被忽略。</param>
    public void AddValue(double y)
    {
        if (_designerFallback)
            return;

        AppendValue(_defaultSeries, y);
    }

    /// <summary>向默认曲线批量添加数据点。</summary>
    /// <param name="values">Y 值集合，非有限值会被忽略。</param>
    public void AddValues(IEnumerable<double> values)
    {
        if (_designerFallback)
            return;

        _defaultSeries.AddValues(values);
    }

    /// <summary>向默认曲线批量添加数据点。</summary>
    /// <param name="values">Y 值数组。</param>
    public void AddValues(params double[] values)
    {
        if (_designerFallback)
            return;

        _defaultSeries.AddValues(values);
    }

    /// <summary>
    /// 向默认曲线批量添加数据点。UI 线程上直接写入、不复制调用方的数据；
    /// 非 UI 线程上会先复制到内部待处理列表（调用返回后即可复用传入的缓冲区）。
    /// </summary>
    /// <param name="values">Y 值，非有限值会被忽略。</param>
    public void AddValues(ReadOnlySpan<double> values)
    {
        if (_designerFallback)
            return;

        AppendValues(_defaultSeries, values);
    }

    /// <summary>清空全部曲线的数据（曲线本身保留），并把坐标轴恢复到初始范围。可以在任意线程调用。</summary>
    public void ClearData()
    {
        if (IsDisposed || Disposing || _designerFallback)
            return;

        if (!IsOnUiThread)
        {
            // 与跨线程写入走同一个队列，保证“写入 → 清空 → 写入”的顺序不被打乱：
            // 清空之前排队的数据直接丢弃，之后到达的数据在清空完成后再追加
            lock (_pendingLock)
            {
                foreach (PlotSeries series in _series)
                {
                    series.PendingValues.Clear();
                    series.PendingClear = true;
                }
            }

            PostPendingFlush();
            return;
        }

        foreach (PlotSeries series in _series)
        {
            series.Data.Clear();
            series.ArrivalStartTime = null;
            series.RejectedValueCount = 0;
        }

        RefreshXMapping();
        OnDataCleared();
    }

    #endregion

    #region 多曲线

    /// <summary>默认曲线：单曲线接口（<see cref="AddValue"/> / <see cref="AddValues(ReadOnlySpan{double})"/> / <see cref="Values"/> ……）读写的曲线，始终存在。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PlotSeries DefaultSeries => _defaultSeries;

    /// <summary>全部曲线（含默认曲线，默认曲线在第一位）。返回调用时刻的快照，可以在任意线程读取。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<PlotSeries> Series
    {
        get
        {
            lock (_pendingLock)
                return [.. _series];
        }
    }

    /// <summary>
    /// 添加一条曲线（对应 imc 曲线窗口中的一个通道）。各曲线的数据彼此独立：点数单位下 X 坐标都从 1 开始，
    /// 时间单位下按各自的起始时间与采样间隔换算；坐标轴按所有可见曲线自动计算。
    /// 可以在任意线程调用（非 UI 线程会同步切换到 UI 线程执行）。
    /// </summary>
    /// <param name="name">曲线名称（显示在图例中，见 <see cref="ShowLegend"/>）。</param>
    /// <param name="color">线条颜色；不指定时按调色板依次取色。</param>
    /// <param name="startTime">曲线的起始时间（等同于创建后设置 <see cref="PlotSeries.StartTime"/>）；不指定时使用 <see cref="DefaultStartTime"/>。</param>
    /// <returns>新曲线；之后通过它写入数据、修改样式或删除。</returns>
    /// <exception cref="InvalidOperationException">
    /// 绝对时间单位下既没有传入 <paramref name="startTime"/>、控件也没有 <see cref="DefaultStartTime"/> 时抛出（曲线不能没有起始时间）。
    /// </exception>
    public PlotSeries AddSeries(string name, ScottPlot.Color? color = null, DateTime? startTime = null)
    {
        // 设计器兜底：允许用户代码在 Form 构造函数里创建曲线（控件本身只显示占位视图）。
        // 曲线只存在于设计时实例的内存里，不会被序列化到 Form。
        if (_designerFallback)
        {
            PlotSeries designerSeries = CreateSeries(name, color, startTime);

            lock (_pendingLock)
                _pendingAttach.Add(designerSeries);

            return designerSeries;
        }

        if (!IsOnUiThread)
        {
            if (IsHandleCreated)
                return (PlotSeries)Invoke(new Func<PlotSeries>(() => AddSeries(name, color, startTime)));

            // 句柄创建前从其它线程添加：先把曲线加入列表（数据会进入待处理队列），
            // 挂接 Plot 与重算坐标轴推迟到 OnHandleCreated 的 UI 线程上执行
            PlotSeries pending = CreateSeries(name, color, startTime);

            lock (_pendingLock)
                _pendingAttach.Add(pending);

            // 与 OnHandleCreated 竞争：句柄可能刚好在判断线程之后创建
            PostPendingAttach();
            return pending;
        }

        PlotSeries series = CreateSeries(name, color, startTime);
        AttachSeries(series, _canvas.Plot);

        // 新曲线没有数据，通常不影响坐标轴，只需重绘（图例）；
        // 但它可能带来更早的起始时间（默认起始时间早于其它曲线各自设置的起始时间），此时参考时刻移动、需要重算
        if (RefreshXMapping())
            OnSeriesLayoutChanged();
        else
            RequestRender();

        return series;
    }

    /// <summary>创建曲线并加入 <see cref="_series"/>（调用方负责判断线程与是否立即挂接）。</summary>
    private PlotSeries CreateSeries(string name, ScottPlot.Color? color, DateTime? startTime)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (_xAxisUnit == XAxisUnit.AbsoluteTime && startTime is null && _defaultStartTime is null)
        {
            throw new InvalidOperationException(
                "绝对时间单位下添加曲线需要起始时间：请传入 startTime 参数，或先设置控件的 DefaultStartTime。");
        }

        PlotSeries series;

        lock (_pendingLock)
        {
            int colorIndex = _seriesCreated++;
            series = new PlotSeries(this, name ?? string.Empty, color ?? SeriesPalette.GetColor(colorIndex), isDefault: false)
            {
                OwnStartTime = startTime,
            };
            _series.Add(series);
        }

        return series;
    }

    /// <summary>
    /// 在 UI 线程上执行（非 UI 线程同步切换；句柄尚未创建时排入队列，在 <see cref="OnHandleCreated"/> 中执行，
    /// 避免后台线程直接修改 Plot / 控件状态）。
    /// 操作本身抛出的异常（例如绝对时间单位下清除起始时间）原样抛给调用方；切换线程时句柄正在销毁则忽略。
    /// </summary>
    internal void RunOnUiThread(Action action)
    {
        if (!IsOnUiThread)
        {
            if (!IsHandleCreated)
            {
                lock (_pendingLock)
                    _pendingUiActions.Enqueue(action);

                // 与 OnHandleCreated 竞争：句柄可能刚好在判断线程之后创建
                PostPendingUiActions();
                return;
            }

            ExceptionDispatchInfo? failure = null;

            try
            {
                Invoke(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        failure = ExceptionDispatchInfo.Capture(exception);
                    }
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // 句柄正在销毁：忽略
            }

            failure?.Throw();
            return;
        }

        action();
    }

    internal void AppendValue(PlotSeries series, double y)
    {
        if (_designerFallback || IsDisposed || Disposing || series.IsRemoved)
            return;

        // 先判断线程再处理：UI 线程（最常见的调用方式）不产生任何闭包 / 委托分配
        if (!IsOnUiThread)
        {
            lock (_pendingLock)
                series.PendingValues.Add(y);

            PostPendingFlush();
            return;
        }

        if (RejectsData(series))
        {
            series.RejectedValueCount++;
            return;
        }

        if (!series.Data.Add(y))
            return;

        if (series.ArrivalStartTime is null)
            CaptureArrivalStartTime(series);

        UpdateView();
    }

    internal void AppendValues(PlotSeries series, ReadOnlySpan<double> values)
    {
        if (_designerFallback || values.IsEmpty || IsDisposed || Disposing || series.IsRemoved)
            return;

        if (!IsOnUiThread)
        {
            lock (_pendingLock)
                series.PendingValues.AddRange(values);

            PostPendingFlush();
            return;
        }

        if (RejectsData(series))
        {
            series.RejectedValueCount += values.Length;
            return;
        }

        if (series.Data.AddRange(values) == 0)
            return;

        if (series.ArrivalStartTime is null)
            CaptureArrivalStartTime(series);

        UpdateView();
    }

    /// <summary>
    /// 曲线清空后的第一批数据到达：记录到达时刻。起始时间由它决定时（曲线与控件都没有设置起始时间）重算 X 映射。
    /// </summary>
    private void CaptureArrivalStartTime(PlotSeries series)
    {
        series.ArrivalStartTime = DateTime.Now;

        if (series.UsesArrivalStartTime)
            RefreshXMapping();
    }

    internal void ClearSeries(PlotSeries series)
    {
        if (_designerFallback || IsDisposed || Disposing || series.IsRemoved)
            return;

        if (!IsOnUiThread)
        {
            // 与该曲线的跨线程写入走同一个队列：清空之前排队的数据丢弃，之后到达的数据在清空后追加
            lock (_pendingLock)
            {
                series.PendingValues.Clear();
                series.PendingClear = true;
            }

            PostPendingFlush();
            return;
        }

        series.Data.Clear();
        series.ArrivalStartTime = null;
        series.RejectedValueCount = 0;
        RefreshXMapping();
        OnDataCleared();
    }

    internal void RemoveSeries(PlotSeries series)
    {
        if (series.IsDefault)
            throw new InvalidOperationException("默认曲线不能删除（单曲线接口 AddValue / AddValues 写入的就是它）；不需要时可设置 IsVisible = false。");

        RunOnUiThread(() =>
        {
            if (series.IsRemoved || IsDisposed || _designerFallback)
                return;

            lock (_pendingLock)
            {
                _series.Remove(series);
                series.IsRemoved = true;
                series.PendingValues.Clear();
                series.PendingClear = false;
            }

            if (series.Plottable is { } plottable && _canvas is not null)
                _canvas.Plot.Remove(plottable);

            series.Plottable = null;
            RefreshXMapping(); // 删除的可能是起始时间最早的曲线：参考时刻随之移动
            OnSeriesLayoutChanged();
            DataChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>曲线的显示状态或曲线集合发生变化：按当前可见曲线重新计算坐标轴。</summary>
    internal void OnSeriesLayoutChanged()
    {
        if (IsDisposed || Disposing || _designerFallback)
            return;

        // 曲线的显示 / 删除会改变堆叠模式的右端：按“显示最新 N%”的语义重新固定原点，
        // 否则隐藏 / 删除最长的一条后，旧原点可能已超过新的右端，剩余数据整段落在视图之外。
        if (_mode == ScrollingMode.Stacked)
            PinStackedOrigin();

        if (!IsUserGestureInProgress || !_axisOptions.PauseAutoScrollDuringGesture)
            ApplyAutoScroll();

        RememberLimits();
        RequestRender();
    }

    /// <summary>
    /// 参与坐标轴计算的曲线（可见且有数据）在 X 轴上的范围：最新 X 与一个数据点的 X 宽度。
    /// 点数单位下最新 X = 点数、宽度 = 1；时间单位下为距参考时刻的秒数与采样间隔。
    /// 返回的列表在下一次调用前有效（复用同一个缓冲区）。
    /// </summary>
    private ReadOnlySpan<SeriesXExtent> VisibleExtents()
    {
        _visibleExtents.Clear();

        foreach (PlotSeries series in _series)
        {
            PlotDataBuffer data = series.Data;

            if (data.Count > 0 && series.IsVisible)
                _visibleExtents.Add(new SeriesXExtent(data.LatestX, data.XStep));
        }

        return CollectionsMarshal.AsSpan(_visibleExtents);
    }

    /// <summary>最长的可见曲线（单位切换时据此换算拖动偏移与视图）；没有可见数据时为 null。</summary>
    private PlotSeries? LongestVisibleSeries()
    {
        PlotSeries? longest = null;

        foreach (PlotSeries series in _series)
        {
            if (series.Data.Count > (longest?.Data.Count ?? 0) && series.IsVisible)
                longest = series;
        }

        return longest;
    }

    /// <summary>数据被清空（全部或某一条曲线）：重新固定堆叠原点并恢复自动视图。</summary>
    private void OnDataCleared()
    {
        // 全部可见数据都没了才复位 X 轴偏移；只清空其中一条曲线时保留用户的拖动结果
        if (VisibleExtents().IsEmpty)
            _xOffset = 0;

        PinStackedOrigin(); // 数据清空后按剩余数据重新固定原点（全部清空时归零，重新开始时从 0 堆叠）
        ApplyAutoScroll();
        RememberLimits();
        RequestRender();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateView()
    {
        // 鼠标手势（拖动平移 / 框选放大 / 拖动缩放）进行中时不移动坐标轴：
        // 否则每来一个新数据点，自动滚动都会把视图拉回自动窗口（表现为“鼠标一操作就卡一下”），
        // 甚至会把用户的拖动结果覆盖掉。手势结束后由 AutoScrollPolicy 统一决定后续行为。
        if (!IsUserGestureInProgress || !_axisOptions.PauseAutoScrollDuringGesture)
            ApplyAutoScroll();

        RememberLimits();
        RequestRender();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region X 轴单位与时间映射

    /// <summary>曲线的采样间隔或起始时间（或控件的默认值）发生变化：重算 X 映射与坐标轴。</summary>
    internal void OnSeriesXMappingChanged()
    {
        if (IsDisposed || Disposing)
            return;

        RefreshXMapping();

        // 点数单位下 X 与采样间隔、起始时间无关
        if (_xAxisUnit != XAxisUnit.Samples)
            OnSeriesLayoutChanged();
    }

    /// <summary>
    /// 重新计算参考时刻（全部曲线生效起始时间中最早的一个），并把每条曲线的“下标 → X”映射
    /// 同步到它的数据缓冲区（坐标轴计算、Y 轴统计）与 ScottPlot 数据源（Period / XOffset：渲染、最近点查询）。
    /// 时间单位下参考时刻移动时（例如起始时间最早的曲线被清空或删除）全部 X 整体平移：
    /// 手动模式把视图跟着平移（仍看着同一段时间），自动模式重新固定堆叠原点。
    /// </summary>
    /// <returns>时间单位下参考时刻是否移动。</returns>
    private bool RefreshXMapping()
    {
        DateTime? reference = null;

        foreach (PlotSeries series in _series)
        {
            if (series.EffectiveStartTime is { } start && (reference is null || start < reference.Value))
                reference = start;
        }

        DateTime? previous = _timeReference;
        _timeReference = reference;

        foreach (PlotSeries series in _series)
            ApplySeriesXMapping(series);

        if (_timeTickGenerator is not null)
            _timeTickGenerator.ReferenceTime = reference ?? _createdAt;

        if (_xAxisUnit == XAxisUnit.Samples || previous is not { } oldReference || reference is not { } newReference || oldReference == newReference)
            return false;

        double shift = (newReference - oldReference).Ticks / (double)TimeSpan.TicksPerSecond;

        if (_mode == ScrollingMode.Manual)
        {
            if (ReadLimits() is { } limits)
            {
                ApplyLimits(limits.Left - shift, limits.Right - shift, limits.Bottom, limits.Top);
                RememberLimits();
            }
        }
        else
        {
            PinStackedOrigin();
        }

        return true;
    }

    /// <summary>
    /// 检查绝对时间单位的前提：假设默认起始时间为 <paramref name="defaultStartTime"/>、
    /// 曲线 <paramref name="changed"/> 自己的起始时间改为 <paramref name="changedStartTime"/>，
    /// 每条需要起始时间的曲线（还没有数据的默认曲线除外）是否都有明确的起始时间；没有则抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    private void EnsureAbsoluteStartTimes(DateTime? defaultStartTime, PlotSeries? changed = null, DateTime? changedStartTime = null)
    {
        List<string>? missing = null;

        foreach (PlotSeries series in _series)
        {
            DateTime? own = ReferenceEquals(series, changed) ? changedStartTime : series.OwnStartTime;

            if (own is null && defaultStartTime is null && series.RequiresAbsoluteStartTime)
                (missing ??= []).Add(series.ToString());
        }

        if (missing is not null)
        {
            throw new InvalidOperationException(
                "绝对时间需要明确设置的起始时间（不使用第一个数据点写入的时刻）：请设置控件的 DefaultStartTime，" +
                $"或为每条曲线设置 StartTime。缺少起始时间的曲线：{string.Join("、", missing)}。");
        }
    }

    /// <summary>设置曲线自己的起始时间（UI 线程）：绝对时间单位下不允许让曲线失去起始时间。</summary>
    internal void SetSeriesStartTime(PlotSeries series, DateTime? value)
    {
        if (value is null && _xAxisUnit == XAxisUnit.AbsoluteTime && !series.IsRemoved)
            EnsureAbsoluteStartTimes(_defaultStartTime, series, changedStartTime: null);

        series.OwnStartTime = value;
        OnSeriesXMappingChanged();
    }

    /// <summary>
    /// 绝对时间单位下没有明确起始时间的曲线不接收数据（只可能是没人设置起始时间、也还没有数据的默认曲线）：
    /// 它的数据无法换算成时刻。数据接口可能在采集线程上调用，因此这里只拒收、计数，不抛异常。
    /// </summary>
    private bool RejectsData(PlotSeries series) =>
        _xAxisUnit == XAxisUnit.AbsoluteTime && series.ExplicitStartTime is null;

    /// <summary>
    /// 曲线的“下标 → X”映射：点数单位 X = 下标 + 1；时间单位 X =（起始时间 − 参考时刻）+ 下标 × 采样间隔（秒）。
    /// </summary>
    private void ApplySeriesXMapping(PlotSeries series)
    {
        double start = 1.0;
        double step = 1.0;

        if (_xAxisUnit != XAxisUnit.Samples)
        {
            step = series.IntervalSeconds;
            start = series.EffectiveStartTime is { } seriesStart && _timeReference is { } reference
                ? (seriesStart - reference).Ticks / (double)TimeSpan.TicksPerSecond
                : 0.0;
        }

        series.Data.XStart = start;
        series.Data.XStep = step;

        if (series.Plottable is { } plottable)
        {
            plottable.Data.XOffset = start;
            plottable.Data.Period = step;
        }
    }

    private void SetXAxisUnit(XAxisUnit unit)
    {
        if (!Enum.IsDefined(unit))
            throw new InvalidEnumArgumentException(nameof(XAxisUnit), (int)unit, typeof(XAxisUnit));

        if (unit == _xAxisUnit)
            return;

        if (_designerFallback)
        {
            _xAxisUnit = unit;
            return;
        }

        // 绝对时间只认明确设置的起始时间：先检查，缺少时抛异常、单位保持不变
        if (unit == XAxisUnit.AbsoluteTime)
            EnsureAbsoluteStartTimes(_defaultStartTime);

        // 以最长的可见曲线为准，把拖动偏移与手动模式下的视图从旧单位换算到新单位（下标不变），保持看着同一段数据
        PlotSeries? reference = LongestVisibleSeries();
        double oldStart = reference?.Data.XStart ?? 1.0;
        double oldStep = reference?.Data.XStep ?? 1.0;
        AxisLimits? limits = ReadLimits();

        _xAxisUnit = unit;
        RefreshXMapping();
        ApplyTickGenerator();

        if (reference is null)
        {
            _xOffset = 0;
        }
        else
        {
            double newStart = reference.Data.XStart;
            double newStep = reference.Data.XStep;
            _xOffset = _xOffset / oldStep * newStep;

            if (_mode == ScrollingMode.Manual && limits is { } view)
            {
                double Convert(double x) => newStart + ((x - oldStart) / oldStep * newStep);
                ApplyLimits(Convert(view.Left), Convert(view.Right), view.Bottom, view.Top);
            }
        }

        PinStackedOrigin();

        if (_mode != ScrollingMode.Manual)
            ApplyAutoScroll();

        RememberLimits();
        RequestRender();
    }

    /// <summary>
    /// 时间单位：为 X 轴安装时间刻度生成器（记下原来的，切回点数单位时恢复）；
    /// 点数单位：若 X 轴仍是控件安装的时间刻度生成器，则恢复原来的。用户自行替换的刻度生成器保持不动。
    /// </summary>
    private void ApplyTickGenerator()
    {
        if (_designerFallback || _canvas is null)
            return;

        Plot? plot = _canvas.Plot;
        if (plot is null)
            return;

        IXAxis axis = plot.Axes.Bottom;

        if (_xAxisUnit == XAxisUnit.Samples)
        {
            if (_timeTickGenerator is not null && ReferenceEquals(axis.TickGenerator, _timeTickGenerator))
                axis.TickGenerator = _tickGeneratorBeforeTimeUnit ?? new ScottPlot.TickGenerators.NumericAutomatic();

            _tickGeneratorBeforeTimeUnit = null;
            return;
        }

        _timeTickGenerator ??= new TimeAxisTickGenerator();
        _timeTickGenerator.Absolute = _xAxisUnit == XAxisUnit.AbsoluteTime;
        _timeTickGenerator.ReferenceTime = _timeReference ?? _createdAt;

        if (!ReferenceEquals(axis.TickGenerator, _timeTickGenerator))
        {
            _tickGeneratorBeforeTimeUnit = axis.TickGenerator;
            axis.TickGenerator = _timeTickGenerator;
        }
    }

    #endregion

    #region IPlotControl（ScottPlot.WinForms 全部接口）

    /// <summary>
    /// 曲线画布（自绘控件：SkiaSharp 直接渲染进 GDI DIB，WM_PAINT 中 BitBlt 到窗口）。
    /// 鼠标 / 键盘输入由它接收并转发给 <see cref="UserInputProcessor"/>；需要监听画布事件或向画布注入输入时使用它。
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control Canvas => _canvas;
    /// <summary>
    /// 已移除：控件不再内嵌 <see cref="ScottPlot.WinForms.FormsPlot"/>（改为自绘画布，见 <see cref="Canvas"/>）。
    /// 绘图对象、输入处理、菜单等请直接使用本控件实现的 <see cref="IPlotControl"/> 成员。
    /// </summary>
    [Obsolete("控件不再内嵌 FormsPlot：请改用 Canvas（画布控件）以及本控件自身的 Plot / UserInputProcessor / Menu / Refresh 等 IPlotControl 成员。", error: true)]
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScottPlot.WinForms.FormsPlot? FormsPlot => null;

    /// <summary>已移除：控件不再使用 SkiaSharp 的 SKControl 画布（改为自绘画布，见 <see cref="Canvas"/>）。</summary>
    [Obsolete("控件不再使用 SKControl：请改用 Canvas（画布控件）。", error: true)]
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TransparentSKControl? SKControl => null;

    /// <summary>中文右键菜单。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ChinesePlotMenu PlotMenu => _plotMenu;

    /// <summary>默认曲线的 ScottPlot 图元（可自行修改颜色、线宽等样式；其它曲线见 <see cref="PlotSeries.Signal"/>）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScottPlot.Plottables.Signal? Signal => _defaultSeries?.Plottable;

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Plot Plot => _canvas?.Plot!;

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IMultiplot Multiplot
    {
        get => _canvas?.Multiplot!;
        set
        {
            if (_canvas is not null)
                _canvas.Multiplot = value;
        }
    }

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IPlotMenu? Menu
    {
        get => _canvas?.Menu;
        set
        {
            if (_canvas is not null)
                _canvas.Menu = value;
        }
    }

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public UserInputProcessor UserInputProcessor => _canvas?.UserInputProcessor!;

    /// <summary>
    /// 显示缩放比例（DPI / 96），默认按画布所在显示器检测。
    /// 设置时同步设置 <see cref="ScottPlot.Plot.ScaleFactor"/>：高 DPI 下字体、线宽与边距随之放大，鼠标坐标仍是物理像素。
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float DisplayScale
    {
        get => _canvas?.DisplayScale ?? DeviceDpi / 96f;
        set
        {
            if (_canvas is not null)
                _canvas.DisplayScale = value;
        }
    }

    /// <summary>软件渲染，不使用 GPU 上下文（始终为 null，与 FormsPlot 相同）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GRContext GRContext => _canvas?.GRContext!;

    /// <inheritdoc />
    public override void Refresh()
    {
        if (_designerFallback || _canvas is null || _canvas.IsDisposed)
            return;

        // 只同步重绘画布：不重绘父控件背景，避免大面积背景闪烁
        _canvas.Invalidate();
        _canvas.Update();
    }

    /// <summary>
    /// 请求重绘：只把画布标记为失效，由消息循环合并后统一绘制。
    /// 与 <see cref="Refresh"/>（同步强制绘制）相比，在高频添加数据时开销更小，也不会阻塞调用线程。
    /// </summary>
    public void RequestRender()
    {
        if (_designerFallback || _canvas is null || _canvas.IsDisposed || Disposing)
            return;

        // 只让画布失效（不连带父控件），避免每次刷新都重绘父控件背景造成大面积闪烁。
        // 同一帧内已经请求过时不再重复调用 Invalidate（WinForms 会合并 WM_PAINT，
        // 重复调用只会多分配约 32 字节，高频逐点写入时这也是可观的 GC 来源）。
        _canvas.RequestRepaint();
    }

    /// <inheritdoc />
    public void Reset()
    {
        if (_designerFallback || _canvas is null)
            return;

        // 新建绘图对象并清空全部曲线的数据；曲线本身（名称、颜色等）保留并挂接到新的绘图对象上
        foreach (PlotSeries series in _series)
        {
            series.Data.Clear();
            series.ArrivalStartTime = null;
            series.RejectedValueCount = 0;
        }

        _xOffset = 0;
        _canvas.Reset();
        _canvas.Plot.DataBackground.Color = Colors.White;
        ApplyDefaultFont();
        AttachDataToCurrentPlot();
        RefreshXMapping();

        // 数据已清空：堆叠原点必须重新固定为零，否则下次从 0 开始写入时
        // X 轴仍停留在 Reset 前的原点（显示百分比 < 100 时新数据会落在视图之外）
        PinStackedOrigin();

        ApplyAutoScroll();
        RememberLimits();
        RequestRender();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Reset(Plot plot) => Reset(plot, disposeOldPlot: true);

    /// <inheritdoc />
    public void Reset(Plot plot, bool disposeOldPlot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        if (_designerFallback || _canvas is null)
            return;

        _canvas.Reset(plot, disposeOldPlot);
        _xOffset = 0;
        ApplyDefaultFont();
        AttachDataToCurrentPlot();
        ApplyAutoScroll();
        RememberLimits();
        RequestRender();
    }

    /// <inheritdoc />
    public float DetectDisplayScale() => _canvas?.DetectDisplayScale() ?? DeviceDpi / 96f;

    /// <inheritdoc />
    public void SetCursor(PlotCursor cursor)
    {
        if (_canvas is not null)
            _canvas.SetCursor(cursor);
    }

    /// <inheritdoc />
    public void ShowContextMenu(Pixel position)
    {
        if (_canvas is not null)
            _canvas.ShowContextMenu(position);
    }

    /// <summary>把像素坐标转换为数据坐标。</summary>
    public Coordinates GetCoordinates(Pixel pixel)
    {
        if (_designerFallback || _canvas is null)
            return new Coordinates(double.NaN, double.NaN);

        Plot plot = _canvas.Plot;
        EnsureAxesCurrent(plot);
        return plot.GetCoordinates(pixel, _xAxis!, _yAxis!);
    }

    #endregion

    #region 帮助

    /// <summary>
    /// 显示帮助：先触发 <see cref="ShowHelpRequested"/> 事件，若未被处理则弹出内置中文帮助窗口。
    /// </summary>
    public void ShowHelp()
    {
        HelpRequestedEventArgs args = new(PlotHelpContent.Text);
        ShowHelpRequested?.Invoke(this, args);

        if (args.Handled)
            return;

        // 使用带完整中文说明的 PlotHelpDialog（标题、模式、X 轴单位、鼠标操作、数据接口），
        // 而不是旧版 ScottPlot.FormHelp（只有鼠标操作提示）；关闭后自动释放。
        PlotHelpDialog dialog = new(args.Content);
        dialog.FormClosed += (_, _) => dialog.Dispose();

        if (FindForm() is { } owner)
            dialog.Show(owner);
        else
            dialog.Show();
    }

    #endregion

    #region 自动滚动

    private void AttachDataToCurrentPlot()
    {
        if (_designerFallback || _canvas is null)
            return;

        Plot? plot = _canvas.Plot;
        if (plot is null)
            return;

        foreach (PlotSeries series in _series)
            AttachSeries(series, plot);

        ApplyLegend();
        ApplyTickGenerator();
        HookRenderFinished(plot);

        // 新 Plot 的默认坐标轴：缓存引用供逐点写入使用（见 _xAxis / _yAxis）
        _xAxis = plot.Axes.Bottom;
        _yAxis = plot.Axes.Left;
    }

    /// <summary>
    /// 确认缓存的默认坐标轴仍属于当前 Plot（用户可能替换 / 删除过坐标轴）：
    /// 使用 <see cref="ICollection{T}.Contains"/> 检查（List 的接口实现，不产生分配），
    /// 只有真的发生变化时才重新调用 Bottom / Left（各分配约 40 字节）。
    /// </summary>
    private void EnsureAxesCurrent(Plot plot)
    {
        if (_xAxis is null || !ContainsAxis(plot.Axes.GetXAxes(), _xAxis))
            _xAxis = plot.Axes.Bottom;

        if (_yAxis is null || !ContainsAxis(plot.Axes.GetYAxes(), _yAxis))
            _yAxis = plot.Axes.Left;
    }

    private static bool ContainsAxis<T>(IEnumerable<T> axes, T axis) where T : class =>
        axes is ICollection<T> collection ? collection.Contains(axis) : axes.Contains(axis);

    /// <summary>为曲线在指定 Plot 上创建图元（Reset 替换 Plot 后重新调用；用户直接改过的样式会保留）。</summary>
    private void AttachSeries(PlotSeries series, Plot plot)
    {
        if (_designerFallback)
            return;

        series.CaptureStyle();

        // 用户在 Reset(当前 Plot, disposeOldPlot: false) 里传入同一个 Plot 时，旧图元仍在 Plot 中：
        // 先移除再新建，否则同一条曲线会被画两次、图例出现重复项。传入新 Plot 时这里是无操作。
        if (series.Plottable is { } previous)
            plot.Remove(previous);

        // 数据源直接引用曲线的内部列表与极值金字塔，后续追加数据无需重建；
        // 每个像素列用金字塔求极值，单帧耗时基本不随数据量增长（渲染结果与 ScottPlot 内置数据源逐像素一致）。
        // 线宽默认 1：SkiaSharp 对不超过 1 个设备像素的线走“发丝线”快速光栅化；
        // 1.5 需要先描边成多边形再做抗锯齿填充，数据密集时单帧耗时约为 1 的 2~4 倍（100 万点 13.8 ms → 3.8 ms）。
        // X 映射（Period / XOffset）由控件按当前单位设置：点数单位 X = 下标 + 1，时间单位见 ApplySeriesXMapping
        series.Plottable = plot.Add.Signal(new PyramidSignalSource(series.Data, period: 1.0));
        ApplySeriesXMapping(series);
        series.ApplyStyle();
    }

    /// <summary>
    /// 订阅当前 Plot 的渲染完成通知（合并写入据此按帧节流）。
    /// Reset 会替换 Plot，因此每次挂接数据时都重新订阅，并解除对旧 Plot 的订阅。
    /// </summary>
    private void HookRenderFinished(Plot plot)
    {
        if (_designerFallback || ReferenceEquals(_renderHookedPlot, plot))
            return;

        UnhookRenderFinished();
        plot.RenderManager.RenderFinished += OnPlotRenderFinished;
        _renderHookedPlot = plot;
    }

    private void UnhookRenderFinished()
    {
        if (_renderHookedPlot is null)
            return;

        // RenderFinished 是委托属性（非事件），ScottPlot 渲染时直接调用：移除后不能留下 null
        ScottPlot.Rendering.RenderManager manager = _renderHookedPlot.RenderManager;
        manager.RenderFinished = (manager.RenderFinished - OnPlotRenderFinished) ?? delegate { };
        _renderHookedPlot = null;
    }

    /// <summary>使用支持中文的字体，保证标题、坐标轴与图例中的中文正常显示。</summary>
    private void ApplyDefaultFont()
    {
        if (_designerFallback || _canvas is null)
            return;

        try
        {
            _canvas.Plot.Font.Set(DefaultFontName);
        }
        catch (Exception)
        {
            // 系统缺少该字体时保留 ScottPlot 的默认字体
        }
    }

    private void ApplyAutoScroll()
    {
        if (_applyingLimits || _designerFallback || _canvas is null)
            return;

        Plot? plot = _canvas.Plot;
        if (plot is null || plot.Axes is null)
            return;

        // 没有可见数据时给出稳定的空视图；X 轴右端 = 可见曲线最新 X 的最大值 + 留白（按最宽的数据点）
        ReadOnlySpan<SeriesXExtent> extents = VisibleExtents();
        if (extents.IsEmpty)
        {
            ApplyLimits(_axisOptions.EmptyXMin, _axisOptions.EmptyXMax, _axisOptions.EmptyYMin, _axisOptions.EmptyYMax);
            return;
        }

        if (!ScrollingAxisCalculator.TryCalculateXWindow(_mode, extents, _xAxisUnit, _axisOptions, _xOffset, _stackedOriginX, out double xMin, out double xMax))
            return; // 手动模式：不修改坐标轴

        if (!TryCalculateVisibleYRange(xMin, xMax, out double yMin, out double yMax))
            return;

        ApplyLimits(xMin, xMax, yMin, yMax);
    }

    private bool TryCalculateVisibleYRange(double xMin, double xMax, out double yMin, out double yMax)
    {
        // Y 轴按“当前显示的数据”统计，取所有可见曲线的并集。
        // 某条曲线的窗口覆盖其全部数据时（堆叠模式显示 100% 的默认情况）直接取运行极值；
        // 确实裁掉了数据时（跟随模式、堆叠模式显示百分比 < 100、拖动后继续滚动）查询极值金字塔，
        // 开销与窗口长度无关，不会再出现“每追加一个点就扫描一遍可见窗口”的 O(N²) 行为。
        double dataMin = double.PositiveInfinity;
        double dataMax = double.NegativeInfinity;
        bool hasRange = false;

        foreach (PlotSeries series in _series)
        {
            PlotDataBuffer data = series.Data;
            if (data.Count == 0 || !series.IsVisible)
                continue;

            // 曲线的数据（X 从 XStart 到 LatestX；点数单位即 1 ~ Count）与窗口完全不重叠时跳过：按 X 范围查询会把越界窗口裁剪到最近的一个点，
            // 单曲线时窗口总包含最新数据所以无碍，多曲线时较短（或时间上更早结束）的曲线可能整条落在窗口之外，不能让它的端点参与缩放。
            // 每条曲线按自己的“下标 ↔ X”映射把窗口换算成下标区间再查极值金字塔
            double firstX = data.XStart;
            double latestX = data.LatestX;

            if (xMax < firstX || xMin > latestX)
                continue;

            bool coversAllData = xMin <= firstX && xMax >= latestX;
            bool found = coversAllData
                ? data.TryGetValueRange(out double seriesMin, out double seriesMax)
                : data.TryGetValueRange(xMin, xMax, out seriesMin, out seriesMax);

            if (found)
                Include(seriesMin, seriesMax);
        }

        // 可见窗口里没有任何曲线的数据（例如拖到了数据之外）：退回各可见曲线的全部数据范围
        if (!hasRange)
        {
            foreach (PlotSeries series in _series)
            {
                if (series.IsVisible && series.Data.TryGetValueRange(out double seriesMin, out double seriesMax))
                    Include(seriesMin, seriesMax);
            }
        }

        if (!hasRange)
        {
            yMin = _axisOptions.EmptyYMin;
            yMax = _axisOptions.EmptyYMax;
            return true;
        }

        return ScrollingAxisCalculator.TryCalculateYWindow(dataMin, dataMax, _axisOptions, out yMin, out yMax);

        void Include(double min, double max)
        {
            if (min < dataMin)
                dataMin = min;
            if (max > dataMax)
                dataMax = max;
            hasRange = true;
        }
    }

    private void ApplyLimits(double xMin, double xMax, double yMin, double yMax)
    {
        if (_designerFallback || _canvas is null)
            return;

        Plot? plot = _canvas.Plot;
        if (plot is null)
            return;

        _applyingLimits = true;
        try
        {
            EnsureAxesCurrent(plot);
            plot.Axes.SetLimits(xMin, xMax, yMin, yMax, _xAxis!, _yAxis!);
        }
        finally
        {
            _applyingLimits = false;
        }
    }

    #endregion

    #region 鼠标操作（只观察，不拦截）

    /// <summary>
    /// 鼠标手势是否正在进行（已按下鼠标键并且产生了拖动）。
    /// 手势期间坐标轴完全交给用户操作，新数据不会触发自动滚动。
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsUserGestureInProgress => _gestureButton != MouseButtons.None && _gestureMoved;

    private void OnPlotMouseDown(object? sender, MouseEventArgs e)
    {
        if (_gestureButton != MouseButtons.None)
            return;

        _gestureButton = e.Button;
        _gestureStartPoint = e.Location;
        _gestureMoved = false;
        _limitsAtGestureStart = ReadLimits();
    }

    private void OnPlotMouseMove(object? sender, MouseEventArgs e)
    {
        if (_gestureButton == MouseButtons.None || _gestureMoved)
            return;

        int deltaX = Math.Abs(e.X - _gestureStartPoint.X);
        int deltaY = Math.Abs(e.Y - _gestureStartPoint.Y);

        if (Math.Max(deltaX, deltaY) >= DragThresholdPixels)
            _gestureMoved = true;
    }

    private void OnPlotMouseUp(object? sender, MouseEventArgs e)
    {
        MouseButtons button = _gestureButton;
        bool moved = _gestureMoved;
        AxisLimits? limitsBefore = _limitsAtGestureStart;

        _gestureButton = MouseButtons.None;
        _gestureMoved = false;
        _limitsAtGestureStart = null;

        if (button == MouseButtons.None || button != e.Button)
            return;

        AxisLimits? limitsAfter = ReadLimits();
        if (limitsBefore is null || limitsAfter is null)
            return;

        if (!ViewChanged(limitsBefore.Value, limitsAfter.Value))
        {
            RememberLimits(limitsAfter);
            return;
        }

        PlotViewGesture gesture = ClassifyGesture(button, moved);
        if (gesture == PlotViewGesture.None)
        {
            RememberLimits(limitsAfter);
            return;
        }

        HandleViewGesture(gesture, limitsAfter.Value, limitsBefore);
    }

    private void OnPlotMouseWheel(object? sender, MouseEventArgs e)
    {
        // 滚轮缩放已经由 ScottPlot 处理完毕，这里只判断视图是否真的发生了变化
        AxisLimits? limitsBefore = _lastKnownLimits;
        AxisLimits? limitsAfter = ReadLimits();

        if (limitsBefore is null || limitsAfter is null)
        {
            RememberLimits(limitsAfter);
            return;
        }

        if (!ViewChanged(limitsBefore.Value, limitsAfter.Value))
        {
            RememberLimits(limitsAfter);
            return;
        }

        HandleViewGesture(PlotViewGesture.WheelZoom, limitsAfter.Value, limitsBefore);
    }

    private static PlotViewGesture ClassifyGesture(MouseButtons button, bool moved) => (button, moved) switch
    {
        (MouseButtons.Left, true) => PlotViewGesture.LeftDragPan,
        (MouseButtons.Middle, true) => PlotViewGesture.MiddleDragZoomRectangle,
        (MouseButtons.Middle, false) => PlotViewGesture.Autoscale, // 中键单击 = 自动缩放
        (MouseButtons.Right, true) => PlotViewGesture.RightDragZoom,
        _ => PlotViewGesture.None,
    };

    /// <summary>根据用户操作调整自动滚动状态（需求 3、6、7）。</summary>
    private void HandleViewGesture(PlotViewGesture gesture, AxisLimits currentLimits, AxisLimits? fallbackLimits)
    {
        // 中键单击 / 菜单“自动缩放” = 整体显示，单独处理
        if (gesture == PlotViewGesture.Autoscale)
        {
            HandleAutoscaleGesture(currentLimits, fallbackLimits);
            return;
        }

        if (_mode == ScrollingMode.Manual)
        {
            // 已经停止滚动：其它鼠标操作保持用户当前视图
            RestoreLimitsIfInvalid(currentLimits, fallbackLimits);
            return;
        }

        bool latestVisible = IsLatestDataVisible(currentLimits);
        AutoScrollDecision decision = AutoScrollPolicy.Decide(_mode, gesture, latestVisible, _axisOptions);

        switch (decision.Action)
        {
            case AutoScrollAction.StopScrolling:
                SetMode(ScrollingMode.Manual, ScrollingModeChangeReason.UserInteraction);
                break;

            case AutoScrollAction.KeepScrollingAtCurrentXMin:
                _xOffset = CalculateOffsetFromCurrentView(currentLimits);
                break;

            case AutoScrollAction.ResetOffset:
                _xOffset = 0;
                break;
        }

        RestoreLimitsIfInvalid(currentLimits, fallbackLimits);
    }

    /// <summary>
    /// 处理“整体显示”（鼠标中键单击或菜单“自动缩放”）：
    /// 退出停止滚动状态，并按需切换模式（跟随模式会自动切换为堆叠模式），最后复位 X 轴偏移。
    /// </summary>
    private void HandleAutoscaleGesture(AxisLimits currentLimits, AxisLimits? fallbackLimits)
    {
        AutoScrollDecision decision = AutoScrollPolicy.Decide(
            _mode, PlotViewGesture.Autoscale, IsLatestDataVisible(currentLimits), _axisOptions);

        if (decision.Action is not (AutoScrollAction.ResumePreviousMode or AutoScrollAction.ResetOffset))
        {
            // 相关开关被关闭：整体显示不改变滚动状态
            RestoreLimitsIfInvalid(currentLimits, fallbackLimits);
            return;
        }

        ScrollingMode target = AutoScrollPolicy.ResolveAutoscaleMode(_mode, _lastAutoScrollMode, _axisOptions);

        if (target != _mode)
        {
            // 例如：跟随模式 → 堆叠模式（需求补充）
            SetMode(target, ScrollingModeChangeReason.Autoscale);
            return;
        }

        _xOffset = 0;
        RestoreLimitsIfInvalid(currentLimits, fallbackLimits);
    }

    /// <summary>
    /// 兜底保护：如果用户操作产生了非法（无限/退化）的坐标轴范围，
    /// 则恢复为操作之前的有效范围，避免渲染异常导致崩溃。
    /// </summary>
    private void RestoreLimitsIfInvalid(AxisLimits currentLimits, AxisLimits? fallbackLimits)
    {
        if (IsUsableLimits(currentLimits))
        {
            RememberLimits(currentLimits);
            return;
        }

        if (fallbackLimits is { } fallback && IsUsableLimits(fallback))
        {
            ApplyLimits(fallback.Left, fallback.Right, fallback.Bottom, fallback.Top);
            RememberLimits(fallback);
            RequestRender();
            return;
        }

        RememberLimits();
    }

    private static bool IsUsableLimits(AxisLimits limits) =>
        limits.IsReal
        && double.IsFinite(limits.Left)
        && double.IsFinite(limits.Right)
        && double.IsFinite(limits.Bottom)
        && double.IsFinite(limits.Top)
        && limits.Left != limits.Right
        && limits.Bottom != limits.Top;

    /// <summary>把当前 X 轴最小值换算为自动滚动窗口的偏移量。</summary>
    private double CalculateOffsetFromCurrentView(AxisLimits limits)
    {
        double currentXMin = Math.Min(limits.Left, limits.Right);

        if (!ScrollingAxisCalculator.TryCalculateXWindow(_mode, VisibleExtents(), _xAxisUnit, _axisOptions, 0, _stackedOriginX, out double autoXMin, out _))
            return _xOffset;

        double offset = currentXMin - autoXMin;
        return double.IsFinite(offset) ? offset : _xOffset;
    }

    /// <summary>
    /// 判断最新数据点（各可见曲线最后一个点中 X 最大的那个；点数单位即最长曲线的最后一个点）
    /// 是否在当前可见的 X 轴范围内（需求 6、7）。
    /// </summary>
    private bool IsLatestDataVisible(AxisLimits limits)
    {
        ReadOnlySpan<SeriesXExtent> extents = VisibleExtents();
        if (extents.IsEmpty || !limits.IsRealX)
            return true;

        double latestX = double.NegativeInfinity;
        foreach (SeriesXExtent extent in extents)
            latestX = Math.Max(latestX, extent.LatestX);

        double xMin = Math.Min(limits.Left, limits.Right);
        double xMax = Math.Max(limits.Left, limits.Right);
        double span = xMax - xMin;
        double tolerance = span > 0 ? span * _axisOptions.LatestVisibilityToleranceFraction : 0;

        return latestX >= xMin - tolerance && latestX <= xMax + tolerance;
    }

    /// <summary>供右键菜单“自动缩放”调用：保持当前模式并复位偏移。</summary>
    internal void HandleAutoscaleRequested()
    {
        AxisLimits? fallback = _lastKnownLimits;
        AxisLimits? limits = ReadLimits();
        if (limits is null)
            return;

        HandleViewGesture(PlotViewGesture.Autoscale, limits.Value, fallback);
    }

    /// <summary>记录菜单，便于在控件释放时统一清理。</summary>
    internal void TrackContextMenu(ContextMenuStrip menu)
    {
        foreach (ContextMenuStrip previous in _contextMenus)
            previous.Dispose();

        _contextMenus.Clear();
        _contextMenus.Add(menu);
    }

    private AxisLimits? ReadLimits()
    {
        if (_designerFallback || _canvas is null)
            return null;

        Plot? plot = _canvas.Plot;
        if (plot is null)
            return null;

        EnsureAxesCurrent(plot);
        AxisLimits limits = plot.Axes.GetLimits(_xAxis!, _yAxis!);
        return limits.IsReal ? limits : null;
    }

    private void RememberLimits() => _lastKnownLimits = ReadLimits();

    private void RememberLimits(AxisLimits? limits) =>
        _lastKnownLimits = limits is { IsReal: true } value ? value : null;

    private static bool ViewChanged(AxisLimits a, AxisLimits b) =>
        a.Left != b.Left || a.Right != b.Right || a.Bottom != b.Bottom || a.Top != b.Top;

    #endregion

    #region 线程与释放

    /// <summary>已执行的跨线程合并写入次数（诊断 / 测试用）。</summary>
    internal int PendingFlushCount { get; private set; }

    /// <summary>帧率上限使用的定时器是否为高精度定时器（未设置帧率上限时为 null；诊断 / 测试用）。</summary>
    internal bool? FrameTimerIsHighResolution => _frameTimer?.IsHighResolution;

    /// <summary>
    /// 投递一次合并写入（任意线程）。同一时刻最多只有一条排队中的投递：已投递且未超时时直接返回，
    /// 因此无论有多少条曲线、采集线程调用多频繁，窗口消息队列里都只有一条消息，不会再出现
    /// “每个点一条 BeginInvoke 把消息队列淹没、WM_PAINT / WM_TIMER 被饿死”的问题。
    /// 设置了 <see cref="MaxFramesPerSecond"/> 时，距上一次合并写入不足一帧间隔的投递由高精度定时器到点再发出。
    /// </summary>
    private void PostPendingFlush()
    {
        long now = Stopwatch.GetTimestamp();
        long interval = Interlocked.Read(ref _minFlushIntervalTicks);
        long postedAt = Interlocked.Read(ref _flushPostedAt);

        // 已有排队中的合并写入（含按帧率上限等待中的）：它会把新数据一起取走。
        // 超时兜底：控件不可见、渲染被关闭等情况下收不到渲染完成通知，此时重新投递，保证数据不会滞留
        long timeout = Math.Max(PendingFlushTimeoutTicks, interval * 2);
        if (postedAt != 0 && now - postedAt < timeout)
            return;

        if (Interlocked.CompareExchange(ref _flushPostedAt, now, postedAt) != postedAt)
            return; // 其它线程抢先投递了

        if (interval > 0 && _frameTimer is { } timer)
        {
            long due = Interlocked.Read(ref _lastFlushTimestamp) + interval;

            if (due > now)
            {
                timer.Schedule(due);
                return;
            }
        }

        BeginInvokeFlush();
    }

    private static readonly long PendingFlushTimeoutTicks = (long)(PendingFlushTimeout.TotalSeconds * Stopwatch.Frequency);

    /// <summary>帧率上限的定时到点（后台线程）：投递这一帧的合并写入。</summary>
    private void OnFrameTimerDue() => BeginInvokeFlush();

    private void BeginInvokeFlush()
    {
        try
        {
            BeginInvoke(_flushPendingAction);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // 句柄尚未创建或正在销毁：撤销标记，句柄创建后由 OnHandleCreated 补投
            Interlocked.Exchange(ref _flushPostedAt, 0);
            Interlocked.Exchange(ref _flushRetryOnHandleCreated, 1);
        }
    }

    /// <summary>UI 线程：一次取走全部曲线的待处理数据，整批只做一次坐标轴计算与一次重绘请求。</summary>
    private void FlushPendingValues()
    {
        if (IsDisposed || Disposing)
            return;

        PendingFlushCount++;
        Interlocked.Exchange(ref _lastFlushTimestamp, Stopwatch.GetTimestamp());

        bool renderExpected = IsRenderExpected();

        if (renderExpected)
        {
            // 先置位再写入：即使 DataChanged 的订阅方在写入过程中同步重绘，也不会错过渲染完成通知
            _flushAwaitingRender = true;
        }
        else
        {
            // 不会有渲染完成通知：先复位再交换，交换之后到达的数据一定会触发下一次投递，不会滞留
            Interlocked.Exchange(ref _flushPostedAt, 0);
        }

        // 锁内只交换各曲线的待处理列表，写入在锁外进行，采集线程几乎不会被阻塞
        lock (_pendingLock)
        {
            foreach (PlotSeries series in _series)
            {
                series.DrainClear = series.PendingClear;
                series.PendingClear = false;
                (series.PendingValues, series.DrainingValues) = (series.DrainingValues, series.PendingValues);
            }
        }

        bool cleared = false;
        bool mappingChanged = false;
        int added = 0;

        foreach (PlotSeries series in _series)
        {
            // 同一曲线先清空、再追加清空之后到达的数据（清空之前排队的数据在清空请求时已丢弃）
            if (series.DrainClear)
            {
                series.DrainClear = false;
                series.Data.Clear();
                series.ArrivalStartTime = null;
                series.RejectedValueCount = 0;
                cleared = true;
                mappingChanged = true;
            }

            List<double> batch = series.DrainingValues;

            // 绝对时间单位下没有起始时间的曲线（只可能是没人设置起始时间的默认曲线）拒收：在 UI 线程上判断，采集线程不会收到异常
            if (batch.Count > 0 && RejectsData(series))
            {
                series.RejectedValueCount += batch.Count;
                batch.Clear();
            }

            if (batch.Count > 0)
            {
                int accepted = series.Data.AddRange(CollectionsMarshal.AsSpan(batch));
                batch.Clear();
                added += accepted;

                // 清空后的第一批数据：记录到达时刻（起始时间由它决定时需要重算 X 映射，循环结束后统一重算一次）
                if (accepted > 0 && series.ArrivalStartTime is null)
                {
                    series.ArrivalStartTime = DateTime.Now;
                    mappingChanged |= series.UsesArrivalStartTime;
                }
            }
        }

        if (mappingChanged)
            RefreshXMapping();

        if (cleared)
            OnDataCleared();

        if (added > 0)
            UpdateView();

        // 已请求重绘时等这一帧画完（OnPlotRenderFinished）再允许下一次投递：
        // 两次合并写入之间消息队列是空的，WM_PAINT / WM_TIMER 一定能插进来，写入频率自动跟随帧率
        if (renderExpected && !cleared && added == 0)
        {
            _flushAwaitingRender = false;
            CompletePendingFlush();
        }
    }

    private void OnPlotRenderFinished(object? sender, RenderDetails e)
    {
        if (!_flushAwaitingRender)
            return;

        _flushAwaitingRender = false;
        CompletePendingFlush();
    }

    /// <summary>结束当前的合并写入；期间又有新数据到达时立即投递下一次（受帧率上限约束）。</summary>
    private void CompletePendingFlush()
    {
        Interlocked.Exchange(ref _flushPostedAt, 0);

        bool hasPending = false;

        lock (_pendingLock)
        {
            foreach (PlotSeries series in _series)
            {
                if (series.PendingValues.Count > 0 || series.PendingClear)
                {
                    hasPending = true;
                    break;
                }
            }
        }

        if (hasPending)
            PostPendingFlush();
    }

    /// <summary>刚请求的重绘是否会真正发生（不会发生时合并写入不能等渲染完成通知）。</summary>
    private bool IsRenderExpected()
    {
        if (_designerFallback || _canvas is null)
            return false;

        PlotCanvas canvas = _canvas;

        if (canvas.IsDisposed || !canvas.IsHandleCreated || !canvas.Visible || canvas.Width <= 0 || canvas.Height <= 0)
            return false;

        if (FindForm() is { WindowState: FormWindowState.Minimized })
            return false;

        return canvas.Plot.RenderManager.EnableRendering;
    }

    private void InitializeComponent()
    {

    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_canvas is not null)
            {
                _canvas.MouseDown -= OnPlotMouseDown;
                _canvas.MouseMove -= OnPlotMouseMove;
                _canvas.MouseUp -= OnPlotMouseUp;
                _canvas.MouseWheel -= OnPlotMouseWheel;
            }

            UnhookRenderFinished();

            _frameTimer?.Dispose();
            _frameTimer = null;

            lock (_pendingLock)
            {
                foreach (PlotSeries series in _series)
                {
                    series.PendingValues.Clear();
                    series.PendingClear = false;
                }
            }

            foreach (ContextMenuStrip menu in _contextMenus)
                menu.Dispose();

            _contextMenus.Clear();
        }

        base.Dispose(disposing);
    }

    #endregion
}
