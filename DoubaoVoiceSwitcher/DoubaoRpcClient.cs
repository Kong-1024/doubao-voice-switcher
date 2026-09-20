using System.Runtime.InteropServices;
using System.Text;

namespace DoubaoVoiceSwitcher;

internal sealed class DoubaoRpcClient : IDisposable
{
    private const string PipeName = @"\\.\pipe\ObricIme\oime-server";
    private const int VoicePressStop = 0x3F0;
    private const int VoicePressStart = 0x3EF;
    private const int VoiceShowWave = 0x3F4;
    private const int VoicePressCancel = 0x3F5;

    private readonly IntPtr _library;
    private readonly IntPtr _clientKey;
    private readonly RpcPipeSimpleMessage _send;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RpcPipeSimpleMessage(
        IntPtr clientKey,
        int message,
        int wParam,
        int lParam,
        int arg5,
        int arg6,
        int arg7,
        int arg8);

    internal DoubaoRpcClient()
    {
        string rpcPath = ResolveRpcPath();
        Logger.Log($"DoubaoRpcClient: loading {rpcPath}");

        _library = NativeLibrary.Load(rpcPath);
        IntPtr export = NativeLibrary.GetExport(_library, "RpcPipe_SimpleMessage");
        _send = Marshal.GetDelegateForFunctionPointer<RpcPipeSimpleMessage>(export);
        _clientKey = Marshal.StringToCoTaskMemUTF8(PipeName);
    }

    internal int BeginVoiceCapture() => Send(VoicePressStart, 0);

    internal int ShowVoice() => Send(VoiceShowWave, 1);

    internal int StopVoice() => Send(VoicePressStop, 0);

    internal int CancelVoice() => Send(VoicePressCancel, 0);

    private int Send(int message, int wParam)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int result = _send(_clientKey, message, wParam, 0, 0, 0, 0, 0);
        Logger.Log($"DoubaoRpcClient: message=0x{message:X}, wParam={wParam}, result={result}");
        return result;
    }

    private static string ResolveRpcPath()
    {
        string versionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "DoubaoIME",
            "versions");

        if (!Directory.Exists(versionsRoot))
        {
            throw new DirectoryNotFoundException($"未找到豆包输入法版本目录：{versionsRoot}");
        }

        string? path = Directory
            .EnumerateDirectories(versionsRoot)
            .OrderByDescending(directory => Directory.GetLastWriteTimeUtc(directory))
            .Select(directory => Path.Combine(directory, "rpc.dll"))
            .FirstOrDefault(File.Exists);

        return path ?? throw new FileNotFoundException("未找到豆包输入法 rpc.dll。", versionsRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Marshal.FreeCoTaskMem(_clientKey);
        NativeLibrary.Free(_library);
    }
}

internal static class VoiceUiProbe
{
    private static readonly HashSet<string> VoiceWindowClasses = new(StringComparer.Ordinal)
    {
        "OimeVoiceWaveWindow",
        "VoiceBarWnd",
        "VoiceBubbleWnd"
    };

    internal static bool IsVoiceVisible()
    {
        bool visible = false;

        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window))
            {
                return true;
            }

            StringBuilder className = new(256);
            _ = NativeMethods.GetClassName(window, className, className.Capacity);
            if (VoiceWindowClasses.Contains(className.ToString()))
            {
                visible = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return visible;
    }
}
