namespace ScottPlotEx.Demo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 无人值守验证：--stacking 直接打开“堆叠 + 堆积”压力测试窗体（VS 里点按钮即可，无需参数）
        if (args.Any(arg => string.Equals(arg, "--stacking", StringComparison.OrdinalIgnoreCase)))
        {
            StackingStressForm stackingForm = new();
            int stackingSeconds = ReadInt(args, "--seconds", 0);
            string stackingSnapshot = ReadString(args, "--snapshot", Path.Combine("artifacts", "stacking-stress.png"));

            if (stackingSeconds > 0)
                stackingForm.RunSmokeTest(stackingSeconds, stackingSnapshot);

            Application.Run(stackingForm);
            return;
        }

        // 无人值守验证：--multiseries 打开“多曲线测试”窗体；--preset <0|1|2> 选择预设场景
        // （0 量级差别大 / 1 长短不一 / 2 十条压测），--unit <0|1|2> 选择 X 轴单位（0 点数 / 1 相对时间 / 2 绝对时间，绝对时间自动把起始时间设为当前时间），
        // --seconds <秒> 到点自动截图并退出、--snapshot <路径> 指定截图文件。
        if (args.Any(arg => string.Equals(arg, "--multiseries", StringComparison.OrdinalIgnoreCase)))
        {
            MultiSeriesTestForm multiForm = new();
            int preset = ReadInt(args, "--preset", 0);
            int unit = ReadInt(args, "--unit", 0);
            int multiSeconds = ReadInt(args, "--seconds", 0);
            string multiSnapshot = ReadString(args, "--snapshot", Path.Combine("artifacts", "multi-series-test.png"));

            if (Enum.IsDefined((MultiSeriesTestForm.Preset)preset) && preset != 0)
                multiForm.Shown += (_, _) => multiForm.ApplyPreset((MultiSeriesTestForm.Preset)preset);

            if (Enum.IsDefined((ScottPlotEx.Controls.XAxisUnit)unit) && unit != 0)
            {
                multiForm.Shown += (_, _) =>
                {
                    // 绝对时间需要明确的起始时间：命令行直接选绝对时间时自动用当前时间
                    if (unit == (int)ScottPlotEx.Controls.XAxisUnit.AbsoluteTime)
                        multiForm.UseCurrentTimeAsStartTime();

                    multiForm.SetXAxisUnit((ScottPlotEx.Controls.XAxisUnit)unit);
                };
            }

            if (multiSeconds > 0)
                multiForm.RunSmokeTest(multiSeconds, multiSnapshot);

            Application.Run(multiForm);
            return;
        }

        // 无人值守验证：--streaming 打开“10 曲线流式演示”窗体（控件多曲线 API）；
        // 可选 --seconds <秒> 到点自动截图并退出、--snapshot <路径> 指定截图文件；
        // --curves / --rate / --window 设置曲线数、每条点/秒、窗口秒数；--fps 设置帧率上限（0 = 不限，旧参数 --frame-ms 仍可用）。
        if (args.Any(arg => string.Equals(arg, "--streaming", StringComparison.OrdinalIgnoreCase)))
        {
            StreamingDemoForm form = new();
            int seconds = ReadInt(args, "--seconds", 0);
            string snapshot = ReadString(args, "--snapshot", Path.Combine("artifacts", "streaming-demo.png"));

            int curves = ReadInt(args, "--curves", 0);
            int rate = ReadInt(args, "--rate", 0);
            int window = ReadInt(args, "--window", 0);
            int frameMs = ReadInt(args, "--frame-ms", 0);
            int fps = ReadInt(args, "--fps", frameMs > 0 ? (int)Math.Round(1000.0 / frameMs) : -1);

            if (curves > 0 || rate > 0 || window > 0 || fps >= 0)
                form.Configure(curves > 0 ? curves : 10, rate > 0 ? rate : 1000, window > 0 ? window : 60, fps);

            if (seconds > 0)
                form.RunSmokeTest(seconds, snapshot);

            Application.Run(form);
            return;
        }

        Application.Run(new MainForm());
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        int index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value) ? value : fallback;
    }

    private static string ReadString(string[] args, string name, string fallback)
    {
        int index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
}





