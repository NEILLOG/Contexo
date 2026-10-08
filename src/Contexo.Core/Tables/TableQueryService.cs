using Contexo.Core.Abstractions;

namespace Contexo.Core.Tables;

/// <summary>Stub. Implemented by T12.</summary>
internal sealed class TableQueryService : ITableQueryService
{
    public Task<TableDescription> DescribeAsync(string tableId, int sampleRows, CancellationToken cancellationToken) => throw new NotImplementedException("T12");

    public Task<TableQueryResult> QueryAsync(string tableId, string sql, int maxRows, CancellationToken cancellationToken) => throw new NotImplementedException("T12");
}
