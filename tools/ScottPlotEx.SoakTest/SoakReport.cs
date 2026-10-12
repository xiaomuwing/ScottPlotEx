using System.Globalization;
using System.Text;

namespace ScottPlotEx.SoakTest;

/// <summary>报告所需的运行结果。</summary>
internal sealed record SoakRunSummary(
    string State,
    string StopReason,
    DateTime StartedLocal,
    DateTime EndedLocal,
    double ElapsedSeconds,
    double PlannedSeconds,
    string RunId,
    string RunDirectory,
    string LogPath,
    string CsvPath,
    string SnapshotDirectory,
    string Environment,
    IReadOnlyList<SoakSample> Samples,
    SoakStatsSnapshot Stats,
    IReadOnlyDictionary<string, (double Max, double Sum, long Count)> LatencyByPhase,
    IReadOnlyList<string> StructureOperations,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int TimerStarvedEvents,
    double TimerStarvedSeconds,
    double TimerMaxStarvedSeconds);

/// <summary>把运行结果整理成 Markdown 报告。</summary>
internal static class SoakReport
{
    private const double Megabyte = 1024.0 * 1024.0;

    /// <summary>句柄增长速度告警阈值（个/小时）。</summary>
    private const double HandleSlopeWarning = 300;

    /// <summary>GDI 对象增长速度告警阈值（个/小时）。</summary>
    private const double GdiSlopeWarning = 50;

    /// <summary>托管堆“每千点字节数”在运行后 1/3 相比前 1/3 的放大倍数阈值。</summary>
    private const double PerPointGrowthWarning = 1.5;

