using System.ComponentModel;
using System.Runtime.InteropServices;
using ScottPlot;
using ScottPlot.Interactivity;
using ScottPlot.WinForms;
using SkiaSharp;
using PlotColor = ScottPlot.Color;
using PlotCursor = ScottPlot.Cursor;

namespace ScottPlotEx.Controls;

/// <summary>
/// 自绘画布：SkiaSharp 直接渲染进 GDI DIB section 的像素缓冲，WM_PAINT 中一次 BitBlt 到窗口。
/// <para>
/// 取代 ScottPlot.WinForms 的 <see cref="FormsPlot"/>：FormsPlot 的画布每帧先把画面画进 GDI+ Bitmap，
/// 再用 GDI+ DrawImage 拷到窗口，这一步在 1100×660 时约 3 ms、1920×1080 时约 6~8 ms，占整帧耗时的六到七成；
/// DIB + BitBlt 不经过 GDI+、也不做像素格式转换。
/// </para>
/// <para>
/// 输入、菜单、背景色、Reset 等行为与 <see cref="FormsPlotBase"/> 保持一致：鼠标 / 键盘消息先经
/// <see cref="FormsPlotExtensions"/> 转发给 <see cref="UserInputProcessor"/>，再触发控件自身的事件，
/// 因此外部观察者看到的始终是 ScottPlot 已经处理过的结果。
/// 与 FormsPlot 的区别：<see cref="DisplayScale"/> 会同步设置 <see cref="ScottPlot.Plot.ScaleFactor"/>（高 DPI 下字体与线宽随之缩放）；
/// 不支持透明背景（BitBlt 不做 Alpha 混合）。
/// </para>
/// </summary>
[ToolboxItem(false)]
internal sealed class PlotCanvas : Control, IPlotControl
{
    private const int SrcCopy = 0x00CC0020;
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;

    private IntPtr _memoryDc;
    private IntPtr _dibSection;
    private IntPtr _previousBitmap;
    private SKSurface? _surface;
    private Size _surfaceSize;
    private float _displayScale = 1f;
    private int _repaintRequested;
    private bool _renderFallback;

    /// <summary>创建画布（含默认 Plot、Multiplot 与输入处理器）。</summary>
    public PlotCanvas()
    {
        // 每帧都由 SkiaSharp 画满整个客户区：不擦除背景；DIB 本身就是后台缓冲，不需要 WinForms 双缓冲
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);

