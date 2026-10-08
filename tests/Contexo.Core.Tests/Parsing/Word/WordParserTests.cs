using System.Diagnostics;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Word;

namespace Contexo.Core.Tests.Parsing.Word;

public sealed class WordParserTests
{
    private static async Task<ParsedDocument> ParseAsync(byte[] bytes, IReadOnlyList<string>? embeddedPath = null)
    {
        using var stream = new MemoryStream(bytes);
        var result = await new WordParser().ParseAsync(
            new ParseContext(stream, "測試.docx", new ParserOptions(), embeddedPath),
            CancellationToken.None);
        Assert.True(stream.CanRead, "The parser must not dispose the input stream.");
        return result;
    }

    private static string AllText(ParsedDocument document) => string.Join("\n", document.Sections.Select(s => s.Text));

    private static string Path(DocumentSection section) => string.Join(" > ", section.Location.HeadingPath ?? []);

    // ---- 1. headings ----

    [Fact]
    public async Task Three_level_heading_styles_give_heading_paths()
    {
        var builder = new DocxBuilder().AddHeadingStyles();
        builder.Para("前言文字");
        builder.Heading(1, "採購規範");
        builder.Para("第一章內容");
        builder.Heading(2, "驗收");
        builder.Para("驗收內容");
        builder.Heading(3, "驗收標準");
        builder.Para("標準內容");
        builder.Heading(2, "付款");
        builder.Para("付款內容");
        builder.Heading(1, "附則");
        builder.Para("附則內容");

        var document = await ParseAsync(builder.Build());

        Assert.Equal(
            ["", "採購規範", "採購規範 > 驗收", "採購規範 > 驗收 > 驗收標準", "採購規範 > 付款", "附則"],
            document.Sections.Select(Path));
        Assert.Equal(
            ["前言文字", "第一章內容", "驗收內容", "標準內容", "付款內容", "附則內容"],
            document.Sections.Select(s => s.Text));
        Assert.All(document.Sections, s => Assert.Equal(SectionKind.Prose, s.Kind));
        Assert.All(document.Sections, s => Assert.Null(s.Location.Title));
    }

    [Fact]
    public async Task Chinese_style_names_are_headings()
    {
        var builder = new DocxBuilder().AddChineseHeadingStyles();
        builder.Para("第一章", "a3");
        builder.Para("章內容");
        builder.Para("第一節", "a4");
        builder.Para("節內容");

        var document = await ParseAsync(builder.Build());

        Assert.Equal(["第一章", "第一章 > 第一節"], document.Sections.Select(Path));
        Assert.Equal(["章內容", "節內容"], document.Sections.Select(s => s.Text));
    }

    [Fact]
    public async Task Outline_level_and_style_inheritance_are_resolved()
    {
        var builder = new DocxBuilder()
            .AddStyle("Normal", "Normal", isDefault: true)
            .AddStyle("Chapter", "章名", basedOn: "Normal", outline: 0)
            .AddStyle("ChapterChild", "章名子樣式", basedOn: "Chapter")
            .AddStyle("Section", "Heading 2 custom", basedOn: "Normal")
            .AddStyle("HeadingBased", "自訂", basedOn: "Heading2")
            .AddStyle("Heading2", "heading 2", basedOn: "Normal")
            .AddStyle("PlainOverride", "不是標題", basedOn: "Chapter", outline: 9);
        builder.Para("第一章", "Chapter");
        builder.Para("a");
        builder.Para("子樣式標題", "ChapterChild");
        builder.Para("b");
        builder.Para("依名稱繼承", "HeadingBased");
        builder.Para("c");
        builder.Para("外觀像標題但層級是內文", "PlainOverride");

        var document = await ParseAsync(builder.Build());

        Assert.Equal(["第一章", "子樣式標題", "子樣式標題 > 依名稱繼承"], document.Sections.Select(Path));
        Assert.Contains("外觀像標題但層級是內文", document.Sections[^1].Text);
    }

    [Fact]
    public async Task Heading_without_content_is_kept_as_text()
    {
        var builder = new DocxBuilder().AddHeadingStyles();
        builder.Heading(1, "有內容");
        builder.Para("內容");
        builder.Heading(1, "空標題");
        builder.Heading(1, "最後一個");

        var document = await ParseAsync(builder.Build());

        Assert.Equal(["有內容", "", ""], document.Sections.Select(Path));
        Assert.Equal(["內容", "空標題", "最後一個"], document.Sections.Select(s => s.Text));
    }

    // ---- 2. heuristic headings ----

