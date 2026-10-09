using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Contexo.Core.Diagnostics;

[SupportedOSPlatform("windows")]
internal static class GpuRegistry
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Reads DriverDesc of every display adapter ("000*" sub keys). Returns an empty list when nothing can be read.</summary>
    public static List<string> ReadDriverDescriptions()
    {
        var result = new List<string>();
        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (classKey is null)
        {
            return result;
        }

        foreach (var name in classKey.GetSubKeyNames().Where(n => n.StartsWith("000", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            try
            {
                using var adapter = classKey.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string description && !string.IsNullOrWhiteSpace(description))
                {
                    result.Add(description);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Skip adapters we cannot read.
            }
        }

        return result;
    }
}