        Plot = new Plot { PlotControl = this };
        Plot.FigureBackground.Color = PlotColor.FromColor(SystemColors.Control);
        Plot.DataBackground.Color = Colors.White;
        Multiplot = new Multiplot(Plot);
        UserInputProcessor = new UserInputProcessor(this);
        DisplayScale = DetectDisplayScale();
    }

    #region IPlotControl

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Plot Plot { get; private set; }

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IMultiplot Multiplot { get; set; }

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IPlotMenu? Menu { get; set; }

    /// <inheritdoc />
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public UserInputProcessor UserInputProcessor { get; }

    /// <summary>软件渲染，不使用 GPU 上下文（与 FormsPlot 相同）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GRContext GRContext => null!;

    /// <summary>
    /// 显示缩放比例（DPI / 96）。设置时同步设置 <see cref="ScottPlot.Plot.ScaleFactor"/>：
    /// ScottPlot 的像素坐标（鼠标位置、GetPixel / GetCoordinates）始终是物理像素，ScaleFactor 只放大字体、线宽与边距。
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float DisplayScale
    {
        get => _displayScale;
        set
        {
            _displayScale = float.IsFinite(value) && value > 0 ? value : 1f;
            Plot.ScaleFactor = _displayScale;
        }
    }

    /// <summary>按控件所在显示器的 DPI 计算缩放比例（不会强制创建窗口句柄）。</summary>
    public float DetectDisplayScale() => DeviceDpi / 96f;

    /// <summary>与 FormsPlotBase 一致：控件背景色同时作为图表的外框背景色。</summary>
    public override System.Drawing.Color BackColor
    {
        get => base.BackColor;
        set
        {
            base.BackColor = value;

            if (Plot is not null)
                Plot.FigureBackground.Color = PlotColor.FromColor(value);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        Plot plot = new();
        plot.FigureBackground.Color = PlotColor.FromColor(BackColor);
        plot.DataBackground.Color = Colors.White;
        Reset(plot);
    }

    /// <inheritdoc />
    public void Reset(Plot plot) => Reset(plot, disposeOldPlot: true);

    /// <summary>替换绘图对象（与 FormsPlotBase.Reset 相同；传入当前对象时不会把它释放掉）。</summary>
    public void Reset(Plot plot, bool disposeOldPlot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        Plot oldPlot = Plot;
        Plot = plot;

        if (disposeOldPlot && !ReferenceEquals(oldPlot, plot))
            oldPlot.Dispose();

        Plot.PlotControl = this;
        Plot.ScaleFactor = _displayScale;
        UserInputProcessor.Reset();
        Multiplot.Reset(plot);
    }

    /// <inheritdoc />
    public void ShowContextMenu(Pixel position) => Menu?.ShowContextMenu(position);

    /// <inheritdoc />
    public void SetCursor(PlotCursor cursor) => Cursor = cursor.GetCursor();

    #endregion

    #region 绘制

    /// <summary>
    /// 请求合并重绘：同一帧内重复调用只生效一次。
    /// WinForms 本身会合并 WM_PAINT，但每次 <see cref="Control.Invalidate()"/> 都会分配约 32 字节；
    /// 高频逐点写入时（每点一次 RequestRender）这些分配会形成可观的 GC 压力。
    /// </summary>
    internal void RequestRepaint()
    {
        if (Interlocked.Exchange(ref _repaintRequested, 1) == 0)
            Invalidate();
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        // 先清标志：绘制过程中再次请求重绘（例如 DataChanged 处理器里继续写数据）仍会触发下一帧
        Interlocked.Exchange(ref _repaintRequested, 0);

        if (_renderFallback)
        {
            DrawRenderFallback(e.Graphics);
            base.OnPaint(e);
            return;
        }

        Size size = ClientSize;

        if (size.Width > 0 && size.Height > 0)
        {
            try
            {
                SKSurface surface = EnsureSurface(size);
                bool singlePass = SinglePassRendering && IsSinglePlot() && Plot.RenderManager.EnableRendering;

                RenderFrame(surface, singlePass);

                // 第一遍渲染时 AxisLimitsChanged 的处理器（联动坐标轴、用户代码）又改动了坐标轴：
                // 按 ScottPlot 原本的方式再渲染，直到坐标轴稳定，保证画面反映最终范围
                if (singlePass && AxisLimitsChangedSinceLastRender(Plot))
                    RenderFrame(surface, singlePass: false);

                surface.Canvas.Flush();

                IntPtr hdc = e.Graphics.GetHdc();
                try
                {
                    BitBlt(hdc, 0, 0, size.Width, size.Height, _memoryDc, 0, 0, SrcCopy);
                }
                finally
                {
                    e.Graphics.ReleaseHdc(hdc);
                }
            }
            catch (Exception exception) when (DesignerSupport.IsNativeLibraryFailure(exception))
            {
                // 设计器 / 缺少 SkiaSharp 原生库：不要抛到设计器，画一段说明文字即可
                _renderFallback = true;
                ReleaseSurface();
                DrawRenderFallback(e.Graphics);
            }
        }

        // 最后再触发 Paint 事件：订阅方画在曲线之上
        base.OnPaint(e);
    }

    private void DrawRenderFallback(Graphics graphics)
    {
        graphics.Clear(BackColor);
        TextRenderer.DrawText(
            graphics,
            "ScrollingPlotControl\r\n\r\n无法加载 SkiaSharp 原生库（libSkiaSharp）。\r\n" +
            "设计器只显示占位视图；运行时请确保输出目录包含 runtimes/win-*/native/libSkiaSharp.dll。",
            Font,
            ClientRectangle,
            SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    /// <summary>
    /// 每帧只渲染一遍（默认开启）。
    /// ScottPlot 的 RenderManager.Render 只要发现“坐标轴与上一帧不同”就会再渲染一遍（循环到坐标轴不再变化为止），
    /// 而本控件每次追加数据都会移动坐标轴，于是每个滚动帧都要渲染两遍、第二遍画出完全相同的画面。
    /// 开启后，本帧第一遍渲染完成即暂停渲染（后续的重复渲染直接返回），渲染结束后恢复；
    /// AxisLimitsChanged 事件照常触发，若其处理器改动了坐标轴则立即按原方式补渲。
    /// </summary>
    internal bool SinglePassRendering { get; set; } = true;

    private Plot? _renderHookedPlot;
    private bool _pauseAfterFirstPass;
    private bool _renderingPausedByCanvas;

    private void RenderFrame(SKSurface surface, bool singlePass)
    {
        HookRenderFinished();
        _pauseAfterFirstPass = singlePass;

        // ScottPlot 的 RenderManager.Render 会对画布执行 canvas.Scale(ScaleFactor) 且不恢复；
        // FormsPlot 每帧新建表面所以不受影响，而这里的表面跨帧复用，必须每帧恢复变换矩阵，
        // 否则 ScaleFactor ≠ 1 时缩放会逐帧累积（1.5 → 2.25 → ……）。
        int saveCount = surface.Canvas.Save();
        try
        {
            Multiplot.Render(surface);
        }
        finally
        {
            surface.Canvas.RestoreToCount(saveCount);
            _pauseAfterFirstPass = false;

            if (_renderingPausedByCanvas)
            {
                _renderingPausedByCanvas = false;
                Plot.RenderManager.EnableRendering = true;
            }
        }
    }

    /// <summary>本帧第一遍渲染完成：暂停渲染，让 ScottPlot 随后的重复渲染直接返回。</summary>
    private void OnPlotRenderFinished(object? sender, RenderDetails e)
    {
        if (!_pauseAfterFirstPass)
            return;

        _pauseAfterFirstPass = false;
        _renderingPausedByCanvas = true;
        Plot.RenderManager.EnableRendering = false;
    }

    /// <summary>只有 Multiplot 中仅有本控件的 Plot 时才做单遍渲染（多子图保持 ScottPlot 原样）。</summary>
    private bool IsSinglePlot() =>
        Multiplot.Subplots.Count == 1 && ReferenceEquals(Multiplot.Subplots.GetPlot(0), Plot);

    /// <summary>当前坐标轴范围是否与最近一次渲染时记录的范围不同（坐标轴增删也算变化）。</summary>
    private static bool AxisLimitsChangedSinceLastRender(Plot plot)
    {
        Dictionary<IAxis, CoordinateRange>? rendered = plot.RenderManager.LastRender.AxisLimitsByAxis;
        IAxis[] axes = [.. plot.Axes.GetAxes()];

        if (rendered is null || rendered.Count != axes.Length)
            return true;

        foreach (IAxis axis in axes)
        {
            if (!rendered.TryGetValue(axis, out CoordinateRange range))
                return true;

            CoordinateRange now = axis.Range.ToCoordinateRange;
            if (!SameValue(now.Min, range.Min) || !SameValue(now.Max, range.Max))
                return true;
        }

        return false;

        static bool SameValue(double a, double b) => a == b || (double.IsNaN(a) && double.IsNaN(b));
    }

    /// <summary>订阅当前 Plot 的渲染完成通知（Reset 替换 Plot 后改为订阅新的 Plot）。</summary>
    private void HookRenderFinished()
    {
        if (ReferenceEquals(_renderHookedPlot, Plot))
            return;

        UnhookRenderFinished();
        Plot.RenderManager.RenderFinished += OnPlotRenderFinished;
        _renderHookedPlot = Plot;
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

    /// <summary>按客户区尺寸（重新）创建 DIB section 及包装它的 SkiaSharp 表面。</summary>
    private SKSurface EnsureSurface(Size size)
    {
        if (_surface is not null && size == _surfaceSize)
            return _surface;

        ReleaseSurface();

        BitmapInfoHeader header = new()
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size.Width,
            Height = -size.Height, // 负值 = 自上而下的行序，与 SkiaSharp 一致
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
        };

        _dibSection = CreateDIBSection(IntPtr.Zero, ref header, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
        if (_dibSection == IntPtr.Zero || bits == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建绘图缓冲区（CreateDIBSection 失败）。");

        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        _previousBitmap = SelectObject(_memoryDc, _dibSection);

        // DIB 的 32 位像素布局为 BGRA，与 SkiaSharp 的 Bgra8888 完全一致，渲染结果可以直接 BitBlt
        _surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), bits, size.Width * 4)
            ?? throw new InvalidOperationException("无法在绘图缓冲区上创建 SkiaSharp 表面。");

        _surfaceSize = size;
        return _surface;
    }

    private void ReleaseSurface()
    {
        _surface?.Dispose();
        _surface = null;

        if (_memoryDc != IntPtr.Zero)
        {
            SelectObject(_memoryDc, _previousBitmap);
            DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
            _previousBitmap = IntPtr.Zero;
        }

        if (_dibSection != IntPtr.Zero)
        {
            DeleteObject(_dibSection);
            _dibSection = IntPtr.Zero;
        }

        _surfaceSize = Size.Empty;
    }

    /// <summary>画布所在显示器的 DPI 改变（拖到另一块屏幕）时重新计算缩放比例。</summary>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        DisplayScale = DetectDisplayScale();
        Invalidate();
        base.OnDpiChangedAfterParent(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            UnhookRenderFinished();

        ReleaseSurface();
        base.Dispose(disposing);
    }

    #endregion

    #region 输入（与 FormsPlotBase 相同：先交给 ScottPlot 处理，再触发控件自身的事件）

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        UseDpiAwareDragPan();
        UserInputProcessor.ProcessMouseDown(e);
        base.OnMouseDown(e);
    }

    /// <summary>
    /// ScaleFactor ≠ 1 时，把 ScottPlot 的 <see cref="ScottPlot.Interactivity.UserActionResponses.MouseDragPan"/>
    /// 换成拖动距离正确的 <see cref="DpiAwareMouseDragPan"/>（保留原有设置与位置）。
    /// 每次按下鼠标前检查一次：UserInputProcessor.Reset() / LeftClickDragPan() 重新加入的默认响应也会被替换；
    /// ScaleFactor = 1 时保持 ScottPlot 原样。
    /// </summary>
    private void UseDpiAwareDragPan()
    {
        if (Plot.ScaleFactor == 1)
            return;

        List<IUserActionResponse> responses = UserInputProcessor.UserActionResponses;

        for (int i = 0; i < responses.Count; i++)
        {
            if (responses[i].GetType() == typeof(ScottPlot.Interactivity.UserActionResponses.MouseDragPan))
                responses[i] = DpiAwareMouseDragPan.From((ScottPlot.Interactivity.UserActionResponses.MouseDragPan)responses[i]);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseEventArgs e)
    {
        UserInputProcessor.ProcessMouseUp(e);
        base.OnMouseUp(e);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        UserInputProcessor.ProcessMouseMove(e);
        base.OnMouseMove(e);
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        UserInputProcessor.ProcessMouseWheel(e);
        base.OnMouseWheel(e);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        UserInputProcessor.ProcessKeyDown(e);
        base.OnKeyDown(e);
    }

    /// <summary>方向键默认不会进入 KeyDown，FormsPlot 也是在 PreviewKeyDown 里交给 ScottPlot 处理的。</summary>
    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        UserInputProcessor.ProcessKeyDown(e);
        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        UserInputProcessor.ProcessKeyUp(e);
        base.OnKeyUp(e);
    }

    /// <summary>失去焦点：交给 ScottPlot（会调用 <see cref="UserInputProcessor.LostFocusAction"/>，可由外部自定义）。</summary>
    protected override void OnLostFocus(EventArgs e)
    {
        UserInputProcessor.ProcessLostFocus();
        base.OnLostFocus(e);
    }

    #endregion

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int width, int height, IntPtr sourceDc, int sourceX, int sourceY, int rasterOperation);

    #endregion
}
