using System.Diagnostics;
using System.Threading;

namespace DoubaoVoiceSwitcher;

internal static class Program
{
    private const string MutexName = @"Local\DoubaoVoiceSwitcher-8EB7D860-7423-46FD-BCF2-B19B365B49C4";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "豆包语音切换器已经在运行，请查看任务栏通知区域。",
                "豆包语音切换器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            AppConfig config = AppConfig.Load();

            if (args.Contains("--validate", StringComparer.OrdinalIgnoreCase))
            {
                new TsfProfileSwitcher().ValidateProfiles();
                WriteTestResult("VALIDATION=PASS\r\n微信输入法和豆包输入法的 TSF 配置均可用。\r\n");
                return;
            }

            if (args.Contains("--switch-test", StringComparer.OrdinalIgnoreCase))
            {
                RunSwitchTest();
                return;
            }

            if (args.Contains("--rpc-probe", StringComparer.OrdinalIgnoreCase))
            {
                RunRpcProbe();
                return;
            }

            Application.Run(new SwitcherContext(config));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "豆包语音切换器无法启动",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void RunSwitchTest()
    {
        TsfProfileSwitcher profiles = new();
        List<string> logs = [];

        try
        {
            profiles.ValidateProfiles();
            logs.Add($"Initial: Doubao={profiles.IsDoubaoActive()}, WeType={profiles.IsWeTypeActive()}");

            for (int round = 1; round <= 3; round++)
            {
                // 切到豆包
                KeyboardHook.SendCtrlShift();
                Thread.Sleep(400);
                logs.Add($"Round {round} after Ctrl+Shift: Doubao={profiles.IsDoubaoActive()}, WeType={profiles.IsWeTypeActive()}");

                // 切回微信
                KeyboardHook.SendWinSpace();
                Thread.Sleep(400);
                logs.Add($"Round {round} after Win+Space: Doubao={profiles.IsDoubaoActive()}, WeType={profiles.IsWeTypeActive()}");
            }
        }
        catch (Exception ex)
        {
            logs.Add($"ERROR: {ex}");
        }

        string result = string.Join(Environment.NewLine, logs);
        Console.WriteLine(result);
        WriteTestResult(result);
    }

    private static void RunRpcProbe()
    {
        TsfProfileSwitcher profiles = new();
        List<string> logs = [];
        bool voiceRequested = false;

        try
        {
            profiles.ValidateProfiles();
            logs.Add($"Initial: Doubao={profiles.IsDoubaoActive()}, WeType={profiles.IsWeTypeActive()}");

            if (!profiles.IsDoubaoActive())
            {
                KeyboardHook.SendWinSpace();
            }

            bool doubaoActive = WaitUntil(profiles.IsDoubaoActive, TimeSpan.FromSeconds(3));
            logs.Add($"After switch: Doubao={doubaoActive}, WeType={profiles.IsWeTypeActive()}");
            if (!doubaoActive)
            {
                throw new InvalidOperationException("未能在 3 秒内切换到豆包输入法。");
            }

            using DoubaoRpcClient rpc = new();
            int beginResult = rpc.BeginVoiceCapture();
            logs.Add($"BeginVoiceCapture: Result={beginResult}");
            Thread.Sleep(120);
            int showResult = rpc.ShowVoice();
            voiceRequested = true;
            bool voiceVisible = WaitUntil(VoiceUiProbe.IsVoiceVisible, TimeSpan.FromSeconds(3));
            logs.Add($"ShowVoice: Result={showResult}, Visible={voiceVisible}");

            Thread.Sleep(2000);

            bool voiceStillVisible = VoiceUiProbe.IsVoiceVisible();
            logs.Add($"After 2 seconds: Visible={voiceStillVisible}");

            int cancelResult = rpc.StopVoice();
            bool voiceHidden = WaitUntil(() => !VoiceUiProbe.IsVoiceVisible(), TimeSpan.FromSeconds(3));
            logs.Add($"StopVoice: Result={cancelResult}, Hidden={voiceHidden}");

            if (beginResult != 0 || !voiceVisible || !voiceStillVisible || !voiceHidden)
            {
                throw new InvalidOperationException("豆包语音界面没有按预期显示或关闭。");
            }
        }
        catch (Exception ex)
        {
            logs.Add($"ERROR: {ex}");
        }
        finally
        {
            try
            {
                if (voiceRequested && VoiceUiProbe.IsVoiceVisible())
                {
                    using DoubaoRpcClient cleanupRpc = new();
                    _ = cleanupRpc.CancelVoice();
                }

                if (!profiles.IsWeTypeActive())
                {
                    KeyboardHook.SendWinSpace();
                }

                bool weTypeActive = WaitUntil(profiles.IsWeTypeActive, TimeSpan.FromSeconds(3));
                logs.Add($"Restored: Doubao={profiles.IsDoubaoActive()}, WeType={weTypeActive}");
            }
            catch (Exception cleanupEx)
            {
                logs.Add($"CLEANUP ERROR: {cleanupEx}");
            }
        }

        string result = string.Join(Environment.NewLine, logs);
        Console.WriteLine(result);
        WriteTestResult(result);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private static void WriteTestResult(string result)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-result.txt"), result);
    }
}
