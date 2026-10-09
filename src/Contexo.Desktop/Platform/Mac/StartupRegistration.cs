using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Contexo.App.Services;

namespace Contexo.Desktop.Platform.Mac;

/// <summary>
/// Development stand-in for the Windows start-up entry: a LaunchAgent plist at
/// <c>~/Library/LaunchAgents/tw.contexo.desktop.plist</c> that runs the current program with <c>--minimized</c> at sign-in.
/// The plist is a file Contexo creates itself (never a user file). <c>launchctl</c> is not called; it takes effect at the next sign-in.
/// </summary>
public sealed class MacStartupRegistration : IStartupRegistration
{
    internal const string Label = "tw.contexo.desktop";
    internal const string FileName = Label + ".plist";
    internal const string MinimizedArgument = "--minimized";

    private readonly string _launchAgentsDirectory;
    private readonly Func<IReadOnlyList<string>?> _programArguments;

    /// <summary>Used by dependency injection: the current user's LaunchAgents folder and the running program.</summary>
    public MacStartupRegistration()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
            () => ResolveProgramArguments(Environment.ProcessPath, EntryAssemblyPath()))
    {
    }

    internal MacStartupRegistration(string launchAgentsDirectory, Func<IReadOnlyList<string>?> programArguments)
    {
        _launchAgentsDirectory = launchAgentsDirectory;
        _programArguments = programArguments;
    }

    private string PlistPath => Path.Combine(_launchAgentsDirectory, FileName);

    public bool IsEnabled
    {
        get
        {
            var expected = _programArguments();
            if (expected is null || !File.Exists(PlistPath))
            {
                return false;
            }

            try
            {
                var stored = ReadProgramArguments(PlistPath);
                return stored is not null && stored.SequenceEqual(expected, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            // Only the plist that Contexo wrote itself is removed.
            if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
            }

            return;
        }

        var arguments = _programArguments() ?? throw new InvalidOperationException("The path of the running program is unknown.");
        Directory.CreateDirectory(_launchAgentsDirectory);
        var temp = PlistPath + ".tmp";
        File.WriteAllText(temp, BuildPlist(arguments));
        File.Move(temp, PlistPath, overwrite: true);
    }

    /// <summary>
    /// The program and its arguments. When the program was started through <c>dotnet</c> (for example <c>dotnet run</c>),
    /// the application's .dll is passed along; a published app host runs by itself.
    /// </summary>
    internal static IReadOnlyList<string>? ResolveProgramArguments(string? processPath, string? entryAssemblyPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost && !string.IsNullOrEmpty(entryAssemblyPath))
        {
            return [processPath, entryAssemblyPath, MinimizedArgument];
        }

        return [processPath, MinimizedArgument];
    }

    internal static string BuildPlist(IReadOnlyList<string> programArguments)
    {
        var document = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    new XElement("key", "Label"),
                    new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array", programArguments.Select(a => new XElement("string", a))),
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"))));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + document + "\n";
    }

    internal static IReadOnlyList<string>? ReadProgramArguments(string plistPath)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(plistPath, settings);
        var document = XDocument.Load(reader);
        var elements = document.Root?.Element("dict")?.Elements().ToList();
        if (elements is null)
        {
            return null;
        }

        for (var i = 0; i + 1 < elements.Count; i++)
        {
            if (elements[i].Name == "key" && elements[i].Value == "ProgramArguments" && elements[i + 1].Name == "array")
            {
                return elements[i + 1].Elements("string").Select(e => e.Value).ToList();
            }
        }

        return null;
    }

    private static string? EntryAssemblyPath()
    {
        var path = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
