using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace Contexo.Core.Parsing.PowerPoint;

/// <summary>Renders a chart's cached values (what PowerPoint last displayed) as "圖表：{title}" plus a categories x series HTML table.</summary>
internal static class ChartReader
{
    private const string ChartNamespace = "http://schemas.openxmlformats.org/drawingml/2006/chart";

    public static string? Read(ChartPart chartPart)
    {
        var chart = chartPart.ChartSpace?.GetFirstChild<C.Chart>();
        if (chart is null)
        {
            return null;
        }

        var title = SlideReader.Collapse(string.Concat(chart.Title?.Descendants<A.Text>().Select(t => t.Text) ?? []));
        var heading = "圖表：" + (title.Length > 0 ? title : "（無標題）");

        var series = chart.PlotArea?.Descendants().Where(e => e.LocalName == "ser" && e.NamespaceUri == ChartNamespace).ToList() ?? [];
        var columns = new List<(string Name, Dictionary<int, string> Values)>();
        Dictionary<int, string>? categories = null;

        foreach (var ser in series)
        {
            // Scatter charts keep their X values in xVal / Y values in yVal.
            var categoryNode = Child(ser, "cat") ?? Child(ser, "xVal");
            var valueNode = Child(ser, "val") ?? Child(ser, "yVal");
            if (categories is null && categoryNode is not null)
            {
                categories = Points(categoryNode);
            }

            var name = Child(ser, "tx")?.Descendants().FirstOrDefault(e => e.LocalName == "v")?.InnerText ?? $"數列{columns.Count + 1}";
            columns.Add((name, valueNode is null ? [] : Points(valueNode)));
        }

        if (columns.Count == 0)
        {
            return heading;
        }

        categories ??= [];
        var rowCount = 0;
        foreach (var index in categories.Keys.Concat(columns.SelectMany(c => c.Values.Keys)))
        {
            rowCount = Math.Max(rowCount, index + 1);
        }

        var cells = new List<TableCell> { new(0, 0, "類別") };
        for (var c = 0; c < columns.Count; c++)
        {
            cells.Add(new TableCell(0, c + 1, columns[c].Name));
        }

        for (var r = 0; r < rowCount; r++)
        {
            cells.Add(new TableCell(r + 1, 0, categories.GetValueOrDefault(r, string.Empty)));
            for (var c = 0; c < columns.Count; c++)
            {
                if (columns[c].Values.TryGetValue(r, out var value))
                {
                    cells.Add(new TableCell(r + 1, c + 1, value));
                }
            }
        }

        var table = new TableModel(rowCount + 1, columns.Count + 1, cells, HeaderRowCount: 1);
        return heading + "\n" + HtmlTableRenderer.Render(table);
    }

    private static OpenXmlElement? Child(OpenXmlElement parent, string localName) =>
        parent.ChildElements.FirstOrDefault(e => e.LocalName == localName);

    /// <summary>Cached points by index. For multi-level categories only the innermost (first) level is used.</summary>
    private static Dictionary<int, string> Points(OpenXmlElement node)
    {
        var scope = node.Descendants().FirstOrDefault(e => e.LocalName == "lvl") ?? node;
        var result = new Dictionary<int, string>();
        foreach (var pt in scope.Descendants().Where(e => e.LocalName == "pt"))
        {
            var idx = pt.GetAttributes().FirstOrDefault(a => a.LocalName == "idx").Value;
            var value = pt.ChildElements.FirstOrDefault(e => e.LocalName == "v");
            if (int.TryParse(idx, out var index) && index >= 0 && value is not null)
            {
                result[index] = value.InnerText;
            }
        }

        return result;
    }
}
