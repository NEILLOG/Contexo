using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Contexo.App.Services;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Desktop.Platform.Windows;

/// <summary>Idle time from GetLastInputInfo.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserActivityMonitor : IUserActivityMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public TimeSpan IdleTime
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            // Both values are 32-bit millisecond tick counts that wrap together; unsigned subtraction handles the wrap.
            var idleMs = unchecked((uint)Environment.TickCount - info.Time);
            return TimeSpan.FromMilliseconds(idleMs);
        }
    }
}

/// <summary>Opens files and folders with Explorer.</summary>
public sealed class WindowsShellLauncher(ILogger<WindowsShellLauncher> logger) : IShellLauncher
{
    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void RevealInFileManager(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" });
        }
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{path}\"" });
        }
    }

    private void Start(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open a file or folder");
        }
    }
}