    [Fact]
    public async Task Bold_large_short_paragraphs_are_headings_by_size_rank()
    {
        var builder = new DocxBuilder().DefaultFontSize(22);
        builder.Para("文件總標", bold: true, size: 36);
        builder.Para("這是一段很普通的內文，字級和預設相同。這是一段很普通的內文，字級和預設相同。");
        builder.Para("第一節", bold: true, size: 28);
        builder.Para("第一節的內文也是普通字級，這裡有足夠的字數讓它成為眾數字級。");
        builder.Para("第二節", bold: true, size: 28);
        builder.Para("第二節的內文同樣是普通字級。");

        var document = await ParseAsync(builder.Build());

        Assert.Equal(["文件總標", "文件總標 > 第一節", "文件總標 > 第二節"], document.Sections.Select(Path));
        Assert.StartsWith("這是一段很普通的內文", document.Sections[0].Text);
        Assert.DoesNotContain("第一節", document.Sections[0].Text);
    }

    [Fact]
    public async Task Ordinary_bold_short_sentence_is_not_a_heading()
    {
        var builder = new DocxBuilder().DefaultFontSize(22);
        builder.Para("這是一段很普通的內文，用來撐起眾數字級。這是一段很普通的內文，用來撐起眾數字級。");
        builder.Para("請務必詳讀以下注意事項", bold: true); // bold, same size as the body
        builder.Para("後面的內文。");
        builder.Para("這是一段又長又大又粗的段落，它超過四十個字，因此就算字級比較大也不可以被當成標題來看待，這一點很重要。", bold: true, size: 32);
        builder.Para("結尾內文。");
        builder.Para("大而粗但後面沒有內文的結尾行", bold: true, size: 32);

        var document = await ParseAsync(builder.Build());

        var section = Assert.Single(document.Sections);
        Assert.Equal("", Path(section));
        Assert.Contains("請務必詳讀以下注意事項", section.Text);
        Assert.Contains("大而粗但後面沒有內文的結尾行", section.Text);
    }

    // ---- 3. tracked changes ----

    [Fact]
    public async Task Tracked_changes_are_read_as_accepted()
    {
        var builder = new DocxBuilder();
        builder.Xml(
            DocxBuilder.RunXml("保留 ") +
            "<w:ins w:id=\"1\" w:author=\"a\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:t>新增文字</w:t></w:r></w:ins>" +
            "<w:del w:id=\"2\" w:author=\"a\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:delText>被刪除的文字</w:delText></w:r></w:del>" +
            "<w:moveFrom w:id=\"3\" w:author=\"a\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:t>搬走的文字</w:t></w:r></w:moveFrom>" +
            "<w:moveTo w:id=\"4\" w:author=\"a\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:t>搬來的文字</w:t></w:r></w:moveTo>");

        var document = await ParseAsync(builder.Build());

        var text = AllText(document);
        Assert.Equal("保留 新增文字搬來的文字", text);
        Assert.DoesNotContain("被刪除", text);
        Assert.DoesNotContain("搬走", text);
    }

    // ---- 4. tables ----

    [Fact]
    public async Task Table_with_merged_cells_keeps_colspan_and_rowspan()
    {
        var builder = new DocxBuilder().AddHeadingStyles();
        builder.Heading(1, "報價");
        builder.Table(
            DocxBuilder.Row(DocxBuilder.Cell("品名") + DocxBuilder.Cell("規格", gridSpan: 2), header: true) +
            DocxBuilder.Row(DocxBuilder.Cell("螺絲", vMerge: "restart") + DocxBuilder.Cell("長") + DocxBuilder.Cell("短")) +
            DocxBuilder.Row(DocxBuilder.Cell("", vMerge: "continue") + DocxBuilder.Cell("10") + DocxBuilder.Cell("5")));
        builder.Para("表格後的說明");

        var document = await ParseAsync(builder.Build());

        Assert.Equal(2, document.Sections.Count);
        var table = document.Sections[0];
        Assert.Equal(SectionKind.Table, table.Kind);
        Assert.True(table.KeepWhole);
        Assert.Equal("報價", Path(table));
        Assert.Equal(
            "<table>\n<thead>\n<tr><th>品名</th><th colspan=\"2\">規格</th></tr>\n</thead>\n<tbody>\n" +
            "<tr><td rowspan=\"2\">螺絲</td><td>長</td><td>短</td></tr>\n" +
            "<tr><td>10</td><td>5</td></tr>\n</tbody>\n</table>",
            table.Text);
        Assert.Equal("表格後的說明", document.Sections[1].Text);
        Assert.Equal("報價", Path(document.Sections[1]));
    }

