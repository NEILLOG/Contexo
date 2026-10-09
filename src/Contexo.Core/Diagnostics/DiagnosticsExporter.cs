using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Diagnostics;

/// <summary>
/// Packs version/system information, masked settings, recent logs and the list of unreadable files into one zip for the administrator.
/// It never includes document contents or search queries, and it never touches the user's own files.
/// </summary>
internal sealed class DiagnosticsExporter : IDiagnosticsExporter
{
    internal const int MaxFailedFiles = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly IKnowledgeStore _store;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<DiagnosticsExporter> _logger;
    private readonly TimeProvider _time;
    private readonly string _userProfile;

    /// <param name="timeProvider">Tests control the clock (file name, log age).</param>
    /// <param name="userProfilePath">Tests pass a fake user folder; the default is the real one.</param>
    public DiagnosticsExporter(
        IAppPaths paths,
        ISettingsStore settings,
        IKnowledgeStore store,
        IEmbeddingService embedding,
        ILogger<DiagnosticsExporter> logger,
        TimeProvider? timeProvider = null,
        string? userProfilePath = null)
    {
        _paths = paths;
        _settings = settings;
        _store = store;
        _embedding = embedding;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _userProfile = userProfilePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public async Task<DiagnosticsResult> ExportAsync(string destinationDirectory, DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(destinationDirectory);
        var now = _time.GetLocalNow();
        var included = new List<string>();
        var (zipPath, stream) = CreateExclusiveFile(destinationDirectory, now);
        var succeeded = false;
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

                var folders = await TryReadFoldersAsync(cancellationToken).ConfigureAwait(false);
                var masker = new PathMasker(folders.Folders, _userProfile, options.IncludeFullPaths);

                var system = await BuildSystemInfoAsync(folders, options, cancellationToken).ConfigureAwait(false);
                await AddTextEntryAsync(archive, included, "system.json", JsonSerializer.Serialize(system, JsonOptions), cancellationToken).ConfigureAwait(false);

                if (options.IncludeSettings)
                {
                    var settingsJson = SettingsMasker.MaskToJson(_settings.Current);
                    await AddTextEntryAsync(archive, included, "settings.json", settingsJson, cancellationToken).ConfigureAwait(false);
                }

                if (options.IncludeRecentLogs)
                {
                    await AddLogsAsync(archive, included, options.LogDays, masker, now, cancellationToken).ConfigureAwait(false);
                }

                if (options.IncludeFailedFileList)
                {
                    var csv = await BuildFailedFilesCsvAsync(folders, options, cancellationToken).ConfigureAwait(false);
                    await AddTextEntryAsync(archive, included, "failed-files.csv", csv, cancellationToken, bom: true).ConfigureAwait(false);
                }

                var manifestName = "manifest.json";
                var entries = new List<string>(included.Count + 1) { manifestName };
                entries.AddRange(included);
                var manifest = new Dictionary<string, object?>
                {
                    ["generatedAt"] = now,
                    ["contexoVersion"] = AppVersion.Current.Version,
                    ["contexoBuild"] = AppVersion.Current.InformationalVersion,
                    ["options"] = new Dictionary<string, object?>
                    {
                        ["includeSettings"] = options.IncludeSettings,
                        ["includeRecentLogs"] = options.IncludeRecentLogs,
                        ["logDays"] = options.IncludeRecentLogs ? options.LogDays : null,
                        ["includeFailedFileList"] = options.IncludeFailedFileList,
                        ["includeFullPaths"] = options.IncludeFullPaths,
                    },
                    ["entries"] = entries,
                };
                await AddTextEntryAsync(archive, [], manifestName, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
                included.Insert(0, manifestName);
            }

            succeeded = true;
            var size = new FileInfo(zipPath).Length;
            _logger.LogInformation("Diagnostics exported: {FileName}, {Size} bytes, {Entries} entries", Path.GetFileName(zipPath), size, included.Count);
            return new DiagnosticsResult(zipPath, size, included);
        }
        finally
        {
            if (!succeeded)
            {
                TryDelete(zipPath);
            }
        }
    }

    // ---- zip file ---------------------------------------------------------------------------------------------

    /// <summary>Creates the zip with FileMode.CreateNew so that two exports in the same minute never overwrite each other.</summary>
    private static (string Path, FileStream Stream) CreateExclusiveFile(string directory, DateTimeOffset now)
    {
        var baseName = $"Contexo問題回報_{now:yyyyMMdd_HHmm}";
        for (var attempt = 1; attempt < 1000; attempt++)
        {
            var name = attempt == 1 ? baseName + ".zip" : $"{baseName}_{attempt}.zip";
            var path = Path.Combine(directory, name);
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
                return (path, stream);
            }
            catch (IOException) when (File.Exists(path))
            {
                // The name is taken; try the next suffix.
            }
        }

