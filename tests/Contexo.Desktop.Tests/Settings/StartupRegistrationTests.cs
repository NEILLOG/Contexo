using Contexo.Desktop.Platform.Mac;
using Contexo.Desktop.Platform.Windows;

namespace Contexo.Desktop.Tests.Settings;

/// <summary>
/// Start-up entries are tested against fakes only: an in-memory "registry" for Windows and a temporary folder for the Mac plist.
/// Nothing here touches the real registry or the real ~/Library/LaunchAgents.
/// </summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "contexo-startup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class FakeRunKey : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Exception? ReadFailure { get; set; }

        public string? Read(string name)
        {
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Values.GetValueOrDefault(name);
        }

        public void Write(string name, string value) => Values[name] = value;

        public void Delete(string name) => Values.Remove(name);
    }

    // --- Windows ------------------------------------------------------------------------------------------------

    private const string ExePath = @"C:\Program Files\Contexo\Contexo.exe";

    [Fact]
    public void Windows_writes_the_quoted_path_and_the_minimized_argument()
    {
        var key = new FakeRunKey();
        var registration = new WindowsStartupRegistration(key, () => ExePath);

        registration.SetEnabled(true);

        Assert.Equal("\"C:\\Program Files\\Contexo\\Contexo.exe\" --minimized", key.Values["Contexo"]);
        Assert.True(registration.IsEnabled);
    }

    [Fact]
    public void Windows_turning_it_off_removes_only_the_Contexo_value()
    {
        var key = new FakeRunKey();
        key.Values["OtherApp"] = "\"C:\\Other\\other.exe\"";
        var registration = new WindowsStartupRegistration(key, () => ExePath);
        registration.SetEnabled(true);

        registration.SetEnabled(false);

        Assert.False(registration.IsEnabled);
        Assert.False(key.Values.ContainsKey("Contexo"));
        Assert.True(key.Values.ContainsKey("OtherApp"));
    }

    [Fact]
    public void Windows_turning_it_off_when_nothing_is_stored_is_harmless()
    {
        var registration = new WindowsStartupRegistration(new FakeRunKey(), () => ExePath);

        registration.SetEnabled(false);

        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void Windows_an_entry_for_another_location_does_not_count_as_enabled_and_is_repaired_by_enabling()
    {
        var key = new FakeRunKey();
        key.Values["Contexo"] = "\"D:\\Old\\Contexo.exe\" --minimized";
        var registration = new WindowsStartupRegistration(key, () => ExePath);

        Assert.False(registration.IsEnabled);

        registration.SetEnabled(true);
        Assert.True(registration.IsEnabled);
        Assert.Equal(WindowsStartupRegistration.BuildCommand(ExePath), key.Values["Contexo"]);
    }

    [Fact]
    public void Windows_path_comparison_ignores_letter_case()
    {
        var key = new FakeRunKey();
        key.Values["Contexo"] = "\"c:\\program files\\contexo\\contexo.exe\" --minimized";

        Assert.True(new WindowsStartupRegistration(key, () => ExePath).IsEnabled);
    }

    [Fact]
    public void Windows_a_registry_that_cannot_be_read_counts_as_off()
    {
        var key = new FakeRunKey { ReadFailure = new UnauthorizedAccessException() };

        Assert.False(new WindowsStartupRegistration(key, () => ExePath).IsEnabled);
    }

    [Theory]
    [InlineData("\"C:\\A B\\x.exe\" --minimized", "C:\\A B\\x.exe")]
    [InlineData("C:\\Apps\\x.exe --minimized", "C:\\Apps\\x.exe")]
    [InlineData("C:\\Apps\\x.exe", "C:\\Apps\\x.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Windows_reads_the_program_path_from_the_stored_command(string? command, string? expected)
    {
        Assert.Equal(expected, WindowsStartupRegistration.ExtractExecutablePath(command));
    }

    [Fact]
    public void Windows_without_a_known_program_path_reports_off_and_refuses_to_enable()
    {
        var registration = new WindowsStartupRegistration(new FakeRunKey(), () => null);

        Assert.False(registration.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => registration.SetEnabled(true));
    }

    // --- macOS --------------------------------------------------------------------------------------------------

    private MacStartupRegistration NewMac(params string[] arguments) => new(_tempDirectory, () => arguments);

    [Fact]
    public void Mac_writes_a_launch_agent_that_runs_at_load_with_the_minimized_argument()
    {
        var registration = NewMac("/Applications/Contexo.app/Contents/MacOS/Contexo", "--minimized");

        registration.SetEnabled(true);

        var plist = Path.Combine(_tempDirectory, "tw.contexo.desktop.plist");
        Assert.True(File.Exists(plist));
        var text = File.ReadAllText(plist);
        Assert.Contains("<key>Label</key>", text);
        Assert.Contains("<string>tw.contexo.desktop</string>", text);
        Assert.Contains("<key>RunAtLoad</key>", text);
        Assert.Contains("<true />", text);
        Assert.Contains("<string>--minimized</string>", text);
        Assert.Equal(
            ["/Applications/Contexo.app/Contents/MacOS/Contexo", "--minimized"],
            MacStartupRegistration.ReadProgramArguments(plist));
        Assert.True(registration.IsEnabled);
        Assert.False(File.Exists(plist + ".tmp"));
    }

    [Fact]
    public void Mac_turning_it_off_deletes_only_its_own_plist()
    {
        Directory.CreateDirectory(_tempDirectory);
        var other = Path.Combine(_tempDirectory, "com.other.plist");
        File.WriteAllText(other, "<plist/>");
        var registration = NewMac("/x/Contexo", "--minimized");
        registration.SetEnabled(true);

        registration.SetEnabled(false);

        Assert.False(registration.IsEnabled);
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "tw.contexo.desktop.plist")));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Mac_turning_it_off_when_there_is_no_plist_is_harmless()
    {
        NewMac("/x/Contexo", "--minimized").SetEnabled(false);
    }

    [Fact]
    public void Mac_a_plist_for_another_program_does_not_count_as_enabled()
    {
        NewMac("/old/Contexo", "--minimized").SetEnabled(true);

        Assert.False(NewMac("/new/Contexo", "--minimized").IsEnabled);
    }

    [Fact]
    public void Mac_an_unreadable_plist_counts_as_off()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, "tw.contexo.desktop.plist"), "not xml at all");

        Assert.False(NewMac("/x/Contexo", "--minimized").IsEnabled);
    }

    [Fact]
    public void Mac_paths_with_special_characters_are_escaped()
    {
        var registration = NewMac("/Users/a&b/<Contexo>", "--minimized");

        registration.SetEnabled(true);

        Assert.True(registration.IsEnabled);
    }

    [Fact]
    public void Mac_started_through_dotnet_passes_the_application_dll()
    {
        Assert.Equal(
            ["/usr/local/share/dotnet/dotnet", "/src/bin/Contexo.dll", "--minimized"],
            MacStartupRegistration.ResolveProgramArguments("/usr/local/share/dotnet/dotnet", "/src/bin/Contexo.dll"));
        Assert.Equal(
            ["/Apps/Contexo", "--minimized"],
            MacStartupRegistration.ResolveProgramArguments("/Apps/Contexo", "/Apps/Contexo.dll"));
        Assert.Null(MacStartupRegistration.ResolveProgramArguments(null, null));
    }

    [Fact]
    public void Mac_without_a_known_program_refuses_to_enable()
    {
        var registration = new MacStartupRegistration(_tempDirectory, () => null);

        Assert.False(registration.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => registration.SetEnabled(true));
    }
}
