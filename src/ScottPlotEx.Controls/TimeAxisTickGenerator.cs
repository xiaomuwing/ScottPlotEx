using System.Globalization;
using ScottPlot;

namespace ScottPlotEx.Controls;

/// <summary>
/// 时间刻度生成器：X 坐标为距参考时刻（<see cref="ReferenceTime"/>）的秒数，刻度落在“整”的时间间隔上
/// （1 / 2 / 5 / 10 / 15 / 30 秒、1 / 2 / 5 / 10 / 15 / 30 分钟、1 / 2 / 3 / 6 / 12 小时、1 / 2 / 5 … 天，不足 1 秒时按 1 / 2 / 5 × 10ⁿ 秒），
/// 间距按标签宽度自动选择，保证标签互不重叠。
/// <list type="bullet">
/// <item>相对时间（<see cref="Absolute"/> = false，对应 imc 的“Days/hours/minutes: Relative”）：按显示范围自动选用
/// <c>12.5 s</c> / <c>2:05</c>（分:秒）/ <c>1:02:05</c>（时:分:秒）/ <c>3天 04:00</c> 的格式，负值前加“-”。</item>
/// <item>绝对时间（<see cref="Absolute"/> = true，对应 imc 的“Date/time: Absolute”）：标签为 <c>参考时刻 + X 秒</c> 的时刻，
/// 按刻度间距显示 <c>HH:mm:ss.fff</c> / <c>HH:mm:ss</c> / <c>HH:mm</c>；第一个刻度以及日期变化处在第二行显示日期 <c>yyyy-MM-dd</c>；
/// 间距达到 1 天时只显示日期。</item>
/// </list>
/// 控件在 <see cref="ScrollingPlotControl.XAxisUnit"/> 为时间单位时自动安装本生成器；也可以单独用于其它 ScottPlot 坐标轴。
/// </summary>
public sealed class TimeAxisTickGenerator : ITickGenerator
{
    private const double TicksPerSecond = TimeSpan.TicksPerSecond;
    private const double SecondsPerDay = 86_400;

    /// <summary>候选刻度间距（秒，升序）与每个主刻度间隔内的次刻度段数。</summary>
    private static readonly (double Spacing, int MinorDivisions)[] Candidates = CreateCandidates();

    /// <summary>创建相对时间刻度生成器。</summary>
    public TimeAxisTickGenerator()
    {
    }

    /// <summary>创建刻度生成器。</summary>
    /// <param name="absolute">true 显示绝对时间（日期时间），false 显示相对时间。</param>
    /// <param name="referenceTime">X = 0 对应的时刻（绝对时间标签 = 该时刻 + X 秒）。</param>
    public TimeAxisTickGenerator(bool absolute, DateTime referenceTime)
    {
        Absolute = absolute;
        ReferenceTime = referenceTime;
    }

    /// <summary>true 显示绝对时间（日期时间），false（默认）显示相对时间。</summary>
    public bool Absolute { get; set; }

    /// <summary>X = 0 对应的时刻。绝对时间标签 = 该时刻 + X 秒（按原样显示，不做时区转换）。</summary>
    public DateTime ReferenceTime { get; set; }

    /// <summary>相邻两个刻度标签之间至少留出的距离（像素），默认 16。</summary>
    public float LabelGap { get; set; } = 16;

    /// <inheritdoc />
    public Tick[] Ticks { get; set; } = [];

    /// <inheritdoc />
    public int MaxTickCount { get; set; } = 1000;

    /// <inheritdoc />
    public void Regenerate(CoordinateRange range, Edge edge, PixelLength size, Paint paint, LabelStyle labelStyle)
    {
        double min = range.Min;
        double max = range.Max;

        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min || !(size.Length > 0))
        {
            Ticks = [];
            return;
        }

        double span = max - min;
        double maxAbs = Math.Max(Math.Abs(min), Math.Abs(max));
        bool vertical = edge.IsVertical();
        (double[] Positions, string[] Labels, double Spacing, int MinorDivisions)? fallback = null;

        // 从小到大尝试候选间距：第一个“标签放得下”的间距就是最密的可用刻度
        foreach ((double spacing, int minorDivisions) in Candidates)
        {
            if (Absolute && spacing < 1 / TicksPerSecond)
                continue; // 绝对时间按 100 ns 刻度对齐，更细的间距没有意义

            double pixels = spacing / span * size.Length;
            if (pixels < LabelGap + 8 || span / spacing > MaxTickCount)
                continue;

            double[] positions = Absolute ? AbsolutePositions(min, max, spacing) : RelativePositions(min, max, spacing);
            if (positions.Length == 0)
                continue;

            string[] labels = FormatLabels(positions, spacing, maxAbs);
            fallback = (positions, labels, spacing, minorDivisions);

            if (pixels >= MeasureLargest(labels, vertical, paint, labelStyle) + LabelGap)
                break;
        }

        if (fallback is not { } chosen)
        {
            Ticks = [];
            return;
        }