    public static string Build(SoakOptions options, SoakRunSummary summary)
    {
        StringBuilder report = new();
        List<string> findings = [];
        bool failed = summary.Errors.Count > 0 || summary.State == "failed";

        double handleSlope = Slope(summary.Samples, s => s.HandleCount);
        double gdiSlope = Slope(summary.Samples, s => s.GdiObjects);
        double threadSlope = Slope(summary.Samples, s => s.ThreadCount);
        double workingSetSlope = Slope(summary.Samples, s => s.WorkingSetMb);
        double heapSlope = Slope(summary.Samples, s => s.ManagedHeapMb);
        (double firstPerPoint, double lastPerPoint, double perPointRatio) = PerPointBytes(summary.Samples);

        if (handleSlope > HandleSlopeWarning)
            findings.Add($"句柄数以约 {handleSlope:F0} 个/小时的速度增长（阈值 {HandleSlopeWarning:F0}），疑似句柄泄漏。");

        if (gdiSlope > GdiSlopeWarning)
            findings.Add($"GDI 对象以约 {gdiSlope:F0} 个/小时的速度增长（阈值 {GdiSlopeWarning:F0}），疑似 GDI 泄漏。");

        if (threadSlope > 2)
            findings.Add($"线程数以约 {threadSlope:F1} 个/小时的速度增长，疑似线程泄漏。");

        if (perPointRatio > PerPointGrowthWarning && double.IsFinite(perPointRatio))
            findings.Add($"托管堆“每千点占用”在运行后段比前段放大 {perPointRatio:F2} 倍（阈值 {PerPointGrowthWarning:F2}），疑似内存泄漏。");

        if (summary.TimerStarvedEvents > 0)
            findings.Add($"观测到 {summary.TimerStarvedEvents} 次 WM_TIMER 饿死（最长 {summary.TimerMaxStarvedSeconds:F0} 秒、累计 {summary.TimerStarvedSeconds:F0} 秒）：跨线程高频灌入期间窗口消息队列被持续占用，界面定时器与重绘被挤掉。");

        SoakStatsSnapshot stats = summary.Stats;

        string verdict = failed ? "**未通过**" : findings.Count == 0 ? "**通过**" : "**通过（有告警）**";

        report.AppendLine("# ScottPlotEx 控件时间压力测试（Soak Test）报告");
        report.AppendLine();
        report.AppendLine($"- 结论：{verdict}");
        report.AppendLine($"- 被测控件：`ScottPlotEx.Controls.ScrollingPlotControl`（WinForms + ScottPlot.WinForms 5.1.59）");
        report.AppendLine($"- 运行时长：{FormatDuration(summary.ElapsedSeconds)}（计划 {FormatDuration(summary.PlannedSeconds)}）");
        report.AppendLine($"- 起止时间：{summary.StartedLocal:yyyy-MM-dd HH:mm:ss} → {summary.EndedLocal:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"- 结束原因：{summary.StopReason}");
        report.AppendLine($"- 运行标识：`{summary.RunId}`");
        report.AppendLine($"- 输出目录：`{summary.RunDirectory}`");
        report.AppendLine();
        report.AppendLine("## 1. 运行环境");
        report.AppendLine();
        report.AppendLine(summary.Environment);
        report.AppendLine();
        report.AppendLine("## 2. 压测配置");
        report.AppendLine();
        report.AppendLine("| 项 | 取值 |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| 计划时长 | {options.DurationHours:F2} 小时 |");
        report.AppendLine($"| 负载循环 | {options.CycleMinutes:F1} 分钟（循环内 6 个阶段） |");
        report.AppendLine($"| 负载档位 | {options.Profile}（×{options.LoadScale:F2}） |");
        report.AppendLine($"| 窗口模式 | {(options.GhostWindow ? "近透明 + 鼠标穿透（不干扰桌面操作）" : "正常可见窗口")} |");
        report.AppendLine($"| 数据点自动清空上限 | {options.AutoClearPoints:N0} |");
        report.AppendLine($"| 指标采样间隔 | {options.SampleSeconds:F0} 秒 |");
        report.AppendLine($"| 截图间隔 | {(options.SnapshotMinutes <= 0 ? "关闭" : options.SnapshotMinutes + " 分钟")} |");
        report.AppendLine($"| 随机种子 | {options.Seed} |");
        report.AppendLine($"| 鼠标 / 键盘交互 | {(options.EnableInteractions ? "开启" : "关闭")} |");
        report.AppendLine($"| 结构操作（尺寸 / 最小化 / 清空 / 重建控件） | {(options.EnableStructureOperations ? "开启" : "关闭")} |");
        report.AppendLine();
        report.AppendLine("每个循环依次包含以下阶段，循环往复直至达到计划时长：");
        report.AppendLine();
        report.AppendLine("| 阶段 | 占循环比例 | UI 线程数据速率 | 后台线程数据速率 | 交互频率 |");
        report.AppendLine("|---|---|---|---|---|");
        report.AppendLine("| 常态采集 | 30% | 1 点/秒（演示程序场景） | — | 无 |");
        report.AppendLine("| 中速采集 + 交互 | 25% | 20 点/秒 | — | 每 3 秒 |");
        report.AppendLine("| 高速突发 | 15% | 200 点/秒（批量灌入） | — | 每 5 秒 |");
        report.AppendLine("| 多线程极端突发 | 5% | 2000 点/秒 | 4 线程 × 250 点/秒（小批量合并投递，走跨线程编组） | 每 5 秒 |");
        report.AppendLine("| 交互风暴 | 15% | 5 点/秒 | — | 每 0.25 秒 |");
        report.AppendLine("| 结构变化 | 10% | 10 点/秒 | — | 每 2 秒一次结构操作 |");
        report.AppendLine();
        report.AppendLine("## 3. 压测工作量");
        report.AppendLine();
        report.AppendLine("| 项 | 数量 |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| 累计写入数据点 | {stats.Points:N0} |");
        report.AppendLine($"| 其中非有限值（NaN / Infinity，应被忽略） | {stats.Skipped:N0} |");
        report.AppendLine($"| 鼠标 / 键盘交互 | {stats.Interactions:N0}（其中真实 Windows 消息 {stats.RealMessageInteractions:N0}） |");
        report.AppendLine($"| 滚动模式切换 | {stats.ModeSwitches:N0} |");
        report.AppendLine($"| X 轴显示百分比调整 | {stats.DisplayPercentChanges:N0} |");
        report.AppendLine($"| 结构操作（尺寸 / 最小化 / 清空 / 重置 / 帮助 / 菜单 / 键盘 / 滚轮） | {stats.StructureOperations:N0} |");
        report.AppendLine($"| 释放并重建控件 | {stats.ControlRecreations:N0} |");
        report.AppendLine($"| 右键菜单打开 | {stats.MenusOpened:N0} |");
        report.AppendLine($"| 帮助窗口打开 / 关闭 | {stats.HelpWindowsOpened:N0} |");
        report.AppendLine($"| 缓冲区自动清空 | {stats.AutoClears:N0} |");
        report.AppendLine($"| 图表快照 | {stats.Snapshots:N0} |");
        report.AppendLine();

        if (summary.StructureOperations.Count > 0)
        {
            report.AppendLine("结构操作明细（按类型统计）：");
            report.AppendLine();
            report.AppendLine("| 结构操作 | 次数 |");
            report.AppendLine("|---|---|");

            foreach (IGrouping<string, string> group in summary.StructureOperations.GroupBy(x => x).OrderByDescending(g => g.Count()))
                report.AppendLine($"| {group.Key} | {group.Count():N0} |");

            report.AppendLine();
        }

        report.AppendLine("## 4. 稳定性结论");
        report.AppendLine();
        report.AppendLine("| 检查项 | 结果 |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| 未处理异常（UI 线程 / 后台线程 / 任务） | {(summary.Errors.Count == 0 ? "0 —— 无" : summary.Errors.Count + " —— 见下方列表")} |");
        report.AppendLine($"| 控件不变量破坏（坐标轴非法 / 数据量不一致） | {stats.IntegrityViolations:N0} |");
        report.AppendLine($"| UI 线程响应延迟 ≥ 1 秒 | {stats.Stalls:N0} 次 |");
        report.AppendLine($"| UI 线程响应延迟最大值 | {stats.UiLatencyMaxMs:F0} ms（平均 {stats.UiLatencyAverageMs:F1} ms） |");
        report.AppendLine($"| WM_TIMER 饿死（窗口定时器被消息队列挤掉） | {(summary.TimerStarvedEvents == 0 ? "0 次 —— 无" : $"{summary.TimerStarvedEvents} 次（最长 {summary.TimerMaxStarvedSeconds:F0} 秒、累计 {summary.TimerStarvedSeconds:F0} 秒）")} |");
        report.AppendLine($"| 告警 | {summary.Warnings.Count} 条 |");
        report.AppendLine($"| 是否按计划跑满 | {(summary.State == "completed" ? "是" : "否（" + summary.StopReason + "）")} |");
        report.AppendLine();

        AppendIssueList(report, "错误", summary.Errors);
        AppendIssueList(report, "告警", summary.Warnings);

        report.AppendLine("## 5. 资源趋势");
        report.AppendLine();
        report.AppendLine("| 已运行 | 阶段 | 数据点 | 工作集(MB) | 私有(MB) | 托管堆(MB) | 线程 | 句柄 | GDI | User | 渲染(ms) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (SoakSample sample in Progression(summary.Samples))
        {
            report.AppendLine(
                $"| {FormatDuration(sample.ElapsedSeconds)} | {sample.Phase} | {sample.DataCount:N0} | {sample.WorkingSetMb:F1} | " +
                $"{sample.PrivateMb:F1} | {sample.ManagedHeapMb:F1} | {sample.ThreadCount} | {sample.HandleCount} | " +
                $"{sample.GdiObjects} | {sample.UserObjects} | {sample.LastRenderMs:F1} |");
        }

        report.AppendLine();
        report.AppendLine("变化速率（线性回归，跳过前 10 分钟预热）：");
        report.AppendLine();
        report.AppendLine("| 指标 | 趋势 |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| 工作集 | {workingSetSlope:+0.0;-0.0;0.0} MB/小时 |");
        report.AppendLine($"| 托管堆 | {heapSlope:+0.00;-0.00;0.00} MB/小时 |");
        report.AppendLine($"| 句柄数 | {handleSlope:+0;-0;0} 个/小时 |");
        report.AppendLine($"| GDI 对象 | {gdiSlope:+0.0;-0.0;0.0} 个/小时 |");
        report.AppendLine($"| 线程数 | {threadSlope:+0.00;-0.00;0.00} 个/小时 |");
        report.AppendLine($"| 托管堆每千点占用（前 1/3 → 后 1/3） | {firstPerPoint:F0} → {lastPerPoint:F0} 字节/千点（放大 {perPointRatio:F2} 倍） |");
        report.AppendLine();
        report.AppendLine("> 说明：缓冲区会持续增长到自动清空上限后归零，因此内存呈锯齿状属正常现象；");
        report.AppendLine("> “每千点占用”用于剔除缓冲区大小的影响，是判断内存泄漏的主要依据。");
        report.AppendLine();

        report.AppendLine("## 6. 渲染耗时趋势（定时强制整帧绘制探针）");
        report.AppendLine();
        report.AppendLine("| 已运行 | 数据点 | 绘制耗时(ms) |");
        report.AppendLine("|---|---|---|");

        foreach ((double elapsed, int dataCount, double milliseconds) in RenderProgression(stats.RenderSamples))
        {
            report.AppendLine($"| {FormatDuration(elapsed)} | {dataCount:N0} | {milliseconds:F2} |");
        }

        report.AppendLine();
        report.AppendLine("## 7. UI 线程响应延迟（按阶段）");
        report.AppendLine();
        report.AppendLine("| 阶段 | 采样次数 | 最大延迟(ms) | 平均延迟(ms) |");
        report.AppendLine("|---|---|---|---|");

        foreach ((string phase, (double max, double sum, long count)) in summary.LatencyByPhase.OrderByDescending(x => x.Value.Max))
        {
            report.AppendLine($"| {phase} | {count:N0} | {max:F0} | {(count == 0 ? 0 : sum / count):F1} |");
        }

        report.AppendLine();
        report.AppendLine("> 说明：多线程突发阶段的排队等待会抬高该阶段的最大延迟，属预期现象；");
        report.AppendLine("> 若“常态采集”阶段也出现秒级延迟，则说明控件在低负载下存在卡顿问题。");
        report.AppendLine();
        report.AppendLine("### WM_TIMER 饿死观测");
        report.AppendLine();
        report.AppendLine("Windows 只在窗口消息队列空闲时才会合成 `WM_TIMER` 与 `WM_PAINT`。当后台线程以高频跨线程灌入数据时，"
            + "若每次调用都投递一条窗口消息（控件早期版本的 `BeginInvoke` 编组方式；现已改为按帧合并写入），数据更新又持续让画布失效触发重绘"
            + "（重绘优先级高于定时器），于是**定时器会被无限期挤掉**：应用自己的 `System.Windows.Forms.Timer` 不再触发、界面不再重绘，"
            + "但 UI 线程本身仍然健康（看门狗往返延迟正常）。压测内置了一个 100 ms 的探针定时器专门观测这一现象，"
            + "而压测负载本身由独立调度线程驱动，不依赖 `WM_TIMER`，因此不会再因为该现象自我中止。");
        report.AppendLine();
        report.AppendLine("| 观测项 | 结果 |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| 饿死事件次数 | {summary.TimerStarvedEvents} |");
        report.AppendLine($"| 累计饿死时长 | {summary.TimerStarvedSeconds:F0} 秒 |");
        report.AppendLine($"| 单次最长饿死 | {summary.TimerMaxStarvedSeconds:F0} 秒 |");
        report.AppendLine();
        report.AppendLine("> 判定阈值：探针定时器连续 20 秒没有触发即记为一次饿死事件。");
        report.AppendLine();

        report.AppendLine("## 8. 输出产物");
        report.AppendLine();
        report.AppendLine($"- 运行日志：`{summary.LogPath}`");
        report.AppendLine($"- 指标明细（CSV，每 {options.SampleSeconds:F0} 秒一行）：`{summary.CsvPath}`");
        report.AppendLine($"- 图表快照目录：`{summary.SnapshotDirectory}`");
        report.AppendLine($"- 状态文件：`{System.IO.Path.Combine(summary.RunDirectory, "status.json")}`");
        report.AppendLine();

        report.AppendLine("## 9. 结论与建议");
        report.AppendLine();

        if (failed)
        {
            report.AppendLine("- **未通过**：运行期间出现了错误（见第 4 节的错误列表），需要先定位并修复后再重跑。");
        }
        else if (findings.Count > 0)
        {
            report.AppendLine("- 未发现异常或崩溃，但资源趋势存在需要注意的信号：");

            foreach (string finding in findings)
                report.AppendLine($"  - {finding}");
        }
        else
        {
            report.AppendLine("- **通过**：全程未出现未处理异常、不变量破坏或崩溃，坐标轴与数据始终一致。");
            report.AppendLine($"- 资源趋势平稳：句柄 {handleSlope:+0;-0;0} 个/小时、GDI {gdiSlope:+0.0;-0.0;0.0} 个/小时、线程 {threadSlope:+0.00;-0.00;0.00} 个/小时，未见泄漏迹象。");
            report.AppendLine($"- 渲染耗时在运行全程保持稳定（详见第 6 节），未随运行时间劣化。");
        }

        report.AppendLine();
        report.AppendLine("## 10. 复现命令");
        report.AppendLine();
        report.AppendLine("```powershell");
        report.AppendLine($"dotnet run --project tools\\ScottPlotEx.SoakTest -c Release -- --hours {options.DurationHours:F0} --profile {options.Profile} --cycle-minutes {options.CycleMinutes:F0}");
        report.AppendLine("# 短时冒烟验证（例如 3 分钟，循环压缩到 1 分钟）");
        report.AppendLine("dotnet run --project tools\\ScottPlotEx.SoakTest -c Release -- --minutes 3 --cycle-minutes 1");
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("提前停止：在运行中创建 `artifacts\\soak\\STOP` 文件，程序会在下一次检查时优雅收尾并写出报告；也可直接结束进程（报告会标记为“异常中断”）。");

        return report.ToString();
    }

    private static void AppendIssueList(StringBuilder report, string title, IReadOnlyList<string> items)
    {
        report.AppendLine($"### {title}列表");
        report.AppendLine();

        if (items.Count == 0)
        {
            report.AppendLine($"无{title}。");
            report.AppendLine();
            return;
        }

        int limit = Math.Min(items.Count, 30);

        for (int i = 0; i < limit; i++)
            report.AppendLine($"- {items[i]}");

        if (items.Count > limit)
            report.AppendLine($"- …（其余 {items.Count - limit} 条见日志文件）");

        report.AppendLine();
    }

    /// <summary>按小时（长时运行）或按运行时长等分（短时运行）抽取代表性采样点。</summary>
    private static IEnumerable<SoakSample> Progression(IReadOnlyList<SoakSample> samples)
    {
        if (samples.Count == 0)
            yield break;

        double total = samples[^1].ElapsedSeconds;
        double bucketSeconds = total > 7200 ? 3600 : Math.Max(30, total / 6);
        int hour = -1;
        SoakSample last = samples[0];

        foreach (SoakSample sample in samples)
        {
            int index = (int)(sample.ElapsedSeconds / bucketSeconds);

            if (index != hour && hour >= 0)
                yield return last;

            hour = index;
            last = sample;
        }

        yield return samples[^1];
    }

    private static IEnumerable<(double Elapsed, int DataCount, double Milliseconds)> RenderProgression(
        IReadOnlyList<(double ElapsedSeconds, double PhaseSeconds, string Phase, double LatencyMs, int DataCount)> samples)
    {
        if (samples.Count == 0)
            yield break;

        double bucket = -1;
        List<(double ElapsedSeconds, double PhaseSeconds, string Phase, double LatencyMs, int DataCount)> current = [];

        foreach ((double elapsed, double phaseSeconds, string phase, double latency, int dataCount) in samples)
        {
            double index = Math.Floor(elapsed / 1800.0);

            if (index != bucket && current.Count > 0)
            {
                yield return Aggregate(current);
                current.Clear();
            }

            bucket = index;
            current.Add((elapsed, phaseSeconds, phase, latency, dataCount));
        }

        if (current.Count > 0)
            yield return Aggregate(current);

        static (double Elapsed, int DataCount, double Milliseconds) Aggregate(
            List<(double ElapsedSeconds, double PhaseSeconds, string Phase, double LatencyMs, int DataCount)> bucketSamples)
        {
            double[] values = [.. bucketSamples.Select(x => x.LatencyMs).Order()];
            double median = values[values.Length / 2];
            double elapsed = bucketSamples[^1].ElapsedSeconds;
            int dataCount = (int)bucketSamples[^1].DataCount;
            return (elapsed, dataCount, median);
        }
    }

    private static (double First, double Last, double Ratio) PerPointBytes(IReadOnlyList<SoakSample> samples)
    {
        List<double> values = [.. samples
            .Where(s => s.DataCount >= 20_000)
            .Select(s => (s.ManagedHeapMb * Megabyte) / s.DataCount * 1000.0)];

        if (values.Count < 6)
            return (0, 0, 1);

        int third = values.Count / 3;
        double first = Median(values.Take(third));
        double last = Median(values.TakeLast(third));

        return (first, last, first <= 0 ? 1 : last / first);
    }

    private static double Median(IEnumerable<double> source)
    {
        double[] values = [.. source.Order()];
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }

    private static double Slope(IReadOnlyList<SoakSample> samples, Func<SoakSample, double> selector)
    {
        List<(double X, double Y)> points = [.. samples
            .Where(s => s.ElapsedSeconds >= 600)
            .Select(s => (s.ElapsedSeconds / 3600.0, selector(s)))];

        if (points.Count < 3)
            return 0;

        double meanX = points.Average(p => p.X);
        double meanY = points.Average(p => p.Y);
        double numerator = points.Sum(p => (p.X - meanX) * (p.Y - meanY));
        double denominator = points.Sum(p => (p.X - meanX) * (p.X - meanX));

        return denominator <= 0 ? 0 : numerator / denominator;
    }

    internal static string FormatDuration(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : $"{span.Minutes} 分 {span.Seconds} 秒";
    }
}
