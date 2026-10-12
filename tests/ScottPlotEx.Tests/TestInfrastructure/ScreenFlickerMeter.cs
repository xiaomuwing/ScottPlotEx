using System.Drawing;
using System.Windows.Forms;
using ScottPlotEx.Controls;

namespace ScottPlotEx.Tests.TestInfrastructure;

/// <summary>
/// 通过高速屏幕采样测量“背景擦除闪帧”比例：
/// 快速刷新期间按固定间隔截取窗口客户区，统计有多少帧出现了大面积背景色
/// （即“内容被擦除、尚未画回来”的中间状态）。所有采样只在内存中分析，不落盘。
/// </summary>
internal static class ScreenFlickerMeter
{
    internal sealed record Result(int Frames, int FlashedFrames, double MaxBackgroundFraction, double AverageBackgroundFraction)
    {
        public double FlashedFrameRatio => Frames == 0 ? 0 : (double)FlashedFrames / Frames;
    }

    /// <summary>背景色容差（SystemColors.Control 附近的像素视为“背景”）。</summary>
    private const int ColorTolerance = 4;

    /// <summary>单帧背景占比超过该阈值即认为出现了“擦除闪帧”。</summary>
    private const double FlashThreshold = 0.60;

    public static Result Measure(
        WinFormsTestHost host,
        Form window,
        ScrollingPlotControl plot,
        Action? beforeEachFrame,
        int durationMs = 1500,
        int sampleIntervalMs = 5)
    {
        Rectangle rect = host.Invoke(() =>
        {
            window.Opacity = 1.0; // 采样期间窗口必须真实可见
            window.TopMost = true;
            window.Refresh();
            return window.RectangleToScreen(window.ClientRectangle);
        });

        host.Pump(400);

        List<double> fractions = [];
        using CancellationTokenSource cancellation = new();

        Thread sampler = new(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using Bitmap bitmap = new(rect.Width, rect.Height);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);

                    fractions.Add(BackgroundFraction(bitmap));
                }
                catch (Exception)
                {
                    // 采样失败忽略（窗口可能正在重绘）
                }

                Thread.Sleep(sampleIntervalMs);
            }
        })
        {
            IsBackground = true,
            Name = "闪烁采样线程",
        };

        sampler.Start();

        host.Invoke(() =>
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int index = 0;

            while (stopwatch.ElapsedMilliseconds < durationMs)
            {
                plot.AddValue(Math.Sin(index / 5.0) * 10);
                beforeEachFrame?.Invoke();
                Application.DoEvents();
                index++;
            }
        });

        cancellation.Cancel();
        sampler.Join(3000);

        host.Invoke(() =>
        {
            window.TopMost = false;
            window.Opacity = 0.01;
        });

        List<double> samples = [.. fractions];
        int flashed = samples.Count(x => x >= FlashThreshold);

        return new Result(
            samples.Count,
            flashed,
            samples.Count == 0 ? 0 : samples.Max(),
            samples.Count == 0 ? 0 : samples.Average());
    }

    /// <summary>统计与 SystemColors.Control 接近的像素占比（即“尚未画上内容”的背景区域）。</summary>
    private static double BackgroundFraction(Bitmap bitmap)
    {
        Color background = SystemColors.Control;
        int matches = 0;
        int total = 0;

        // 隔行隔列采样即可，兼顾速度与准确性
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                Color pixel = bitmap.GetPixel(x, y);
                total++;

                if (Math.Abs(pixel.R - background.R) <= ColorTolerance
                    && Math.Abs(pixel.G - background.G) <= ColorTolerance
                    && Math.Abs(pixel.B - background.B) <= ColorTolerance)
                {
                    matches++;
                }
            }
        }

        return total == 0 ? 0 : (double)matches / total;
    }
}
