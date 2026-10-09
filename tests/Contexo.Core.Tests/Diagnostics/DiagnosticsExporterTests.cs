using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Core.Diagnostics;
using Contexo.Core.Tests.Common;
using Contexo.Core.Tests.Storage;

namespace Contexo.Core.Tests.Diagnostics;

public sealed class DiagnosticsExporterTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 4, 30, TimeSpan.Zero);

    private StoreFixture _fixture = null!;
    private TempDirectory _output = null!;
    private AppPaths _paths = null!;
    private string _userProfile = null!;

    public async Task InitializeAsync()
    {
        _fixture = await StoreFixture.CreateAsync();
        _output = new TempDirectory();
        _paths = new AppPaths(new AppPathsOverrides { DataDirectory = _fixture.PathOf("data"), DatabasePath = _fixture.DatabasePath });
        _userProfile = _fixture.PathOf("Users", "FakeUser");
    }

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        _output.Dispose();
        return Task.CompletedTask;
    }

    private DiagnosticsExporter CreateExporter(ISettingsStore? settings = null, TimeProvider? time = null) =>
        new(_paths, settings ?? new FakeSettings(), _fixture.Store, new FakeEmbedding(), new TestLogger<DiagnosticsExporter>(), time ?? new FixedTime(Now), _userProfile);

    private string WriteLog(string name, string content, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(_paths.LogsDirectory, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? Now.UtcDateTime);
        return path;
    }

    private static string[] EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    private static string ReadEntry(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string AllText(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var builder = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            builder.AppendLine(entry.FullName).AppendLine(reader.ReadToEnd());
        }

        return builder.ToString();
    }

    [Fact]
    public async Task Default_options_include_every_item_and_the_manifest_lists_them()
    {
        WriteLog("contexo-20261008.log", "hello\n");
        var folder = await _fixture.AddFolderAsync("Docs");
        await _fixture.Store.MarkDocumentAsync(folder.Id, Path.Combine(folder.Path, "a.docx"), StoreFixture.Fingerprint(), DocumentStatus.Failed, DocumentErrorCode.PasswordProtected, "x", null, false, CancellationToken.None);

        var result = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        Assert.Equal(Path.Combine(_output.Path, "Contexo問題回報_20261008_0004.zip"), result.ZipPath);
        Assert.Equal(new FileInfo(result.ZipPath).Length, result.SizeBytes);
        string[] expected = ["failed-files.csv", "logs/contexo-20261008.log", "manifest.json", "settings.json", "system.json"];
        Assert.Equal(expected, EntryNames(result.ZipPath));
        Assert.Equal(expected, result.IncludedEntries.Order(StringComparer.Ordinal).ToArray());

        using var manifest = JsonDocument.Parse(ReadEntry(result.ZipPath, "manifest.json"));
        var listed = manifest.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, listed);
        Assert.True(manifest.RootElement.GetProperty("options").GetProperty("includeSettings").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("options").GetProperty("includeFullPaths").GetBoolean());
    }

    [Fact]
    public async Task Unticked_options_leave_their_items_out()
    {
        WriteLog("contexo-20261008.log", "hello\n");
        var options = new DiagnosticsOptions { IncludeSettings = false, IncludeRecentLogs = false, IncludeFailedFileList = false };

        var result = await CreateExporter().ExportAsync(_output.Path, options, CancellationToken.None);

        Assert.Equal(["manifest.json", "system.json"], EntryNames(result.ZipPath));
        using var manifest = JsonDocument.Parse(ReadEntry(result.ZipPath, "manifest.json"));
        Assert.Equal(2, manifest.RootElement.GetProperty("entries").GetArrayLength());
        Assert.False(manifest.RootElement.GetProperty("options").GetProperty("includeRecentLogs").GetBoolean());
    }

    [Fact]
    public async Task System_json_has_version_machine_model_and_statistics()
    {
        var folder = await _fixture.AddFolderAsync("Docs");
        await _fixture.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, Path.Combine(folder.Path, "ok.txt"), [StoreFixture.NoVector(0, "text")]), CancellationToken.None);

        var result = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        using var system = JsonDocument.Parse(ReadEntry(result.ZipPath, "system.json"));
        var root = system.RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("contexoVersion").GetString()));
        Assert.True(root.GetProperty("processorCount").GetInt32() >= 1);
        Assert.True(root.GetProperty("totalAvailableMemoryBytes").GetInt64() > 0);
        Assert.Equal("fake-model/int8", root.GetProperty("embeddingModel").GetProperty("id").GetString());
        Assert.True(root.GetProperty("embeddingModel").GetProperty("available").GetBoolean());
        Assert.Equal(1, root.GetProperty("database").GetProperty("statistics").GetProperty("DocumentCount").GetInt32());
        Assert.True(root.GetProperty("database").GetProperty("schemaVersion").GetInt32() >= 1);
        var first = root.GetProperty("folders")[0];
        Assert.Equal("Docs", first.GetProperty("name").GetString());
        Assert.Equal(1, first.GetProperty("documentCount").GetInt32());
        Assert.False(first.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task Settings_are_exported_and_secrets_masked()
    {
        var settings = new FakeSettings(new AppSettings { Theme = ThemePreference.Dark, MaxFileSizeMb = 12 });

        var result = await CreateExporter(settings).ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        using var json = JsonDocument.Parse(ReadEntry(result.ZipPath, "settings.json"));
        Assert.Equal("Dark", json.RootElement.GetProperty("Theme").GetString());
        Assert.Equal(12, json.RootElement.GetProperty("MaxFileSizeMb").GetInt32());
    }

    [Fact]
    public void Secret_looking_values_are_masked_at_any_depth_and_in_any_case()
    {
        const string input = """
            {
              "Theme": "Dark",
              "ApiKey": "abc",
              "server": { "url": "https://x", "PASSWORD": "p@ss", "inner": { "clientSecret": "s", "accessToken": "t", "name": "keep" } },
              "list": [ { "token": "1", "ok": 2 }, { "plain": 3 } ],
              "keyboard": "also masked because the name contains key"
            }
            """;

        using var json = JsonDocument.Parse(SettingsMasker.MaskJson(input));
        var root = json.RootElement;

        Assert.Equal("Dark", root.GetProperty("Theme").GetString());
        Assert.Equal("***", root.GetProperty("ApiKey").GetString());
        Assert.Equal("https://x", root.GetProperty("server").GetProperty("url").GetString());
        Assert.Equal("***", root.GetProperty("server").GetProperty("PASSWORD").GetString());
        Assert.Equal("***", root.GetProperty("server").GetProperty("inner").GetProperty("clientSecret").GetString());
        Assert.Equal("***", root.GetProperty("server").GetProperty("inner").GetProperty("accessToken").GetString());
        Assert.Equal("keep", root.GetProperty("server").GetProperty("inner").GetProperty("name").GetString());
        Assert.Equal("***", root.GetProperty("list")[0].GetProperty("token").GetString());
        Assert.Equal(2, root.GetProperty("list")[0].GetProperty("ok").GetInt32());
        Assert.Equal(3, root.GetProperty("list")[1].GetProperty("plain").GetInt32());
        Assert.Equal("***", root.GetProperty("keyboard").GetString());
    }

    [Fact]
    public async Task Logs_are_copied_while_another_stream_has_them_open_for_writing()
    {
        var path = Path.Combine(_paths.LogsDirectory, "contexo-20261008.log");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("2026-10-08 INFO Contexo started\n"));
        await writer.FlushAsync();

        var result = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        Assert.Contains("Contexo started", ReadEntry(result.ZipPath, "logs/contexo-20261008.log"));
    }

    [Fact]
    public async Task Only_logs_from_the_requested_number_of_days_are_included()
    {
        WriteLog("contexo-today.log", "today\n", Now.UtcDateTime);
        WriteLog("contexo-6days.log", "six\n", Now.UtcDateTime.AddDays(-6));
        WriteLog("contexo-10days.log", "ten\n", Now.UtcDateTime.AddDays(-10));

        var week = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions { LogDays = 7 }, CancellationToken.None);
        var month = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions { LogDays = 30 }, CancellationToken.None);

        Assert.Equal(["logs/contexo-6days.log", "logs/contexo-today.log"], EntryNames(week.ZipPath).Where(n => n.StartsWith("logs/")).ToArray());
        Assert.Equal(3, EntryNames(month.ZipPath).Count(n => n.StartsWith("logs/")));
    }

    [Fact]
    public async Task Without_full_paths_nothing_in_the_zip_contains_the_user_folder()
    {
        var documents = _fixture.PathOf("Users", "FakeUser", "Documents", "報價");
        var folder = await _fixture.Store.AddFolderAsync(documents, CancellationToken.None);
        var failedPath = Path.Combine(documents, "客戶王小明", "報價單.docx");
        await _fixture.Store.MarkDocumentAsync(folder.Id, failedPath, StoreFixture.Fingerprint(), DocumentStatus.Failed, DocumentErrorCode.Corrupted, "壞掉", null, false, CancellationToken.None);
        WriteLog(
            "contexo-20261008.log",
            $"INFO scanning {documents}\nERROR failed {failedPath}\nINFO data in {_userProfile}/AppData/Local\nINFO json {_userProfile.Replace("\\", "\\\\")}\nINFO C:\\Users\\OtherPerson\\Desktop\\a.txt\nINFO /Users/someone/x\n");

        var result = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        var text = AllText(result.ZipPath);
        Assert.DoesNotContain(_userProfile, text);
        Assert.DoesNotContain(documents, text);
        Assert.DoesNotContain("FakeUser", text);
        Assert.DoesNotContain("OtherPerson", text);
        Assert.DoesNotContain("someone", text);
        Assert.Contains("%USERPROFILE%", ReadEntry(result.ZipPath, "logs/contexo-20261008.log"));

        var csv = ReadEntry(result.ZipPath, "failed-files.csv");
        Assert.Contains("報價單.docx", csv);
        Assert.Contains("報價\\…\\報價單.docx", csv);
    }

    [Fact]
    public async Task With_full_paths_the_paths_are_kept()
    {
        var documents = _fixture.PathOf("Users", "FakeUser", "Documents", "報價");
        var folder = await _fixture.Store.AddFolderAsync(documents, CancellationToken.None);
        var failedPath = Path.Combine(documents, "a.docx");
        await _fixture.Store.MarkDocumentAsync(folder.Id, failedPath, StoreFixture.Fingerprint(), DocumentStatus.Failed, DocumentErrorCode.Locked, null, null, false, CancellationToken.None);

        var result = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions { IncludeFullPaths = true }, CancellationToken.None);

        Assert.Contains(failedPath, ReadEntry(result.ZipPath, "failed-files.csv"));
        using var system = JsonDocument.Parse(ReadEntry(result.ZipPath, "system.json"));
        Assert.Equal(folder.Path, system.RootElement.GetProperty("folders")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Failed_file_list_has_names_and_reasons_but_no_file_content()
    {
        var folder = await _fixture.AddFolderAsync("Docs");
        var secretText = "機密內容-不可外流";
        await _fixture.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, Path.Combine(folder.Path, "ok.txt"), [StoreFixture.NoVector(0, secretText)]), CancellationToken.None);
        await _fixture.Store.MarkDocumentAsync(folder.Id, Path.Combine(folder.Path, "=bad,\"name\".docx"), StoreFixture.Fingerprint(), DocumentStatus.Failed, DocumentErrorCode.PasswordProtected, secretText, null, false, CancellationToken.None);

        var result = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        var csv = ReadEntry(result.ZipPath, "failed-files.csv");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("檔名,資料夾,位置,錯誤碼,原因,時間", lines[0].TrimStart('\uFEFF'));
        Assert.Equal(2, lines.Length);
        Assert.Contains("PasswordProtected", lines[1]);
        Assert.Contains("有密碼保護", lines[1]);
        Assert.Contains("\"'=bad,\"\"name\"\".docx\"", lines[1]);
        Assert.DoesNotContain(secretText, AllText(result.ZipPath));
    }

    [Fact]
    public async Task Secrets_in_log_lines_are_masked()
    {
        WriteLog("contexo-20261008.log", "INFO call apiKey=sk-12345 done\nINFO {\"password\": \"hunter2\"} token: abcdef\n");

        var result = await CreateExporter().ExportAsync(_output.Path, new DiagnosticsOptions { IncludeFullPaths = true }, CancellationToken.None);

        var log = ReadEntry(result.ZipPath, "logs/contexo-20261008.log");
        Assert.DoesNotContain("sk-12345", log);
        Assert.DoesNotContain("hunter2", log);
        Assert.DoesNotContain("abcdef", log);
        Assert.Contains("***", log);
    }

    [Fact]
    public async Task A_name_that_is_taken_gets_a_numeric_suffix()
    {
        var exporter = CreateExporter();

        var first = await exporter.ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);
        var second = await exporter.ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);
        var third = await exporter.ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        Assert.Equal("Contexo問題回報_20261008_0004.zip", Path.GetFileName(first.ZipPath));
        Assert.Equal("Contexo問題回報_20261008_0004_2.zip", Path.GetFileName(second.ZipPath));
        Assert.Equal("Contexo問題回報_20261008_0004_3.zip", Path.GetFileName(third.ZipPath));
    }

    [Fact]
    public async Task A_cancelled_export_leaves_no_partial_zip_and_keeps_existing_ones()
    {
        var exporter = CreateExporter();
        var existing = await exporter.ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

        // The settings store cancels the export once the zip file has been created and is being filled.
        using var cts = new CancellationTokenSource();
        var cancelling = new FakeSettings(onRead: cts.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateExporter(cancelling).ExportAsync(_output.Path, new DiagnosticsOptions(), cts.Token));

        Assert.Equal([existing.ZipPath], Directory.GetFiles(_output.Path));
    }

    [Fact]
    public async Task A_failure_while_writing_removes_the_incomplete_zip()
    {
        var failing = new FakeSettings(onRead: () => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateExporter(failing).ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_output.Path));
    }

    [Fact]
    public async Task A_database_that_cannot_be_read_still_produces_a_report()
    {
        var emptyDirectory = new TempDirectory();
        try
        {
            var paths = new AppPaths(new AppPathsOverrides { DataDirectory = emptyDirectory.Combine("data"), DatabasePath = emptyDirectory.Combine("data", "missing.db") });
            var uninitialised = new Contexo.Core.Storage.SqliteKnowledgeStore(paths, new TestLogger<Contexo.Core.Storage.SqliteKnowledgeStore>());
            var exporter = new DiagnosticsExporter(paths, new FakeSettings(), uninitialised, new FakeEmbedding(), new TestLogger<DiagnosticsExporter>(), new FixedTime(Now), _userProfile);

            var result = await exporter.ExportAsync(_output.Path, new DiagnosticsOptions(), CancellationToken.None);

            Assert.Contains("system.json", EntryNames(result.ZipPath));
            Assert.Contains("failed-files.csv", EntryNames(result.ZipPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            emptyDirectory.Dispose();
        }
    }

    [Fact]
    public void Csv_cells_are_quoted_and_protected_from_formulas()
    {
        Assert.Equal("plain", DiagnosticsExporter.Csv("plain"));
        Assert.Equal("\"a,b\"", DiagnosticsExporter.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", DiagnosticsExporter.Csv("say \"hi\""));
        Assert.Equal("'=SUM(A1)", DiagnosticsExporter.Csv("=SUM(A1)"));
        Assert.Equal("'@x", DiagnosticsExporter.Csv("@x"));
    }

    [Fact]
    public async Task Database_version_reader_reads_the_schema_version_and_returns_null_for_a_missing_file()
    {
        var version = await DatabaseVersionReader.TryReadAsync(_fixture.DatabasePath, CancellationToken.None);
        var missing = await DatabaseVersionReader.TryReadAsync(Path.Combine(_output.Path, "nope.db"), CancellationToken.None);

        Assert.True(version >= 1);
        Assert.Null(missing);
    }

    [SkippableFact]
    [Trait("Category", "Windows")]
    public void Gpu_names_can_be_read_from_the_registry_on_windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows());

#pragma warning disable CA1416 // guarded by the Skip.IfNot above
        var names = GpuRegistry.ReadDriverDescriptions();
#pragma warning restore CA1416

        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeSettings(AppSettings? settings = null, Action? onRead = null) : ISettingsStore
    {
        public AppSettings Current
        {
            get
            {
                onRead?.Invoke();
                return settings ?? new AppSettings();
            }
        }

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task SaveAsync(AppSettings s, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeEmbedding : IEmbeddingService
    {
        public string ModelId => "fake-model/int8";

        public int Dimensions => 4;

        public bool IsAvailable => true;

        public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