    [Fact]
    public async Task First_row_is_header_when_no_row_is_marked_and_header_rows_are_counted()
    {
        var builder = new DocxBuilder();
        builder.Table(
            DocxBuilder.Row(DocxBuilder.Cell("A") + DocxBuilder.Cell("B")) +
            DocxBuilder.Row(DocxBuilder.Cell("1") + DocxBuilder.Cell("2")));
        builder.Table(
            DocxBuilder.Row(DocxBuilder.Cell("A") + DocxBuilder.Cell("B"), header: true) +
            DocxBuilder.Row(DocxBuilder.Cell("A2") + DocxBuilder.Cell("B2"), header: true) +
            DocxBuilder.Row(DocxBuilder.Cell("1") + DocxBuilder.Cell("2")));

        var document = await ParseAsync(builder.Build());

        Assert.Equal(2, document.Sections.Count);
        Assert.Contains("<thead>\n<tr><th>A</th><th>B</th></tr>\n</thead>", document.Sections[0].Text);
        Assert.Contains("<tbody>\n<tr><td>1</td><td>2</td></tr>", document.Sections[0].Text);
        Assert.Contains("<thead>\n<tr><th>A</th><th>B</th></tr>\n<tr><th>A2</th><th>B2</th></tr>\n</thead>", document.Sections[1].Text);
    }

    [Fact]
    public async Task Nested_table_text_ends_up_in_the_outer_cell()
    {
        var builder = new DocxBuilder();
        var inner =
            $"<w:tbl {DocxBuilder.NamespaceDeclarations}><w:tblPr/>" +
            DocxBuilder.Row(DocxBuilder.Cell("內層甲") + DocxBuilder.Cell("內層乙")) +
            DocxBuilder.Row(DocxBuilder.Cell("內層丙") + DocxBuilder.Cell("內層丁")) +
            "</w:tbl>";
        var outerCell = $"<w:tc><w:tcPr/><w:p>{DocxBuilder.RunXml("外層文字")}</w:p>{inner}<w:p/></w:tc>";
        builder.Table(DocxBuilder.Row(DocxBuilder.Cell("標題") + DocxBuilder.Cell("內容"), header: true) + DocxBuilder.Row(DocxBuilder.Cell("甲") + outerCell));

        var document = await ParseAsync(builder.Build());

        var table = Assert.Single(document.Sections);
        Assert.Contains("外層文字", table.Text);
        foreach (var text in new[] { "內層甲", "內層乙", "內層丙", "內層丁" })
        {
            Assert.Contains(text, table.Text);
        }
    }

    // ---- 5. text boxes ----

    [Fact]
    public async Task Text_box_content_follows_its_paragraph_and_is_not_duplicated()
    {
        const string content = "<w:txbxContent><w:p><w:r><w:t>文字方塊內容</w:t></w:r></w:p><w:p><w:r><w:t>第二行</w:t></w:r></w:p></w:txbxContent>";
        var builder = new DocxBuilder();
        builder.Xml(
            DocxBuilder.RunXml("所在段落") +
            "<w:r><mc:AlternateContent>" +
            "<mc:Choice Requires=\"wps\"><w:drawing><wp:anchor><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">" +
            $"<wps:wsp><wps:txbx>{content}</wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>" +
            $"<mc:Fallback><w:pict><v:shape><v:textbox>{content}</v:textbox></v:shape></w:pict></mc:Fallback>" +
            "</mc:AlternateContent></w:r>");
        builder.Para("下一段");

        var document = await ParseAsync(builder.Build());

        var section = Assert.Single(document.Sections);
        Assert.Equal("所在段落\n文字方塊內容\n第二行\n下一段", section.Text);
    }

    [Fact]
    public async Task Vml_only_text_box_is_read()
    {
        var builder = new DocxBuilder();
        builder.Xml("<w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>舊式文字方塊</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r>");

        var document = await ParseAsync(builder.Build());

        Assert.Equal("舊式文字方塊", AllText(document));
    }

    // ---- 6. toc, header / footer, fields ----

