using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Mcp;
using Contexo.Mcp.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

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

    // T13: register the MCP server (AddMcpServer().WithStdioServerTransport() and the tools) here.

    using var host = builder.Build();
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Contexo.Mcp").LogInformation("Contexo.Mcp started");

    // T13: replace StartAsync/StopAsync with `await host.RunAsync()` once the MCP server is registered.
    await host.StartAsync();
    await host.StopAsync();
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
