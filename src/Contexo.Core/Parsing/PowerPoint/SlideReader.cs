using System.Text;
using System.Text.RegularExpressions;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Contexo.Core.Parsing.PowerPoint;

/// <summary>What <see cref="SlideReader"/> found on one slide.</summary>
/// <param name="Title">Text of the title placeholder, whitespace collapsed; null when the slide has none.</param>
/// <param name="BodyText">Title and all other visible text in reading order; empty when the slide has no text.</param>
/// <param name="DiagramText">Mermaid-style connector lines and SmartArt lists; empty when there are none.</param>
internal sealed record SlideContent(string? Title, string BodyText, string DiagramText);

/// <summary>Reads the shapes of one slide: text in reading order, tables, charts, connectors and SmartArt.</summary>
internal sealed partial class SlideReader
{
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const long RowToleranceMin = 91_440;   // 0.1 inch
    private const long RowToleranceMax = 457_200;  // 0.5 inch

    private readonly SlidePart _slidePart;
    private readonly int _slideNumber;
    private readonly List<string> _warnings;
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<uint, string> _shapeText = [];
    private readonly List<ConnectorInfo> _connectors = [];
    private readonly List<string> _smartArts = [];
    private string? _title;
    private int _order;

    private SlideReader(SlidePart slidePart, int slideNumber, List<string> warnings)
    {
        _slidePart = slidePart;
        _slideNumber = slideNumber;
        _warnings = warnings;
    }

    public static SlideContent Read(SlidePart slidePart, int slideNumber, List<string> warnings)
    {
        var reader = new SlideReader(slidePart, slideNumber, warnings);
        var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
        if (tree is not null)
        {
            reader.Walk(tree, Affine.Identity);
        }

        return reader.Build();
    }

    /// <summary>Text of all paragraphs of the notes slide's body placeholder(s); null when there is none.</summary>
    public static string? ReadNotes(SlidePart slidePart)
    {
        var tree = slidePart.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree;
        if (tree is null)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var shape in tree.Elements<P.Shape>())
        {
            var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
            if (ph is null || PlaceholderKey(ph) != "body" || shape.TextBody is null)
            {
                continue;
            }

            foreach (var paragraph in shape.TextBody.Elements<A.Paragraph>())
            {
                var text = ParagraphText(paragraph).Trim();
                if (text.Length > 0)
                {
                    lines.Add(text);
                }
            }
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private SlideContent Build()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(_title))
        {
            parts.Add(_title);
        }

        parts.AddRange(OrderForReading(_blocks).Select(b => b.Text));
        var body = string.Join("\n\n", parts);

        var diagramParts = new List<string>();
        var lines = BuildConnectorLines();
        if (lines.Count > 0)
        {
            diagramParts.Add(string.Join('\n', lines));
        }

