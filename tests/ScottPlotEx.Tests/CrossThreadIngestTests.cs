using System.Diagnostics;
using ScottPlotEx.Controls;
using ScottPlotEx.Tests.TestInfrastructure;

namespace ScottPlotEx.Tests;

/// <summary>
/// 跨线程写入：采集线程的调用被合并为少量 UI 消息，数据不丢失、不滞留、顺序不乱。
/// </summary>
public class CrossThreadIngestTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "跨线程写入：多线程逐点 AddValue 全部到达，且被合并为少量 UI 消息")]
    public void ManyThreads_AllValuesArrive_AndAreCoalesced()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        host.Invoke(() => PlotTestHelper.EnsureRendered(plot));

        const int ThreadCount = 4;
        const int ValuesPerThread = 25_000;
        const int Total = ThreadCount * ValuesPerThread;

        Thread[] writers =
        [
            .. Enumerable.Range(0, ThreadCount).Select(index => new Thread(() =>
            {
                for (int i = 0; i < ValuesPerThread; i++)
                    plot.AddValue((index * 1000) + (i % 100));
            })
            {
                IsBackground = true,
            }),
        ];

        foreach (Thread writer in writers)
            writer.Start();

        foreach (Thread writer in writers)
            Assert.True(writer.Join(WaitTimeout), "采集线程未能按时结束。");

        WaitUntil(host, () => plot.DataCount == Total, () => $"数据未全部到达：{host.Invoke(() => plot.DataCount)} / {Total}");

        host.Invoke(() =>
        {
            // 旧实现每次调用都会投递一条 BeginInvoke（10 万条消息）；合并后只需少量几次
            Assert.True(plot.PendingFlushCount < Total / 10, $"合并写入执行了 {plot.PendingFlushCount} 次，未达到合并效果。");

            (double yMin, double yMax) = PlotTestHelper.GetYRange(plot);
            Assert.True(yMin < 0 && yMax > 3099, $"Y 轴未覆盖全部数据：{yMin} ~ {yMax}");
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "跨线程写入：顺序保持不变，清空请求按调用顺序生效，复用的批量缓冲区不受影响")]
    public void SingleThread_PreservesOrder_ClearOrdering_AndCopiesBatches()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        host.Invoke(() => PlotTestHelper.EnsureRendered(plot));

        Thread writer = new(() =>
        {
            for (int i = 0; i < 5_000; i++)
                plot.AddValue(-1 - i);

            // 清空之前排队的数据必须被丢弃，之后的数据必须保留
            plot.ClearData();

            double[] reused = new double[100];

            for (int batch = 0; batch < 50; batch++)
            {
                for (int i = 0; i < reused.Length; i++)
                    reused[i] = (batch * 100) + i;

                plot.AddValues(reused); // 调用返回后立即复用同一个缓冲区

                if (batch % 10 == 0)
                    plot.AddValue(double.NaN);
            }
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout), "采集线程未能按时结束。");

        WaitUntil(
            host,
            () => plot.DataCount == 5_000 && plot.LatestValue == 4_999,
            () => $"数据未按预期到达：DataCount = {host.Invoke(() => plot.DataCount)}");

        host.Invoke(() =>
        {
            Assert.Equal(Enumerable.Range(0, 5_000).Select(i => (double)i), plot.Values);
            Assert.Equal(5, plot.SkippedValueCount);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "跨线程写入：控件不可见（不会渲染）时数据也不会滞留")]
    public void HiddenControl_ValuesStillArrive()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();
        host.Invoke(() => plot.Visible = false);

        Thread writer = new(() =>
        {
            for (int i = 0; i < 3_000; i++)
            {
                plot.AddValue(i);

                if (i % 500 == 0)
                    Thread.Sleep(2);
            }
        })
        {
            IsBackground = true,
        };

        writer.Start();
        Assert.True(writer.Join(WaitTimeout), "采集线程未能按时结束。");

        WaitUntil(host, () => plot.DataCount == 3_000, () => $"不可见时数据滞留：{host.Invoke(() => plot.DataCount)} / 3000");

        // 重新显示后能正常渲染
        host.Invoke(() =>
        {
            plot.Visible = true;
            PlotTestHelper.EnsureRendered(plot);
            Assert.Equal(3_000 + 2, PlotTestHelper.GetXRange(plot).Max, 6);
        });

        host.AssertNoUnhandledExceptions();
    }

    [Fact(DisplayName = "UI 线程写入保持同步语义：调用返回时坐标轴已更新")]
    public void UiThreadWrites_RemainSynchronous()
    {
        using WinFormsTestHost host = WinFormsTestHost.Start(() => new ScrollingPlotControl());
        ScrollingPlotControl plot = host.ControlAs<ScrollingPlotControl>();

        host.Invoke(() =>
        {
            plot.AddValue(1);
            plot.AddValues([2.0, 3, 4]);
            plot.AddValues(new List<double> { 5, 6 });
            plot.AddValues(Enumerable.Range(7, 4).Select(i => (double)i));

            Assert.Equal(10, plot.DataCount);
            Assert.Equal(10 + 2, PlotTestHelper.GetXRange(plot).Max, 6);
            Assert.Equal(0, plot.PendingFlushCount);
        });

        host.AssertNoUnhandledExceptions();
    }

    private static void WaitUntil(WinFormsTestHost host, Func<bool> condition, Func<string> failureMessage)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (host.Invoke(condition))
                return;

            host.Pump(20);
        }

        Assert.Fail(failureMessage());
    }
}
