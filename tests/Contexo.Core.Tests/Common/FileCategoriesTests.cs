using Contexo.Core.Abstractions;
using Contexo.Core.Common;

namespace Contexo.Core.Tests.Common;

public sealed class FileCategoriesTests
{
    [Theory]
    [InlineData(".docx", FileCategory.Documents)]
    [InlineData(".txt", FileCategory.Documents)]
    [InlineData(".md", FileCategory.Documents)]
    [InlineData(".markdown", FileCategory.Documents)]
    [InlineData(".json", FileCategory.Documents)]
    [InlineData(".xml", FileCategory.Documents)]
    [InlineData(".log", FileCategory.Documents)]
    [InlineData(".html", FileCategory.Documents)]
    [InlineData(".htm", FileCategory.Documents)]
    [InlineData(".rtf", FileCategory.Documents)]
    [InlineData(".pptx", FileCategory.Presentations)]
    [InlineData(".xlsx", FileCategory.Spreadsheets)]
    [InlineData(".xlsm", FileCategory.Spreadsheets)]
    [InlineData(".csv", FileCategory.Spreadsheets)]
    [InlineData(".pdf", FileCategory.Pdf)]
    [InlineData(".DOCX", FileCategory.Documents)]
    [InlineData(".Pdf", FileCategory.Pdf)]
    public void Extensions_map_to_their_category(string extension, FileCategory expected)
    {
        Assert.Equal(expected, FileCategories.GetCategory(extension));
        Assert.Contains(extension.ToLowerInvariant(), FileCategories.GetExtensions(expected));
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".exe")]
    [InlineData(".png")]
    [InlineData("")]
    public void Unknown_extensions_have_no_category(string extension) =>
        Assert.Null(FileCategories.GetCategory(extension));

    [Fact]
    public void Email_and_images_have_no_extensions_in_version_1()
    {
        Assert.Empty(FileCategories.GetExtensions(FileCategory.Email));
        Assert.Empty(FileCategories.GetExtensions(FileCategory.Images));
    }

    [Fact]
    public void Extensions_of_several_categories_are_combined()
    {
        var extensions = FileCategories.GetExtensions([FileCategory.Pdf, FileCategory.Presentations, FileCategory.Email]);

        Assert.Equal(new[] { ".pdf", ".pptx" }, extensions.Order().ToArray());
        Assert.Contains(".PDF", extensions);
    }

    [Theory]
    [InlineData("~$報價單.docx")]
    [InlineData("work.tmp")]
    [InlineData("捷徑.lnk")]
    [InlineData("setup.exe")]
    [InlineData("library.dll")]
    [InlineData("driver.sys")]
    [InlineData("boot.ini")]
    [InlineData("cache.db")]
    [InlineData("cache.db-wal")]
    [InlineData("cache.db-shm")]
    [InlineData("desktop.ini")]
    [InlineData("Thumbs.db")]
    [InlineData("thumbs.db")]
    [InlineData(".DS_Store")]
    [InlineData("WORK.TMP")]
    public void Built_in_excluded_files(string fileName)
    {
        Assert.True(FileCategories.IsBuiltInExcludedFile(fileName));
        Assert.True(FileCategories.IsBuiltInExcludedFile(Path.Combine("folder", "sub", fileName)));
    }

    [Theory]
    [InlineData("報價單.docx")]
    [InlineData("data.csv")]
    [InlineData("notes.txt")]
    [InlineData("README")]
    [InlineData("a~$b.docx")]
    public void Normal_files_are_not_excluded(string fileName) =>
        Assert.False(FileCategories.IsBuiltInExcludedFile(Path.Combine("folder", fileName)));

    [Theory]
    [InlineData(".git")]
    [InlineData(".svn")]
    [InlineData(".hg")]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".vs")]
    [InlineData(".idea")]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    [InlineData(".anything")]
    [InlineData("Node_Modules")]
    public void Built_in_excluded_directories(string name) =>
        Assert.True(FileCategories.IsBuiltInExcludedDirectory(name));

    [Theory]
    [InlineData("文件")]
    [InlineData("2026 報價")]
    [InlineData("binary")]
    [InlineData("")]
    public void Normal_directories_are_not_excluded(string name) =>
        Assert.False(FileCategories.IsBuiltInExcludedDirectory(name));

    [Fact]
    public void Hidden_files_and_folders_are_detected()
    {
        var root = Directory.CreateTempSubdirectory("contexo-cat-");
        try
        {
            var visible = new FileInfo(Path.Combine(root.FullName, "visible.txt"));
            File.WriteAllText(visible.FullName, "x");
            var hidden = new FileInfo(Path.Combine(root.FullName, ".hidden.txt"));
            File.WriteAllText(hidden.FullName, "x");
            var hiddenDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "hidden-dir"));

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(hidden.FullName, FileAttributes.Hidden);
                File.SetAttributes(hiddenDirectory.FullName, FileAttributes.Hidden | FileAttributes.Directory);
            }
            else
            {
                // Unix: a leading dot marks the file as hidden.
                hiddenDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, ".hidden-dir"));
            }

            visible.Refresh();
            hidden.Refresh();
            hiddenDirectory.Refresh();

            Assert.False(FileCategories.IsHiddenOrSystem(visible));
            Assert.True(FileCategories.IsHiddenOrSystem(hidden));
            Assert.True(FileCategories.IsHiddenOrSystem(hiddenDirectory));
            Assert.False(FileCategories.IsHiddenOrSystem(root));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
