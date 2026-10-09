using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>Where one AI client keeps its MCP config, plus extra places that prove the client is installed.</summary>
internal sealed record ClientLocations(string ConfigPath, IReadOnlyList<string> InstallMarkers);

/// <summary>Implemented by integrations that can explain, in plain Traditional Chinese, why <see cref="ClientConfigState.Broken"/> was reported.</summary>
internal interface IConfigProblemSource
{
    string? DescribeProblem();
}

/// <summary>
/// Shared logic for AI clients whose MCP config is a JSON file with a named root object
/// ("mcpServers" or "servers") holding one entry per server. Only the "contexo" entry is ever touched.
/// </summary>
internal abstract class JsonMcpClientIntegration : IAiClientIntegration, IConfigProblemSource
{
    internal const string EntryName = "contexo";
    internal const string BackupSuffix = ".contexo.bak";

    internal const string MalformedMessage = "設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。";
    internal const string WriteFailedMessage = "無法寫入設定檔，可能正被其他程式使用。請先完全關閉該軟體，再試一次。";

    private const int WriteAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep Chinese paths readable and avoid needless \uXXXX escapes. Backslashes are still escaped by the serializer.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAppPaths _paths;
    private readonly ClientPathOptions _pathOptions;

    protected JsonMcpClientIntegration(IAppPaths paths, ClientPathOptions? pathOptions)
    {
        _paths = paths;
        _pathOptions = pathOptions ?? new ClientPathOptions();
    }

    public abstract string ClientId { get; }

    public abstract string DisplayName { get; }

    public abstract IReadOnlyCollection<string> KnownClientNames { get; }

    /// <summary>"mcpServers" or "servers".</summary>
    protected abstract string RootKey { get; }

    /// <summary>VS Code requires <c>"type": "stdio"</c> on each entry.</summary>
    protected virtual bool IncludeType => false;

    protected ClientPathOptions Options => _pathOptions;

    /// <summary>Null when the platform has no known location (the client then counts as not installed).</summary>
    protected abstract ClientLocations? GetLocations();

    /// <summary>Full path of the config file on this platform, or null when the platform is unsupported.</summary>
    internal string? ConfigPath => GetLocations()?.ConfigPath;

    public ClientConfigState GetConfigState() => Inspect().State;

    public string? DescribeProblem()
    {
        var inspection = Inspect();
        return inspection.State == ClientConfigState.Broken ? inspection.Problem : null;
    }

    public void AddOrRepair(McpServerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var locations = GetLocations()
            ?? throw new InvalidOperationException($"這台電腦上找不到 {DisplayName}。");
        var configPath = locations.ConfigPath;

        WithRetry(() =>
        {
            var root = LoadForEdit(configPath);
            var servers = GetOrCreateServers(root);
            servers[EntryName] = BuildEntry(launch);
            Save(configPath, root, backup: File.Exists(configPath));
        });
    }

    public void Remove()
    {
        var locations = GetLocations();
        if (locations is null || !File.Exists(locations.ConfigPath))
        {
            return;
        }

        var configPath = locations.ConfigPath;
        WithRetry(() =>
        {
            var root = LoadForEdit(configPath);
            if (root[RootKey] is not JsonObject servers || !servers.ContainsKey(EntryName))
            {
                return;
            }

            servers.Remove(EntryName);
            Save(configPath, root, backup: true);
        });
    }

    public string BuildManualSnippet(McpServerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var root = new JsonObject
        {
            [RootKey] = new JsonObject { [EntryName] = BuildEntry(launch) },
        };
        return root.ToJsonString(WriteOptions);
    }

    private ConfigInspection Inspect()
    {
        var locations = GetLocations();
        if (locations is null || !IsInstalled(locations))
        {
            return new(ClientConfigState.NotInstalled, null);
        }

        if (!File.Exists(locations.ConfigPath))
        {
            return new(ClientConfigState.NotConfigured, null);
        }

        JsonObject root;
        try
        {
            root = ParseRoot(File.ReadAllText(locations.ConfigPath));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new(ClientConfigState.Broken, "設定檔格式有誤，Contexo 讀不懂它。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ClientConfigState.Broken, "無法讀取設定檔。");
        }

        var serversNode = root[RootKey];
        if (serversNode is null)
        {
            return new(ClientConfigState.NotConfigured, null);
        }

        if (serversNode is not JsonObject servers)
        {
            return new(ClientConfigState.Broken, "設定檔格式有誤，Contexo 讀不懂它。");
        }

        if (!servers.ContainsKey(EntryName))
        {
            return new(ClientConfigState.NotConfigured, null);
        }

        if (servers[EntryName] is not JsonObject entry
            || entry["command"] is not JsonValue commandValue
            || !commandValue.TryGetValue<string>(out var command)
            || string.IsNullOrWhiteSpace(command))
        {
            return new(ClientConfigState.Broken, "設定內容不完整，需要修復。");
        }

        if (!File.Exists(command))
        {
            return new(ClientConfigState.Broken, "設定裡的 Contexo 程式位置已經不存在，需要修復。");
        }

        if (!SamePath(command, _paths.McpExecutablePath))
        {
            return new(ClientConfigState.Broken, "設定裡的 Contexo 程式位置和目前安裝的位置不同，需要修復。");
        }

        return new(ClientConfigState.Configured, null);
    }

    private static bool IsInstalled(ClientLocations locations)
    {
        var configDirectory = Path.GetDirectoryName(locations.ConfigPath);
        if (!string.IsNullOrEmpty(configDirectory) && Directory.Exists(configDirectory))
        {
            return true;
        }

        return locations.InstallMarkers.Any(marker => Directory.Exists(marker) || File.Exists(marker));
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static JsonObject ParseRoot(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(text, nodeOptions: null, ReadOptions) as JsonObject
            ?? throw new InvalidOperationException(MalformedMessage);
    }

    private static JsonObject LoadForEdit(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new JsonObject();
        }

        try
        {
            return ParseRoot(File.ReadAllText(configPath));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(MalformedMessage, ex);
        }
    }

    private JsonObject GetOrCreateServers(JsonObject root)
    {
        var node = root[RootKey];
        if (node is null)
        {
            var created = new JsonObject();
            root[RootKey] = created;
            return created;
        }

        return node as JsonObject ?? throw new InvalidOperationException(MalformedMessage);
    }

    private JsonObject BuildEntry(McpServerLaunch launch)
    {
        var args = new JsonArray();
        foreach (var argument in launch.Arguments)
        {
            args.Add(argument);
        }

        var entry = new JsonObject();
        if (IncludeType)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = launch.ExecutablePath;
        entry["args"] = args;
        return entry;
    }

    private static void Save(string configPath, JsonObject root, bool backup)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (backup)
        {
            File.Copy(configPath, configPath + BackupSuffix, overwrite: true);
        }

        var temp = configPath + ".contexo.tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(WriteOptions));
            File.Move(temp, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void WithRetry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= WriteAttempts)
                {
                    throw new InvalidOperationException(WriteFailedMessage, ex);
                }

                Thread.Sleep(RetryDelay);
            }
        }
    }

    private sealed record ConfigInspection(ClientConfigState State, string? Problem);
}
