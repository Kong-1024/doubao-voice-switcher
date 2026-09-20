using System;
using System.IO;

namespace DoubaoVoiceSwitcher;

internal static class Logger
{
    private static readonly object LockObj = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "switcher.log");

    internal static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (LockObj)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch { }
    }
}
