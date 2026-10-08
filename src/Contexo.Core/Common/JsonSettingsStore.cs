using System.Text.Json;
using System.Text.Json.Serialization;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Common;

/// <summary>Stores <see cref="AppSettings"/> as settings.json. Never throws on read: a missing or broken file yields the defaults.</summary>
internal sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly Lock _gate = new();
    private AppSettings? _current;

    public JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _path = paths.SettingsPath;
        _logger = logger;
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Leftover temp file is harmless.
                }
            }
        }

        lock (_gate)
        {
            _current = settings;
        }

        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Only the error type is logged, never the file content.
            _logger.LogWarning("Could not read settings file, using defaults ({ErrorType})", ex.GetType().Name);
            return new AppSettings();
        }
    }
}
