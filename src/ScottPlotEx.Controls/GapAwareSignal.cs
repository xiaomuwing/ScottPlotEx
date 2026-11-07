using ScottPlot;
using ScottPlot.Plottables;

namespace ScottPlotEx.Controls;

/// <summary>
/// 在缺失值（NaN）处断开的 Signal 曲线。
/// <para>
/// ScottPlot 5.1.59 自带的 Signal 遇到 NaN 时整条连线都画不出来（SkiaSharp 路径中出现 NaN 坐标后整条路径失效）。
/// 控件把缺失值按 NaN 占位保存（见 <see cref="PlotDataBuffer"/>），这里把可见范围按缺失区间切成若干段，
/// 每段用 ScottPlot 原来的绘制方法单独绘制（通过数据源的 MinimumIndex / MaximumIndex 限定下标范围），
/// 因此线型、标记、高低密度两种绘制方式与 ScottPlot 完全一致；没有缺失值时直接调用原来的绘制，画面逐像素相同。
/// </para>
/// <para>
/// 断开的规则：缺失区间两侧的有效点在 X 上相距超过一个像素列时断开；更短的缺失（数据很密、一个像素列内就有有效点）不断开，
/// 由数据源在像素列内跳过缺失值，避免高密度数据中零星的缺失把曲线切成大量碎段。
/// </para>
/// </summary>
internal sealed class GapAwareSignal : Signal
{
    private readonly PlotDataBuffer _buffer;

    public GapAwareSignal(PyramidSignalSource source)
        : base(source)
    {
        _buffer = source.Buffer;
    }

    /// <inheritdoc />
    public override void Render(RenderPack rp)
    {
        ReadOnlySpan<(int Start, int End)> runs = _buffer.MissingRuns;
        int count = _buffer.Count;

        if (runs.IsEmpty || count == 0)
        {
            base.Render(rp);
            return;
        }

        ScottPlot.ISignalSource data = Data;
        int originalMin = data.MinimumIndex;
        int originalMax = data.MaximumIndex;
        int userMin = Math.Max(0, originalMin);
        int userMax = Math.Min(count - 1, originalMax);

        if (userMin > userMax)
            return;

        // 可见下标范围，两端各多取一个点，让视图边缘的线段连到视图之外的点
        double viewLeft = Math.Min(Axes.XAxis.Min, Axes.XAxis.Max);
        double viewRight = Math.Max(Axes.XAxis.Min, Axes.XAxis.Max);
        int first = Math.Max(userMin, IndexAt(viewLeft) - 1);
        int last = Math.Min(userMax, IndexAt(viewRight) + 1);

        if (first > last)
            return;

        double unitsPerPixel = rp.DataRect.Width > 0 ? Math.Abs(viewRight - viewLeft) / rp.DataRect.Width : double.PositiveInfinity;
        double period = Math.Abs(data.Period);

        try
        {
            int segmentStart = first;
            int run = _buffer.FirstMissingRunEndingAtOrAfter(first);

            for (; run < runs.Length && runs[run].Start <= last; run++)
            {
                (int missingStart, int missingEnd) = runs[run];

                // 两侧的有效点相距不超过一个像素列：不断开（像素列内跳过缺失值即可）。低密度绘制逐点连线，必须断开
                bool breaksLine = AlwaysUseLowDensityMode || (missingEnd - missingStart + 2) * period > unitsPerPixel;
                if (!breaksLine)
                    continue;

                RenderSegment(rp, segmentStart, Math.Min(missingStart - 1, last));
                segmentStart = Math.Max(segmentStart, missingEnd + 1);
            }

            RenderSegment(rp, segmentStart, last);
        }
        finally
        {
            // 恢复用户设置的下标范围（默认 0 ~ int.MaxValue）
            data.MinimumIndex = originalMin;
            data.MaximumIndex = originalMax;
        }

        int IndexAt(double x)
        {
            double index = (x - data.XOffset) / data.Period;
            return double.IsFinite(index) ? (int)Math.Clamp(Math.Floor(index), 0, count - 1) : 0;
        }
    }

    /// <summary>用 ScottPlot 原来的绘制方法画下标区间 [start, end]（只含有效点或不足一个像素列的短缺失）。</summary>
    private void RenderSegment(RenderPack rp, int start, int end)
    {
        // 去掉两端的缺失值：段内首尾必须是有效点
        start = _buffer.NextFiniteIndex(start);
        end = _buffer.PreviousFiniteIndex(end);

        if (start < 0 || end < 0 || start > end)
            return;

        Data.MinimumIndex = start;
        Data.MaximumIndex = end;
        base.Render(rp);
    }
}
