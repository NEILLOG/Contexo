using Contexo.Core.Abstractions;

namespace Contexo.Core.Diagnostics;

/// <summary>Stub. Implemented by T20.</summary>
internal sealed class DiagnosticsExporter : IDiagnosticsExporter
{
    public Task<DiagnosticsResult> ExportAsync(string destinationDirectory, DiagnosticsOptions options, CancellationToken cancellationToken) => throw new NotImplementedException("T20");
}
