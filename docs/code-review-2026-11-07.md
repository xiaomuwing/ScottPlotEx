# ScottPlotEx 代码复审（2026-11-07）

审查对象：`src/ScottPlotEx.Controls` 全部源文件（控件、画布、缓冲区、极值金字塔、帧率定时器、时间刻度、菜单 / 帮助），
以及测试、工具与工程配置。基线为提交 `c7a0c8a`（含工作区中未提交的 csproj 改动）。

方法：逐文件阅读；Release 构建（0 警告 / 0 错误）；对可疑点写临时探针实测（独立控制台程序引用控件库，
窗体句柄已创建但不显示，只测数据接入，不含绘制）。本文只记录**上一轮审查（[2026-10-20](code-review-2026-10-20.md)）之后新发现**的问题，
上一轮的遗留项见第 4 节。

标注说明：**实测** = 已用探针复现或测量；**推断** = 由代码路径推出，未复现。

> **修复状态（2026-11-07）**：本文各项已按建议修改并补了回归测试，逐项结果、未处理项与修复后的测量见[第 8 节](#8-修复记录2026-11-07)。

## 结论摘要

- **2 个已复现的正确性问题**：UI 线程上清空 / Reset 不会丢弃此前排队的跨线程数据（清空后旧点又出现）；
  `PlotDataBuffer.AddRange(double[])` 在 C# 12 下编译报二义性错误。
- **1 个已测量的性能问题**：UI 线程逐点写入时，每个点都要遍历全部曲线重算坐标轴，开销随可见曲线数线性增长；
  跟随模式 100 条曲线时约 **4.5 µs/点**（1 条曲线时 0.15 µs/点）。跨线程写入按帧合并，不受影响。
- **1 个推断的释放竞态**：帧率定时器在控件释放时可能把 `ObjectDisposedException` 抛进采集线程的 `AddValue` 调用。
- 其余为资源占用、API 一致性与工程卫生方面的改进项，风险都较低。

---

## 1. P1：建议优先处理

### 1.1 UI 线程清空 / Reset 不丢弃排队中的跨线程数据（实测）

**现象**：采集线程写入的数据先进入各曲线的 `PendingValues`，由 UI 线程按帧合并写入。
在后台线程调用 `ClearData()` 时，清空之前排队的数据会被丢弃（见 [`ClearData` 的非 UI 分支](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L934)），
但在 UI 线程上调用时只清空了 `Data`，没有动待处理列表；随后到来的合并写入把“清空之前”写入的点追加到清空之后。

| 操作顺序（同一控件，句柄已创建） | 清空后数据点数 | 期望 |
|---|---:|---:|
| 后台写 3 个点 → **UI 线程** `ClearData()` → 处理消息 | **3** | 0 |
| 后台写 3 个点 → **后台线程** `ClearData()` → 处理消息 | 0 | 0 |
| 后台写 2 个点 → **UI 线程** `Reset()` → 处理消息 | **2** | 0 |

典型场景：采集线程持续写入，用户点击界面上的“清空”按钮（UI 线程）——清空后立刻出现最多一帧（设置帧率上限时更多）的旧数据。

**涉及位置**：

- [`ClearData` 的 UI 线程分支](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L951)
- [`ClearSeries` 的 UI 线程分支](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1200)
- [`Reset()`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1663)

**建议修复**：UI 线程分支也在锁内丢弃对应曲线的待处理数据（`Reset` / `ClearData` 为全部曲线，`ClearSeries` 只处理该曲线）。
已投递的合并写入随后会取到空列表，`FlushPendingValues` 已能正确处理“没有新数据”的情况。

```csharp
lock (_pendingLock)
{
    foreach (PlotSeries series in _series)
    {
        series.PendingValues.Clear();
        series.PendingClear = false;
    }
}
```

回归测试：在 `CrossThreadIngestTests` 中按上表三种顺序断言清空后 `DataCount == 0`。

### 1.2 UI 线程逐点写入的开销随曲线数线性增长（实测）

**原因**：UI 线程上每次 `AddValue` 都走 [`UpdateView`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1301) →
[`ApplyAutoScroll`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1867)，每个点都要：

1. `VisibleExtents()` 遍历全部曲线；
2. [`TryCalculateVisibleYRange`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1893) 再遍历全部曲线，窗口裁掉数据时（跟随模式、堆叠 < 100%）每条曲线查一次极值金字塔；
3. `SetLimits` 写坐标轴，随后 `RememberLimits()` 又把刚写入的范围读回来；
4. 触发一次 `DataChanged`（订阅方的开销同样按点放大）。

**测量**（Release，1100×660 窗体，句柄已创建、不显示；每条曲线预灌 10,000 点；轮流向各曲线逐点写入共 20,000 点）：

| 可见曲线数 | 堆叠模式 (ns/点) | 跟随模式 (ns/点) |
|---:|---:|---:|
| 1 | 80 | 147 |
| 10 | 130 | 510 |
| 50 | 314 | 2,081 |
| 100 | 569 | **4,456** |

按 4.5 µs/点估算，100 条曲线、每条约 2,200 点/秒的 UI 线程逐点写入就会占满 UI 线程（尚未计入绘制）。
跨线程写入（采集线程直接 `AddValue` / `AddValues`）每帧只做一次坐标轴计算，不受影响；UI 线程批量 `AddValues` 每批一次，也不受影响。

**建议修复**：把自动滚动改为“标记待更新、每帧绘制前计算一次”。

- `UpdateView` 只置位 `_autoScrollPending` 并请求重绘；画布在 `RenderFrame` 之前回调控件执行一次 `ApplyAutoScroll` + `RememberLimits`。
- 控件内部读取坐标轴的路径（`ReadLimits`、手势开始 / 结束、`GetCoordinates`、`IsLatestDataVisible`）先调用一次“立即应用”，保证手势判断不受影响。
- 预计不会绘制时（最小化、不可见、渲染关闭，见 `IsRenderExpected`）保持立即计算，避免外部读取到过期范围。
- `RememberLimits()` 改为直接记录刚算出的范围，省掉一次读回。
- **兼容性风险**：外部代码若在 `AddValue` 之后立即读 `Plot.Axes.GetLimits()`，会读到上一帧的范围。
  可提供公开的 `ApplyAutoScrollNow()`，或以 `DeferAutoScrollToRender` 选项的形式引入（默认值需权衡），并相应调整依赖即时范围的测试。

预期效果：UI 线程逐点写入的开销与曲线数基本无关（约等于 1 条曲线时的水平）。

### 1.3 帧率定时器的释放竞态（推断）

- 采集线程在 [`PostPendingFlush`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L2282) 中先取到 `_frameTimer`，随后调用
  [`HighResolutionTimer.Schedule`](../src/ScottPlotEx.Controls/HighResolutionTimer.cs#L60)；若 UI 线程恰好在这两步之间释放了控件，
  `_wake.Set()` 会对已释放的事件对象抛出 `ObjectDisposedException`。这条路径没有 `try/catch`，异常会抛进用户采集线程的 `AddValue` 调用。
  （`BeginInvoke` 那条路径已经捕获了同类异常。）
- [`HighResolutionTimer.Dispose`](../src/ScottPlotEx.Controls/HighResolutionTimer.cs#L121) 在 `Join(1000)` 超时后仍会释放定时器句柄和事件，
  后台线程若此时还在 `SetWaitableTimer` / `WaitAny` 中，会抛出未处理异常并终止进程。

**建议修复**：`Schedule` 与 `Run` 内捕获 `ObjectDisposedException`；只有确认线程已退出后才释放句柄（超时则交给终结器）。

---

## 2. P2：性能与资源

| # | 问题 | 位置 | 建议 |
|---|---|---|---|
| 2.1 | `DataChanged` 在 UI 线程上**每个点触发一次**；演示程序的订阅方每次都格式化状态文字并读取一次坐标轴 | [`UpdateView`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1311) | 随 1.2 一起处理：增加按帧合并的事件，或在文档中注明触发频率 |
| 2.2 | 最小化 / 不可见且 `MaxFramesPerSecond = 0`（默认）时，合并写入**不节流**：`FlushPendingValues` 一开始就清除投递标记，每批数据到达都会触发一次合并写入与坐标轴重算（推断） | [`FlushPendingValues`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L2331) | 预计不会绘制时按 `PendingFlushTimeout`（100 ms）节流，或复用帧率定时器 |
| 2.3 | `PendingValues` / `DrainingValues` 只扩容不收缩：UI 线程卡顿一次造成的突发积压，会让每条曲线长期占用两份大数组 | [`PlotSeries`](../src/ScottPlotEx.Controls/PlotSeries.cs#L55) | 取走数据后，若容量远大于本批点数（例如超过 64K 且是本批的 4 倍以上），释放多余容量 |
| 2.4 | 每个设置了帧率上限的控件各自占用一个后台线程；上限改回 0 后线程保留到控件释放（上一轮 P3 遗留） | [`MaxFramesPerSecond`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L723) | 所有控件共用一个静态调度线程；上限改回 0 时释放 |

---

## 3. P3：API、正确性细节与可维护性

| # | 问题 | 位置 |
|---|---|---|
| 3.1 | 公开的 `AxisOptions` 可直接修改但不会立即生效：例如直接设置 `AxisOptions.StackedDisplayPercent` 不会重新固定堆叠原点，要等下一次切换模式 / 清空 / 隐藏曲线时才“突然”生效；各字段也没有校验（如 `EmptyXMax <= EmptyXMin`、负的留白比例）。建议提供 `ApplyAxisOptions()` 或变更通知，并把与控件属性重复的字段收为内部设置 | [`AxisOptions`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L476) |
| 3.2 | **`PlotDataBuffer.AddRange(double[])` 在 C# 12（net8.0 默认语言版本）下报 CS0121**：`IEnumerable<double>` 与 `ReadOnlySpan<double>` 两个重载二义（实测；控件库自身用 `LangVersion latest` 编译，不会暴露）。补一个 `AddRange(double[])` 重载即可 | [`PlotDataBuffer.AddRange`](../src/ScottPlotEx.Controls/PlotDataBuffer.cs#L112) |
| 3.3 | `Values` 直接返回内部 `List<double>`：调用方强转为 `List<double>` 后修改会让极值金字塔失步、绘制错误；后台线程读取时也可能遇到扩容竞争（上一轮 P3 遗留的线程安全问题）。建议返回只读视图，并提供 `CopyTo(Span<double>)` / 快照接口 | [`PlotDataBuffer.Values`](../src/ScottPlotEx.Controls/PlotDataBuffer.cs#L25)、[`PlotSeries.Values`](../src/ScottPlotEx.Controls/PlotSeries.cs#L228) |
| 3.4 | 死代码：`FormHelp` 是**声明在 `ScottPlot` 命名空间中的公开类型**（污染第三方命名空间），产品代码已改用 `PlotHelpDialog`，只剩压测工具用它关窗口；`PlotHelpDialog` 与本控件各有一个从未调用的 `InitializeComponent()` | [FormHelp.cs](../src/ScottPlotEx.Controls/FormHelp.cs#L6)、[PlotHelpDialog.cs](../src/ScottPlotEx.Controls/PlotHelpDialog.cs#L61)、[ScrollingPlotControl.cs](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L2456)、[SoakWorkload.cs](../tools/ScottPlotEx.SoakTest/SoakWorkload.cs#L946) |
| 3.5 | 每次点击“显示帮助”都新开一个非模态窗口，可以同时打开多个。建议复用同一个实例（已打开时激活） | [`ShowHelp`](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1742) |
| 3.6 | 右键菜单按**显示文字**识别模式项：同名的自定义菜单项会被当成模式项，模式项文字也无法改名 / 本地化。建议用菜单项实例或专用标记识别 | [`BuildContextMenu`](../src/ScottPlotEx.Controls/ChinesePlotMenu.cs#L218) |
| 3.7 | 菜单细节：“自动缩放”先同步 `Refresh()` 一帧，紧接着 `HandleAutoscaleRequested()` 往往又请求重绘（多画一帧）；“在新窗口中打开”的标题仍为英文 “Interactive Plot”；从未渲染过时“复制到剪贴板”以 0×0 尺寸取图会抛异常 | [ChinesePlotMenu.cs](../src/ScottPlotEx.Controls/ChinesePlotMenu.cs#L128) |
| 3.8 | 工程卫生：① 4 个 csproj 重复声明目标框架 / 语言版本 / 可空性，上次改目标框架就要改 4 处，建议改用 `Directory.Build.props` + `Directory.Packages.props`；② 工作区有**未提交**改动：控件库目标框架被改为 `net8.0-windows7.0`、演示与测试项目 csproj 被加了 BOM（疑似 Visual Studio 改写），与上次提交不一致，需确认后提交或还原；③ 仓库没有 CI，建议加 Windows 工作流运行构建与 `Category!=Performance` 测试；④ `artifacts/tmp-axisprobe` 是已修复缺陷的临时探针，可删除或确认已有等价回归测试 | — |

---

## 4. 上一轮遗留项现状

| 上一轮编号 | 内容 | 现状 |
|---|---|---|
| P1 | 数据内存无界增长（无 `RetentionPoints` / 保留上限） | **未处理**，仍是长时间运行唯一的结构性缺口 |
| P2 | 100 条以上曲线的光栅化瓶颈 | 未处理（渲染本身的限制，需 GPU 或自定义绘制路径） |
| P3 | `MinMaxPyramid.Query` 窄区间扫描阈值（仍为 32） | 未处理，收益很小 |
| P3 | `PlotCanvas.AxisLimitsChangedSinceLastRender` 每帧数组分配 | 未处理，收益很小 |
| P3 | 帧率上限改回 0 后定时器线程不释放 | 未处理，并入本文 2.4 |
| P3 | `PlotCanvas.Dispose` 不释放 `Plot` | 未处理 |
| P3 | `Values` 后台线程读取的线程安全 | 未处理，并入本文 3.3 |

---

## 5. 已核实、无需优化

| 怀疑点 | 测量 | 结论 |
|---|---:|---|
| `IsOnUiThread` 句柄创建后每次调用 `InvokeRequired`（两次 P/Invoke） | 7.4 ns/次（改为比较线程 ID 为 0.6 ns） | 只占逐点写入的约 5%~9%，不值得改 |
| `PlotDataBuffer.AddRange` 逐点维护极值金字塔 | 9.5 ns/点（每批 1000 点） | 不是瓶颈，无需向量化 |
| 时间单位刻度生成器每帧重新格式化标签 | 已有基准：时间单位每帧 3.87 ms，点数单位 4.15 ms | 没有额外开销 |

---

## 6. 复现方法

探针为临时控制台程序（`net8.0-windows`、`UseWindowsForms`，引用 `ScottPlotEx.Controls.csproj`，Release 构建），不在仓库中：

- **1.1**：创建窗体与控件并取 `Handle`（不显示）；后台线程写入若干点后 `Join`；UI 线程调用 `ClearData()` / `Reset()`；
  循环 `Application.DoEvents()` 处理合并写入后读取 `DataCount`。
- **1.2**：同上创建控件，设置模式后添加 N−1 条曲线，每条预灌 10,000 点；预热 2,000 次写入后，
  轮流向各曲线逐点 `AddValue` 共 20,000 点，用 `Stopwatch` 计时取平均。
- **3.2**：在默认语言版本（C# 12）的项目中调用 `new PlotDataBuffer().AddRange(new double[10])`。

修复时建议把 1.1、1.2、3.2 转为正式的回归测试（1.2 可放入 `IngestAllocationTests` 同类的性能护栏：100 条曲线时逐点开销不超过 1 条曲线时的若干倍）。

---

## 7. 建议路线

1. **1.1 + 1.3**：改动小、属于正确性问题，先修并补回归测试。
2. **1.2（同时解决 2.1）**：收益最大，但涉及“坐标轴何时更新”的语义，需要先确定兼容策略。
3. **3.2、3.4、3.8**：低风险清理，可以和上面任意一项一起提交。
4. 上一轮的 **有界内存（P1）** 仍是长期运行的硬限制，排在上述修复之后。

---

## 8. 修复记录（2026-11-07）

回归测试集中在 [`ReviewFixes20261107Tests`](../tests/ScottPlotEx.Tests/ReviewFixes20261107Tests.cs)（16 个）。
其中 1.1（3 种清空方式）、1.2 性能护栏、2.2、2.3 这 6 个测试放到修改前的代码上运行**全部失败**，修改后全部通过；
2.2 原先是推断，这次实测确认：修改前窗口最小化时 1 秒内合并写入 **1000 次**（每批数据一次），修改后按 100 ms 节流（理论约 10 次，回归测试以 30 次为上限）。

### 8.1 逐项结果

| 编号 | 处理 | 说明 |
|---|---|---|
| 1.1 | ✅ 已修复 | UI 线程上的 `ClearData` / `PlotSeries.Clear` / `Reset` 在锁内丢弃对应曲线排队中的跨线程数据（`DiscardPendingWrites`），与后台线程清空的语义一致 |
| 1.2 | ✅ 已修复 | 新增 `AxisUpdateMode`（`Auto` 默认 / `Immediate` / `Deferred`）与 `UpdateAxesNow()`。`Auto`：可见曲线不超过 `AutoDeferVisibleSeriesThreshold`（8）条时保持原来的立即更新，超过时把坐标轴计算合并到本轮消息处理结束后（投递一条消息）；绘制、鼠标 / 键盘输入、控件内部读取坐标轴（手势判断、`GetCoordinates`、菜单导出图像 / 自动缩放）之前都会先完成合并中的更新。跨线程合并写入仍立即计算。**兼容性**：曲线不超过 8 条时行为与修改前完全相同；超过 8 条且在写入的同一段代码里直接读 `Plot.Axes.GetLimits()` 会读到更新前的范围（文档与 XML 注释已说明，调用 `UpdateAxesNow()` 即可）。“`RememberLimits` 直接记录刚算出的范围”这一微优化未做：坐标轴反向时读回值与写入值不同，收益不值得冒这个风险 |
| 1.3 | ✅ 已修复 | `HighResolutionTimer.Schedule` 返回是否生效，并发释放时不再抛异常（调用方改为直接投递）；`Run` 捕获释放后的 `ObjectDisposedException`；`Dispose` 只有确认等待线程退出后才释放句柄 |
| 2.1 | ✅ 文档 | `DataChanged` 的触发频率与合并更新时的注意事项写进 XML 注释与 README；事件语义（每次写入触发一次）保持不变，避免破坏现有订阅方 |
| 2.2 | ✅ 已修复 | 不会绘制时保留投递标记、按 100 ms 节流，并用一次性线程池定时器保证最后一批数据在超时后写入 |
| 2.3 | ✅ 已修复 | 取走数据后，容量超过 64K 且远大于本批点数（4 倍以上）时收缩到本批的 2 倍（至少 1024） |
| 2.4 | ⚠️ 部分 | 帧率上限改回 0 时释放定时线程（若有一次合并写入正等着定时器，改为立即投递）。“多个控件共用一个调度线程”未做：实测每个定时线程绝大部分时间阻塞在内核等待中，CPU 不到 1%，收益很小 |
| 3.1 | ✅ 已修复 | 新增 `ApplyAxisOptions()`（校验 → 限制百分比 → 重新固定堆叠原点 → 重算坐标轴）与 `ScrollingAxisOptions.Validate()` |
| 3.2 | ✅ 已修复 | `PlotDataBuffer.AddRange(double[])` 重载 |
| 3.3 | ✅ 已修复 | `Values` 改为实时只读视图（`ReadOnlyValueList`，不复制、不能转回 `List`）；新增可在任意线程调用的 `GetValuesSnapshot()`（控件与 `PlotSeries` 各一个） |
| 3.4 | ✅ 已修复 | 删除 `ScottPlot.FormHelp`（及其 Designer / resx）和两个空的 `InitializeComponent`；压测工具改为按标题关闭帮助窗口。**这是一处破坏性变更**：外部代码若引用了 `ScottPlot.FormHelp` 需要改用 `ShowHelp()` |
| 3.5 | ✅ 已修复 | 帮助窗口已打开时复用（更新正文并激活），关闭后再请求才新开；控件释放时关闭 |
| 3.6 | ✅ 已修复 | 模式菜单项按动作委托识别，`ToolStripMenuItem.Tag` 记录模式；`TryGetMode(string)` 保留（只认默认文字） |
| 3.7 | ✅ 已修复 | “自动缩放”不再中途同步绘制一帧；新窗口标题改为“交互式查看”；从未渲染过时按画布尺寸导出图像（尺寸仍为 0 时提示而不是抛异常）；导出与新窗口之前先完成合并中的坐标轴更新 |
| 3.8 | ✅ 已处理 | 新增 `Directory.Build.props`（目标框架等公共设置）与 `Directory.Packages.props`（集中包版本），4 个 csproj 去掉重复设置；目标框架统一为 `net8.0-windows`（与工作区里未提交的 `net8.0-windows7.0` 等价，输出目录与压测脚本保持一致）；CI 工作流（Windows 上构建并运行 `Category!=Performance` 测试）**另行提交**，不在本次改动内；删除 `artifacts/tmp-axisprobe`（已由 `NewControl_HasStableEmptyViewBeforeFirstDataPoint` 覆盖） |

### 8.2 审查之后追加的两处修改

- **帧率上限改为固定时间表调度**：下一帧时刻 = 上一帧的计划时刻 + 间隔（`_frameSlot`），落后超过一帧时以当前时刻重新对齐、不补帧。
  原先是“上一次合并写入实际开始的时刻 + 间隔”，定时迟到和消息排队的延迟会叠加进每个周期，30 帧实际只有约 29.6、60 帧约 58.3。
- **更正 `WM_TIMER` 的说法**：实测不限帧率（默认 `MaxFramesPerSecond = 0`）且数据持续到达时，UI 线程占满 100%，
  窗体上 50 ms 的 WinForms 定时器 6 秒内一次都没触发。代码注释与 README 原来写着“不会饿死 `WM_TIMER`”，已改为如实说明，并建议此时设置帧率上限。
  **默认值没有改**：把默认帧率上限从 0 改成 60 会改变现有行为（不限帧率时可以跑到约 115 帧），需要另行决定。

### 8.3 上一轮遗留项

| 内容 | 现状 |
|---|---|
| `PlotCanvas.AxisLimitsChangedSinceLastRender` 每帧数组分配 | ✅ 改为按下标遍历 X / Y 坐标轴列表；新增契约测试确认 `GetAxes()` 正好是 X、Y 坐标轴之和（否则单遍渲染会每帧多画一遍） |
| 有界内存（保留上限） | 未处理，仍是长时间运行的结构性缺口 |
| `MinMaxPyramid.Query` 窄区间阈值、`PlotCanvas.Dispose` 不释放 `Plot` | 未处理（前者收益只有几个百分点；后者可能影响在控件释放后仍使用同一个 `Plot` 的调用方，需要先确定语义） |

### 8.4 验证

```powershell
dotnet build ScottPlotEx.sln -c Release      # 0 警告 / 0 错误
dotnet test  ScottPlotEx.sln -c Debug --filter "Category!=Performance"
```

结果：Release 构建 0 警告 / 0 错误；非性能测试 **252 个全部通过**（原有 236 个 + 新增 16 个）；
性能基准（`Category=Performance`，Release）4 个测试类全部通过，`artifacts/` 下的 4 份报告已按新代码重新生成。

修复后的关键测量（Release，本机）：

| 场景 | 修复前 | 修复后 |
|---|---:|---:|
| UI 线程逐点写入，跟随模式，1 / 10 / 50 / 100 条曲线（ns/点，窗体句柄已创建、不绘制） | 147 / 510 / 2,081 / 4,456 | 153 / 39 / 40 / 39 |
| UI 线程逐点写入，堆叠模式，1 / 100 条曲线（ns/点） | 80 / 569 | 68 / 43 |
| 10 条曲线 × 1000 点/秒，`MaxFramesPerSecond = 30` 的实测帧率（`multi-series-streaming-report.md`） | 29.6 | **30.0** |
| 窗口最小化时 1 秒内的合并写入次数（约 1 kHz 逐点跨线程写入） | 1,000 | 按 100 ms 节流（理论约 10 次；测试上限 30 次，已通过） |

单条曲线时仍是立即更新，开销与修复前相同；性能报告里单曲线接入的小幅差异（例如堆叠模式 0.29 → 0.32 µs/点）在同一测试多次运行的波动范围之内，单独复测为 68 ns/点（修复前 80 ns/点）。
