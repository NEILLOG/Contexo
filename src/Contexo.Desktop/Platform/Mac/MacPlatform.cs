using System.Diagnostics;
using System.Runtime.InteropServices;
using Contexo.App.Services;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Desktop.Platform.Mac;

/// <summary>
/// Idle time from CoreGraphics. When the call is unavailable the user is reported as idle, so work is never held back.
/// Also used on other systems (Linux CI) where the call fails.
/// </summary>
public sealed class MacUserActivityMonitor : IUserActivityMonitor
{
    private const int CombinedSessionState = 0;
    private const uint AnyInputEventType = uint.MaxValue;

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int sourceStateId, uint eventType);

    public TimeSpan IdleTime
    {
        get
        {
            if (!OperatingSystem.IsMacOS())
            {
                return TimeSpan.MaxValue;
            }

            try
            {
                var seconds = CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEventType);
                return seconds is >= 0 and < 1e9 ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return TimeSpan.MaxValue;
            }
        }
    }
}

/// <summary>Opens files and folders with <c>open</c>. Also used on other systems where nothing better exists.</summary>
public sealed class MacShellLauncher(ILogger<MacShellLauncher> logger) : IShellLauncher
{
    public void OpenFile(string path)
    {
        if (File.Exists(path))
        {
            Run(path);
        }
    }

    public void RevealInFileManager(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            Run("-R", path);
        }
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Run(path);
        }
    }

    private void Run(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("open") { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open a file or folder");
        }
    }
}
