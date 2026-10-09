using System.Runtime.Versioning;
using Contexo.App.Services;
using Microsoft.Win32;

namespace Contexo.Desktop.Platform.Windows;

/// <summary>The one registry value the start-up entry lives in. Abstracted so the logic can be tested without touching the real registry.</summary>
internal interface IRunKeyStore
{
    /// <summary>Returns the value as text, or null when it does not exist.</summary>
    string? Read(string name);

    void Write(string name, string value);

    void Delete(string name);
}

/// <summary><c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. Only used on Windows.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryRunKeyStore : IRunKeyStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Write(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Starts Contexo when the user signs in: a value named <c>Contexo</c> in the per-user Run key with the content
/// <c>"{full path of Contexo.exe}" --minimized</c>. <see cref="IsEnabled"/> is true only when the stored path is the
/// running program, so a moved or updated program shows as "off" and the settings sync rewrites the entry.
/// </summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    internal const string ValueName = "Contexo";
    internal const string MinimizedArgument = "--minimized";

    private readonly IRunKeyStore _store;
    private readonly Func<string?> _executablePath;

    /// <summary>Used by dependency injection: the real registry and the running program.</summary>
    public WindowsStartupRegistration()
        : this(CreateRealStore(), () => Environment.ProcessPath)
    {
    }

    internal WindowsStartupRegistration(IRunKeyStore store, Func<string?> executablePath)
    {
        _store = store;
        _executablePath = executablePath;
    }

    public bool IsEnabled
    {
        get
        {
            var path = _executablePath();
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string? stored;
            try
            {
                stored = _store.Read(ValueName);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }

            var storedPath = ExtractExecutablePath(stored);
            return storedPath is not null && PathsEqual(storedPath, path);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var path = _executablePath();
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("The path of the running program is unknown.");
            }

            _store.Write(ValueName, BuildCommand(path));
        }
        else
        {
            _store.Delete(ValueName);
        }
    }

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\" {MinimizedArgument}";

    /// <summary>Reads the program path from a Run value such as <c>"C:\Apps\Contexo.exe" --minimized</c>.</summary>
    internal static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IRunKeyStore CreateRealStore() =>
        OperatingSystem.IsWindows() ? new RegistryRunKeyStore() : new InertRunKeyStore();

    /// <summary>Stand-in on other systems (this class is only used on Windows): nothing is stored.</summary>
    private sealed class InertRunKeyStore : IRunKeyStore
    {
        public string? Read(string name) => null;

        public void Write(string name, string value)
        {
        }

        public void Delete(string name)
        {
        }
    }
}
