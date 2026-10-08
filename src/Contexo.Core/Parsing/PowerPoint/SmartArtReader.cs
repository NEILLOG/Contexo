using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using Dgm = DocumentFormat.OpenXml.Drawing.Diagrams;

namespace Contexo.Core.Parsing.PowerPoint;

/// <summary>Reads the node tree of a SmartArt graphic from its data part and renders it as an indented "- " list.</summary>
internal static class SmartArtReader
{
    public static string? Read(DiagramDataPart dataPart)
    {
        var model = dataPart.DataModelRoot;
        if (model is null)
        {
            return null;
        }

        var nodes = new Dictionary<string, string>();
        var nodeOrder = new List<string>();
        string? docId = null;
        foreach (var point in model.PointList?.Elements<Dgm.Point>() ?? [])
        {
            var id = point.ModelId?.Value;
            if (id is null)
            {
                continue;
            }

            var type = point.Type?.InnerText;
            if (type == "doc")
            {
                docId ??= id;
            }
            else if (type is null or "" or "node" or "asst")
            {
                var text = string.Join(' ', point.Descendants<A.Paragraph>()
                    .Select(p => SlideReader.Collapse(SlideReader.ParagraphText(p)))
                    .Where(t => t.Length > 0));
                nodes[id] = text;
                nodeOrder.Add(id);
            }
        }

        // parOf connections run from the parent (source) to the child (destination); srcOrd orders siblings.
        var children = new Dictionary<string, List<(string Child, uint Order)>>();
        var hasParent = new HashSet<string>();
        foreach (var cxn in model.ConnectionList?.Elements<Dgm.Connection>() ?? [])
        {
            var type = cxn.Type?.InnerText;
            if (type is not (null or "" or "parOf"))
            {
                continue;
            }

            var source = cxn.SourceId?.Value;
            var destination = cxn.DestinationId?.Value;
            if (source is null || destination is null || !nodes.ContainsKey(destination))
            {
                continue;
            }

            if (source != docId && !nodes.ContainsKey(source))
            {
                continue;
            }

            if (!children.TryGetValue(source, out var list))
            {
                children[source] = list = [];
            }

            list.Add((destination, cxn.SourcePosition?.Value ?? uint.MaxValue));
            hasParent.Add(destination);
        }

        var roots = new List<string>();
        if (docId is not null && children.TryGetValue(docId, out var top))
        {
            roots.AddRange(top.OrderBy(c => c.Order).Select(c => c.Child));
        }

        roots.AddRange(nodeOrder.Where(id => !hasParent.Contains(id)).Except(roots));

        var lines = new List<string>();
        var visited = new HashSet<string>();

        void Emit(string id, int depth)
        {
            if (!visited.Add(id))
            {
                return;
            }

            var text = nodes[id];
            var childDepth = depth;
            if (text.Length > 0)
            {
                lines.Add(new string(' ', depth * 2) + "- " + text);
                childDepth++;
            }

            if (children.TryGetValue(id, out var list))
            {
                foreach (var (child, _) in list.OrderBy(c => c.Order))
                {
                    Emit(child, childDepth);
                }
            }
        }

        foreach (var root in roots)
        {
            Emit(root, 0);
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }
}
