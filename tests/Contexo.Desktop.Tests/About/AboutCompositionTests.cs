using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Contexo.App.About;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Desktop.Platform.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contexo.Desktop.Tests.About;

/// <summary>Resolves the About page from the product's real service registrations and exports with the real exporter.</summary>
public sealed class AboutCompositionTests
{
    [AvaloniaFact]
    public async Task About_page_is_created_by_the_container_and_the_real_exporter_produces_a_zip()
    {
        var directory = Directory.CreateTempSubdirectory("contexo-compose-").FullName;
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAppPaths>(new AppPaths(new AppPathsOverrides { DataDirectory = Path.Combine(directory, "data"), ModelsDirectory = Path.Combine(directory, "models") }));
            services.AddContexoDesktop();
            services.AddContexoCore();
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IKnowledgeStore>().InitializeAsync(CancellationToken.None);
            var about = provider.GetRequiredService<AboutViewModel>();
            Assert.True(about.CanExport);
            await about.RefreshAsync();
            Assert.StartsWith("schema ", about.DatabaseVersionText);

            var output = Path.Combine(directory, "out");
            var result = await provider.GetRequiredService<IDiagnosticsExporter>().ExportAsync(output, new DiagnosticsOptions(), CancellationToken.None);

            using var zip = ZipFile.OpenRead(result.ZipPath);
            Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
            Assert.Contains(zip.Entries, e => e.FullName == "system.json");
            Assert.Contains(zip.Entries, e => e.FullName == "settings.json");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