        throw new IOException("Could not find a free file name for the diagnostics zip.");
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the incomplete diagnostics zip");
        }
    }

    private static async Task AddTextEntryAsync(ZipArchive archive, List<string> included, string name, string text, CancellationToken cancellationToken, bool bom = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        var bytes = new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        included.Add(name);
    }

    // ---- system.json ------------------------------------------------------------------------------------------

    private sealed record FolderData(IReadOnlyList<WatchedFolder> Folders, IReadOnlyDictionary<long, int> DocumentCounts, string? Error);

    private async Task<FolderData> TryReadFoldersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var folders = await _store.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
            var counts = new Dictionary<long, int>();
            foreach (var folder in folders)
            {
                counts[folder.Id] = (await _store.GetDocumentsAsync(folder.Id, cancellationToken).ConfigureAwait(false)).Count;
            }

            return new FolderData(folders, counts, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Typical on the start-up error screen: the database could not be opened. Only the exception type is recorded.
            _logger.LogWarning(ex, "Could not read folders for the diagnostics export");
            return new FolderData([], new Dictionary<long, int>(), ex.GetType().Name);
        }
    }

    private async Task<Dictionary<string, object?>> BuildSystemInfoAsync(FolderData folderData, DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        var version = AppVersion.Current;
        var system = new Dictionary<string, object?>
        {
            ["contexoVersion"] = version.Version,
            ["contexoBuild"] = version.InformationalVersion,
            ["buildDate"] = version.BuildDate,
            ["os"] = RuntimeInformation.OSDescription,
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount,
            ["totalAvailableMemoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        };

        var gpus = ReadGpuNames();
        if (gpus.Count > 0)
        {
            system["gpu"] = gpus;
        }

        try
        {
            system["embeddingModel"] = new Dictionary<string, object?> { ["id"] = _embedding.ModelId, ["available"] = _embedding.IsAvailable };
        }
        catch (Exception ex)
        {
            system["embeddingModel"] = new Dictionary<string, object?> { ["error"] = ex.GetType().Name };
        }

        try
        {
            system["database"] = new Dictionary<string, object?>
            {
                ["schemaVersion"] = await DatabaseVersionReader.TryReadAsync(_paths.DatabasePath, cancellationToken).ConfigureAwait(false),
                ["statistics"] = await _store.GetStatisticsAsync(cancellationToken).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            system["database"] = new Dictionary<string, object?> { ["error"] = ex.GetType().Name };
        }

        system["folders"] = folderData.Error is not null
            ? new Dictionary<string, object?> { ["error"] = folderData.Error }
            : folderData.Folders.Select(f =>
            {
                var item = new Dictionary<string, object?>
                {
                    ["name"] = f.DisplayName,
                    ["state"] = f.State,
                    ["documentCount"] = folderData.DocumentCounts.GetValueOrDefault(f.Id),
                    ["lastScanAt"] = f.LastScanAt,
                };
                if (options.IncludeFullPaths)
                {
                    item["path"] = f.Path;
                }

                return item;
            }).ToList();
        return system;
    }

    private static List<string> ReadGpuNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return GpuRegistry.ReadDriverDescriptions();
        }
        catch (Exception)
        {
            // Registry unreadable: the GPU name is optional.
            return [];
        }
    }

    // ---- logs -------------------------------------------------------------------------------------------------

    private async Task AddLogsAsync(ZipArchive archive, List<string> included, int logDays, PathMasker masker, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var directory = _paths.LogsDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var since = now.UtcDateTime - TimeSpan.FromDays(Math.Max(logDays, 0));
        var files = new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(f => f.LastWriteTimeUtc >= since)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = "logs/" + file.Name;
            try
            {
                // Serilog keeps the file open for writing, so open it with the most permissive sharing.
                await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var output = entry.Open();
                await using var writer = new StreamWriter(output, new UTF8Encoding(false));
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    await writer.WriteLineAsync(masker.MaskLogLine(line).AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                included.Add(entryName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipped a log file that could not be read: {FileName}", file.Name);
            }
        }
    }

    // ---- failed-files.csv -------------------------------------------------------------------------------------

    private async Task<string> BuildFailedFilesCsvAsync(FolderData folderData, DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("檔名,資料夾,位置,錯誤碼,原因,時間\r\n");

        IReadOnlyList<DocumentRecord> failed;
        try
        {
            failed = await _store.GetFailedDocumentsAsync(MaxFailedFiles, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the failed file list for the diagnostics export");
            return builder.ToString();
        }

        var byId = folderData.Folders.ToDictionary(f => f.Id);
        foreach (var document in failed)
        {
            var fileName = PathMasker.FileNameOf(document.Path);
            byId.TryGetValue(document.FolderId, out var folder);
            var folderName = folder?.DisplayName ?? "（未知）";
            var location = options.IncludeFullPaths ? document.Path : $"{folderName}\\…\\{fileName}";
            builder.Append(string.Join(',',
                Csv(fileName),
                Csv(folderName),
                Csv(location),
                Csv(document.ErrorCode.ToString()),
                Csv(PlainReason(document.ErrorCode)),
                Csv(document.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"))));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>Escapes one CSV cell. Cells starting with = + - @ get a leading apostrophe so spreadsheet programs do not run them as formulas.</summary>
    internal static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // The enum switch has no discard arm on purpose: a new DocumentErrorCode without wording fails the build (CS8509).
#pragma warning disable CS8524 // unnamed numeric values are not covered on purpose
    private static string PlainReason(DocumentErrorCode code) => code switch
    {
        DocumentErrorCode.None => "正常",
        DocumentErrorCode.PasswordProtected => "有密碼保護",
        DocumentErrorCode.Locked => "正被其他程式開啟",
        DocumentErrorCode.Corrupted => "檔案可能已損壞",
        DocumentErrorCode.TooLarge => "超過大小上限",
        DocumentErrorCode.Unsupported => "不支援這種檔案",
        DocumentErrorCode.Timeout => "讀取時間過長",
        DocumentErrorCode.AccessDenied => "沒有權限讀取",
        DocumentErrorCode.Unknown => "讀取時發生問題",
    };
}
