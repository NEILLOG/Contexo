using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.PowerPoint;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using static Contexo.Core.Tests.Parsing.PowerPoint.PptxBuilder.SlideSpec;

namespace Contexo.Core.Tests.Parsing.PowerPoint;

public sealed class PowerPointParserTests
{
    // 1x1 transparent PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static Task<ParsedDocument> ParseAsync(PptxBuilder builder, ParserOptions? options = null, IReadOnlyList<string>? embeddedPath = null)
    {
        var stream = builder.BuildStream();
        return new PowerPointParser().ParseAsync(
            new ParseContext(stream, "簡報.pptx", options ?? new ParserOptions(), embeddedPath), CancellationToken.None);
    }

    private static DocumentSection Slide(ParsedDocument document, int slide) =>
        Assert.Single(document.Sections, s => s.Kind == SectionKind.Slide && s.Location.Slide == slide);

    private static int IndexOf(string text, string value)
    {
        var index = text.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' not found in: {text}");
        return index;
    }

    [Fact]
    public void Parser_declares_pptx_only()
    {
        Assert.Equal([".pptx"], new PowerPointParser().SupportedExtensions);
    }

    [Fact]
    public async Task Slides_come_out_in_presentation_order_with_titles_and_numbers()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("第一張：公司簡介").TextBox(3, 500_000, 1_500_000, 4_000_000, 500_000, "我們成立於 2001 年");
        builder.AddSlide().Title("第二張：產品").TextBox(3, 500_000, 1_500_000, 4_000_000, 500_000, "產品 A 與產品 B");
        builder.AddSlide().CenteredTitle("第三張：聯絡方式").TextBox(3, 500_000, 1_500_000, 4_000_000, 500_000, "service@example.com");

        var document = await ParseAsync(builder);

