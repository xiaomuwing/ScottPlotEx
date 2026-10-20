# ScottPlotEx 代码与性能审查（2026-10-20）

审查对象：`src/ScottPlotEx.Controls`（控件、画布、缓冲区、极值金字塔、自动滚动、时间轴、菜单/帮助）
以及测试、性能基准与压测产物。方法：逐文件阅读、运行 236 个功能测试、Release 性能基准、
针对热点做临时分配/吞吐/帧耗时探针，然后修复确认的问题并补回归测试。

## 结论摘要

- **不是“没有优化空间”**：逐点写入原先每个点分配约 **192 字节**，是高频采集时 GC 压力的主要来源；
  修复后为 **0 B/点**，堆叠模式接入从约 1.0 µs/点降到 **0.29 µs/点（约 3.4 倍）**。
- 同时确认并修复了 6 个边界问题：`Reset` 后堆叠原点不归零、隐藏最长曲线后视图空白、
  `Reset(当前 Plot)` 重复挂接图元、句柄创建前跨线程写入的竞争/数据滞留、`Signal` 线型与标记样式在 `Reset` 后丢失、
  “显示帮助”打开的是旧版鼠标提示而不是完整中文帮助。
- 剩余的两个真正上限：**数据内存无界增长**（长期运行的结构性限制）和**多曲线光栅化**（100 条曲线时每帧约 48 ms）。
  其余只剩少量低风险微优化。

> **为什么帧率没有变化？** 本轮优化的是数据接入 / 托管分配，不是绘制。100 条曲线、每系列 1 万可见点、
> Release、1100×660 的单帧剖析：总耗时 48.31 ms，其中 `RenderPlottables` **46.89 ms（97.1%）**，
> `CalculateLayout` 0.17 ms、`RenderPanels` 0.29 ms、`RegenerateTicks` 0.05 ms。
> 所以平均 FPS 由 SkiaSharp 路径构建 + 抗锯齿光栅化决定；把每点 192 B 分配降到 0 只会减少 GC 抖动，
> 不会让这一帧画得更快。跨线程流式写入本来就按帧合并，旧分配也不按“屏上点数”产生。

---

## 1. 已修复的隐藏分配（本轮最大收益）

### 测量结果

在稳定状态下测量 UI 线程 `GC.GetAllocatedBytesForCurrentThread()`，旧实现：

| `AddValue()` 内部操作 | 分配/点 |
|---|---:|
| `ReadLimits()` 调用 `Plot.Axes.GetLimits()` → `AxisManager.Bottom` + `Left` 的 LINQ 查找 | 80 B |
| `ApplyLimits()` 调用 `Axes.SetLimits(...)` → 同样的 `Bottom` + `Left` 查找 | 80 B |
| `RequestRender()` → `Control.Invalidate()` | 32 B |
| **合计** | **192 B/点** |

### 修复

- 在控件中缓存自动滚动使用的默认 X / Y 轴引用（`_xAxis` / `_yAxis`），用
  `ICollection<T>.Contains` 做零分配的有效性检查；用户替换 / 删除默认坐标轴时自动重新解析。
- `PlotCanvas.RequestRepaint()` 合并同一帧内的重复重绘请求；`OnPaint` 开头清标志，
  绘制过程中再次写入仍会触发下一帧。
- `GetCoordinates` 也改用缓存坐标轴，避免每次鼠标查询的 80 B 分配。

关键代码：

