namespace ScottPlotEx.Controls;

/// <summary>
/// “显示帮助”对话框中显示的中文说明文字。
/// </summary>
public static class PlotHelpContent
{
    /// <summary>对话框标题。</summary>
    public const string Title = "ScottPlot 实时曲线控件 - 帮助";

    /// <summary>说明正文。</summary>
    public static string Text { get; } =
        """
        【自动滚动模式】

        1. 堆叠模式（默认）
           X 轴范围：0 ~ 当前数据点数量 + 2（默认显示全部数据）
           “X 轴显示 %”控制显示长度：设置时把原点固定在 当时的（点数+2）× (1−百分比)，
           之后原点不再移动，数据增长只向右扩展（默认 100% = 原点 0，显示全部数据；
           设为 90 表示原点固定在设置时刻 10% 的位置）
           Y 轴范围：已添加数据的最大值与最小值各向外让出 5%

        2. 跟随模式
           X 轴范围：最后（最新）N% 的数据窗口（N 由“X 轴显示 %”设置，默认 30%），最大值同样为 当前数据点数量 + 2
           Y 轴范围：当前显示数据的最大值与最小值各向外让出 5%

        3. 手动模式（停止滚动）
           放大局部数据，或拖动到看不到最新数据时自动进入该模式。
           重新选择“堆叠模式”或“跟随模式”即可恢复滚动。

        （以上“数据点数量 + 2”是默认的点数单位；相对时间 / 绝对时间单位下为
          “最新时间 + 2 个最大的采样间隔”，右端跟着最快的曲线平滑滚动。）


        【X 轴单位】（XAxisUnit）

        · 点数（默认）：第 n 个数据点 X = n，各曲线按序号对齐。
        · 相对时间：X = 距参考时刻的秒数（起始时间 + 下标 × 采样间隔），各曲线在时间上对齐；
          刻度按时长显示为 秒 / 分:秒 / 时:分:秒 / 天。
        · 绝对时间：同相对时间，刻度显示时刻，第一个刻度与日期变化处标出日期。
          必须先设置起始时间（DefaultStartTime 或各曲线的 StartTime），不使用第一个数据点到达的时刻；
          没有设置时不能切换到绝对时间，处于绝对时间时也不能清除起始时间。
        · 每条曲线可以有自己的采样间隔（PlotSeries.SampleInterval / SampleRate）与起始时间（StartTime）；
          相对时间下没有设置起始时间的曲线以第一个数据点到达的时刻为起始时间；
          跟随模式在时间单位下可用 FollowWindowDuration 固定显示最近一段时间。
        · 运行中切换单位：数据保留，坐标轴立即重算。


        【鼠标操作】

        · 左键拖动：平移视图。
          拖动后仍能看到最新数据 -> 保持当前模式，X 轴最小值取当前最小值，曲线继续滚动；
          拖动后看不到最新数据 -> 曲线停止滚动（手动模式）。

        · 中键拖动：框选放大，处理逻辑与左键拖动完全一致。

        · 右键拖动：缩放视图（默认停止滚动）。

        · 滚轮：以鼠标位置为中心缩放（默认停止滚动）。

        · 中键单击：自动缩放（整体显示全部数据）。
          若此时曲线已停止滚动，会退出停止状态并继续滚动；
          若此时是跟随模式，会自动切换为堆叠模式（跟随模式只显示最新 30% 数据，与整体显示冲突）。

        · 右键单击：弹出菜单（保存图像 / 复制到剪贴板 / 自动缩放 / 在新窗口中打开 / 显示帮助）。

        · 左键双击：ScottPlot 自带的性能基准测试。

        · 键盘：方向键平移，A 键自动缩放。


        【数据接口】

        · AddValue(double y)：向默认曲线追加一个数据点（点数单位下 X 从 1 开始自动递增）。
        · AddValues(...)：向默认曲线批量追加数据点。
        · AddSeries(名称, 颜色)：添加一条曲线，之后通过它 AddValue / AddValues / Clear / Remove；
          各曲线数据独立，坐标轴按所有可见曲线计算（任意线程都可以写入）。
        · ClearData()：清空全部曲线的数据。
        · FollowWindowPoints：跟随模式固定显示最后 N 个点（0 = 按百分比）。
        · FollowWindowDuration：时间单位下跟随模式固定显示最近这么长时间（0 = 按点数换算或百分比）。
        · XAxisUnit：X 轴单位（Samples 点数 / RelativeTime 相对时间 / AbsoluteTime 绝对时间）。
        · MaxFramesPerSecond：跨线程写入时的重绘帧率上限（0 = 不限）。
        · Mode：ScrollingMode.Stacked / Follow / Manual。
        · SetXAxisDisplayPercent(百分比, 模式)：设置 X 轴显示长度百分比（模式必填）
          堆叠 = 显示最新 百分比 的数据（默认 100% 即全部数据，90% 只裁掉最早的 10%）
          跟随 = 显示最后 百分比 的数据窗口（默认 30%）
          两种模式方向一致：数值越大，看到的数据越多
        · GetXAxisDisplayPercent(模式) / CurrentXAxisDisplayPercent：读取当前百分比
        · StackedDisplayPercent / FollowWindowPercent：直接读写两种模式的百分比（默认 100% / 30%）
        · ShowAllData()：恢复显示全部数据（堆叠显示百分比设为 100%）
        · 重新选择模式会回到该模式的默认视图：选择堆叠模式 = 100%（显示全部数据），
          选择跟随模式 = 默认 30% 窗口（可用 AxisOptions.ResetDisplayPercentOnModeChange 关闭）
        · 所有 IPlotControl 接口（Plot、Menu、UserInputProcessor 等）均已转发到内嵌的 ScottPlot 控件。
        """;
}