        var slides = document.Sections.Where(s => s.Kind == SectionKind.Slide).ToList();
        Assert.Equal(3, slides.Count);
        Assert.Equal([1, 2, 3], slides.Select(s => s.Location.Slide!.Value));
        Assert.Equal(["第一張：公司簡介", "第二張：產品", "第三張：聯絡方式"], slides.Select(s => s.Location.Title));
        Assert.All(slides, s => Assert.True(s.KeepWhole));
        Assert.StartsWith("第一張：公司簡介", slides[0].Text);
        Assert.Contains("我們成立於 2001 年", slides[0].Text);
        Assert.Contains("產品 A 與產品 B", slides[1].Text);
        Assert.Contains("service@example.com", slides[2].Text);
        Assert.Null(slides[0].Location.EmbeddedPath);
    }

    [Fact]
    public async Task Hidden_slides_are_parsed_and_marked_in_the_title()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("公開").TextBox(3, 0, 1_500_000, 1_000_000, 500_000, "公開內容");
        var hidden = builder.AddSlide().Title("備用方案").TextBox(3, 0, 1_500_000, 1_000_000, 500_000, "隱藏內容");
        hidden.Hidden = true;

        var document = await ParseAsync(builder);

        var slide = Slide(document, 2);
        Assert.Equal("備用方案（隱藏）", slide.Location.Title);
        Assert.Contains("隱藏內容", slide.Text);
        Assert.Equal("公開", Slide(document, 1).Location.Title);
    }

    [Fact]
    public async Task Text_follows_reading_order_rows_top_down_then_left_to_right()
    {
        var builder = new PptxBuilder();
        // Declared in scrambled order; the right box sits slightly lower than the left one but on the same row.
        builder.AddSlide()
            .Title("版面")
            .TextBox(5, 500_000, 3_500_000, 3_000_000, 800_000, "左下")
            .TextBox(4, 5_000_000, 1_560_000, 3_000_000, 800_000, "右上")
            .TextBox(3, 500_000, 1_500_000, 3_000_000, 800_000, "左上");

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.True(IndexOf(text, "左上") < IndexOf(text, "右上"));
        Assert.True(IndexOf(text, "右上") < IndexOf(text, "左下"));
    }

    [Fact]
    public async Task Group_children_use_group_coordinates_for_reading_order()
    {
        var builder = new PptxBuilder();
        // Group scale is 2x: children land at (1M,2M) and (3M,2M) on the slide, between the two plain boxes.
        // Without the transform they would sit at y=0 and sort before everything.
        var group = GroupXml(
            10,
            outer: (1_000_000, 2_000_000, 4_000_000, 1_000_000),
            inner: (0, 0, 2_000_000, 500_000),
            PptxBuilder.TextShape(11, "G1", 0, 0, 800_000, 200_000, ["群組左"], null),
            PptxBuilder.TextShape(12, "G2", 1_000_000, 0, 800_000, 200_000, ["群組右"], null));
        builder.AddSlide()
            .Title("群組")
            .TextBox(3, 0, 4_000_000, 2_000_000, 500_000, "下方")
            .Raw(group)
            .TextBox(4, 0, 1_000_000, 2_000_000, 500_000, "上方");

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.True(IndexOf(text, "上方") < IndexOf(text, "群組左"));
        Assert.True(IndexOf(text, "群組左") < IndexOf(text, "群組右"));
        Assert.True(IndexOf(text, "群組右") < IndexOf(text, "下方"));
    }

    [Fact]
    public async Task Nested_group_offsets_are_composed()
    {
        var builder = new PptxBuilder();
        var inner = GroupXml(
            20,
            outer: (500_000, 500_000, 1_000_000, 1_000_000),
            inner: (0, 0, 1_000_000, 1_000_000),
            PptxBuilder.TextShape(21, "Deep", 0, 0, 500_000, 200_000, ["深層"], null));
        var outer = GroupXml(
            10,
            outer: (0, 3_000_000, 2_000_000, 2_000_000),
            inner: (0, 0, 2_000_000, 2_000_000),
            inner);
        builder.AddSlide()
            .Raw(outer)
            .TextBox(3, 0, 3_200_000, 1_000_000, 300_000, "中間")
            .TextBox(4, 0, 5_000_000, 1_000_000, 300_000, "最後")
            .TextBox(5, 0, 1_000_000, 1_000_000, 300_000, "最前");

        var text = Slide(await ParseAsync(builder), 1).Text;

        // Deep child is at y = 3_000_000 + 500_000 = 3_500_000: after 中間 (3.2M), before 最後 (5M).
        Assert.True(IndexOf(text, "最前") < IndexOf(text, "中間"));
        Assert.True(IndexOf(text, "中間") < IndexOf(text, "深層"));
        Assert.True(IndexOf(text, "深層") < IndexOf(text, "最後"));
    }

    [Fact]
    public async Task Placeholders_inherit_their_position_from_the_layout_and_unpositioned_shapes_come_last()
    {
        var builder = new PptxBuilder();
        // Layout puts the body placeholder (idx 1) at y=4M. A placeholder with an unknown idx has no position at all.
        builder.AddSlide()
            .Title("繼承")
            .Placeholder(3, "<p:ph idx=\"1\"/>", "內容預留位置")
            .Placeholder(4, "<p:ph type=\"dgm\" idx=\"77\"/>", "無座標")
            .TextBox(5, 500_000, 5_500_000, 2_000_000, 300_000, "最下方文字框")
            .TextBox(6, 500_000, 2_000_000, 2_000_000, 300_000, "上方文字框");

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.True(IndexOf(text, "上方文字框") < IndexOf(text, "內容預留位置"));
        Assert.True(IndexOf(text, "內容預留位置") < IndexOf(text, "最下方文字框"));
        Assert.True(IndexOf(text, "最下方文字框") < IndexOf(text, "無座標"));
    }

    [Fact]
    public async Task Paragraph_levels_become_indented_bullets_and_paragraphs_stay_separate()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("大綱").TextBox(3, 0, 1_500_000, 3_000_000, 2_000_000, "主題", "\t子項", "\t\t細項", "結尾");

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.Contains("主題\n  - 子項\n    - 細項\n結尾", text);
    }

    [Fact]
    public async Task Flowchart_connectors_become_directed_relations()
    {
        var builder = new PptxBuilder();
        builder.AddSlide()
            .Title("流程", id: 20)
            .TextBox(2, 500_000, 1_500_000, 1_500_000, 600_000, "申請")
            .TextBox(3, 2_500_000, 1_500_000, 1_500_000, 600_000, "主管審核")
            .TextBox(4, 4_500_000, 1_500_000, 1_500_000, 600_000, "採購")
            .Connector(10, 2, 3)
            .Connector(11, 3, 4)
            .Connector(12, 4, null); // only one end attached: ignored

        var document = await ParseAsync(builder);

        var diagram = Assert.Single(document.Sections, s => s.Kind == SectionKind.Diagram);
        Assert.Equal("[申請] --> [主管審核]\n[主管審核] --> [採購]", diagram.Text);
        Assert.Equal(1, diagram.Location.Slide);
        Assert.Equal("流程", diagram.Location.Title);
        // The shapes themselves are still ordinary slide text.
        Assert.Contains("主管審核", Slide(document, 1).Text);
    }

    [Fact]
    public async Task A_connector_with_only_a_head_arrow_is_reversed()
    {
        var builder = new PptxBuilder();
        builder.AddSlide()
            .TextBox(2, 0, 1_000_000, 1_000_000, 500_000, "甲")
            .TextBox(3, 2_000_000, 1_000_000, 1_000_000, 500_000, "乙")
            .TextBox(4, 4_000_000, 1_000_000, 1_000_000, 500_000, "丙")
            .Connector(10, 2, 3, headEnd: "triangle", tailEnd: null)      // arrow at the start: 乙 --> 甲
            .Connector(11, 3, 4, headEnd: "triangle", tailEnd: "triangle") // both ends: keep start --> end
            .Connector(12, 2, 4, headEnd: "none", tailEnd: "arrow");        // explicit none + tail arrow

        var diagram = Assert.Single((await ParseAsync(builder)).Sections, s => s.Kind == SectionKind.Diagram);

        Assert.Equal("[乙] --> [甲]\n[乙] --> [丙]\n[甲] --> [丙]", diagram.Text);
    }

    [Fact]
    public async Task Connector_text_is_written_as_edge_label_and_unlabeled_shapes_get_an_id_name()
    {
        var builder = new PptxBuilder();
        builder.AddSlide()
            .TextBox(2, 0, 1_000_000, 1_000_000, 500_000, "判斷")
            .Raw(PptxBuilder.TextShape(7, "Box", 2_000_000, 1_000_000, 1_000_000, 500_000, [], null))
            .Connector(10, 2, 7, label: "通過");

        var diagram = Assert.Single((await ParseAsync(builder)).Sections, s => s.Kind == SectionKind.Diagram);

        Assert.Equal("[判斷] -->|通過| [圖形7]", diagram.Text);
    }

    [Fact]
    public async Task Slides_without_usable_connectors_have_no_diagram_section()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().TextBox(2, 0, 1_000_000, 1_000_000, 500_000, "孤立").Connector(10, 2, null).Connector(11, null, 2);

        var document = await ParseAsync(builder);

        Assert.DoesNotContain(document.Sections, s => s.Kind == SectionKind.Diagram);
    }

    [Fact]
    public async Task SmartArt_hierarchy_is_written_as_an_indented_list()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("組織").SmartArt(
            9,
            ("1", null, "總經理"),
            ("2", "1", "業務部"),
            ("3", "1", "技術部"),
            ("4", "3", "前端組"));

        var document = await ParseAsync(builder);

        var diagram = Assert.Single(document.Sections, s => s.Kind == SectionKind.Diagram);
        Assert.Equal("SmartArt：\n- 總經理\n  - 業務部\n  - 技術部\n    - 前端組", diagram.Text);
        Assert.DoesNotContain("版面點", diagram.Text);
    }

    [Fact]
    public async Task Merged_table_cells_keep_their_spans_in_html()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("報價").Table(
            5, 500_000, 2_000_000, 3,
            [Cell("項目"), Cell("單價", gridSpan: 2), Cell("", hMerge: true)],
            [Cell("鍵盤", rowSpan: 2), Cell("100"), Cell("含稅")],
            [Cell("", vMerge: true), Cell("200"), Cell("未稅")]);

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.Contains("<th>項目</th><th colspan=\"2\">單價</th>", text);
        Assert.Contains("<td rowspan=\"2\">鍵盤</td><td>100</td><td>含稅</td>", text);
        Assert.Contains("<tr><td>200</td><td>未稅</td></tr>", text);
        Assert.Contains("<thead>", text);
    }

    [Fact]
    public async Task Chart_values_are_read_from_the_cache_as_a_table()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("營收").Chart(
            6, 500_000, 2_000_000, "季度營收", ["第一季", "第二季"],
            ("北區", [120, 150.5]),
            ("南區", [80, 95]));

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.Contains("圖表：季度營收", text);
        Assert.Contains("<th>類別</th><th>北區</th><th>南區</th>", text);
        Assert.Contains("<td>第一季</td><td>120</td><td>80</td>", text);
        Assert.Contains("<td>第二季</td><td>150.5</td><td>95</td>", text);
    }

    [Fact]
    public async Task Chart_without_a_title_is_still_listed()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Chart(6, 0, 1_000_000, null, ["A"], ("S", [1]));

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.Contains("圖表：（無標題）", text);
        Assert.Contains("<td>A</td><td>1</td>", text);
    }

    [Fact]
    public async Task Speaker_notes_become_a_notes_section_without_the_slide_number()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("有備忘稿").TextBox(3, 0, 1_500_000, 1_000_000, 500_000, "投影片文字").Notes("提醒一：先講背景", "提醒二：再講數字");
        builder.AddSlide().Title("沒有備忘稿");

        var document = await ParseAsync(builder);

        var notes = Assert.Single(document.Sections, s => s.Kind == SectionKind.Notes);
        Assert.Equal("提醒一：先講背景\n提醒二：再講數字", notes.Text);
        Assert.Equal(1, notes.Location.Slide);
        Assert.Equal("有備忘稿", notes.Location.Title);
        Assert.DoesNotContain("備忘稿母片文字", notes.Text);
    }

    [Fact]
    public async Task Embedded_docx_and_images_are_collected_with_slide_location_and_context()
    {
        var docx = CreateDocx();
        var builder = new PptxBuilder();
        builder.AddSlide().Title("一般");
        builder.AddSlide().Title("附件頁").EmbeddedDocx(docx).Image(Png);

        var document = await ParseAsync(builder, embeddedPath: ["外層.zip"]);

        var file = Assert.Single(document.EmbeddedFiles);
        Assert.EndsWith(".docx", file.FileName);
        Assert.Equal(docx, file.Content);
        Assert.Equal(2, file.ContainerLocation.Slide);
        Assert.Equal("附件頁", file.ContainerLocation.Title);

        var image = Assert.Single(document.Images);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(2, image.Location.Slide);
        Assert.Equal("附件頁", image.ContextText);
        Assert.Equal(["外層.zip"], Slide(document, 2).Location.EmbeddedPath);
    }

    [Fact]
    public async Task Master_and_layout_text_never_appears()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("正文").TextBox(3, 0, 1_500_000, 1_000_000, 500_000, "只有這個").Notes("備忘");

        var document = await ParseAsync(builder);

        var all = string.Join('\n', document.Sections.Select(s => s.Text));
        Assert.Contains("只有這個", all);
        Assert.DoesNotContain(PptxBuilder.MasterFooterText, all);
        Assert.DoesNotContain(PptxBuilder.MasterCompanyText, all);
        Assert.DoesNotContain(PptxBuilder.LayoutFooterText, all);
        Assert.DoesNotContain("備忘稿母片文字", all);
    }

    [Fact]
    public async Task Footer_date_and_slide_number_placeholders_on_the_slide_are_ignored()
    {
        var builder = new PptxBuilder();
        builder.AddSlide()
            .Title("正文")
            .TextBox(3, 0, 1_500_000, 1_000_000, 500_000, "內容")
            .Placeholder(4, "<p:ph type=\"ftr\" idx=\"11\"/>", "每頁都有的頁尾")
            .Placeholder(5, "<p:ph type=\"sldNum\" idx=\"12\"/>", "7");

        var text = Slide(await ParseAsync(builder), 1).Text;

        Assert.DoesNotContain("每頁都有的頁尾", text);
        Assert.Equal("正文\n\n內容", text);
    }

    [Fact]
    public async Task Slides_without_any_text_do_not_produce_empty_sections()
    {
        var builder = new PptxBuilder();
        builder.AddSlide();
        builder.AddSlide().Title("有字");

        var document = await ParseAsync(builder);

        var section = Assert.Single(document.Sections);
        Assert.Equal(2, section.Location.Slide);
    }

    [Fact]
    public async Task Extracted_characters_are_capped_with_a_warning()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("一").TextBox(3, 0, 1_500_000, 1_000_000, 500_000, new string('甲', 40));
        builder.AddSlide().Title("二").TextBox(3, 0, 1_500_000, 1_000_000, 500_000, new string('乙', 40));

        var document = await ParseAsync(builder, new ParserOptions { MaxExtractedChars = 60 });

        var section = Assert.Single(document.Sections);
        Assert.Equal(1, section.Location.Slide);
        Assert.Single(document.Warnings);
    }

    [Fact]
    public async Task The_input_stream_is_left_open()
    {
        using var stream = new PptxBuilder().BuildStream();
        var parser = new PowerPointParser();

        await parser.ParseAsync(new ParseContext(stream, "a.pptx", new ParserOptions()), CancellationToken.None);

        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public async Task Garbage_bytes_are_reported_as_corrupted()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("this is definitely not a presentation"));

        var ex = await Assert.ThrowsAsync<DocumentParseException>(() =>
            new PowerPointParser().ParseAsync(new ParseContext(stream, "壞.pptx", new ParserOptions()), CancellationToken.None));

        Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
    }

    [Fact]
    public async Task A_truncated_file_is_reported_as_corrupted()
    {
        var builder = new PptxBuilder();
        builder.AddSlide().Title("標題").TextBox(3, 0, 1_500_000, 1_000_000, 500_000, "內容");
        var bytes = builder.Build();
        using var stream = new MemoryStream(bytes[..(bytes.Length / 2)]);

        var ex = await Assert.ThrowsAsync<DocumentParseException>(() =>
            new PowerPointParser().ParseAsync(new ParseContext(stream, "斷.pptx", new ParserOptions()), CancellationToken.None));

        Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
    }

    [Fact]
    public async Task An_empty_stream_is_reported_as_corrupted()
    {
        using var stream = new MemoryStream();

        var ex = await Assert.ThrowsAsync<DocumentParseException>(() =>
            new PowerPointParser().ParseAsync(new ParseContext(stream, "空.pptx", new ParserOptions()), CancellationToken.None));

        Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
    }

    [Fact]
    public async Task An_encrypted_package_is_reported_as_password_protected()
    {
        // Password-protected OOXML is a compound file holding an "EncryptedPackage" stream.
        var bytes = new byte[4096];
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(bytes, 0);
        Encoding.Unicode.GetBytes("EncryptedPackage").CopyTo(bytes, 2048);
        using var stream = new MemoryStream(bytes);

        var ex = await Assert.ThrowsAsync<DocumentParseException>(() =>
            new PowerPointParser().ParseAsync(new ParseContext(stream, "密.pptx", new ParserOptions()), CancellationToken.None));

        Assert.Equal(DocumentErrorCode.PasswordProtected, ex.Code);
    }

    [Fact]
    public async Task A_cancelled_token_stops_parsing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stream = new PptxBuilder().BuildStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PowerPointParser().ParseAsync(new ParseContext(stream, "a.pptx", new ParserOptions()), cts.Token));
    }

    private static byte[] CreateDocx()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("內嵌文件")))));
        }

        return stream.ToArray();
    }
}
