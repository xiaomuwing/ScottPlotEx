using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 数据接入的分配回归：逐点写入（自动滚动 + 重绘请求）在稳定状态下不应产生托管分配。
/// 旧实现每个点分配约 192 字节（AxisManager.Bottom/Left 的 LINQ 查找 80 B、SetLimits 80 B、Invalidate 32 B），
/// 高频写入时会产生大量 GC；这里用分配量守住优化结果。
/// </summary>
public class IngestAllocationTests
{
    [Fact(DisplayName = "数据接入：逐点 AddValue 在稳定状态下不产生托管分配")]
    public void AddValue_SteadyState_DoesNotAllocate()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValues(Enumerable.Range(0, 50_000).Select(i => Math.Sin(i / 10.0)));

            for (int i = 0; i < 2_000; i++) // 预热（JIT、List 扩容）
                plot.AddValue(i % 100);

            const int Points = 20_000;
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < Points; i++)
                plot.AddValue(i % 100);

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(
                allocated < Points * 8,
                $"逐点写入产生了 {allocated / (double)Points:F2} B/点的托管分配（期望 < 8 B/点，稳定状态应为 0）。");
        });

        host.AssertNoUnhandledExceptions();
    }
}
