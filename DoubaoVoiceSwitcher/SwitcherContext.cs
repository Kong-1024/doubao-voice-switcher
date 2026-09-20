using System.Diagnostics;
using Microsoft.Win32;

namespace DoubaoVoiceSwitcher;

internal sealed class SwitcherContext : ApplicationContext
{
    private enum SwitcherState
    {
        Ready,
        Starting,
        Listening,
        Stopping
    }

    private const string StartupValueName = "DoubaoVoiceSwitcher";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly AppConfig _config;
    private readonly TsfProfileSwitcher _profiles = new();
    private readonly DoubaoRpcClient _rpc;
    private readonly KeyboardHook _keyboard = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly SynchronizationContext _uiContext;

    private SwitcherState _state = SwitcherState.Ready;
    private CancellationTokenSource? _listeningGuardCts;
    private int _operationVersion;
    private bool _disposed;

    internal SwitcherContext(AppConfig config)
    {
        _config = config;
        _uiContext = SynchronizationContext.Current
            ?? new WindowsFormsSynchronizationContext();

        _profiles.ValidateProfiles();
        EnsureWeTypeActiveSync();
        _rpc = new DoubaoRpcClient();

        _statusItem = new ToolStripMenuItem("状态：就绪（单击右 Alt 开启语音）") { Enabled = false };
        ToolStripMenuItem restoreItem = new("取消语音并恢复微信输入法");
        restoreItem.Click += async (_, _) => await RestoreWeTypeNowAsync();

        _startupItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabled()
        };
        _startupItem.CheckedChanged += (_, _) => SetStartup(_startupItem.Checked);

        ToolStripMenuItem exitItem = new("退出");
        exitItem.Click += (_, _) => ExitThread();

        ContextMenuStrip menu = new();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(restoreItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "豆包语音切换器：单击右 Alt 开启/结束",
            Visible = true,
            ContextMenuStrip = menu
        };

        _keyboard.RightAltPressed += () => _uiContext.Post(async _ => await HandleRightAltAsync(), null);
        _keyboard.EscapePressed += () => _uiContext.Post(async _ => await HandleEscapeAsync(), null);
        _keyboard.Install();

