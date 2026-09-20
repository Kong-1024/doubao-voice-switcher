using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DoubaoVoiceSwitcher;

internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private bool _rightAltPhysicalDown;

    internal enum HookMode
    {
        Ready,
        Busy,
        Listening
    }

    internal volatile HookMode Mode = HookMode.Ready;

    internal event Action? RightAltPressed;
    internal event Action? EscapePressed;

    internal KeyboardHook()
    {
        _callback = HookCallback;
    }

    internal void Install()
    {
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        IntPtr moduleHandle = NativeMethods.GetModuleHandle(module?.ModuleName);
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _callback,
            moduleHandle,
            0);

        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册全局键盘监听。");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        NativeMethods.KbdLlHookStruct data =
            Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);

        bool injected = (data.Flags & NativeMethods.LlkhfInjected) != 0 ||
                        (data.Flags & NativeMethods.LlkhfLowerIlInjected) != 0;
        int message = unchecked((int)wParam.ToInt64());
        bool isDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        bool isUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;

        if (injected)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (data.VkCode == NativeMethods.VkRMenu)
        {
            if (isDown && !_rightAltPhysicalDown)
            {
                _rightAltPhysicalDown = true;
                Logger.Log($"Hook: Physical RightAlt Down (Mode={Mode})");
                RightAltPressed?.Invoke();
            }
            else if (isUp)
            {
                _rightAltPhysicalDown = false;
                Logger.Log($"Hook: Physical RightAlt Up (Mode={Mode})");
            }

            // 右 Alt 专用于本工具。Down/Up 全部拦截，避免 Alt 菜单或豆包原生快捷键抢占。
            return new IntPtr(1);
        }

        if (isDown && data.VkCode == NativeMethods.VkEscape)
        {
            EscapePressed?.Invoke();
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    internal static void SendCtrlShift()
    {
        Logger.Log("SendCtrlShift: Sending Ctrl+Shift...");

        NativeMethods.Input[] ctrlDown = [CreateKeyInput(0x11, 0x1D, 0)];
        NativeMethods.Input[] shiftDown = [CreateKeyInput(0x10, 0x2A, 0)];
        NativeMethods.Input[] shiftUp = [CreateKeyInput(0x10, 0x2A, NativeMethods.KeyeventfKeyup)];
        NativeMethods.Input[] ctrlUp = [CreateKeyInput(0x11, 0x1D, NativeMethods.KeyeventfKeyup)];

        NativeMethods.SendInput(1, ctrlDown, Marshal.SizeOf<NativeMethods.Input>());
        Thread.Sleep(20);
        NativeMethods.SendInput(1, shiftDown, Marshal.SizeOf<NativeMethods.Input>());
        Thread.Sleep(20);
        NativeMethods.SendInput(1, shiftUp, Marshal.SizeOf<NativeMethods.Input>());
        Thread.Sleep(20);
        NativeMethods.SendInput(1, ctrlUp, Marshal.SizeOf<NativeMethods.Input>());

        Logger.Log("SendCtrlShift: Completed.");
    }

    internal static void SendWinSpace()
    {
        Logger.Log("SendWinSpace: Sending Win+Space...");

        NativeMethods.Input[] winDown = [CreateKeyInput(0x5B, 0x5B, NativeMethods.KeyeventfExtendedkey)];
        NativeMethods.Input[] spaceDown = [CreateKeyInput(0x20, 0x39, 0)];
        NativeMethods.Input[] spaceUp = [CreateKeyInput(0x20, 0x39, NativeMethods.KeyeventfKeyup)];
        NativeMethods.Input[] winUp = [CreateKeyInput(0x5B, 0x5B, NativeMethods.KeyeventfExtendedkey | NativeMethods.KeyeventfKeyup)];

        NativeMethods.SendInput(1, winDown, Marshal.SizeOf<NativeMethods.Input>());
        Thread.Sleep(20);
        NativeMethods.SendInput(1, spaceDown, Marshal.SizeOf<NativeMethods.Input>());
        Thread.Sleep(20);
        NativeMethods.SendInput(1, spaceUp, Marshal.SizeOf<NativeMethods.Input>());
        Thread.Sleep(20);
        NativeMethods.SendInput(1, winUp, Marshal.SizeOf<NativeMethods.Input>());

        Logger.Log("SendWinSpace: Completed.");
    }

    private static NativeMethods.Input CreateKeyInput(ushort vk, ushort scan, uint flags) =>
        new()
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeybdInput
                {
                    VirtualKey = vk,
                    Scan = scan,
                    Flags = flags
                }
            }
        };

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
