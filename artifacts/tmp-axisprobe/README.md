# 坐标轴“空视图”诊断探针（AxisProbe）

这是一个临时诊断工具，用来复现并验证 `ScrollingPlotControl` 的一个缺陷：

> 新建（或释放后重建）的控件在**首个数据点到达 / 首次绘制之前**，`Plot.Axes.GetLimits()` 返回的是
> ScottPlot 的“未设置”哨兵值 —— `Left = +∞、Right = -∞`（`IsReal = false`），
> 而不是控件文档承诺的稳定空视图（`X 0~1`、`Y -0.5~0.5`）。

该缺陷由 2026-09-23 的 8 小时时间压力测试捕获（`结构变化` 阶段反复“释放并重建控件”时命中）：

```
[ERROR] 控件不变量被破坏：坐标轴不是实数范围（IsReal = false）；坐标轴包含非有限值
        （当前 X: [-∞, ∞] Y: [-∞, ∞]，模式 Stacked，数据点 0）
```

## 用法

```powershell
dotnet run --project artifacts\tmp-axisprobe\AxisProbe.csproj -c Debug
```

探针会依次打印“构造后 / 首次渲染后 / Refresh 后 / 写入首个数据点后 / ClearData 后 /
空图中键整体显示后 / Reset 后 / 释放并重建后”各个时机的坐标轴范围，
便于确认修复前后行为差异（修复后：构造完成即为 `IsReal = true`、`X=[0,1]`、`Y=[-0.5,0.5]`）。

## 说明

- 该目录不属于解决方案，也不会被 `dotnet build ScottPlotEx.sln` 编译；用完可以直接删除。
- 对应的回归测试是 `tests\ScottPlotEx.Tests\ScrollingPlotControlTests.NewControl_HasStableEmptyViewBeforeFirstDataPoint`
  （去掉构造函数里的 `ApplyAutoScroll()` 后该测试会失败）。
