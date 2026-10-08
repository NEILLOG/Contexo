using System.Reflection;
using System.Text.RegularExpressions;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Common;

/// <summary>Reads version information that MinVer stamped into <see cref="AssemblyInformationalVersionAttribute"/>.</summary>
public static partial class AppVersion
{
    // "1.4.2+37.g3f2a9c1", "1.4.2-alpha.0.37+3f2a9c1abc", "1.4.2": version core, optional pre-release, optional +build metadata.
    [GeneratedRegex(@"^v?(?<core>\d+\.\d+\.\d+(?:\.\d+)?)(?<pre>-[0-9A-Za-z.\-]+)?(?:\+(?<meta>.+))?$")]
    private static partial Regex VersionPattern { get; }

    [GeneratedRegex(@"(?:^|\.)g?(?<sha>[0-9a-fA-F]{7,40})$")]
    private static partial Regex ShaPattern { get; }

    /// <summary>Version of the running application (the entry assembly, or Contexo.Core when there is none, e.g. under a test host).</summary>
    public static AppVersionInfo Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            if (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() is null)
            {
                assembly = typeof(AppVersion).Assembly;
            }

            return FromAssembly(assembly);
        }
    }

    public static AppVersionInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var buildDate = TryGetBuildDate(assembly);
        return informational is null
            ? new AppVersionInfo(assembly.GetName().Version?.ToString(3) ?? "0.0.0", assembly.GetName().Version?.ToString() ?? "0.0.0", null, buildDate)
            : Parse(informational, buildDate);
    }

    /// <summary>Parses an informational version. Unrecognised strings are returned unchanged as <see cref="AppVersionInfo.Version"/>.</summary>
    public static AppVersionInfo Parse(string informationalVersion, DateTimeOffset? buildDate = null)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);

        var match = VersionPattern.Match(informationalVersion.Trim());
        if (!match.Success)
        {
            return new AppVersionInfo(informationalVersion, informationalVersion, null, buildDate);
        }

        var version = match.Groups["core"].Value + match.Groups["pre"].Value;
        string? sha = null;
        if (match.Groups["meta"].Success)
        {
            var shaMatch = ShaPattern.Match(match.Groups["meta"].Value);
            if (shaMatch.Success)
            {
                sha = shaMatch.Groups["sha"].Value.ToLowerInvariant();
            }
        }

        return new AppVersionInfo(version, informationalVersion, sha, buildDate);
    }

    private static DateTimeOffset? TryGetBuildDate(Assembly assembly)
    {
        try
        {
            return string.IsNullOrEmpty(assembly.Location) ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(assembly.Location), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
