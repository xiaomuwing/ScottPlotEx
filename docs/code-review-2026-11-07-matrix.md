# ScottPlotEx 覆盖矩阵审查（2026-11-07）

审查对象：分支 `review-fixes-2026-11-07`（[PR #1](https://github.com/xiaomuwing/ScottPlotEx/pull/1)）上的 `src/ScottPlotEx.Controls`。
预期行为以 README 的“需求实现对照”和“行为细则”为准；README 没有规定的地方单独列为“待确认”。

> **状态（2026-11-07）**：待确认的业务语义已定稿（[7.1 节](#71-定稿结果2026-11-07)）；M1–M8、P2、P3 已按定稿修改，修复记录见[第 10 节](#10-修复记录2026-11-07)。M9、P1 随 Q6 延后。

## 为什么做这一轮

前两轮审查（[2026-10-20](code-review-2026-10-20.md)、[2026-11-07](code-review-2026-11-07.md)）都是挑一个角度深挖，
没有按清单覆盖所有维度，所以修完一轮、换个角度又能找到一批。本轮的做法是：
- 先列出要覆盖的维度，逐格验证；
- 每一格给出结论和依据；
- 以后每次改动，只复查受影响的格子。

本文列出的问题，在上次修改之前的 `master`（`c7a0c8a`）上就已经存在（已逐项核对代码），不是上次修改引入的。

## 方法

**5 个维度**

| 矩阵 | 内容 |
|---|---|
| A | 输入方式 × 滚动模式 |
| B | 接口 × 调用线程 |
| C | 数据情况 × 功能 |
| D | 生命周期 × 数据流 |
| E | 规模 × 性能指标 |

**依据的三种来源**
- **实测**：用临时探针在真实窗体里运行。窗体句柄、消息循环、ScottPlot 输入管线都是真实的；鼠标、键盘事件从画布的 `OnMouseXxx` / `OnKeyXxx` 注入，做法与现有的 `MouseInteractionTests` 相同。探针在审查后已删除，关键数值记在各格里。
- **已有测试**：现有自动化测试已经覆盖，格中写出测试名。
- **推断**：读代码得出，没有复现。

**图例**

| 标记 | 含义 |
|---|---|
| ✅ | 符合预期 |
| ❌ | 有问题，编号见第 6 节 |
| ⚠️ | 需要确认业务语义，编号见第 7 节 |
| 🔲 | 未验证 |
| ➖ | 不适用 |

矩阵 A–D 的初始数据都是 100 个点：正弦波，Y 范围约 [-1, 21]；控件尺寸 900×500。

## 结论摘要

**高优先级：3 项**
- **M1** 缺失值（NaN、Infinity）被丢弃且不占位，之后的点整体往前错一格：多曲线对齐、时间位置都会错。
- **M2** 数据持续到达时，拖动和框选几乎总会被判定为"看不到最新数据"而停止滚动。数据频率越高越明显：1 kHz 下轻拖 300 ms 就会停止。
- **M3** 后台线程调用 `AddSeries`、曲线属性等接口时同步等待 UI 线程；如果 UI 线程正在等后台线程（例如关闭窗体时），两边互相等待，形成死锁。

**中优先级：6 项**
- **M4** 键盘操作（方向键、Ctrl+方向键、A 键）不参与滚动策略。
- **M5** 堆叠模式下中键单击或菜单"自动缩放"后，视图不符合需求 1。
- **M6** 剪贴板被其它程序占用时，"复制到剪贴板"抛出未处理异常。
- **M7** 数据绝对值接近 1e308 时，每次绘制都抛出未处理异常。
- **M8** 控件自身的配置接口没有线程保护：在后台线程调用时直接改 Plot，不报错也不切换线程。
- **M9** 内存没有上限（上一轮遗留）。

**性能：3 项**
- **P1** 数据列表翻倍扩容时，UI 线程卡顿。
- **P2** 画面内容没变的重绘也要完整渲染一遍。
- **P3** 默认不限帧率：同样的数据量下，UI 线程 CPU 约为限 30 帧时的两倍，还会让窗体上的 WinForms 定时器长时间不触发。

**另外**：待确认的业务语义 7 项（第 7 节）；未验证 4 格（第 8 节）。

---

## A. 输入方式 × 滚动模式

"之后"指操作完成后立即读取的坐标轴范围；"+1 点"指随后再写入 1 个点时的范围。

| 输入 | 堆叠 | 跟随 | 手动（由堆叠停止） | 手动（由跟随停止） |
|---|---|---|---|---|
| 左键拖动，仍能看到最新数据 | ✅ 继续滚动；X 最小值保持在拖动后的 8.27，+1 点后为 [8.27, 103] | ✅ 继续滚动，偏移 2.43，窗口随数据滑动 | ✅ 保持视图 | ✅ 保持视图 |
| 左键拖动，看不到最新数据 | ✅ 停止滚动 | ✅ 停止滚动 | ✅ | ✅ |
| 左键只做竖直拖动 | ⚠️ Q2：拖动后 Y 为 [10, 32]，+1 点后回到 [-1, 21] | ⚠️ Q2，同左 | ✅ 保持视图 | ✅ 保持视图 |
| 中键框选，框内含最新数据 | ✅ 继续滚动，X 最小值保持 61.24 | ✅ 继续滚动 | ✅ 保持视图 | ✅ 保持视图 |
| 中键框选，框内不含最新数据 | ✅ 停止滚动 | ✅ 停止滚动 | ✅ | ✅ |
| Alt+左键框选 | 🔲 注入的 Alt 键没有生效，实际执行的是平移；按代码会被识别为左键拖动，规则与中键框选一致 | 🔲 | 🔲 | 🔲 |
| 右键拖动缩放 | ✅ 停止滚动 | ✅ 停止滚动 | ✅ | ✅ |
| 滚轮放大 / 缩小 | ✅ 停止滚动 | ✅ 停止滚动 | ✅ | ✅ |
| 中键单击（整体显示） | ❌ **M5**：之后为 X [-3.95, 104.95]、Y [-1.5, 21.5]（ScottPlot 自动缩放的范围），+1 点后才回到 [0, 103] | ✅ 换成堆叠，立即为 [0, 102] | ✅ 恢复堆叠，[0, 102] | ✅ 恢复后再换成堆叠 |
| 菜单"自动缩放" | ❌ **M5**，同上 | ✅ | ✅ | ✅ |
| 左键双击 | ✅ 视图不变（显示 ScottPlot 渲染耗时信息） | ✅ | ✅ | ✅ |
| 方向键 ← | ❌ **M4**：之后为 X [-2.4, 99.6]，+1 点后被拉回 | ❌ **M4** | ✅ 保持视图 | ✅ 保持视图 |
| Ctrl+↑（缩放） | ❌ **M4**：Y 缩放到 [0.43, 19.57]，没有停止滚动，+1 点后被覆盖（违背需求 3） | ❌ **M4** | ✅ 保持视图 | ✅ 保持视图 |
| A 键（自动缩放） | ❌ **M4** + **M5**：显示 ScottPlot 自动缩放的范围，模式不变 | ❌ **M4**：没有换成堆叠，与中键行为不一致（补充 2） | ❌ **M4**：没有恢复滚动（补充 1） | ❌ **M4** |
| 拖动期间有新数据到达 | ❌ **M2**：见下表 | ❌ **M2** | ➖ | ➖ |
| 多曲线合并更新时立即滚轮 | ✅ 10 条曲线写入 50 点后立即滚轮，缩放基于最新的自动范围 | — | — | — |

**M2 的实测数据**（拖动幅度相同，只是拖动期间到达的点数不同）：

| 场景 | 松开后 |
|---|---|
| 竖直拖动，期间到达 1 个点 | 堆叠（继续滚动） |
| 竖直拖动，期间到达 3 个点 | **手动（停止滚动）** |
| 后台 1 kHz 持续写入，向左轻拖 8%，按住约 300 ms | **手动**：最新数据 X = 151，视图右端只到 132.97 |

**右键菜单 × 状态**

| 菜单项 | 结论 |
|---|---|
| 保存图像 | ✅ 有异常处理（推断） |
| 复制到剪贴板 | ❌ **M6**：剪贴板被占用时约 1.1 秒后抛出 `ExternalException`，菜单点击里没有捕获 |
| 自动缩放 | 见上表 |
| 在新窗口中打开 | ✅ 窗口打开期间后台数据继续写入（100 → 199 点）；关闭后画布恢复，滚轮缩放正常（实测） |
| 堆叠 / 跟随 / 手动 勾选项 | ✅ 已有测试 `ChinesePlotMenuTests.ModeItems_*` |
| 显示帮助 | ✅ 重复打开时复用同一个窗口（已有测试 `ReviewFixes20261107Tests`） |

## B. 接口 × 调用线程

"UI 线程等待时"指 UI 线程正在 `Join` 或 `Wait` 采集线程，例如关闭窗体时等采集线程退出。

| 接口 | UI 线程 | 后台线程（句柄已创建） | 后台线程（句柄创建前） | UI 线程等待时 |
|---|---|---|---|---|
| `AddValue` / `AddValues` | ✅ 同步写入 | ✅ 排队、按帧合并，调用耗时 0 ms（实测） | ✅ 已有测试 `BackgroundWrites_BeforeHandleCreation_*` | ✅ 不阻塞 |
| `ClearData` / `PlotSeries.Clear` | ✅ 会丢弃排队中的数据 | ✅ 排队，顺序保持 | ✅ 排队（推断） | ✅ 不阻塞 |
| `AddSeries` | ✅ | ✅ 同步切到 UI 线程，约 2 ms | ✅ 已有测试 `AddSeries_BeforeHandleCreation_*` | ❌ **M3**：死锁（实测等待 3 s 超时） |
| 曲线属性（`Name` / `Color` / `LineWidth` / `IsVisible` / `SampleRate` / `SampleInterval` / `StartTime`）、`Remove` | ✅ | ✅ 同步切到 UI 线程，1–2 ms | ✅ 排队 | ❌ **M3** |
| `GetValuesSnapshot` / `UpdateAxesNow` | ✅ | ✅ 同步切到 UI 线程 | ➖ `GetValuesSnapshot` 按文档抛 `InvalidOperationException` | ❌ **M3** |
| `Values` / `DataCount` / `LatestX` 读取 | ✅ | ➖ 文档规定只在 UI 线程读取（其它线程用 `GetValuesSnapshot`） | ➖ | ➖ |
| 控件配置：`Mode`、`StopScrolling`、`SetXAxisDisplayPercent`、各百分比 / 窗口属性、`XAxisUnit`、`DefaultSampleInterval`、`DefaultStartTime`、`ShowLegend`、`ApplyAxisOptions`、`ShowAllData`、`Reset()` | ✅ | ❌ **M8**：13 个接口都在后台线程直接执行、正常返回，与 UI 线程的渲染并发修改 Plot（实测） | ❌ **M8** | ➖ |
| `MaxFramesPerSecond` / `AxisUpdateMode` | ✅ | ✅ 用原子操作保存，可以调用（推断） | ✅ | ✅ |
| `Refresh()` | ✅ | ✅ 抛出 WinForms 的"线程间操作无效"（符合 WinForms 惯例） | ➖ | ➖ |
| `RequestRender()` | ✅ | ⚠️ 属于 **M8**（推断）：会在后台线程调用 `Invalidate()`。Release 下 Win32 的 `InvalidateRect` 本身线程安全；附加调试器时，如果没有正在排队的重绘请求，WinForms 会抛出"线程间操作无效" | ➖ | ➖ |

## C. 数据情况 × 功能

渲染耗时为 900×500 下同步重绘一帧的时间（实测）。

| 数据情况 | 坐标轴 | 渲染 | 结论 |
|---|---|---|---|
| 空数据 | X [0, 1]，Y [-0.5, 0.5] | 1.2 ms | ✅ |
| 单点 5 | X [0, 3]，Y [4.5, 5.5] | 0.8 ms | ✅ |
| 全部相同 / 全 0 | Y 为 [4.5, 5.5] / [-0.5, 0.5] | 约 1 ms | ✅ |
| 混入 NaN、Infinity | 被丢弃且不占位 | — | ❌ **M1**：A 曲线写入 `[1,2,NaN,4,5]`，B 曲线写入 `[1,2,3,4,5]`；A 只剩 4 个点，值 5 落在 X=4，而 B 在 X=4 处是 4 |
| 全部为 NaN | 空视图 | 1.5 ms | ✅ |
| 量级 1e-300 | 正常 | 1.5 ms | ✅ |
| 1e15 附近 0.01 级的波动 | 正常 | 1.2 ms | ✅ |
| ±1e307、1e300 | 正常 | — | ✅ |
| ±1e308、[0, 1.7e308] | — | ❌ | ❌ **M7**：ScottPlot 抛出"limits returned by the autoscaler must always be in a good state"，绘制时未处理，之后每次重绘都失败 |
| 隐藏全部曲线 | 空视图 | 1.2 ms | ✅ |
| 相对时间，1 MHz，100 万点 | X [0, 1] 秒 | 3.4 ms | ✅ |
| 绝对时间，跨午夜 | 刻度正常 | 1.2 ms | ✅ |
| 滚轮连续缩小 400 格 | 范围约 ±1450，刻度正常 | 0.7 ms | ✅ |
| 多条曲线长度、采样率、起始时间不同 | — | — | ✅ 已有测试 `MultiSeriesTests`、`XAxisUnitTests` |
| 参考时刻移动（删除或清空最早的曲线） | — | — | ✅ 已有测试 `RemovingEarliestSeries_ShiftsReference` |
| 采集丢点、不等间隔时间戳 | 无法表达：没有按 (时间, 值) 写入的接口 | — | ⚠️ **Q4**（与 M1 同源） |
| 千万级点数 | 正常 | 见 E | ❌ **P1**、**M9** |

## D. 生命周期 × 数据流

| 场景 | 结论 |
|---|---|
| 最小化时后台持续写入 | ✅ 按 100 ms 节流（上一轮实测：1 秒内合并写入 ≤ 30 次，最后到达的数据不会滞留） |
| `Visible = false` 时后台持续写入 | ✅ 已有测试 `HiddenControl_ValuesStillArrive` |
| 后台写入期间在 UI 线程调用 `Reset()` / `ClearData()` | ✅ 已有测试 `ReviewFixes20261107Tests` 1.1 |
| `Reset(plot)` 传入新 Plot 或同一个 Plot | ✅ 已有测试 `Reset_WithSamePlot_DoesNotDuplicateSignals`、`SeriesStyles_SurviveReset` |
| 后台仍在写入时释放控件 | ✅ 已有测试 `AfterDispose_DataCallsAreSafe`、`ReviewFixes` 1.3；另有 8 小时压力测试 |
| 关闭窗体时 UI 线程等待采集线程 | ❌ **M3** |
| 调整大小 | ✅ 已有测试 `ResizingForm_DoesNotThrow`、`Resizing_DoesNotLeakGdiObjects` |
| 拖到另一块 DPI 不同的显示器 | 🔲 只有 `DisplayScale` 的单元测试，没有在真实多显示器上验证 |
| 右键菜单打开期间模式变化 | ✅ 已有测试 `ModeItems_SyncWhileMenuIsOpen` |
| "在新窗口中打开"期间后台持续写入 | ✅ 实测，见矩阵 A |
| 长时间、高频率运行 | ❌ **M9**、**P1** |

## E. 规模 × 性能指标

测试尺寸 1100×660，Release 构建。数据来源：
- `artifacts/` 下的 4 份性能报告（本次 PR 重新生成）；
- 本轮新增的实测。

| 规模 | 数据接入 | 单帧 | UI 线程 CPU | 内存 | 卡顿 | 结论 |
|---|---|---|---|---|---|---|
| 单曲线，每秒 1 点（需求基线） | — | 约 2 ms | 可忽略 | 可忽略 | 无 | ✅ |
| 单曲线，100 万点 | 0.36 µs/点（约 270 万点/秒） | 4.2 ms（1920×1080 下 5.3 ms） | — | 约 9 MB（推算） | 无 | ✅ |
| 10 路 × 1 kHz，限 30 帧 | 达成 100% | 6.6 ms | 19.8% | 每小时 336 MB（实测：1 小时数据后托管堆增加 336 MB） | 见下行 | ✅（内存见 **M9**） |
| 10 路 × 1 kHz，不限帧率（默认） | 达成 100% | 4.2 ms | **37.1%** | 同上 | — | ❌ **P3** |
| 10 路 × 1 kHz，运行 1 小时（每路 360 万点） | 每次写入 6 万点最慢 8 ms | 堆叠全量 10.9 ms；跟随 60 秒窗口 6.1 ms | — | 336 MB | — | ✅ |
| 10 路 × 1 kHz，运行约 9 小时（每路约 3277 万点） | — | — | — | 约 3 GB | 单条曲线扩容一次 76 ms，10 条在同一次合并写入里依次扩容约 0.76 s（推算） | ❌ **P1** |
| 10 路 × 1 kHz，运行 24 小时 | — | — | — | 约 8 GB（推算，扩容后瞬时更高） | — | ❌ **M9** |
| 100 条曲线 × 100 Hz，限 30 帧 | 达成 100% | 21.6 ms | 65% | — | — | ✅ |
| 150 条曲线 × 100 Hz，限 30 帧 | 达成 100% | 32.1 ms | 96% | — | — | ⚠️ 已到上限 |
| 200 条以上曲线 | 达成 100% | 42 ms 以上 | 约 100% | — | 掉帧 | ➖ 规模上限。瓶颈是 ScottPlot 逐条绘制曲线（约 0.2 ms/条/帧），控件这边没有明显可省的开销 |
| 画面静止，窗口局部被遮挡后重新露出 | — | 9.7 ms/次（1600×900，8 条 × 20 万点） | — | — | — | ❌ **P2** |

---

## 6. 问题清单

### 高优先级

**M1 缺失值被丢弃且不占位，之后的点 X 全部错位（实测）**
- **位置**：[PlotDataBuffer.cs:95](../src/ScottPlotEx.Controls/PlotDataBuffer.cs#L95)。非有限值只计数、不写入，而 X 由下标决定（X = 起始 + 下标 × 间隔）。
- **影响**：
  - 一个 NaN 之后，这条曲线所有点的 X 都提前一个间隔；
  - 多条曲线按序号或按时间的对齐被打乱（需求补充 7）；
  - 时间单位下显示的时刻错误。
- **建议**：缺失值也占一个下标，画成断线，坐标轴计算跳过它。要改三处：
  - `PlotDataBuffer` 保存 NaN；
  - 极值金字塔跳过 NaN（新块的第一个值是 NaN 时不能作为初值）；
  - `PyramidSignalSource` 遇到全 NaN 的像素列时不连线。

  显示方式需要先确认，见 Q4。

**M2 数据持续到达时，拖动 / 框选会被误判为"看不到最新数据"（实测）**
- **位置**：[ScrollingPlotControl.cs:2383](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L2383)、[:2481](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L2481)。
- **原因**：
  - 手势期间暂停自动滚动（补充 4），新数据照常写入、坐标轴不动；
  - 松开时用"现在的最新数据"判断是否可见；
  - 堆叠模式右侧只留 2 个点，所以手势期间只要到达 3 个点以上，最新数据就落在视图外，于是停止滚动。
- **影响**：需求 6、7 在数据流持续写入时基本不成立：
  - 1 Hz 数据下，按住超过 2 秒就会停止滚动；
  - 1 kHz 数据下，任何拖动都会停止滚动。

  同样的手势，结果取决于按住了多久。
- **建议**：用按下鼠标时的最新数据判断是否可见（手势期间到达的数据不计入）；或者把可见判定的右侧容差放宽到"手势期间新增的 X 跨度"。前者更直观。

**M3 后台线程同步等待 UI 线程的接口会死锁（实测）**
- **位置**：
  - `AddSeries` [:1187](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1187)；
  - `RunOnUiThread` [:1245](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1245)（曲线的全部属性和 `Remove` 都走这里）；
  - `GetValuesSnapshot` [:1025](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L1025)；
  - `UpdateAxesNow`。
- **复现**：UI 线程 `Join` 采集线程，采集线程正在调用 `AddSeries`，结果 3 秒超时。
- **典型场景**：关闭窗体时在 `FormClosing` 里停止并等待采集线程，而采集线程恰好在配置通道。
- **建议**：
  - `AddSeries` 句柄创建后也走"立即创建曲线、挂接排队到 UI 线程"的路径（句柄创建前已经这样做）；
  - 属性设置排队执行（`BeginInvoke`），读取直接返回本地字段；
  - `GetValuesSnapshot` / `UpdateAxesNow` 保留同步语义，在文档里注明"UI 线程等待本线程时不要调用"，或者提供带超时的版本。

### 中优先级

**M4 键盘操作不参与滚动策略（实测）**
- **现象**：
  - ScottPlot 默认响应方向键平移、Ctrl+方向键缩放、A 键自动缩放（README 的"鼠标操作一览"也写了这几项）；
  - 控件只观察鼠标事件，键盘改变视图后模式不变，下一个数据点就把视图拉回去；
  - Ctrl+方向键缩放后不会停止滚动（需求 3）；
  - A 键不能恢复滚动（补充 1），也不会把跟随换成堆叠（补充 2）。
- **建议**：在画布的 `KeyDown` 之后比较前后视图，按以下映射处理：
  - 方向键 → 按左键拖动规则；
  - Ctrl+方向键 → 按滚轮缩放规则；
  - A 键 → 按整体显示规则。

  如果业务不需要键盘操作，也可以直接移除这几项 ScottPlot 默认响应。

**M5 堆叠模式下整体显示后，视图不符合需求 1（实测）**
- **位置**：[ScrollingPlotControl.cs:2429](../src/ScottPlotEx.Controls/ScrollingPlotControl.cs#L2429)。模式不变的分支只复位偏移，保留 ScottPlot 自动缩放的结果（X、Y 两边各带边距）；要等下一个数据点，才回到 [0, 点数+2]、Y 上下各外扩 5%。数据暂停时会一直停在这个视图。从跟随或手动进入堆叠的路径会立即重算，所以是对的。
- **测试缺口**：现有测试 `AutoscaleItem_ResetsOffsetAndKeepsMode` 只检查了偏移和模式，没有检查视图范围。
- **建议**：模式不变的分支里调用 `ApplyAutoScroll()` + `RememberLimits()` + `RequestRender()`。是否同时把堆叠百分比恢复为 100 见 Q1。

**M6 剪贴板被占用时，"复制到剪贴板"抛出未处理异常（实测）**
- **位置**：[ChinesePlotMenu.cs:148](../src/ScottPlotEx.Controls/ChinesePlotMenu.cs#L148)。`Clipboard.SetImage` 重试约 1.1 秒后抛出 `ExternalException`。菜单点击里没有捕获，会变成 UI 线程的未处理异常，违背需求 8"不能闪退"。"保存图像"有异常处理，"复制"没有。
- **建议**：捕获 `ExternalException`，像"保存图像"一样弹出提示。

**M7 数据绝对值接近 1e308 时，每次绘制都抛出未处理异常（实测）**
- **现象**：数据为 ±1e308 或 [0, 1.7e308] 时，控件算出的 Y 范围有效，但 ScottPlot 自动缩放未设置的坐标轴时溢出，绘制过程抛出 `InvalidOperationException`。
- **触发条件**：只有接近 `double.MaxValue` 的哨兵值才会触发（±1e307 正常）。但一旦触发，之后每一帧都会失败。
- **建议**：
  - 写入时把绝对值超过约 1e300 的值当作非有限值处理（与 M1 一起设计）；
  - 画布的 `OnPaint` 捕获渲染异常并画出错误提示，不让异常冒到消息循环，作为需求 8 的兜底。

**M8 控件配置接口没有线程保护（实测）**
- **现象**：`Mode`、`XAxisUnit`、`Reset()` 等 13 个接口在后台线程直接修改 Plot 与坐标轴，和 UI 线程上正在进行的渲染并发执行。不报错，也不切换线程。
- **不一致之处**：`PlotSeries` 的属性会切换到 UI 线程，控件自身的属性不会；README 也没有说明这些接口只能在 UI 线程调用。
- **建议**：二选一。
  - 在非 UI 线程调用时立即抛出 `InvalidOperationException`，提示改到 UI 线程（推荐：改动小，问题暴露得早）；
  - 统一排队到 UI 线程执行。

**M9 内存没有上限（上一轮遗留，实测加推算）**
- **数据**：每点约 9 字节（数据 8 字节，加约 13% 的极值金字塔），列表刚翻倍扩容后瞬时约 18 字节。实测 10 路 × 1 kHz 运行 1 小时增加 336 MB；推算 24 小时约 8 GB。每秒 1 点的单曲线可以忽略。
- **建议**：提供最大点数上限或环形缓冲，或者对旧数据降采样归档。具体方案取决于 Q6 定下的运行时长和路数。

### 性能

**P1 数据列表翻倍扩容时 UI 线程卡顿（实测）**
- **数据**：单条曲线跨过 1638 万点时，一次写入耗时 35 ms；跨过 3277 万点时 76 ms；6554 万点时推算约 150 ms。多条同速率的曲线会在同一次合并写入里依次扩容。
- **建议**：
  - 改为分段存储（固定大小的块），彻底消除大块复制；需要同步修改数据源与金字塔对原始列表的访问；
  - 或者先提供 `PlotSeries.EnsureCapacity(int)`，让已知规模的应用预先分配，改动最小。

**P2 画面内容没变的重绘也完整渲染一遍（实测）**
- **位置**：[PlotCanvas.cs:174](../src/ScottPlotEx.Controls/PlotCanvas.cs#L174)。窗口被遮挡后重新露出、菜单关闭、对话框拖过曲线区域时，每个 `WM_PAINT` 都重新渲染整幅图：1600×900、8 条 × 20 万点时 9.7 ms/次。
- **建议**：
  - 在 `OnInvalidated` 里置脏标志（`Invalidate` / `Refresh` 都会触发它）；
  - 画面不脏且尺寸没变时，只把已有的 DIB 拷到窗口（`BitBlt`），预计不到 1 ms。

**P3 默认不限帧率（需要你决定）**
- **数据**：10 路 × 1 kHz 时，UI 线程 CPU 不限帧率为 37.1%，限 30 帧为 19.8%。不限帧率时，窗体上的 WinForms 定时器可能长时间不触发（上一轮实测）。
- **建议**：把 `MaxFramesPerSecond` 的默认值改为 60（或 30）。这会改变现有默认行为，需要你决定（Q7）。

---

## 7. 待确认的业务语义

| 编号 | 问题 | 现状 | 可选方案 |
|---|---|---|---|
| Q1 | 堆叠百分比调小后，"整体显示"是否恢复为 100%？ | 不恢复：实测百分比为 50 时，整体显示只显示一半（200 点时 X [101, 202]）。README 写明这是有意设计 | ① 保持现状；② 整体显示时恢复为 100% |
| Q2 | 自动滚动时，用户在 Y 方向的调整要不要保留？ | 下一个数据点就被覆盖（需求 6 只规定了 X） | ① 保持现状；② 记住 Y 偏移或缩放；③ Y 被调整过就停止滚动 |
| Q3 | 显示百分比是否需要超过 100%？ | 控件限制在 1–100（[ScrollingAxisOptions.cs:156](../src/ScottPlotEx.Controls/ScrollingAxisOptions.cs#L156)），演示程序的输入框上限却是 240 | ① 演示程序的上限改为 100；② 定义超过 100% 的含义（例如堆叠模式右侧预留空白，避免每来一个点就整体缩放一次） |
| Q4 | 缺失值、丢点、不等间隔数据怎么显示？ | 丢弃且不占位（M1） | ① 占位并画成断线；② 占位、跳过它连线；③ 提供按 (时间, 值) 写入的接口 |
| Q5 | M2 修正后，"能看到最新数据"以哪个时刻为准？ | 以松开鼠标时为准 | ① 以按下鼠标时为准（推荐）；② 放宽容差 |
| Q6 | 性能与容量目标 | 没有约定 | 需要给出：路数、采样率、连续运行时长、内存上限、单帧耗时上限、可接受的卡顿 |
| Q7 | `MaxFramesPerSecond` 的默认值 | 0（不限） | 60 / 30 / 保持 0 |

### 7.1 定稿结果（2026-11-07）

| 编号 | 决定 | 实施要点 |
|---|---|---|
| Q1 | 整体显示时，堆叠百分比**一律**恢复为 100%（无论原来小于还是大于 100） | 中键单击、菜单"自动缩放"、A 键走同一个处理：①停止滚动时恢复滚动；②跟随模式换成堆叠；③百分比设为 100，偏移清零，解除 Y 锁定；④立即按需求 1 重算坐标轴（同时修复 M5）。`ShowAllData()` 仍然只把百分比设为 100，不切换模式 |
| Q2 | 锁定 Y | 继续滚动的操作改变了 Y 时锁定 Y：左键拖动且竖直移动达到 5 像素、中键框选、方向键 ↑↓。锁定后 X 继续滚动，Y 保持用户的范围。整体显示、重新选择模式（包括选回当前模式）、`ClearData()`、`Reset()`、`ResetYAxis()` 时解除。新增 `IsYAxisLocked`、`ResetYAxis()`；右键菜单不新增菜单项 |
| Q3 | 可以超过 100%：堆叠模式分阶段扩展；跟随模式最大 100%；重新选择堆叠模式时复位为 100% | 堆叠百分比范围改为 1–1000。≤ 100 时行为不变；> 100 时原点为 0，设置时把右端固定为"(点数 + 2) × 百分比"，数据写到右端（右侧留白不足 2 个点）时再按同一比例扩展。时间单位同理。演示程序的输入框上限随模式变化 |
| Q4 | 按推荐：缺失值占位、画成断线 | NaN、±Infinity、绝对值超过 1e300 的值都按缺失值占一个位置；绘制时在缺失处断开（缺失段短于一个像素列时不断开）；Y 轴自动范围、最近点查询跳过缺失值。`DataCount` 包含缺失点，`LatestValue` 可能是 NaN，`SkippedValueCount` 保留原名、含义改为缺失点数量（同时修复 M1、M7）。ScottPlot 5.1.59 自带的曲线遇到 NaN 会整条消失，因此需要控件自己实现断线绘制；不含缺失值时仍与 ScottPlot 逐像素一致 |
| Q5 | 按推荐：用按下鼠标时的最新数据判断 | 按下鼠标时记录当时的最新 X；松开时用松开时的视图判断能否看到它（修复 M2）。滚轮、键盘是瞬间操作，仍按当前最新数据判断 |
| Q6 | 暂不处理 | 将来参照 imc 曲线组件，改为"内存热数据 + 硬盘临时文件冷数据"。M9（内存上限）、P1（扩容卡顿）一并留到那时处理。改造时要满足：热数据按固定大小的块存储（不再整体扩容）；冷数据在内存里保留每块的最大值、最小值，堆叠模式显示全部历史时不读盘；放大到冷数据区间时再从硬盘加载 |
| Q7 | 默认 60 帧 | `MaxFramesPerSecond` 默认值改为 60，仍由高精度定时器控制。另加空闲保护：每帧画完后至少留出"帧耗时的 20%，最少 2 ms"再处理下一批，保证重负载下应用自己的 WinForms 定时器也能触发。设为 0 仍表示不限帧率（此时不做空闲保护） |

**本次统一修改的范围**：M1–M8、P2、P3，以及上表 Q1–Q5、Q7 的方案。M9、P1 随 Q6 延后。

## 8. 未验证的格子

| 格子 | 原因 |
|---|---|
| Alt+左键框选 | 注入的按键没有被 ScottPlot 识别为 Alt；按代码推断与中键框选规则一致 |
| 运行中拖到 DPI 不同的显示器 | 没有真实多显示器环境 |
| 触摸 / 笔输入 | 没有设备；ScottPlot 按鼠标消息处理 |
| 设计器中的实际拖放 | 只有占位模式的自动化测试，没有在 Visual Studio 设计器里实际操作 |

## 9. 建议的修改顺序与验收

**修改顺序**
1. **先定业务语义**：Q1–Q7，其中 Q4、Q5、Q6 决定 M1、M2、M9 的方案。
2. **修不依赖语义的问题**：M3、M5、M6、M7、M8、P2。改动集中、风险低，每项配一个回归测试。
3. **按语义修**：M1、M2、M4、M9、P1、P3。
4. **按本文矩阵复查**：只复查改动涉及的行，结果更新到本文。

**需要补的测试**

本轮的 ❌ 格子都没有自动化测试，修复时一并补上：
- 堆叠模式下整体显示后的视图范围；
- 键盘操作；
- 手势期间有数据到达；
- 剪贴板被占用；
- 极大值；
- NaN 对齐；
- 后台线程调用配置接口；
- UI 线程等待时调用 `AddSeries`。

## 10. 修复记录（2026-11-07）

按 7.1 节的定稿统一修改。下表"测试"一列均在 `tests/ScottPlotEx.Tests/MatrixReviewFixesTests.cs` 中。

| 项 | 修改 | 测试 |
|---|---|---|
| M1 / Q4 缺失值 | `PlotDataBuffer` 把 NaN、±Infinity、绝对值超过 1e300 的值按 NaN 占位保存，并记录连续缺失区间；极值金字塔与取值范围统计跳过 NaN；新增 `GapAwareSignal`（`Signal` 的子类），按缺失区间把可见范围分段，每段用 ScottPlot 原来的绘制方法画（不含缺失值时直接调用原方法）；缺失段短于一个像素列时不断开；数据游标跳到最近的有效点。`DataCount` 包含缺失点，`SkippedValueCount` 改为缺失点数量 | `MissingValues_KeepAlignment`、`MissingValues_RenderAsGap`（低密度 / 高密度） |
| M2 / Q5 拖动期间到达数据 | 按下鼠标时记录最新数据的 X，松开时用它判断能否看到最新数据 | `DataArrivingDuringDrag_DoesNotStopScrolling`、`DragWhileStreamingAt1kHz_KeepsScrolling` |
| M3 后台线程卡死 | `AddSeries` 在非 UI 线程上立即创建曲线、挂接排队到 UI 线程；`PlotSeries` 的外观、采样间隔、起始时间、`Remove` 改为排队执行（`PostToUiThread`），调用不等待，读取立即返回新值；起始时间在调用线程上先检查，异常照常抛给调用方。`GetValuesSnapshot`、`UpdateAxesNow` 保留同步语义，文档注明"UI 线程等待本线程时不要调用"。顺带修正：设置一项外观不再把用户直接通过 `Signal` 改过的其它样式覆盖掉 | `BackgroundSeriesConfiguration_DoesNotDeadlockWhenUiWaits`、`BackgroundStartTimeValidation_StillThrowsToCaller` |
| M4 键盘 | 画布的 `PreviewKeyDown` / `KeyDown` 之后比较视图：A 键按整体显示；跨度不变（方向键平移）按左键拖动；跨度改变（Ctrl + 方向键）按滚轮缩放。`PlotViewGesture` 新增 `KeyboardPan`、`KeyboardZoom` | `Keyboard_ZoomStops_PanKeepsScrolling`、`Keyboard_A_ResumesAndSwitchesToStacked` |
| M5 / Q1 整体显示 | 中键单击、菜单"自动缩放"、A 键统一：堆叠百分比恢复为 100%、复位偏移、解除 Y 锁定，并立即按自动滚动规则重算（模式不变时也重算） | `Autoscale_InStackedMode_ShowsRequirementViewImmediately`（4 组）、`Autoscale_InFollowMode_SwitchesToFullStackedView` |
| M6 剪贴板 | "复制到剪贴板"捕获 `ExternalException` 并提示；菜单的错误提示统一走 `ErrorReporter`（测试中替换，避免弹出模态框） | `CopyToClipboard_WhenBusy_ReportsError` |
| M7 极大值 / 渲染异常 | 绝对值超过 1e300 按缺失值处理；画布 `OnPaint` 捕获渲染异常，画出错误提示、下一帧重试，不让异常冒到消息循环 | `HugeValues_AreMissing_AndRenderSafely`、`RenderException_IsContained` |
| M8 配置接口线程 | `Mode`、`StopScrolling`、百分比与窗口属性、`XAxisUnit`、`DefaultSampleInterval`、`DefaultStartTime`、`ShowLegend`、`ShowModeMenuItems`、`ApplyAxisOptions`、`Reset`、`ResetYAxis`、`ShowHelp`、`Refresh` 在非 UI 线程调用时抛出 `InvalidOperationException`（控件已释放时不检查）；`RequestRender` 改为可在任意线程调用 | `ConfigurationApis_RequireUiThread` |
| Q2 Y 锁定 | 继续滚动的操作结束后，上下边界任一移动达到 5 像素即锁定 Y；自动滚动只滚动 X。新增 `IsYAxisLocked`、`ResetYAxis()` | `VerticalDrag_LocksYAxis_UntilAutoscale`、`HorizontalDragWithJitter_DoesNotLockY`、`ZoomRectangle_LocksY_AndUnlockTriggers` |
| Q3 堆叠超过 100% | 堆叠百分比范围改为 1–1000；超过 100 时原点为 0，右端固定为"(点数 + 2) × 百分比"，数据写满时按同一比例扩展（`ScrollingAxisCalculator.CalculateStackedRightLimit`、`TryCalculateXWindow` 新增带固定右端的重载）。演示程序的输入框上限随模式变化 | `StackedPercentAbove100_ExpandsInStages` |
| P2 静态重绘 | 画布在 `OnInvalidated` 置脏标志；不脏且尺寸不变的重绘（窗口重新露出等）直接 `BitBlt` 上一帧 | `ExposureRepaint_ReusesLastFrame` |
| P3 / Q7 默认 60 帧 | `MaxFramesPerSecond` 默认 60；定时线程在第一次跨线程写入时才创建；每帧画完后至少空闲"帧耗时的 20%，最少 2 ms"再处理下一批 | `DefaultFrameCap_And_IdleGapKeepsWinFormsTimersAlive`（每帧 40 ms 时 50 ms 定时器仍照常触发） |

**按新语义更新的原有测试（6 个）**：
- `PlotDataBufferTests.Add_KeepsMissingValuesAsPlaceholders`；
- `ScrollingPlotControlTests.NonFiniteValues_AreMissingPlaceholders`；
- `CrossThreadIngestTests.SingleThread_PreservesOrder_ClearOrdering_AndCopiesBatches`；
- `ReviewFixes20261107Tests.PlotDataBuffer_AddRangeArray_AndSelfView`（以上四个：缺失值占位）；
- `XAxisDisplayPercentTests.PercentValues_AreClamped`（堆叠上限改为 1000）；
- `StressAndRobustnessTests.AfterDispose_DataCallsAreSafe`：控件释放后从其它线程设置 `Mode` 仍不抛异常（`ThrowIfNotUiThread` 对已释放控件不检查）。

**兼容性变化**（README 已同步）：
- 缺失值占位：`DataCount` 包含缺失点，`LatestValue` 可能是 NaN，`PlotDataBuffer.Add` 总是返回 true；
- 堆叠百分比上限从 100 变为 1000，`CalculateStackedOrigin` 在百分比不小于 100 时返回 0；
- 整体显示会把堆叠百分比恢复为 100%；
- 控件配置接口不能再在非 UI 线程调用；
- `PlotSeries` 的属性在非 UI 线程上改为异步生效；
- 默认帧率上限从 0 变为 60。

**验证（2026-11-07）**：
- Release 构建：0 警告，0 错误；
- 非性能测试：276/276 通过（原有 252 个，其中 6 个按新语义更新；新增 24 个）；
- 性能基准（`Category=Performance`）：4/4 通过，`artifacts/` 下的报告已用新代码重新生成。
