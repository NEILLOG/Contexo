using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Contexo.Core.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(IServiceCollection? services = null) =>
        (services ?? new ServiceCollection()).AddContexoCore()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    [Theory]
    [InlineData(typeof(ISettingsStore))]
    [InlineData(typeof(IAppPaths))]
    [InlineData(typeof(IUserActivityMonitor))]
    [InlineData(typeof(IKnowledgeStore))]
    [InlineData(typeof(IEmbeddingService))]
    [InlineData(typeof(IParserRegistry))]
    [InlineData(typeof(ISpreadsheetRegionReader))]
    [InlineData(typeof(IChunker))]
    [InlineData(typeof(IIndexingService))]
    [InlineData(typeof(ISearchService))]
    [InlineData(typeof(ITableQueryService))]
    [InlineData(typeof(IAiClientStatusService))]
    [InlineData(typeof(IDiagnosticsExporter))]
    [InlineData(typeof(ChunkingOptions))]
    [InlineData(typeof(ParserOptions))]
    public void Every_contract_resolves(Type serviceType)
    {
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    [Fact]
    public void All_parsers_and_integrations_are_registered()
    {
        using var provider = Build();

        Assert.Equal(7, provider.GetServices<IDocumentParser>().Count());
        Assert.Equal(4, provider.GetServices<IAiClientIntegration>().Count());
    }

    [Fact]
    public void Contracts_are_singletons()
    {
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<IKnowledgeStore>(), provider.GetRequiredService<IKnowledgeStore>());
        Assert.Same(provider.GetRequiredService<IParserRegistry>(), provider.GetRequiredService<IParserRegistry>());
    }

    [Fact]
    public void Registry_knows_every_parser_extension()
    {
        using var provider = Build();

        var registry = provider.GetRequiredService<IParserRegistry>();

        Assert.NotNull(registry.Resolve(".docx"));
        Assert.NotNull(registry.Resolve(".CSV"));
        Assert.Null(registry.Resolve(".doc"));
    }

    [Fact]
    public void Idle_monitor_defaults_to_always_idle()
    {
        using var provider = Build();

        Assert.Equal(TimeSpan.MaxValue, provider.GetRequiredService<IUserActivityMonitor>().IdleTime);
    }

    [Fact]
    public void Apps_can_override_paths_and_idle_monitor_by_registering_first()
    {
        var services = new ServiceCollection();
        var paths = new AppPaths(new AppPathsOverrides { DatabasePath = "custom.db" });
        var monitor = new FixedIdleMonitor();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IUserActivityMonitor>(monitor);

        using var provider = Build(services);

        Assert.Same(paths, provider.GetRequiredService<IAppPaths>());
        Assert.Same(monitor, provider.GetRequiredService<IUserActivityMonitor>());
    }

    [Fact]
    public void Default_options_match_the_contract_defaults()
    {
        using var provider = Build();

        Assert.Equal(new ChunkingOptions().MaxChars, provider.GetRequiredService<ChunkingOptions>().MaxChars);
        Assert.Equal(new ParserOptions().SmallTableMaxCells, provider.GetRequiredService<ParserOptions>().SmallTableMaxCells);
    }

    [Fact]
    public void Code_page_encodings_are_available_after_registration()
    {
        using var provider = Build();

        Assert.Equal(950, System.Text.Encoding.GetEncoding(950).CodePage);
    }

    private sealed class FixedIdleMonitor : IUserActivityMonitor
    {
        public TimeSpan IdleTime => TimeSpan.FromMinutes(5);
    }
}