    [Fact]
    public async Task Toc_header_and_footer_are_skipped_and_field_codes_do_not_leak()
    {
        var builder = new DocxBuilder().AddHeadingStyles().AddHeaderAndFooter("頁首雜訊", "頁尾雜訊");

        // Table of contents as a content control.
        builder.AppendBodyXml(
            "<w:sdt><w:sdtPr><w:docPartObj><w:docPartGallery w:val=\"Table of Contents\"/><w:docPartUnique/></w:docPartObj></w:sdtPr>" +
            "<w:sdtContent><w:p><w:r><w:t>目錄項目控制項</w:t></w:r></w:p></w:sdtContent></w:sdt>");

        // Table of contents as a field range spanning paragraphs.
        builder.Xml(
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> TOC \\o \"1-3\" \\h </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>目錄項目一</w:t></w:r>");
        builder.Xml("<w:r><w:t>目錄項目二</w:t></w:r>");
        builder.Xml("<w:r><w:t>目錄項目三</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>");

        builder.Heading(1, "正文");
        builder.Xml(
            DocxBuilder.RunXml("請見") +
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> HYPERLINK \"https://example.com/secret\" </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>官方網站</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            DocxBuilder.RunXml("，第 ") +
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "<w:r><w:t>7</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            DocxBuilder.RunXml(" 頁。") +
            "<w:fldSimple w:instr=\" DATE \\@ yyyy \"><w:r><w:t>2026</w:t></w:r></w:fldSimple>");

        var document = await ParseAsync(builder.Build());

        var section = Assert.Single(document.Sections);
        Assert.Equal("正文", Path(section));
        Assert.Equal("請見官方網站，第 7 頁。2026", section.Text);
        var all = AllText(document);
        foreach (var noise in new[] { "目錄項目", "頁首雜訊", "頁尾雜訊", "HYPERLINK", "example.com", "PAGE", "DATE" })
        {
            Assert.DoesNotContain(noise, all);
        }
    }

    [Fact]
    public async Task Tab_line_break_page_break_and_symbols_are_handled()
    {
        var builder = new DocxBuilder();
        builder.Xml(
            "<w:r><w:t>甲</w:t><w:tab/><w:t>乙</w:t><w:br/><w:t>丙</w:t><w:br w:type=\"page\"/><w:t>丁</w:t><w:cr/><w:t>戊</w:t>" +
            "<w:sym w:font=\"Wingdings\" w:char=\"F0FC\"/><w:sym w:font=\"Arial\" w:char=\"00A9\"/></w:r>");

        var document = await ParseAsync(builder.Build());

        Assert.Equal("甲\t乙\n丙丁\n戊©", AllText(document));
    }

    [Fact]
    public async Task List_items_get_a_dash_prefix()
    {
        var builder = new DocxBuilder();
        builder.Para("說明");
        builder.Para("項目一", list: true);
        builder.Para("項目二", list: true);

        var document = await ParseAsync(builder.Build());

        Assert.Equal("說明\n- 項目一\n- 項目二", AllText(document));
    }

    // ---- 7. notes and comments ----

    [Fact]
    public async Task Footnotes_endnotes_and_comments_are_included()
    {
        var builder = new DocxBuilder().AddHeadingStyles()
            .AddFootnotes((1, "這是註腳內容"))
            .AddEndnotes((1, "這是尾註內容"))
            .AddComments((0, "這是註解內容"));
        builder.Heading(1, "章");
        builder.Xml(
            DocxBuilder.RunXml("有註腳的句子") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>" +
            DocxBuilder.RunXml("，還有尾註") + "<w:r><w:endnoteReference w:id=\"1\"/></w:r>");
        builder.Para("最後一段");

        var document = await ParseAsync(builder.Build());

        var section = Assert.Single(document.Sections);
        Assert.Equal(
            "有註腳的句子，還有尾註\n〔註〕這是註腳內容\n〔註〕這是尾註內容\n最後一段\n〔註解〕這是註解內容",
            section.Text);
    }

    [Fact]
    public async Task Comments_go_into_a_new_section_when_the_document_ends_with_a_table()
    {
        var builder = new DocxBuilder().AddComments((0, "表格旁的註解"));
        builder.Table(DocxBuilder.Row(DocxBuilder.Cell("A")));

        var document = await ParseAsync(builder.Build());

        Assert.Equal([SectionKind.Table, SectionKind.Prose], document.Sections.Select(s => s.Kind));
        Assert.Equal("〔註解〕表格旁的註解", document.Sections[1].Text);
        Assert.DoesNotContain("註解", document.Sections[0].Text);
    }

    // ---- 8. embedded files and images ----

