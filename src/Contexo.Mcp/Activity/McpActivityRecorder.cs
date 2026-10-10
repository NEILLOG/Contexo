using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Contexo.Mcp.Activity;

/// <summary>
/// Writes <see cref="McpActivity"/> rows so the desktop app can show which AI software connected.
/// Recording is best effort: a locked database or any other failure is logged (without content) and never reaches the tool result.
/// </summary>
internal sealed class McpActivityRecorder(IKnowledgeStore store, ILogger<McpActivityRecorder> logger, TimeProvider? timeProvider = null)
{
    internal const string UnknownClient = "unknown";
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private int _connectedRecorded;

    /// <summary>Records <see cref="McpEventKind.Connected"/> once per process (the process serves exactly one client).</summary>
    public Task RecordConnectedAsync(Implementation? client)
    {
        if (Interlocked.Exchange(ref _connectedRecorded, 1) == 1)
        {
            return Task.CompletedTask;
        }

        return RecordAsync(client, McpEventKind.Connected, toolName: null, detail: null);
    }

    public Task RecordToolCallAsync(Implementation? client, string toolName) => RecordAsync(client, McpEventKind.ToolCall, toolName, detail: null);

    /// <param name="detail">Exception type and a fixed plain-language sentence. Never query text, SQL or document content.</param>
    public Task RecordErrorAsync(Implementation? client, string toolName, string detail) => RecordAsync(client, McpEventKind.Error, toolName, detail);

    private async Task RecordAsync(Implementation? client, McpEventKind kind, string? toolName, string? detail)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(client?.Name) ? UnknownClient : client.Name;
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await store.RecordMcpActivityAsync(
                new McpActivity(kind, name, client?.Version, toolName, detail, _time.GetUtcNow()),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not record MCP activity {Kind} ({ExceptionType})", kind, ex.GetType().Name);
        }
    }
}
