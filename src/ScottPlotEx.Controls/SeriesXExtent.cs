namespace ScottPlotEx.Controls;

/// <summary>
/// 一条曲线在 X 轴上的范围，供 <see cref="ScrollingAxisCalculator"/> 计算坐标轴使用。
/// 点数单位下 <see cref="LatestX"/> = 点数、<see cref="StepX"/> = 1；
/// 时间单位下 <see cref="LatestX"/> = 最新数据点距参考时刻的秒数、<see cref="StepX"/> = 采样间隔（秒）。
/// </summary>
/// <param name="LatestX">最新数据点的 X 坐标。</param>
/// <param name="StepX">相邻两个数据点的 X 间距（一个数据点在 X 轴上的宽度）。</param>
public readonly record struct SeriesXExtent(double LatestX, double StepX);