- [`ScrollingPlotControl.EnsureAxesCurrent`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1591)
- [`ScrollingPlotControl.ApplyLimits`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1750)
- [`PlotCanvas.RequestRepaint`](../src/ScottPlotEx.Controls/PlotCanvas.cs#L160)
- 回归测试：[`IngestAllocationTests`](../tests/ScottPlotEx.Tests/IngestAllocationTests.cs#L11)
  （稳定状态下 `AddValue` 每点分配 < 8 B 即失败）

### 效果

Release、本机、1100×660（完整数据见 [`artifacts/performance-report.md`](../artifacts/performance-report.md)）：

| 场景 | 修复前 | 修复后 |
|---|---:|---:|
| 堆叠模式数据接入 | 约 1.0 µs/点（约 100 万点/秒） | **0.28 ~ 0.29 µs/点（约 343 万 ~ 352 万点/秒）** |
| 堆叠模式显示 90%（100 万点缓冲） | 约 1.13 µs/点（约 89 万点/秒） | **0.75 ~ 0.82 µs/点（约 123 万 ~ 133 万点/秒）** |
| 跟随模式（1 万 ~ 100 万点缓冲） | 约 1.0 ~ 1.16 µs/点（约 86 万 ~ 95 万点/秒） | **≥ 125 万点/秒**（不同运行 125 万 ~ 667 万，受 JIT / 调度影响） |
| 逐点写入的托管分配 | **192 B/点** | **0 B/点** |

> 注：旧表里的 1.0 µs/点已经包含 GC 开销；把分配降到 0 后，吞吐提升主要来自去掉 GC 与少量 GDI 调用，
> 而不是改写了缓冲区或金字塔算法（它们本来就已经很快）。

---

## 2. 已修复的边界问题

| # | 问题 | 根因 | 修复 | 回归测试 |
|---|---|---|---|---|
| 1 | `Reset()` 后，若堆叠显示百分比 < 100，新数据仍落在旧原点之后，视图几乎空白 | `Reset` 清空数据但没有重新固定 `_stackedOriginX` | `Reset()` 清空后调用 `PinStackedOrigin()` | `Reset_AfterStackedPercent_ReestablishesZeroOrigin` |
| 2 | 堆叠百分比 < 100 时隐藏/删除最长曲线，剩余曲线整段不可见（X 轴退化为 `[旧原点, 旧原点+1]`） | 布局变化后右端变小，但固定原点没有重算 | `OnSeriesLayoutChanged()` 在堆叠模式下重新固定原点 | `HidingLongestSeries_WithStackedPercent_KeepsRemainingDataVisible` |
| 3 | `Reset(当前 Plot, disposeOldPlot: false)` 后同一条曲线被画两次、图例重复 | `AttachSeries` 只新建图元，没有移除当前 Plot 上的旧图元 | 挂接前 `plot.Remove(previous)` | `Reset_WithSamePlot_DoesNotDuplicateSignals` |
| 4 | 句柄创建前的后台写入直接跨线程修改 `List<double>` / `Plot`，可能竞争或丢数据；清空/写入顺序也无法保证 | 句柄未创建时 `Control.InvokeRequired` 恒为 false，被误判成 UI 线程 | 记录创建线程 ID；非 UI 线程写入排队；`OnHandleCreated` 统一执行排队操作、挂接排队曲线、补投合并写入 | `BackgroundWrites_BeforeHandleCreation_AreQueuedAndFlushed`、`AddSeries_BeforeHandleCreation_IsDeferredUntilHandleCreated` |
| 5 | 通过 `Signal` 直接设置的虚线 / 标记样式在 `Reset()` 后丢失 | `CaptureStyle` 只保存名称、颜色、线宽、可见性 | 同时保存 `LineStyle` / `MarkerStyle` 对象并在重建时恢复 | `SeriesStyles_SurviveReset` |
| 6 | “显示帮助”打开的是旧版 `ScottPlot.FormHelp`（只有鼠标提示），`PlotHelpContent` / `PlotHelpDialog` 实际未被产品使用 | `ShowHelp()` 仍实例化旧对话框 | 改用 `PlotHelpDialog(args.Content)`，关闭后释放 | `BuiltInHelp_UsesRichDialog` |

另外补了一个坐标轴缓存的升级护栏：用户替换默认 X 轴后，自动滚动会重新解析并作用到新轴
（`ReplacingDefaultXAxis_RefreshesCachedAxis`）。

---

## 3. 仍然存在的优化空间（按优先级）

### P1：数据内存无界增长（结构性限制，最值得做）

`PlotDataBuffer` 只追加、不淘汰：原始 `List<double>` 约 8 B/点，极值金字塔约 1.07 B/点，
再考虑 `List` 容量余量，约 **9 ~ 10 B/点**。10 条曲线 × 每条 1000 点/秒约为 **0.32 ~ 0.36 GB/小时**，
8 小时约 2.6 ~ 2.9 GB；这与 README 中“每条曲线每小时约 32 MB”一致。

现有压测报告（`artifacts/soak-report.md`）是 16 分钟、缓冲区到 250,000 点自动清空的短测，
不能证明长时间运行的内存有界；`StreamingDemoForm` 也在文档里明确提示“保留全部历史、需定期清空”。

建议新增 `RetentionPoints` / `MaxPointsPerSeries`（或“分层存储”）：

- 用环形缓冲 + 绝对下标原点，保留 `XStart / LatestX` 的绝对映射；
- 金字塔按块/分段维护，淘汰时只丢弃最老的块，避免每次 O(N) 重建；
- 调整 `Signal` 的可见下标范围（`MinRenderIndex/MaxRenderIndex`）或自定义数据源的 base index；
- 覆盖堆叠原点、跟随窗口、Y 轴统计、时间单位、拖动偏移与 `Reset`。

这是“能不能连续跑 8 小时”的硬限制，优先级高于任何微优化。

### P2：多曲线光栅化（不是微优化能解决的）

临时探针（1100×660、每系列 10,000 个可见点、Release、普通不透明窗口）：

| 系列数 | 毫秒/帧 | FPS |
|---:|---:|---:|
| 1 | 2.7 | 368 |
| 10 | 8.5 | 118 |
| 25 | 14.8 | 68 |
| 50 | 25.2 | 40 |
| 100 | 48.1 | 21 |

现有容量报告也给出同样结论：每帧耗时随曲线数线性增长，约 89% 花在 ScottPlot 拼路径 +
SkiaSharp 抗锯齿描边上，极值金字塔列查询只占约 11%。因此：

- 50 条以内：当前软件渲染仍足够（30 FPS 上限下余量充足）；
- 100 条以上且要求 60 FPS：需要 **GPU 渲染（GRContext / OpenGL）**，或自定义批量绘制路径；
- 关闭抗锯齿反而更慢（已有实测），不建议。

### P3：小优化（可选）

- `MinMaxPyramid.Query` 在极窄区间（例如每像素列只有几十个点）有固定开销：
  32 点宽实测约 68 ns/次，直接线性扫描约 57 ns/次。可在 `i2 - i1 < 64` 时直接扫描原始数据，
  或只在 `PyramidSignalSource.GetPixelColumn` 中做阈值判断；预计只影响小窗口跟随模式的几个百分点。
- `PlotCanvas.AxisLimitsChangedSinceLastRender` 每帧通过 `Axes.GetAxes()` / 数组复制分配少量对象；
  可改为遍历 `GetXAxes()/GetYAxes()` 的 `IReadOnlyList` 视图，收益很小。
- `MaxFramesPerSecond` 从 > 0 改回 0 时，`HighResolutionTimer` 的后台线程会保留到控件释放；
  频繁切换帧率上限时可考虑在安全时点释放。
- `PlotCanvas.Dispose` 释放 DIB / SKSurface，但没有释放 `Plot`；大量创建/销毁控件时建议确认
  `Plot` 是否持有需要释放的字体/图片资源（ScottPlot 的 `FormsPlot` 会释放）。
- `PlotSeries.Values` / `DataCount` 文档要求 UI 线程读取，但从后台线程读取仍可能遇到 `List` 扩容竞争；
  可提供快照接口或加锁，属于 API 硬化而非性能问题。

---

## 4. 验证

```powershell
dotnet build ScottPlotEx.sln -c Release          # 0 警告 / 0 错误
dotnet test  ScottPlotEx.sln -c Debug --filter "Category!=Performance"
# 已通过! - 失败: 0，通过: 236，已跳过: 0，总计: 236（不含 Category=Performance）

dotnet test  ScottPlotEx.sln -c Release --filter "Category=Performance"
# 结果写入 artifacts/performance-report.md（报告现在会记录 Debug/Release 构建配置）
```

新增测试：`EdgeCaseRegressionTests`（8 个）、`IngestAllocationTests`（1 个）；
性能基准的 README 命令已改为 `-c Release`，避免 Debug 构建给出偏低且不可比的数字。

---

## 5. 建议路线

1. **有界内存（P1）**：先做 `RetentionPoints` + 环形/分段缓冲；这是长期运行唯一的结构性缺口。
2. **多曲线 GPU 渲染（P2，仅当 100+ 曲线且 60 FPS 是硬需求）**：否则保持当前软件渲染，
   用 `MaxFramesPerSecond` 限制帧率。
3. **P3 的低风险清理**：窄区间扫描阈值、帧内数组分配、定时器释放、`Plot` 释放、`Values` 线程安全。