        // 紧贴坐标轴末端的刻度（例如堆叠模式右端只留 2 个采样间隔）的标签会有一半伸出绘图区被裁掉：
        // 只留刻度线与网格线，不显示被裁掉的标签（时间标签比数字长，比 ScottPlot 默认刻度更容易出现）
        int last = chosen.Positions.Length - 1;
        if (last > 0)
        {
            double room = (max - chosen.Positions[last]) / span * size.Length;
            if (room + EdgeOverflowPixels < MeasureLargest([chosen.Labels[last]], vertical, paint, labelStyle) / 2)
                chosen.Labels[last] = string.Empty;
        }

        Ticks = BuildTicks(chosen.Positions, chosen.Labels, chosen.Spacing, chosen.MinorDivisions, min, max);
    }

    /// <summary>坐标轴末端的标签最多允许伸出绘图区这么多像素（绘图区外侧通常有一小段留白）。</summary>
    private const float EdgeOverflowPixels = 6;

    /// <summary>
    /// 相对时间标签：按显示范围的最大绝对值选择格式——不足 1 分钟 <c>12.5 s</c>，不足 1 小时 <c>m:ss</c>，
    /// 不足 1 天 <c>h:mm:ss</c>，否则 <c>d天 hh:mm[:ss]</c>；刻度间距不足 1 秒时追加小数位。
    /// </summary>
    /// <param name="seconds">距参考时刻的秒数（可以为负）。</param>
    /// <param name="tickSpacing">刻度间距（秒），决定小数位数与是否显示秒。</param>
    /// <param name="rangeMaxAbs">显示范围两端绝对值的较大者（秒），决定使用哪种格式。</param>
    public static string FormatRelative(double seconds, double tickSpacing, double rangeMaxAbs)
    {
        int digits = FractionDigits(tickSpacing);
        long scale = Pow10(digits);
        long units = (long)Math.Round(Math.Abs(seconds) * scale, MidpointRounding.AwayFromZero);
        string sign = seconds < 0 && units != 0 ? "-" : string.Empty;
        long whole = units / scale;
        string fraction = digits > 0
            ? "." + (units % scale).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : string.Empty;

        long s = whole % 60;
        long m = whole / 60 % 60;
        long h = whole / 3600 % 24;

        if (rangeMaxAbs < 60)
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{whole}{fraction} s");

        if (rangeMaxAbs < 3600)
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{whole / 60}:{s:00}{fraction}");

        if (rangeMaxAbs < SecondsPerDay)
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{whole / 3600}:{m:00}:{s:00}{fraction}");

        long d = whole / (long)SecondsPerDay;

        if (tickSpacing >= SecondsPerDay)
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{d}天");

        return tickSpacing >= 60
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{d}天 {h:00}:{m:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{d}天 {h:00}:{m:00}:{s:00}{fraction}");
    }

    /// <summary>
    /// 绝对时间标签：刻度间距不足 1 秒 <c>HH:mm:ss.f…</c>，不足 1 分钟 <c>HH:mm:ss</c>，不足 1 天 <c>HH:mm</c>；
    /// <paramref name="includeDate"/> 为 true 时在第二行追加日期 <c>yyyy-MM-dd</c>。间距达到 1 天时只显示日期。
    /// </summary>
    /// <param name="time">刻度对应的时刻。</param>
    /// <param name="tickSpacing">刻度间距（秒）。</param>
    /// <param name="includeDate">是否显示日期（第一个刻度与日期变化处）。</param>
    public static string FormatAbsolute(DateTime time, double tickSpacing, bool includeDate)
    {
        if (tickSpacing >= SecondsPerDay)
            return time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string format = tickSpacing >= 60 ? "HH:mm"
            : tickSpacing >= 1 ? "HH:mm:ss"
            : "HH:mm:ss." + new string('f', FractionDigits(tickSpacing));

        string text = time.ToString(format, CultureInfo.InvariantCulture);
        return includeDate ? text + "\n" + time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>X（距参考时刻的秒数）对应的时刻，超出 <see cref="DateTime"/> 范围时取边界值。</summary>
    public DateTime ToDateTime(double seconds) => AddSeconds(ReferenceTime, seconds);

    internal static DateTime AddSeconds(DateTime reference, double seconds)
    {
        double ticks = reference.Ticks + Math.Round(seconds * TicksPerSecond);
        ticks = Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        return new DateTime((long)ticks, reference.Kind);
    }

    private string[] FormatLabels(double[] positions, double spacing, double maxAbs)
    {
        string[] labels = new string[positions.Length];

        if (!Absolute)
        {
            for (int i = 0; i < positions.Length; i++)
                labels[i] = FormatRelative(positions[i], spacing, maxAbs);

            return labels;
        }

        // 第一个刻度与日期变化处显示日期（间距达到 1 天时标签本身就是日期）
        DateTime previousDate = DateTime.MinValue;

        for (int i = 0; i < positions.Length; i++)
        {
            DateTime time = ToDateTime(positions[i]);
            bool includeDate = i == 0 || time.Date != previousDate;
            labels[i] = FormatAbsolute(time, spacing, includeDate);
            previousDate = time.Date;
        }

        return labels;
    }

    private static double[] RelativePositions(double min, double max, double spacing)
    {
        // 用整数倍计算，避免逐次累加的舍入误差
        double first = Math.Ceiling(min / spacing);
        double last = Math.Floor(max / spacing);
        int count = (int)Math.Max(0, Math.Min(last - first + 1, int.MaxValue / 2));

        double[] positions = new double[count];
        for (int i = 0; i < count; i++)
            positions[i] = (first + i) * spacing;

        return positions;
    }

    private double[] AbsolutePositions(double min, double max, double spacing)
    {
        // 刻度对齐到“整”的时刻（例如间距 10 秒时落在 :00 / :10 / :20 …），按 100 ns 刻度做整数运算
        long reference = ReferenceTime.Ticks;
        long step = (long)Math.Round(spacing * TicksPerSecond);
        if (step <= 0)
            return [];

        double lowTicks = reference + Math.Ceiling(min * TicksPerSecond);
        double highTicks = reference + Math.Floor(max * TicksPerSecond);
        if (lowTicks < DateTime.MinValue.Ticks || highTicks > DateTime.MaxValue.Ticks)
            return [];

        long first = CeilingMultiple((long)lowTicks, step);
        long last = FloorMultiple((long)highTicks, step);
        if (last < first)
            return [];

        int count = (int)Math.Min(((last - first) / step) + 1, int.MaxValue / 2);
        double[] positions = new double[count];

        for (int i = 0; i < count; i++)
            positions[i] = (first + (i * step) - reference) / TicksPerSecond;

        return positions;
    }

    private Tick[] BuildTicks(double[] positions, string[] labels, double spacing, int minorDivisions, double min, double max)
    {
        List<Tick> ticks = new(positions.Length * (minorDivisions + 1));

        for (int i = 0; i < positions.Length; i++)
            ticks.Add(Tick.Major(positions[i], labels[i]));

        // 次刻度：主刻度间隔等分（包括第一个主刻度之前、最后一个之后落在范围内的部分）
        if (minorDivisions > 1)
        {
            double minor = spacing / minorDivisions;

            for (int i = -1; i < positions.Length; i++)
            {
                double start = i >= 0 ? positions[i] : positions[0] - spacing;

                for (int k = 1; k < minorDivisions; k++)
                {
                    double x = start + (k * minor);
                    if (x >= min && x <= max && ticks.Count < MaxTickCount)
                        ticks.Add(Tick.Minor(x));
                }
            }
        }

        return [.. ticks];
    }

    private static float MeasureLargest(string[] labels, bool vertical, Paint paint, LabelStyle labelStyle)
    {
        // 同一种格式的标签宽度基本相同：只测量第一个（可能带日期行）与字符最多的一个
        int longest = 0;
        for (int i = 1; i < labels.Length; i++)
        {
            if (labels[i].Length > labels[longest].Length)
                longest = i;
        }

        float largest = Measure(labels[0]);
        if (longest != 0)
            largest = Math.Max(largest, Measure(labels[longest]));

        return largest;

        float Measure(string text)
        {
            PixelSize measured = labelStyle.Measure(text, paint).Size;
            return vertical ? measured.Height : measured.Width;
        }
    }

    private static (double Spacing, int MinorDivisions)[] CreateCandidates()
    {
        List<(double, int)> candidates = [];

        // 不足 1 秒：1 / 2 / 5 × 10ⁿ（最小 1 µs 以下也保留，供相对时间的高采样率曲线放大查看）
        for (int exponent = -9; exponent < 0; exponent++)
        {
            double decade = Math.Pow(10, exponent);
            candidates.Add((1 * decade, 5));
            candidates.Add((2 * decade, 4));
            candidates.Add((5 * decade, 5));
        }

        candidates.AddRange([
            (1, 5), (2, 4), (5, 5), (10, 5), (15, 3), (30, 6),            // 秒
            (60, 6), (120, 4), (300, 5), (600, 5), (900, 3), (1800, 6),   // 分钟
            (3600, 4), (7200, 4), (10800, 3), (21600, 6), (43200, 4),     // 小时
        ]);

        // 天：1 / 2 / 5 × 10ⁿ 天
        for (int exponent = 0; exponent <= 6; exponent++)
        {
            double decade = SecondsPerDay * Math.Pow(10, exponent);
            candidates.Add((1 * decade, exponent == 0 ? 4 : 5));
            candidates.Add((2 * decade, 2));
            candidates.Add((5 * decade, 5));
        }

        return [.. candidates];
    }

    private static int FractionDigits(double spacing)
    {
        if (!(spacing > 0) || spacing >= 1)
            return 0;

        return Math.Clamp((int)Math.Ceiling(-Math.Log10(spacing) - 1e-9), 0, 7);
    }

    private static long Pow10(int digits)
    {
        long value = 1;
        for (int i = 0; i < digits; i++)
            value *= 10;

        return value;
    }

    private static long CeilingMultiple(long value, long step)
    {
        long remainder = value % step;
        return remainder == 0 ? value : value - remainder + (value > 0 ? step : 0);
    }

    private static long FloorMultiple(long value, long step)
    {
        long remainder = value % step;
        return remainder == 0 ? value : value - remainder - (value < 0 ? step : 0);
    }
}
