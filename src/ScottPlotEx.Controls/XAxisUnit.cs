namespace ScottPlotEx.Controls;

/// <summary>
/// X 轴单位（对标 imc 曲线窗口的 X 轴显示方式）。
/// </summary>
public enum XAxisUnit
{
    /// <summary>
    /// 点数（默认）：第 n 个数据点 X = n（从 1 开始），各曲线按序号对齐，与采样间隔无关。
    /// </summary>
    Samples = 0,

    /// <summary>
    /// 相对时间（对应 imc 的“Days/hours/minutes: Relative”）：X 为距参考时刻（<see cref="ScrollingPlotControl.TimeReference"/>）的秒数，
    /// 各曲线按“起始时间 + 下标 × 采样间隔”在时间上对齐；刻度按时长自动显示为 秒 / 分:秒 / 时:分:秒 / 天。
    /// </summary>
    RelativeTime = 1,

    /// <summary>
    /// 绝对时间（对应 imc 的“Date/time: Absolute”）：X 与相对时间相同（距参考时刻的秒数），刻度标签显示日期时间。
    /// </summary>
    AbsoluteTime = 2,
}
