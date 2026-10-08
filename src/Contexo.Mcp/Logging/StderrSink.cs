using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Contexo.Mcp.Logging;

/// <summary>Writes log events to stderr. stdout belongs to the MCP protocol and must never receive anything else.</summary>
internal sealed class StderrSink : ILogEventSink
{
    private readonly ITextFormatter _formatter =
        new MessageTemplateTextFormatter("{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

    private readonly Lock _gate = new();

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            _formatter.Format(logEvent, Console.Error);
        }
    }
}