        _tray.ShowBalloonTip(
            3500,
            "豆包语音切换器已就绪",
            "单击右 Alt 开启豆包语音；说完再单击右 Alt，文字上屏并自动恢复微信输入法。按 Esc 可取消。",
            ToolTipIcon.Info);
    }

    private async Task HandleRightAltAsync()
    {
        Logger.Log($"HandleRightAltAsync: State={_state}");

        if (_state == SwitcherState.Ready)
        {
            await StartVoiceAsync();
        }
        else if (_state == SwitcherState.Listening)
        {
            await StopVoiceAndRestoreAsync("右 Alt");
        }
    }

    private async Task StartVoiceAsync()
    {
        if (_state != SwitcherState.Ready)
        {
            return;
        }

        int operation = ++_operationVersion;
        _state = SwitcherState.Starting;
        _keyboard.Mode = KeyboardHook.HookMode.Busy;
        UpdateStatus("正在启动豆包语音…");

        try
        {
            bool activated = await EnsureDoubaoActiveAsync();
            if (!IsCurrentOperation(operation, SwitcherState.Starting))
            {
                return;
            }

            if (!activated)
            {
                throw new InvalidOperationException("未能切换到豆包输入法。");
            }

            int beginResult = _rpc.BeginVoiceCapture();
            if (beginResult != 0)
            {
                throw new InvalidOperationException($"豆包录音预启动失败（返回 {beginResult}）。");
            }

            // 模拟豆包官方快捷键的“按下→短按松开”时序：先启动录音，再显示免按波形条。
            await Task.Delay(120);
            int showResult = _rpc.ShowVoice();
            if (showResult != 0)
            {
                throw new InvalidOperationException($"豆包语音界面启动失败（返回 {showResult}）。");
            }

            bool visible = await WaitUntilAsync(VoiceUiProbe.IsVoiceVisible, TimeSpan.FromSeconds(3));
            if (!IsCurrentOperation(operation, SwitcherState.Starting))
            {
                return;
            }

            if (!visible)
            {
                throw new InvalidOperationException("豆包语音条未在 3 秒内出现。");
            }

            _state = SwitcherState.Listening;
            _keyboard.Mode = KeyboardHook.HookMode.Listening;
            UpdateStatus("豆包语音识别中；再按右 Alt 结束");
            PlayFeedbackSound(1000, 80);
            StartListeningGuard();
            Logger.Log("State transitioned to Listening; voice UI verified visible.");
        }
        catch (Exception ex)
        {
            Logger.Log($"StartVoiceAsync ERROR: {ex}");
            TryCancelVoice();
            await RestoreAfterFailureAsync("语音启动失败", ex.Message);
        }
    }

    private async Task StopVoiceAndRestoreAsync(string reason)
    {
        if (_state != SwitcherState.Listening)
        {
            return;
        }

        ++_operationVersion;
        _state = SwitcherState.Stopping;
        _keyboard.Mode = KeyboardHook.HookMode.Busy;
        CancelListeningGuard();
        UpdateStatus("正在结束语音并恢复微信…");
        Logger.Log($"StopVoiceAndRestoreAsync: reason={reason}");

        try
        {
            int result = _rpc.StopVoice();
            if (result != 0)
            {
                throw new InvalidOperationException($"豆包语音结束消息失败（返回 {result}）。");
            }

            bool hidden = await WaitUntilAsync(() => !VoiceUiProbe.IsVoiceVisible(), TimeSpan.FromSeconds(3));
            if (!hidden)
            {
                TryCancelVoice();
                throw new InvalidOperationException("豆包语音条未在 3 秒内关闭。");
            }

            // 语音条关闭不等于文字已完成上屏，额外等待豆包提交文本。
            await Task.Delay(_config.CommitDelayMs);
            bool restored = await EnsureWeTypeActiveAsync();
            if (!restored)
            {
                throw new InvalidOperationException("语音已结束，但未能恢复微信输入法。");
            }

            PlayFeedbackSound(650, 80);
            ResetToReady();
            Logger.Log("Voice stopped and WeType restored successfully.");
        }
        catch (Exception ex)
        {
            Logger.Log($"StopVoiceAndRestoreAsync ERROR: {ex}");
            await RestoreAfterFailureAsync("语音结束异常", ex.Message);
        }
    }

    private async Task HandleEscapeAsync()
    {
        if (!_config.CancelOnEsc || _state == SwitcherState.Ready)
        {
            return;
        }

        ++_operationVersion;
        _state = SwitcherState.Stopping;
        _keyboard.Mode = KeyboardHook.HookMode.Busy;
        CancelListeningGuard();
        UpdateStatus("正在取消语音并恢复微信…");
        Logger.Log("HandleEscapeAsync: cancel requested.");

        TryCancelVoice();
        await EnsureWeTypeActiveAsync();
        PlayFeedbackSound(500, 90);
        ResetToReady();
    }

    private void StartListeningGuard()
    {
        CancelListeningGuard();
        _listeningGuardCts = new CancellationTokenSource();
        CancellationToken token = _listeningGuardCts.Token;

        _ = Task.Run(async () =>
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int hiddenSamples = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(120, token);

                    if (VoiceUiProbe.IsVoiceVisible())
                    {
                        hiddenSamples = 0;
                    }
                    else
                    {
                        hiddenSamples++;
                        if (hiddenSamples >= 3)
                        {
                            _uiContext.Post(async _ => await RestoreAfterExternalStopAsync(), null);
                            return;
                        }
                    }

                    if (elapsed.Elapsed >= TimeSpan.FromSeconds(_config.MaxListeningSeconds))
                    {
                        _uiContext.Post(async _ => await StopVoiceAndRestoreAsync("超时保护"), null);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Log($"Listening guard ERROR: {ex}");
            }
        }, token);
    }

    private async Task RestoreAfterExternalStopAsync()
    {
        if (_state != SwitcherState.Listening)
        {
            return;
        }

        ++_operationVersion;
        _state = SwitcherState.Stopping;
        _keyboard.Mode = KeyboardHook.HookMode.Busy;
        CancelListeningGuard();
        UpdateStatus("语音已结束，正在恢复微信…");
        Logger.Log("Voice UI disappeared outside the explicit RightAlt stop path.");

        await Task.Delay(_config.CommitDelayMs);
        await EnsureWeTypeActiveAsync();
        ResetToReady();
    }

    private async Task RestoreWeTypeNowAsync()
    {
        ++_operationVersion;
        _state = SwitcherState.Stopping;
        _keyboard.Mode = KeyboardHook.HookMode.Busy;
        CancelListeningGuard();
        TryCancelVoice();
        await EnsureWeTypeActiveAsync();
        ResetToReady();
        _tray.ShowBalloonTip(2000, "已恢复微信输入法", "豆包语音已取消，当前输入法为微信输入法。", ToolTipIcon.Info);
    }

    private async Task RestoreAfterFailureAsync(string title, string message)
    {
        ++_operationVersion;
        CancelListeningGuard();
        _state = SwitcherState.Stopping;
        _keyboard.Mode = KeyboardHook.HookMode.Busy;
        await EnsureWeTypeActiveAsync();
        ResetToReady();
        _tray.ShowBalloonTip(4000, title, $"{message}\r\n已恢复微信输入法。", ToolTipIcon.Warning);
    }

    private async Task<bool> EnsureDoubaoActiveAsync()
    {
        if (_profiles.IsDoubaoActive())
        {
            return true;
        }

        Logger.Log("EnsureDoubaoActiveAsync: trying TSF activation.");
        _profiles.ActivateDoubao();
        if (await WaitUntilAsync(_profiles.IsDoubaoActive, TimeSpan.FromMilliseconds(_config.ActivationDelayMs)))
        {
            return true;
        }

        Logger.Log("EnsureDoubaoActiveAsync: TSF activation did not reach Doubao; trying Win+Space.");
        KeyboardHook.SendWinSpace();
        return await WaitUntilAsync(_profiles.IsDoubaoActive, TimeSpan.FromMilliseconds(_config.ActivationDelayMs));
    }

    private async Task<bool> EnsureWeTypeActiveAsync()
    {
        if (_profiles.IsWeTypeActive())
        {
            return true;
        }

        Logger.Log("EnsureWeTypeActiveAsync: trying TSF activation.");
        _profiles.ActivateWeType();
        if (await WaitUntilAsync(_profiles.IsWeTypeActive, TimeSpan.FromMilliseconds(_config.ActivationDelayMs)))
        {
            return true;
        }

        Logger.Log("EnsureWeTypeActiveAsync: TSF activation did not reach WeType; trying Win+Space.");
        KeyboardHook.SendWinSpace();
        return await WaitUntilAsync(_profiles.IsWeTypeActive, TimeSpan.FromMilliseconds(_config.ActivationDelayMs));
    }

    private void EnsureWeTypeActiveSync()
    {
        if (_profiles.IsWeTypeActive())
        {
            return;
        }

        _profiles.ActivateWeType();
        if (WaitUntilSync(_profiles.IsWeTypeActive, TimeSpan.FromMilliseconds(_config.ActivationDelayMs)))
        {
            return;
        }

        KeyboardHook.SendWinSpace();
        if (!WaitUntilSync(_profiles.IsWeTypeActive, TimeSpan.FromMilliseconds(_config.ActivationDelayMs)))
        {
            throw new InvalidOperationException("启动时无法恢复微信输入法。");
        }
    }

    private void TryCancelVoice()
    {
        try
        {
            if (VoiceUiProbe.IsVoiceVisible())
            {
                _ = _rpc.CancelVoice();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"TryCancelVoice ERROR: {ex}");
        }
    }

    private bool IsCurrentOperation(int operation, SwitcherState expectedState) =>
        operation == _operationVersion && _state == expectedState;

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static bool WaitUntilSync(Func<bool> condition, TimeSpan timeout)
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

    private void CancelListeningGuard()
    {
        try
        {
            _listeningGuardCts?.Cancel();
            _listeningGuardCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _listeningGuardCts = null;
        }
    }

    private void ResetToReady()
    {
        CancelListeningGuard();
        _state = SwitcherState.Ready;
        _keyboard.Mode = KeyboardHook.HookMode.Ready;
        UpdateStatus("就绪（单击右 Alt 开启语音）");
    }

    private void PlayFeedbackSound(int frequency, int durationMs)
    {
        if (!_config.PlaySoundFeedback)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Console.Beep(frequency, durationMs);
            }
            catch
            {
            }
        });
    }

    private void UpdateStatus(string status)
    {
        _statusItem.Text = $"状态：{status}";
        string trayText = $"豆包语音切换器：{status}";
        _tray.Text = trayText.Length <= 63 ? trayText : trayText[..63];
    }

    private static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
        return key?.GetValue(StartupValueName) is string value &&
               string.Equals(value, Quote(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartup(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath);
        if (enabled)
        {
            key.SetValue(StartupValueName, Quote(Application.ExecutablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    protected override void ExitThreadCore()
    {
        if (!_disposed)
        {
            _disposed = true;
            ++_operationVersion;
            CancelListeningGuard();
            TryCancelVoice();
            EnsureWeTypeActiveSync();
            _keyboard.Dispose();
            _rpc.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.ExitThreadCore();
    }
}
