using Contexo.Core.Abstractions;
using Contexo.Core.Chunking;
using Contexo.Core.Common;
using Contexo.Core.Diagnostics;
using Contexo.Core.Embedding;
using Contexo.Core.Indexing;
using Contexo.Core.Integrations;
using Contexo.Core.Parsing;
using Contexo.Core.Parsing.Pdf;
using Contexo.Core.Parsing.PowerPoint;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Parsing.Text;
using Contexo.Core.Parsing.Word;
using Contexo.Core.Search;
using Contexo.Core.Storage;
using Contexo.Core.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every contract implementation as a singleton.
    /// <see cref="IAppPaths"/> and <see cref="IUserActivityMonitor"/> use TryAdd: register your own before calling this to override them
    /// (Contexo.Mcp for --db / --models, Contexo.Desktop for platform idle detection).
    /// Logging: when no <c>ILogger&lt;T&gt;</c> is registered yet (a bare ServiceCollection) a no-op logger is used, so call <c>AddLogging()</c>
    /// first if you want real logs. The generic host already does.
    /// </summary>
    public static IServiceCollection AddContexoCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        TextDecoder.EnsureCodePages();

        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));
        services.TryAddSingleton<IAppPaths>(_ => new AppPaths());
        services.TryAddSingleton<IUserActivityMonitor, AlwaysIdleActivityMonitor>();
        services.TryAddSingleton(new ChunkingOptions());
        services.TryAddSingleton(new ParserOptions());

        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IKnowledgeStore, SqliteKnowledgeStore>();
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();

        services.AddSingleton<IDocumentParser, PlainTextParser>();
        services.AddSingleton<IDocumentParser, HtmlParser>();
        services.AddSingleton<IDocumentParser, RtfParser>();
        services.AddSingleton<IDocumentParser, WordParser>();
        services.AddSingleton<IDocumentParser, PowerPointParser>();
        services.AddSingleton<IDocumentParser, PdfParser>();
        services.AddSingleton<IDocumentParser, SpreadsheetParser>();
        services.AddSingleton<IParserRegistry, ParserRegistry>();
        services.AddSingleton<ISpreadsheetRegionReader, SpreadsheetRegionReader>();

        services.AddSingleton<IChunker, StructuredChunker>();
        services.AddSingleton<IIndexingService, IndexingService>();
        services.AddSingleton<ISearchService, HybridSearchService>();
        services.AddSingleton<ITableQueryService, TableQueryService>();

        services.AddSingleton<IAiClientIntegration, ClaudeDesktopIntegration>();
        services.AddSingleton<IAiClientIntegration, VsCodeIntegration>();
        services.AddSingleton<IAiClientIntegration, CursorIntegration>();
        services.AddSingleton<IAiClientIntegration, LmStudioIntegration>();
        services.AddSingleton<IAiClientStatusService, AiClientStatusService>();

        services.AddSingleton<IDiagnosticsExporter, DiagnosticsExporter>();

        return services;
    }
}
