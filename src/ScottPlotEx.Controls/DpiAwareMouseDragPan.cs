using ScottPlot;
using ScottPlot.Interactivity;
using ScottPlot.Interactivity.UserActionResponses;

namespace ScottPlotEx.Controls;

/// <summary>
/// 与 ScottPlot 的 <see cref="MouseDragPan"/> 行为完全相同的鼠标拖动平移，只修正了高 DPI 下的拖动距离。
/// <para>
/// ScottPlot 5.1.59 的 <c>MouseAxisManipulation.DragPan</c> 先把鼠标像素除以 <see cref="Plot.ScaleFactor"/>，
/// 随后又把位移再除以一次，因此 ScaleFactor ≠ 1 时拖动距离只有鼠标移动距离的 1/ScaleFactor（曲线不跟手）。
/// FormsPlot 从不设置 ScaleFactor，所以这个问题此前不会出现；本控件的画布会按 DPI 设置 ScaleFactor，
/// 因此在 ScaleFactor ≠ 1 时用本类替换 ScottPlot 的 MouseDragPan（见 <see cref="PlotCanvas"/>）。
/// </para>
/// <para>实现按 ScottPlot（MIT 许可）的 MouseDragPan 与 MouseAxisManipulation.DragPan 改写，只去掉了重复的一次除法。</para>
/// </summary>
internal sealed class DpiAwareMouseDragPan(MouseButton button) : IUserActionResponse
{
    /// <summary>拖动拖动用的鼠标键。</summary>
    public MouseButton MouseButton { get; } = button;

    /// <summary>按下这些键时禁止垂直方向平移。</summary>
    public List<Key> KeysThatLockY { get; } = [StandardKeys.Control];

    /// <summary>按下这些键时禁止水平方向平移。</summary>
    public List<Key> KeysThatLockX { get; } = [StandardKeys.Shift];

    /// <summary>禁止垂直方向平移。</summary>
    public bool LockY { get; set; }

    /// <summary>禁止水平方向平移。</summary>
    public bool LockX { get; set; }

    /// <summary>在单根坐标轴上拖动时，同方向的所有坐标轴一起移动。</summary>
    public bool ChangeOpposingAxesTogether { get; set; }

    private Pixel _mouseDownPixel = Pixel.NaN;
    private MultiAxisLimits? _rememberedLimits;

    /// <summary>复制 ScottPlot MouseDragPan 的全部设置。</summary>
    public static DpiAwareMouseDragPan From(MouseDragPan source)
    {
        ArgumentNullException.ThrowIfNull(source);

        DpiAwareMouseDragPan response = new(source.MouseButton)
        {
            LockX = source.LockX,
            LockY = source.LockY,
            ChangeOpposingAxesTogether = source.ChangeOpposingAxesTogether,
        };

        response.KeysThatLockX.Clear();
        response.KeysThatLockX.AddRange(source.KeysThatLockX);
        response.KeysThatLockY.Clear();
        response.KeysThatLockY.AddRange(source.KeysThatLockY);
        return response;
    }

    /// <inheritdoc />
    public void ResetState(IPlotControl plotControl)
    {
        _rememberedLimits = null;
        _mouseDownPixel = Pixel.NaN;
    }

    /// <inheritdoc />
    public ResponseInfo Execute(IPlotControl plotControl, IUserAction userInput, KeyboardState keys)
    {
        // 按下：开始拖动
        if (userInput is IMouseButtonAction mouseDownAction
            && mouseDownAction.Button == MouseButton
            && mouseDownAction.IsPressed)
        {
            Plot? plot = plotControl.GetPlotAtPixel(mouseDownAction.Pixel);
            if (plot is not null)
            {
                _rememberedLimits = new(plot);
                _mouseDownPixel = mouseDownAction.Pixel;
            }

            return ResponseInfo.NoActionRequired;
        }

        // 抬起：结束拖动
        if (userInput is IMouseButtonAction mouseUpAction
            && mouseUpAction.Button == MouseButton
            && !mouseUpAction.IsPressed
            && _rememberedLimits is not null)
        {
            _rememberedLimits.Recall();
            Plot plot = _rememberedLimits.Plot;
            _rememberedLimits = null;
            ApplyToPlot(plot, _mouseDownPixel, mouseUpAction.Pixel, keys);

            return ResponseInfo.Refresh;
        }

        // 拖动中移动
        if (userInput is IMouseAction mouseAction
            && _rememberedLimits is not null)
        {
            // 与 ScottPlot 相同：位移不足 5 像素不算拖动（给单击 / 双击留余量）
            double dX = Math.Abs(mouseAction.Pixel.X - _mouseDownPixel.X);
            double dY = Math.Abs(mouseAction.Pixel.Y - _mouseDownPixel.Y);
            if (Math.Max(dX, dY) < 5)
                return ResponseInfo.NoActionRequired;

            _rememberedLimits.Recall();
            Plot plot = _rememberedLimits.Plot;
            ApplyToPlot(plot, _mouseDownPixel, mouseAction.Pixel, keys);
            return new ResponseInfo { RefreshNeeded = true, IsPrimary = true };
        }

        return ResponseInfo.NoActionRequired;
    }

    private void ApplyToPlot(Plot plot, Pixel mouseDown, Pixel mouseNow, KeyboardState keys)
    {
        if (LockX || keys.IsPressed(KeysThatLockX))
            mouseNow.X = mouseDown.X;

        if (LockY || keys.IsPressed(KeysThatLockY))
            mouseNow.Y = mouseDown.Y;

        DragPan(plot, mouseDown, mouseNow, ChangeOpposingAxesTogether);
    }

    /// <summary>ScottPlot MouseAxisManipulation.DragPan 的修正版：像素只换算一次到布局单位（与 LastRender.DataRect 一致）。</summary>
    private static void DragPan(Plot plot, Pixel mouseDown, Pixel mouseNow, bool changeOpposingAxesTogether)
    {
        float scale = (float)plot.ScaleFactor;
        mouseDown = mouseDown.Divide(scale);
        mouseNow = mouseNow.Divide(scale);

        float deltaX = -(mouseNow.X - mouseDown.X);
        float deltaY = mouseNow.Y - mouseDown.Y;

        IAxis? axisUnderMouse = plot.GetAxis(mouseDown);
        PixelRect dataRect = plot.RenderManager.LastRender.DataRect;

        if (axisUnderMouse is not null)
        {
            if (changeOpposingAxesTogether && axisUnderMouse.IsHorizontal())
            {
                foreach (IXAxis xAxis in plot.Axes.GetXAxes())
                    xAxis.Range.PanMouse(deltaX, dataRect.Width);
            }
            else if (changeOpposingAxesTogether && axisUnderMouse.IsVertical())
            {
                foreach (IYAxis yAxis in plot.Axes.GetYAxes())
                    yAxis.Range.PanMouse(deltaY, dataRect.Height);
            }
            else
            {
                float delta = axisUnderMouse.IsHorizontal() ? deltaX : deltaY;
                float dataSize = axisUnderMouse.IsHorizontal() ? dataRect.Width : dataRect.Height;
                axisUnderMouse.Range.PanMouse(delta, dataSize);
            }
        }
        else
        {
            foreach (IXAxis xAxis in plot.Axes.GetXAxes())
                xAxis.Range.PanMouse(deltaX, dataRect.Width);

            foreach (IYAxis yAxis in plot.Axes.GetYAxes())
                yAxis.Range.PanMouse(deltaY, dataRect.Height);
        }
    }
}
