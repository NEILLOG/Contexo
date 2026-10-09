using Contexo.App.Services;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.About;

internal sealed class FakeDiagnosticsExporter : IDiagnosticsExporter
{
    public string? Directory { get; private set; }

    public DiagnosticsOptions? Options { get; private set; }

    public long SizeBytes { get; set; } = 1_258_291;

    /// <summary>When set, the export waits for this task (or its cancellation) before finishing.</summary>
    public Task? Gate { get; set; }

    public Exception? Failure { get; set; }

    public async Task<DiagnosticsResult> ExportAsync(string destinationDirectory, DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        Directory = destinationDirectory;
        Options = options;
        if (Failure is not null)
        {
            throw Failure;
        }

        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        return new DiagnosticsResult(Path.Combine(destinationDirectory, "Contexo問題回報_20261008_0004.zip"), SizeBytes, ["manifest.json"]);
    }
}

internal sealed class FakeFilePicker(string? folder) : IFilePicker
{
    public int Calls { get; private set; }

    public Task<string?> PickSaveFolderAsync()
    {
        Calls++;
        return Task.FromResult(folder);
    }
}

internal sealed class FakeShellLauncher : IShellLauncher
{
    public List<string> Revealed { get; } = [];

    public void OpenFile(string path)
    {
    }

    public void RevealInFileManager(string path) => Revealed.Add(path);

    public void OpenFolder(string path)
    {
    }
}

internal sealed class FakeClipboard(bool works) : IClipboardService
{
    public string? Text { get; private set; }

    public Task<bool> TrySetTextAsync(string text)
    {
        if (works)
        {
            Text = text;
        }

        return Task.FromResult(works);
    }
}

internal sealed class FakeEmbeddingInfo(string modelId = "bge-small-zh-v1.5/int8", bool available = true) : IEmbeddingService
{
    public string ModelId => modelId;

    public int Dimensions => 512;

    public bool IsAvailable => available;

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakePaths(string databasePath) : IAppPaths
{
    public string DataDirectory => Path.GetDirectoryName(databasePath)!;

    public string DatabasePath => databasePath;

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string ModelsDirectory => Path.Combine(DataDirectory, "models");

    public string McpExecutablePath => Path.Combine(DataDirectory, "Contexo.Mcp");
}