    [Fact]
    public async Task Embedded_workbook_and_image_are_extracted_with_heading_path()
    {
        var builder = new DocxBuilder().AddHeadingStyles();
        var workbook = builder.AddEmbeddedWorkbook();
        var image = builder.AddImage();
        builder.Heading(1, "附件章");
        builder.Para("下列為成本表");
        builder.Xml($"<w:r><w:object><o:OLEObject r:id=\"{workbook}\"/></w:object></w:r>");
        builder.Xml($"<w:r><w:pict><v:shape><v:imagedata r:id=\"{image}\"/></v:shape></w:pict></w:r>");

        var document = await ParseAsync(builder.Build(), embeddedPath: ["外層.pptx"]);

        var file = Assert.Single(document.EmbeddedFiles);
        Assert.Equal(".xlsx", System.IO.Path.GetExtension(file.FileName));
        Assert.NotEmpty(file.Content);
        Assert.Equal(["附件章"], file.ContainerLocation.HeadingPath);
        Assert.Equal(["外層.pptx"], file.ContainerLocation.EmbeddedPath);

        var picture = Assert.Single(document.Images);
        Assert.Equal("image/png", picture.ContentType);
        Assert.Equal(["附件章"], picture.Location.HeadingPath);
        Assert.Equal(["外層.pptx"], picture.Location.EmbeddedPath);
        Assert.NotNull(picture.ContextText);
    }

    [Fact]
    public async Task Unreferenced_embedded_workbook_is_still_returned()
    {
        var builder = new DocxBuilder();
        builder.AddEmbeddedWorkbook();
        builder.Para("內文");

        var document = await ParseAsync(builder.Build());

        var file = Assert.Single(document.EmbeddedFiles);
        Assert.EndsWith(".xlsx", file.FileName);
        Assert.Empty(file.ContainerLocation.HeadingPath!);
    }

    // ---- 9. errors ----

    [Fact]
    public async Task Random_bytes_are_corrupted()
    {
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        bytes[0] = 0x01; // make sure it does not accidentally look like an OLE header

        var error = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(bytes));

        Assert.Equal(DocumentErrorCode.Corrupted, error.Code);
    }

    [Fact]
    public async Task Empty_file_and_zip_without_word_document_are_corrupted()
    {
        var empty = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync([]));
        Assert.Equal(DocumentErrorCode.Corrupted, empty.Code);

        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("hello.txt").Open());
            writer.Write("hello");
        }

        var notDocx = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(zip.ToArray()));
        Assert.Equal(DocumentErrorCode.Corrupted, notDocx.Code);
    }

    [Fact]
    public async Task Ole_header_means_password_protected()
    {
        var bytes = new byte[2048];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);

        var error = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(bytes));

        Assert.Equal(DocumentErrorCode.PasswordProtected, error.Code);
    }

    [Fact]
    public async Task Parser_reads_from_the_start_even_when_the_stream_position_is_elsewhere()
    {
        var builder = new DocxBuilder();
        builder.Para("內文");
        using var stream = new MemoryStream(builder.Build());
        stream.Seek(100, SeekOrigin.Begin);

        var document = await new WordParser().ParseAsync(new ParseContext(stream, "a.docx", new ParserOptions()), CancellationToken.None);

        Assert.Equal("內文", AllText(document));
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var builder = new DocxBuilder();
        builder.Para("內文");
        using var stream = new MemoryStream(builder.Build());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WordParser().ParseAsync(new ParseContext(stream, "a.docx", new ParserOptions()), cts.Token));
    }

    [Fact]
    public void Parser_claims_docx()
    {
        Assert.Equal([".docx"], new WordParser().SupportedExtensions);
    }

    // ---- limits and performance ----

    [Fact]
    public async Task Extra_content_beyond_the_character_limit_is_dropped_with_a_warning()
    {
        var builder = new DocxBuilder().AddHeadingStyles();
        for (var i = 0; i < 50; i++)
        {
            builder.Heading(1, $"章 {i}");
            builder.Para(new string('字', 100));
        }

        using var stream = new MemoryStream(builder.Build());
        var document = await new WordParser().ParseAsync(
            new ParseContext(stream, "a.docx", new ParserOptions { MaxExtractedChars = 1000 }),
            CancellationToken.None);

        Assert.NotEmpty(document.Warnings);
        Assert.InRange(document.Sections.Count, 1, 40);
    }

    [Fact]
    public async Task Thousand_paragraphs_parse_within_two_seconds()
    {
        var builder = new DocxBuilder().AddHeadingStyles();
        for (var i = 0; i < 1000; i++)
        {
            if (i % 50 == 0)
            {
                builder.Heading(1, $"第 {i / 50} 章");
            }

            builder.Para($"這是第 {i} 段的內容，包含一些文字讓解析器有事可做。");
        }

        var bytes = builder.Build();
        var stopwatch = Stopwatch.StartNew();
        var document = await ParseAsync(bytes);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
        Assert.Equal(20, document.Sections.Count);
    }
}
