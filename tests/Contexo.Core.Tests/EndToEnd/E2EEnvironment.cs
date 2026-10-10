using System.Diagnostics;

namespace Contexo.Core.Tests.EndToEnd;

/// <summary>Finds the repository, the optional embedding model and the corpus generator, and generates the shared test corpus once per test run.</summary>
internal static class E2EEnvironment
{
    private static readonly Lazy<string> SharedCorpus = new(GenerateSharedCorpus);

    public static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Contexo.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>The folder that holds model folders, or null when no model has been downloaded (run tools/download-models.sh).</summary>
    public static string? FindModelsDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CONTEXO_MODELS_DIR");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            candidates.Add(fromEnvironment);
        }

        var root = FindRepositoryRoot();
        if (root is not null)
        {
            candidates.Add(Path.Combine(root, "models"));
        }

        return candidates.FirstOrDefault(d => Directory.Exists(d) && Directory.EnumerateDirectories(d).Any(m => File.Exists(Path.Combine(m, "contexo-model.json"))));
    }

    /// <summary>Copies the generated corpus (corpus/, queries.json, expected.json) into <paramref name="destination"/>. Every caller gets its own copy.</summary>
    public static void CopyCorpusTo(string destination)
    {
        CopyDirectory(SharedCorpus.Value, destination);
    }

    private static string GenerateSharedCorpus()
    {
        var directory = Directory.CreateTempSubdirectory("contexo-e2e-corpus-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        };

        var root = FindRepositoryRoot() ?? throw new InvalidOperationException("Contexo.slnx was not found above " + AppContext.BaseDirectory);
        var project = Path.Combine(root, "tools", "Contexo.CorpusGen", "Contexo.CorpusGen.csproj");
        var built = new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(root, "tools", "Contexo.CorpusGen", "bin", configuration, "net10.0", "Contexo.CorpusGen.dll"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        var start = new ProcessStartInfo(DotnetPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        if (built is not null)
        {
            start.ArgumentList.Add(built);
        }
        else
        {
            // Not built yet (for example `dotnet test` on this project alone): build and run it.
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(project);
            start.ArgumentList.Add("--");
        }

        start.ArgumentList.Add("generate");
        start.ArgumentList.Add(directory);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the corpus generator");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The corpus generator did not finish in 5 minutes");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The corpus generator failed ({process.ExitCode}): {errors.Result}{output.Result}");
        }

        return directory;
    }

    private static string DotnetPath =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath! : "dotnet";

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
