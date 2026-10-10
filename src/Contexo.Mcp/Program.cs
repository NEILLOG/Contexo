using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Mcp;
using Contexo.Mcp.Activity;
using Contexo.Mcp.Logging;
using Contexo.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Serilog;

// stdout carries MCP protocol messages and nothing else. The SDK's stdio transport writes through
// Console.OpenStandardOutput() (a raw stream, not Console.Out), so redirecting Console.Out only catches stray Console.WriteLine calls.
Console.SetOut(Console.Error);

var arguments = McpArguments.Parse(args);
var paths = new AppPaths(new AppPathsOverrides
{
    DatabasePath = arguments.DatabasePath,
    ModelsDirectory = arguments.ModelsDirectory,
});

// stdout is reserved for MCP protocol messages: log to a file and to stderr only.
var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(paths.LogsDirectory, "mcp-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true)
    .WriteTo.Sink(new StderrSink())
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder();

    // The default host adds a console logger, which writes to stdout.
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(logger, dispose: false);

    builder.Services.AddSingleton<IAppPaths>(paths);
    builder.Services.AddContexoCore();
    builder.Services.AddSingleton<McpActivityRecorder>();
    builder.Services.AddSingleton<ContexoToolService>();

    var version = AppVersion.Current.Version;
    builder.Services
        .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "contexo", Version = version })
        .WithStdioServerTransport()
        .WithTools<ContexoTools>()
        .WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, cancellationToken) =>
        {
            // Clients on protocol versions before 2026-07-28 finish a handshake (initialize, then an "initialized" notification).
            // Newer clients have no handshake and name themselves on every request, so the first request that carries a name counts as connecting.
            var clientInfo = context.Server.ClientInfo;
            var connecting = context.JsonRpcMessage is JsonRpcNotification { Method: NotificationMethods.InitializedNotification }
                || (context.JsonRpcMessage is JsonRpcRequest { Method: not RequestMethods.Initialize and not RequestMethods.Ping } && clientInfo is not null);
            if (connecting)
            {
                await context.Services!.GetRequiredService<McpActivityRecorder>().RecordConnectedAsync(clientInfo).ConfigureAwait(false);
            }

            await next(context, cancellationToken).ConfigureAwait(false);
        }));

    using var host = builder.Build();
    var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Contexo.Mcp");
    log.LogInformation("Contexo.Mcp started (version {Version})", version);

    // Create or migrate the database before the first request. A failure is not fatal: every tool call tries again and answers in plain language.
    if (!await host.Services.GetRequiredService<ContexoToolService>().EnsureStoreAsync(CancellationToken.None))
    {
        log.LogWarning("The Contexo database could not be opened at startup; tool calls will report it");
    }

    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    logger.Fatal(ex, "Contexo.Mcp stopped unexpectedly");
    return 1;
}
finally
{
    await logger.DisposeAsync();
}
