namespace Contexo.Core.Abstractions;

public sealed record DiagnosticsOptions
{
    public bool IncludeSettings { get; init; } = true;
    public bool IncludeRecentLogs { get; init; } = true;
    public int LogDays { get; init; } = 7;
    public bool IncludeFailedFileList { get; init; } = true;
    /// <summary>Off by default: full paths may contain personal names. When off, paths are reduced to "{folder display name}\…\{file name}".</summary>
    public bool IncludeFullPaths { get; init; }
}

public sealed record DiagnosticsResult(string ZipPath, long SizeBytes, IReadOnlyList<string> IncludedEntries);

public interface IDiagnosticsExporter
{
    /// <summary>Writes a zip to <paramref name="destinationDirectory"/> named "Contexo問題回報_yyyyMMdd_HHmm.zip". Secrets are always masked.</summary>
    Task<DiagnosticsResult> ExportAsync(string destinationDirectory, DiagnosticsOptions options, CancellationToken cancellationToken);
}

/// <summary>Version information read from AssemblyInformationalVersion (set by MinVer from git).</summary>
public sealed record AppVersionInfo(string Version, string InformationalVersion, string? CommitSha, DateTimeOffset? BuildDate);