        diagramParts.AddRange(_smartArts);
        return new SlideContent(_title, body, string.Join("\n\n", diagramParts));
    }

    // ---------------------------------------------------------------- shape tree

    private void Walk(OpenXmlElement parent, Affine transform)
    {
        foreach (var child in parent.ChildElements)
        {
            switch (child)
            {
                case P.Shape shape:
                    ReadShape(shape, transform);
                    break;
                case P.GroupShape group:
                    ReadGroup(group, transform);
                    break;
                case P.ConnectionShape connector:
                    ReadConnector(connector);
                    break;
                case P.GraphicFrame frame:
                    ReadGraphicFrame(frame, transform);
                    break;
                case P.Picture picture:
                    Register(picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Id?.Value, null);
                    break;
                default:
                    if (child.LocalName == "AlternateContent")
                    {
                        var choice = child.ChildElements.FirstOrDefault(e => e.LocalName == "Choice")
                            ?? child.ChildElements.FirstOrDefault(e => e.LocalName == "Fallback");
                        if (choice is not null)
                        {
                            Walk(choice, transform);
                        }
                    }

                    break;
            }
        }
    }

    private void ReadGroup(P.GroupShape group, Affine transform)
    {
        Register(group.NonVisualGroupShapeProperties?.NonVisualDrawingProperties?.Id?.Value, null);

        var next = transform;
        var xfrm = group.GroupShapeProperties?.TransformGroup;
        if (xfrm?.Offset?.X?.Value is { } offX && xfrm.Offset.Y?.Value is { } offY
            && xfrm.Extents?.Cx?.Value is { } extCx && xfrm.Extents.Cy?.Value is { } extCy)
        {
            var chOffX = xfrm.ChildOffset?.X?.Value ?? offX;
            var chOffY = xfrm.ChildOffset?.Y?.Value ?? offY;
            var chExtCx = xfrm.ChildExtents?.Cx?.Value ?? extCx;
            var chExtCy = xfrm.ChildExtents?.Cy?.Value ?? extCy;

            var scaleX = chExtCx == 0 ? 1.0 : (double)extCx / chExtCx;
            var scaleY = chExtCy == 0 ? 1.0 : (double)extCy / chExtCy;
            var sx = transform.Sx * scaleX;
            var sy = transform.Sy * scaleY;
            next = new Affine(sx, sy, transform.X(offX) - chOffX * sx, transform.Y(offY) - chOffY * sy);
        }

        Walk(group, next);
    }

    private void ReadShape(P.Shape shape, Affine transform)
    {
        var nv = shape.NonVisualShapeProperties;
        var id = nv?.NonVisualDrawingProperties?.Id?.Value;
        var ph = nv?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
        var lines = ReadLines(shape.TextBody, withLevels: true);
        var flat = Collapse(string.Join(' ', lines));
        Register(id, flat.Length > 0 ? flat : null);

        if (lines.Count == 0)
        {
            return;
        }

        var key = ph is null ? null : PlaceholderKey(ph);
        if (key is "dt" or "ftr" or "sldNum")
        {
            // Date, footer and slide number are repeated noise on every slide.
            return;
        }

        if (key == "title" && _title is null)
        {
            _title = flat;
            return;
        }

        var (x, y, height) = ShapeBounds(shape, ph, transform);
        _blocks.Add(new Block(x, y, height, _order++, string.Join('\n', lines)));
    }

    private void ReadConnector(P.ConnectionShape connector)
    {
        var id = connector.NonVisualConnectionShapeProperties?.NonVisualDrawingProperties?.Id?.Value;
        Register(id, null);

        var drawing = connector.NonVisualConnectionShapeProperties?.NonVisualConnectorShapeDrawingProperties;
        var start = drawing?.StartConnection?.Id?.Value;
        var end = drawing?.EndConnection?.Id?.Value;
        if (start is null || end is null)
        {
            return;
        }

        var outline = connector.ShapeProperties?.GetFirstChild<A.Outline>();
        var headArrow = HasLineEnd(outline?.GetFirstChild<A.HeadEnd>()?.Type?.InnerText);
        var tailArrow = HasLineEnd(outline?.GetFirstChild<A.TailEnd>()?.Type?.InnerText);
        // A connector has no text body in the schema, but some producers write one; read any a:t below it by name.
        var label = Collapse(string.Concat(connector.Descendants()
            .Where(e => e.LocalName == "t" && e.NamespaceUri == DrawingNamespace)
            .Select(e => e.InnerText)));

        // The arrow at the head decorates the line's start. Only a head arrow means the relation runs end -> start.
        var reversed = headArrow && !tailArrow;
        _connectors.Add(new ConnectorInfo(start.Value, end.Value, reversed, label));
    }

    private void ReadGraphicFrame(P.GraphicFrame frame, Affine transform)
    {
        Register(frame.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Id?.Value, null);

        var graphicData = frame.Graphic?.GraphicData;
        if (graphicData is null)
        {
            return;
        }

        var (x, y, height) = FrameBounds(frame, transform);

        var table = graphicData.GetFirstChild<A.Table>();
        if (table is not null)
        {
            var html = HtmlTableRenderer.Render(ReadTable(table));
            _blocks.Add(new Block(x, y, height, _order++, html));
            return;
        }

        var chartRef = graphicData.Descendants<DocumentFormat.OpenXml.Drawing.Charts.ChartReference>().FirstOrDefault();
        if (chartRef?.Id?.Value is { } chartId)
        {
            try
            {
                if (_slidePart.GetPartById(chartId) is ChartPart chartPart)
                {
                    var text = ChartReader.Read(chartPart);
                    if (text is not null)
                    {
                        _blocks.Add(new Block(x, y, height, _order++, text));
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException or OpenXmlPackageException)
            {
                _warnings.Add($"第 {_slideNumber} 張投影片的圖表無法讀取。");
            }

            return;
        }

        var relIds = graphicData.Descendants<DocumentFormat.OpenXml.Drawing.Diagrams.RelationshipIds>().FirstOrDefault();
        if (relIds?.DataPart?.Value is { } dataId)
        {
            try
            {
                if (_slidePart.GetPartById(dataId) is DiagramDataPart dataPart)
                {
                    var text = SmartArtReader.Read(dataPart);
                    if (text is not null)
                    {
                        _smartArts.Add("SmartArt：\n" + text);
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException or OpenXmlPackageException)
            {
                _warnings.Add($"第 {_slideNumber} 張投影片的 SmartArt 無法讀取。");
            }
        }
    }

    private void Register(uint? id, string? text)
    {
        if (id is { } value && text is not null)
        {
            _shapeText.TryAdd(value, text);
        }
    }

    // ---------------------------------------------------------------- connectors

    private List<string> BuildConnectorLines()
    {
        var lines = new List<string>();
        foreach (var c in _connectors)
        {
            var from = c.Reversed ? c.EndId : c.StartId;
            var to = c.Reversed ? c.StartId : c.EndId;
            var arrow = c.Label.Length > 0 ? $"-->|{c.Label}|" : "-->";
            lines.Add($"[{NameOf(from)}] {arrow} [{NameOf(to)}]");
        }

        return lines;
    }

    private string NameOf(uint id) => _shapeText.TryGetValue(id, out var text) ? text : $"圖形{id}";

    private static bool HasLineEnd(string? type) => !string.IsNullOrEmpty(type) && type != "none";

    // ---------------------------------------------------------------- text

    private static List<string> ReadLines(OpenXmlElement? textBody, bool withLevels)
    {
        var lines = new List<string>();
        if (textBody is null)
        {
            return lines;
        }

        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            var text = ParagraphText(paragraph).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var level = withLevels ? paragraph.ParagraphProperties?.Level?.Value ?? 0 : 0;
            lines.Add(level > 0 ? new string(' ', level * 2) + "- " + text : text);
        }

        return lines;
    }

    /// <summary>Joins the runs of one paragraph from the individual a:t elements (InnerText would glue paragraphs together).</summary>
    internal static string ParagraphText(A.Paragraph paragraph)
    {
        var sb = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            if (element is A.Text text)
            {
                sb.Append(text.Text);
            }
            else if (element is A.Break)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    internal static string Collapse(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    // ---------------------------------------------------------------- tables

    private static TableModel ReadTable(A.Table table)
    {
        var gridColumns = table.TableGrid?.Elements<A.GridColumn>().Count() ?? 0;
        var rows = table.Elements<A.TableRow>().ToList();
        var cells = new List<TableCell>();
        var columnCount = gridColumns;

        for (var r = 0; r < rows.Count; r++)
        {
            var c = 0;
            foreach (var cell in rows[r].Elements<A.TableCell>())
            {
                // Cells covered by a merge are still present in the XML, flagged hMerge / vMerge.
                if (cell.HorizontalMerge?.Value != true && cell.VerticalMerge?.Value != true)
                {
                    var colSpan = Math.Max(cell.GridSpan?.Value ?? 1, 1);
                    var rowSpan = Math.Max(cell.RowSpan?.Value ?? 1, 1);
                    var text = string.Join('\n', ReadLines(cell.TextBody, withLevels: false));
                    cells.Add(new TableCell(r, c, text, rowSpan, colSpan));
                }

                c++;
            }

            columnCount = Math.Max(columnCount, c);
        }

        // Keep spans inside the grid so the renderer never emits cells outside it.
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var colSpan = Math.Min(cell.ColSpan, Math.Max(columnCount - cell.Column, 1));
            var rowSpan = Math.Min(cell.RowSpan, Math.Max(rows.Count - cell.Row, 1));
            if (colSpan != cell.ColSpan || rowSpan != cell.RowSpan)
            {
                cells[i] = cell with { ColSpan = colSpan, RowSpan = rowSpan };
            }
        }

        var header = table.TableProperties?.FirstRow?.Value == true && rows.Count > 1 ? 1 : 0;
        return new TableModel(rows.Count, columnCount, cells, header);
    }

    // ---------------------------------------------------------------- geometry and reading order

    private (long? X, long? Y, long? Height) ShapeBounds(P.Shape shape, P.PlaceholderShape? ph, Affine transform)
    {
        var xfrm = shape.ShapeProperties?.Transform2D;
        if (xfrm?.Offset?.X?.Value is { } x && xfrm.Offset.Y?.Value is { } y)
        {
            return (Scale(transform.X(x)), Scale(transform.Y(y)), xfrm.Extents?.Cy?.Value is { } cy ? Scale(cy * transform.Sy) : null);
        }

        // Placeholders usually carry no position of their own: inherit from the layout, then the master.
        if (ph is not null)
        {
            var inherited = InheritedBounds(ph);
            if (inherited is { } b)
            {
                return (b.X, b.Y, b.Cy);
            }
        }

        return (null, null, null);
    }

    private static (long? X, long? Y, long? Height) FrameBounds(P.GraphicFrame frame, Affine transform)
    {
        var xfrm = frame.Transform;
        if (xfrm?.Offset?.X?.Value is { } x && xfrm.Offset.Y?.Value is { } y)
        {
            return (Scale(transform.X(x)), Scale(transform.Y(y)), xfrm.Extents?.Cy?.Value is { } cy ? Scale(cy * transform.Sy) : null);
        }

        return (null, null, null);
    }

    private static long Scale(double value) => (long)Math.Round(value);

    private (long X, long Y, long Cx, long Cy)? InheritedBounds(P.PlaceholderShape ph)
    {
        var layoutPart = _slidePart.SlideLayoutPart;
        var fromLayout = BoundsOf(FindPlaceholder(layoutPart?.SlideLayout?.CommonSlideData?.ShapeTree, ph, useIndex: true));
        if (fromLayout is not null)
        {
            return fromLayout;
        }

        var masterTree = layoutPart?.SlideMasterPart?.SlideMaster?.CommonSlideData?.ShapeTree;
        return BoundsOf(FindPlaceholder(masterTree, ph, useIndex: false));
    }

    private static P.Shape? FindPlaceholder(P.ShapeTree? tree, P.PlaceholderShape wanted, bool useIndex)
    {
        if (tree is null)
        {
            return null;
        }

        var candidates = tree.Elements<P.Shape>()
            .Select(s => (Shape: s, Ph: s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape))
            .Where(c => c.Ph is not null)
            .ToList();

        if (useIndex && wanted.Index?.Value is { } index)
        {
            var byIndex = candidates.FirstOrDefault(c => c.Ph!.Index?.Value == index);
            if (byIndex.Shape is not null)
            {
                return byIndex.Shape;
            }
        }

        var key = PlaceholderKey(wanted);
        return candidates.FirstOrDefault(c => PlaceholderKey(c.Ph!) == key).Shape;
    }

    private static (long X, long Y, long Cx, long Cy)? BoundsOf(P.Shape? shape)
    {
        var xfrm = shape?.ShapeProperties?.Transform2D;
        if (xfrm?.Offset?.X?.Value is { } x && xfrm.Offset.Y?.Value is { } y)
        {
            return (x, y, xfrm.Extents?.Cx?.Value ?? 0, xfrm.Extents?.Cy?.Value ?? 0);
        }

        return null;
    }

    /// <summary>Normalised placeholder kind: title, body, dt, ftr, sldNum or the raw type name.</summary>
    private static string PlaceholderKey(P.PlaceholderShape ph)
    {
        var type = ph.Type?.InnerText;
        return type switch
        {
            null or "" or "obj" or "subTitle" or "body" => "body",
            "title" or "ctrTitle" => "title",
            _ => type,
        };
    }

    /// <summary>Rows by Y (top to bottom), inside a row by X (left to right). Shapes without a position come last.</summary>
    private static List<Block> OrderForReading(List<Block> blocks)
    {
        var positioned = blocks
            .Where(b => b.Y is not null)
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ThenBy(b => b.Order)
            .ToList();

        var result = new List<Block>(blocks.Count);
        var row = new List<Block>();
        long anchorY = 0;
        long tolerance = 0;

        void FlushRow()
        {
            result.AddRange(row.OrderBy(b => b.X ?? 0).ThenBy(b => b.Order));
            row.Clear();
        }

        foreach (var block in positioned)
        {
            var y = block.Y!.Value;
            if (row.Count > 0 && y - anchorY > tolerance)
            {
                FlushRow();
            }

            if (row.Count == 0)
            {
                anchorY = y;
                tolerance = Math.Clamp((long)((block.Height ?? 0) * 0.3), RowToleranceMin, RowToleranceMax);
            }

            row.Add(block);
        }

        FlushRow();
        result.AddRange(blocks.Where(b => b.Y is null).OrderBy(b => b.Order));
        return result;
    }

    private sealed record Block(long? X, long? Y, long? Height, int Order, string Text);

    private sealed record ConnectorInfo(uint StartId, uint EndId, bool Reversed, string Label);

    /// <summary>Maps child coordinates of nested groups to slide coordinates: slide = child * scale + offset.</summary>
    private readonly record struct Affine(double Sx, double Sy, double Tx, double Ty)
    {
        public static Affine Identity { get; } = new(1, 1, 0, 0);

        public double X(double x) => x * Sx + Tx;

        public double Y(double y) => y * Sy + Ty;
    }
}
