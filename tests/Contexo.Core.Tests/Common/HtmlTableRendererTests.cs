using Contexo.Core.Abstractions;
using Contexo.Core.Common;

namespace Contexo.Core.Tests.Common;

public sealed class HtmlTableRendererTests
{
    [Fact]
    public void Header_rows_go_into_thead_with_th_and_the_rest_into_tbody()
    {
        var table = new TableModel(
            RowCount: 3,
            ColumnCount: 2,
            Cells:
            [
                new TableCell(0, 0, "品名"), new TableCell(0, 1, "金額"),
                new TableCell(1, 0, "桌子"), new TableCell(1, 1, "100"),
                new TableCell(2, 0, "椅子"), new TableCell(2, 1, "50"),
            ],
            HeaderRowCount: 1);

        var html = HtmlTableRenderer.Render(table);

        Assert.Equal(
            "<table>\n" +
            "<thead>\n<tr><th>品名</th><th>金額</th></tr>\n</thead>\n" +
            "<tbody>\n<tr><td>桌子</td><td>100</td></tr>\n<tr><td>椅子</td><td>50</td></tr>\n</tbody>\n" +
            "</table>",
            html);
    }

    [Fact]
    public void Without_header_rows_there_is_no_thead()
    {
        var table = new TableModel(1, 1, [new TableCell(0, 0, "x")]);

        var html = HtmlTableRenderer.Render(table);

        Assert.DoesNotContain("<thead>", html);
        Assert.DoesNotContain("<th>", html);
        Assert.Contains("<tbody>\n<tr><td>x</td></tr>\n</tbody>", html);
    }

    [Fact]
    public void Merged_cells_write_rowspan_and_colspan_only_when_greater_than_one_and_skip_covered_positions()
    {
        // | A (colspan 2) |
        // | B (rowspan 2) | C |
        // |               | D |
        var table = new TableModel(
            RowCount: 3,
            ColumnCount: 2,
            Cells:
            [
                new TableCell(0, 0, "A", ColSpan: 2),
                new TableCell(1, 0, "B", RowSpan: 2),
                new TableCell(1, 1, "C"),
                new TableCell(2, 1, "D"),
            ],
            HeaderRowCount: 1);

        var html = HtmlTableRenderer.Render(table);

        Assert.Equal(
            "<table>\n" +
            "<thead>\n<tr><th colspan=\"2\">A</th></tr>\n</thead>\n" +
            "<tbody>\n<tr><td rowspan=\"2\">B</td><td>C</td></tr>\n<tr><td>D</td></tr>\n</tbody>\n" +
            "</table>",
            html);
    }

    [Fact]
    public void Plain_cells_have_no_span_attributes_and_nothing_is_styled()
    {
        var table = new TableModel(1, 2, [new TableCell(0, 0, "a"), new TableCell(0, 1, "b")]);

        var html = HtmlTableRenderer.Render(table);

        Assert.DoesNotContain("span", html);
        Assert.DoesNotContain("style", html);
    }

    [Fact]
    public void Text_is_html_encoded()
    {
        var table = new TableModel(1, 1, [new TableCell(0, 0, "<b>A&B</b> \"x\"")]);

        var html = HtmlTableRenderer.Render(table);

        Assert.Contains("<td>&lt;b&gt;A&amp;B&lt;/b&gt; &quot;x&quot;</td>", html);
    }

    [Fact]
    public void Line_breaks_become_br()
    {
        var table = new TableModel(1, 1, [new TableCell(0, 0, "第一行\n第二行\r\n第三行\r第四行")]);

        var html = HtmlTableRenderer.Render(table);

        Assert.Contains("<td>第一行<br>第二行<br>第三行<br>第四行</td>", html);
        Assert.DoesNotContain('\r', html);
    }

    [Fact]
    public void Caption_is_written_and_encoded()
    {
        var table = new TableModel(1, 1, [new TableCell(0, 0, "x")], Caption: "表 1 <總表>");

        var html = HtmlTableRenderer.Render(table);

        Assert.StartsWith("<table>\n<caption>表 1 &lt;總表&gt;</caption>\n", html);
    }

    [Fact]
    public void Positions_missing_from_the_model_are_rendered_as_empty_cells_to_keep_columns_aligned()
    {
        var table = new TableModel(2, 3, [new TableCell(0, 0, "a"), new TableCell(0, 2, "c"), new TableCell(1, 1, "e")]);

        var html = HtmlTableRenderer.Render(table);

        Assert.Contains("<tr><td>a</td><td></td><td>c</td></tr>", html);
        Assert.Contains("<tr><td></td><td>e</td><td></td></tr>", html);
    }

    [Fact]
    public void Header_row_count_larger_than_the_table_produces_no_tbody()
    {
        var table = new TableModel(1, 1, [new TableCell(0, 0, "h")], HeaderRowCount: 5);

        var html = HtmlTableRenderer.Render(table);

        Assert.Contains("<thead>", html);
        Assert.DoesNotContain("<tbody>", html);
    }
}
