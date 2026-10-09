using Contexo.App.About;

namespace Contexo.App.Tests.About;

public sealed class ProblemReportExportTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "contexo-fake-desktop");

    private static ProblemReportExport Create(FakeDiagnosticsExporter exporter, string? pickedFolder, FakeShellLauncher? launcher = null, FakeFilePicker? picker = null) =>
        new(exporter, picker ?? new FakeFilePicker(pickedFolder), launcher ?? new FakeShellLauncher(), desktopPath: Desktop);

    [Fact]
    public void Defaults_match_the_mockup()
    {
        var flow = Create(new FakeDiagnosticsExporter(), Desktop);

        var options = flow.CurrentOptions;

        Assert.True(options.IncludeSettings);
        Assert.True(options.IncludeRecentLogs);
        Assert.Equal(7, options.LogDays);
        Assert.True(options.IncludeFailedFileList);
        Assert.False(options.IncludeFullPaths);
    }

    [Fact]
    public async Task Export_passes_the_ticked_options_and_describes_the_result_for_the_desktop()
    {
        var exporter = new FakeDiagnosticsExporter { SizeBytes = 1_258_291 };
        var flow = Create(exporter, Desktop);
        flow.IncludeSettings = false;
        flow.IncludeFullPaths = true;

        await flow.ExportCommand.ExecuteAsync(null);

        Assert.Equal(Desktop, exporter.Directory);
        Assert.False(exporter.Options!.IncludeSettings);
        Assert.True(exporter.Options.IncludeFullPaths);
        Assert.Equal("已儲存到桌面：Contexo問題回報_20261008_0004.zip（1.2 MB）", flow.ResultText);
        Assert.True(flow.HasResult);
        Assert.False(flow.HasError);
        Assert.False(flow.IsExporting);
    }

    [Fact]
    public async Task Another_folder_is_named_in_the_result()
    {
        var other = Path.Combine(Path.GetTempPath(), "給管理者");
        var flow = Create(new FakeDiagnosticsExporter { SizeBytes = 200_000 }, other);

        await flow.ExportCommand.ExecuteAsync(null);

        Assert.Equal("已儲存到「給管理者」資料夾：Contexo問題回報_20261008_0004.zip（196 KB）", flow.ResultText);
    }

    [Fact]
    public async Task Cancelling_the_folder_picker_does_nothing()
    {
        var exporter = new FakeDiagnosticsExporter();
        var flow = Create(exporter, pickedFolder: null);

        await flow.ExportCommand.ExecuteAsync(null);

        Assert.Null(exporter.Options);
        Assert.False(flow.HasResult);
        Assert.False(flow.HasError);
    }

    [Fact]
    public async Task Open_folder_reveals_the_zip()
    {
        var launcher = new FakeShellLauncher();
        var flow = Create(new FakeDiagnosticsExporter(), Desktop, launcher);
        flow.OpenFolderCommand.Execute(null);
        Assert.Empty(launcher.Revealed);

        await flow.ExportCommand.ExecuteAsync(null);
        flow.OpenFolderCommand.Execute(null);

        Assert.Equal([Path.Combine(Desktop, "Contexo問題回報_20261008_0004.zip")], launcher.Revealed);
    }

    [Fact]
    public async Task A_running_export_can_be_cancelled()
    {
        var exporter = new FakeDiagnosticsExporter { Gate = new TaskCompletionSource().Task };
        var flow = Create(exporter, Desktop);

        var running = flow.ExportCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => flow.IsExporting && exporter.Options is not null);
        Assert.False(flow.ExportCommand.CanExecute(null));
        flow.CancelExportCommand.Execute(null);
        await running;

        Assert.False(flow.IsExporting);
        Assert.False(flow.HasResult);
        Assert.Contains("取消", flow.ErrorMessage);
        Assert.True(flow.ExportCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "沒有權限")]
    [InlineData(typeof(IOException), "存檔時發生問題")]
    [InlineData(typeof(InvalidOperationException), "匯出沒有成功")]
    public async Task Failures_become_plain_messages(Type exceptionType, string expectedStart)
    {
        var exporter = new FakeDiagnosticsExporter { Failure = (Exception)Activator.CreateInstance(exceptionType, "secret technical detail")! };
        var flow = Create(exporter, Desktop);

        await flow.ExportCommand.ExecuteAsync(null);

        Assert.StartsWith(expectedStart, flow.ErrorMessage);
        Assert.DoesNotContain("technical", flow.ErrorMessage);
        Assert.False(flow.HasResult);
        Assert.False(flow.IsExporting);
    }

    [Fact]
    public async Task A_new_export_clears_the_previous_result()
    {
        var exporter = new FakeDiagnosticsExporter();
        var flow = Create(exporter, Desktop);
        await flow.ExportCommand.ExecuteAsync(null);
        Assert.True(flow.HasResult);

        exporter.Failure = new IOException();
        await flow.ExportCommand.ExecuteAsync(null);

        Assert.False(flow.HasResult);
        Assert.True(flow.HasError);
    }

    [Theory]
    [InlineData(0, "1 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(1_258_291, "1.2 MB")]
    public void Sizes_are_shown_in_kb_or_mb(long bytes, string expected)
    {
        Assert.Equal(expected, ProblemReportExport.FormatSize(bytes));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition");
            await Task.Delay(10, CancellationToken.None);
        }
    }
}
