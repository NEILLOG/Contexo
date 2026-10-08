using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace Contexo.Core.Tests.Parsing.PowerPoint;

/// <summary>
/// Builds small .pptx files for tests from hand-written PresentationML, so each test controls exactly which shapes,
/// connector ends, merges and placeholders exist. The master and layout carry footer text that must never be parsed.
/// </summary>
internal sealed class PptxBuilder
{
    public const string MasterFooterText = "母片頁尾機密文字";
    public const string MasterCompanyText = "母片公司名稱";
    public const string LayoutFooterText = "版面配置頁尾文字";

    private const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private readonly List<SlideSpec> _slides = [];

    public SlideSpec AddSlide()
    {
        var slide = new SlideSpec();
        _slides.Add(slide);
        return slide;
    }

    public MemoryStream BuildStream() => new(Build());

    public byte[] Build()
    {
        using var buffer = new MemoryStream();
        using (var document = PresentationDocument.Create(buffer, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            var master = presentation.AddNewPart<SlideMasterPart>();
            var layout = master.AddNewPart<SlideLayoutPart>();
            layout.SlideLayout = new P.SlideLayout(LayoutXml());
            layout.AddPart(master);
            master.SlideMaster = new P.SlideMaster(MasterXml(master.GetIdOfPart(layout)));

            NotesMasterPart? notesMaster = null;
            var slideIds = new StringBuilder();
            var id = 256;
            foreach (var spec in _slides)
            {
                var slide = presentation.AddNewPart<SlidePart>();
                slide.AddPart(layout);
                spec.Attach(slide, ref notesMaster, presentation);
                slideIds.Append($"<p:sldId id=\"{id++}\" r:id=\"{presentation.GetIdOfPart(slide)}\"/>");
            }

            var notesMasterIds = notesMaster is null
                ? string.Empty
                : $"<p:notesMasterIdLst><p:notesMasterId r:id=\"{presentation.GetIdOfPart(notesMaster)}\"/></p:notesMasterIdLst>";
            presentation.Presentation = new P.Presentation(
                $"<p:presentation {Namespaces}>" +
                $"<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"{presentation.GetIdOfPart(master)}\"/></p:sldMasterIdLst>" +
                notesMasterIds +
                $"<p:sldIdLst>{slideIds}</p:sldIdLst>" +
                "<p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
        }

        return buffer.ToArray();
    }

    private static string MasterXml(string layoutRelationshipId) =>
        $"<p:sldMaster {Namespaces}><p:cSld><p:spTree>{GroupHeader}" +
        TextShape(2, "Title Master", 457200, 274638, 8229600, 1143000, [], "<p:ph type=\"title\"/>") +
        TextShape(3, "Body Master", 457200, 1600200, 8229600, 4525963, [], "<p:ph type=\"body\" idx=\"1\"/>") +
        TextShape(4, "Footer Master", 3124200, 6356350, 2895600, 365125, [MasterFooterText], "<p:ph type=\"ftr\" sz=\"quarter\" idx=\"3\"/>") +
        TextShape(5, "Company", 100000, 6356350, 2000000, 365125, [MasterCompanyText], null) +
        "</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        $"<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"{layoutRelationshipId}\"/></p:sldLayoutIdLst></p:sldMaster>";

    // The layout positions the body placeholder (idx 1) in the lower half; the master positions only the title.
    private static string LayoutXml() =>
        $"<p:sldLayout {Namespaces} type=\"obj\"><p:cSld name=\"Title and Content\"><p:spTree>{GroupHeader}" +
        TextShape(2, "Title", 457200, 274638, 8229600, 1143000, [], "<p:ph type=\"title\"/>") +
        TextShape(3, "Content", 457200, 4000000, 8229600, 2000000, [], "<p:ph idx=\"1\"/>") +
        TextShape(4, "Footer", 3124200, 6356350, 2895600, 365125, [LayoutFooterText], "<p:ph type=\"ftr\" sz=\"quarter\" idx=\"11\"/>") +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    internal const string GroupHeader =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    /// <summary>Paragraph text; each leading tab raises the paragraph level by one.</summary>
    internal static string Paragraphs(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var level = line.TakeWhile(c => c == '\t').Count();
            var levelAttr = level > 0 ? $" lvl=\"{level}\"" : string.Empty;
            sb.Append($"<a:p><a:pPr{levelAttr}/><a:r><a:rPr lang=\"zh-TW\"/><a:t>{Escape(line.TrimStart('\t'))}</a:t></a:r></a:p>");
        }

        return sb.ToString();
    }

    internal static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

    /// <summary>A p:sp. <paramref name="placeholder"/> is the raw p:ph element (or null); position is omitted when <paramref name="x"/> is null.</summary>
    internal static string TextShape(int id, string name, long? x, long? y, long cx, long cy, IEnumerable<string> lines, string? placeholder)
    {
        var xfrm = x is null
            ? string.Empty
            : $"<a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>";
        var geometry = placeholder is null ? "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" : string.Empty;
        var body = Paragraphs(lines);
        var textBody = body.Length == 0 ? "<p:txBody><a:bodyPr/><a:p/></p:txBody>" : $"<p:txBody><a:bodyPr/>{body}</p:txBody>";
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{Escape(name)}\"/><p:cNvSpPr/><p:nvPr>{placeholder}</p:nvPr></p:nvSpPr>" +
               $"<p:spPr>{xfrm}{geometry}</p:spPr>{textBody}</p:sp>";
    }

    internal sealed class SlideSpec
    {
        private readonly List<string> _shapes = [];
        private readonly List<string> _charts = [];
        private readonly List<string> _smartArts = [];
        private string[] _notes = [];
        private byte[]? _embeddedDocx;
        private byte[]? _image;

        public bool Hidden { get; set; }

        /// <summary>Title placeholder (with its own position unless <paramref name="inheritPosition"/>).</summary>
        public SlideSpec Title(string text, int id = 2, bool inheritPosition = true)
        {
            _shapes.Add(TextShape(id, "Title", inheritPosition ? null : 457200, 274638, 8229600, 1143000, [text], "<p:ph type=\"title\"/>"));
            return this;
        }

        public SlideSpec CenteredTitle(string text, int id = 2)
        {
            _shapes.Add(TextShape(id, "Title", null, null, 0, 0, [text], "<p:ph type=\"ctrTitle\"/>"));
            return this;
        }

        public SlideSpec TextBox(int id, long x, long y, long cx, long cy, params string[] lines)
        {
            _shapes.Add(TextShape(id, "TextBox " + id, x, y, cx, cy, lines, null));
            return this;
        }

        /// <summary>A placeholder with no position of its own (inherits from the layout). <paramref name="ph"/> is the raw p:ph element.</summary>
        public SlideSpec Placeholder(int id, string ph, params string[] lines)
        {
            _shapes.Add(TextShape(id, "Placeholder " + id, null, null, 0, 0, lines, ph));
            return this;
        }

        public SlideSpec Raw(string shapeXml)
        {
            _shapes.Add(shapeXml);
            return this;
        }

        public SlideSpec Group(int id, (long X, long Y, long Cx, long Cy) outer, (long X, long Y, long Cx, long Cy) inner, params string[] children)
        {
            _shapes.Add(GroupXml(id, outer, inner, children));
            return this;
        }

        public static string GroupXml(int id, (long X, long Y, long Cx, long Cy) outer, (long X, long Y, long Cx, long Cy) inner, params string[] children) =>
            $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"{id}\" name=\"Group {id}\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
            $"<p:grpSpPr><a:xfrm><a:off x=\"{outer.X}\" y=\"{outer.Y}\"/><a:ext cx=\"{outer.Cx}\" cy=\"{outer.Cy}\"/>" +
            $"<a:chOff x=\"{inner.X}\" y=\"{inner.Y}\"/><a:chExt cx=\"{inner.Cx}\" cy=\"{inner.Cy}\"/></a:xfrm></p:grpSpPr>" +
            string.Concat(children) + "</p:grpSp>";

        /// <summary>A connector. A null <paramref name="startId"/> / <paramref name="endId"/> leaves that end unattached.</summary>
        public SlideSpec Connector(int id, int? startId, int? endId, string? headEnd = null, string? tailEnd = "triangle", string? label = null)
        {
            var start = startId is null ? string.Empty : $"<a:stCxn id=\"{startId}\" idx=\"3\"/>";
            var end = endId is null ? string.Empty : $"<a:endCxn id=\"{endId}\" idx=\"1\"/>";
            var head = headEnd is null ? string.Empty : $"<a:headEnd type=\"{headEnd}\"/>";
            var tail = tailEnd is null ? string.Empty : $"<a:tailEnd type=\"{tailEnd}\"/>";
            var text = label is null ? string.Empty : $"<p:txBody><a:bodyPr/><a:p><a:r><a:t>{Escape(label)}</a:t></a:r></a:p></p:txBody>";
            _shapes.Add(
                $"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"Connector {id}\"/><p:cNvCxnSpPr>{start}{end}</p:cNvCxnSpPr><p:nvPr/></p:nvCxnSpPr>" +
                "<p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>" +
                $"<a:ln>{head}{tail}</a:ln></p:spPr>{text}</p:cxnSp>");
            return this;
        }

        public SlideSpec Table(int id, long x, long y, int columns, params string[][] rows)
        {
            var grid = string.Concat(Enumerable.Repeat("<a:gridCol w=\"1000000\"/>", columns));
            var rowsXml = string.Concat(rows.Select(r => $"<a:tr h=\"370840\">{string.Concat(r)}</a:tr>"));
            _shapes.Add(
                $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
                $"<p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{columns * 1000000}\" cy=\"{rows.Length * 370840}\"/></p:xfrm>" +
                "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">" +
                $"<a:tbl><a:tblPr firstRow=\"1\"/><a:tblGrid>{grid}</a:tblGrid>{rowsXml}</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
            return this;
        }

        public static string Cell(string text, int gridSpan = 1, int rowSpan = 1, bool hMerge = false, bool vMerge = false)
        {
            var attrs = (gridSpan > 1 ? $" gridSpan=\"{gridSpan}\"" : string.Empty)
                        + (rowSpan > 1 ? $" rowSpan=\"{rowSpan}\"" : string.Empty)
                        + (hMerge ? " hMerge=\"1\"" : string.Empty)
                        + (vMerge ? " vMerge=\"1\"" : string.Empty);
            var body = text.Length == 0 ? "<a:p/>" : Paragraphs([text]);
            return $"<a:tc{attrs}><a:txBody><a:bodyPr/><a:lstStyle/>{body}</a:txBody><a:tcPr/></a:tc>";
        }

        public SlideSpec Chart(int id, long x, long y, string? title, string[] categories, params (string Name, double[] Values)[] series)
        {
            var token = $"@@CHART{_charts.Count}@@";
            _charts.Add(ChartXml(title, categories, series));
            _shapes.Add(
                $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Chart {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
                $"<p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"4000000\" cy=\"3000000\"/></p:xfrm>" +
                "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
                $"<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"{token}\"/></a:graphicData></a:graphic></p:graphicFrame>");
            return this;
        }

        /// <summary>A SmartArt graphic. Each entry is (id, parent id or null for a top-level node, text).</summary>
        public SlideSpec SmartArt(int id, params (string Id, string? ParentId, string Text)[] nodes)
        {
            var token = $"@@DGM{_smartArts.Count}@@";
            _smartArts.Add(SmartArtXml(nodes));
            _shapes.Add(
                $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Diagram {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
                "<p:xfrm><a:off x=\"0\" y=\"3000000\"/><a:ext cx=\"4000000\" cy=\"3000000\"/></p:xfrm>" +
                "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\">" +
                $"<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"{token}\"/></a:graphicData></a:graphic></p:graphicFrame>");
            return this;
        }

        public SlideSpec Notes(params string[] paragraphs)
        {
            _notes = paragraphs;
            return this;
        }

        public SlideSpec EmbeddedDocx(byte[] content)
        {
            _embeddedDocx = content;
            return this;
        }

        public SlideSpec Image(byte[] png)
        {
            _image = png;
            return this;
        }

        internal void Attach(SlidePart slide, ref NotesMasterPart? notesMaster, PresentationPart presentation)
        {
            var xml = string.Concat(_shapes);
            for (var i = 0; i < _charts.Count; i++)
            {
                var chart = slide.AddNewPart<ChartPart>();
                chart.ChartSpace = new DocumentFormat.OpenXml.Drawing.Charts.ChartSpace(_charts[i]);
                xml = xml.Replace($"@@CHART{i}@@", slide.GetIdOfPart(chart), StringComparison.Ordinal);
            }

            for (var i = 0; i < _smartArts.Count; i++)
            {
                var data = slide.AddNewPart<DiagramDataPart>();
                data.DataModelRoot = new DocumentFormat.OpenXml.Drawing.Diagrams.DataModelRoot(_smartArts[i]);
                xml = xml.Replace($"@@DGM{i}@@", slide.GetIdOfPart(data), StringComparison.Ordinal);
            }

            var show = Hidden ? " show=\"0\"" : string.Empty;
            slide.Slide = new P.Slide($"<p:sld {Namespaces}{show}><p:cSld><p:spTree>{GroupHeader}{xml}</p:spTree></p:cSld></p:sld>");

            if (_embeddedDocx is not null)
            {
                var part = slide.AddEmbeddedPackagePart("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
                using var target = part.GetStream(FileMode.Create, FileAccess.Write);
                target.Write(_embeddedDocx);
            }

            if (_image is not null)
            {
                var part = slide.AddImagePart(ImagePartType.Png);
                using var target = part.GetStream(FileMode.Create, FileAccess.Write);
                target.Write(_image);
            }

            if (_notes.Length > 0)
            {
                notesMaster ??= CreateNotesMaster(presentation);
                var notes = slide.AddNewPart<NotesSlidePart>();
                notes.AddPart(notesMaster);
                notes.AddPart(slide);
                notes.NotesSlide = new P.NotesSlide(
                    $"<p:notes {Namespaces}><p:cSld><p:spTree>{GroupHeader}" +
                    TextShape(2, "Slide Image", 685800, 1143000, 5486400, 3086100, [], "<p:ph type=\"sldImg\"/>") +
                    TextShape(3, "Notes", 685800, 4400550, 5486400, 3600450, _notes, "<p:ph type=\"body\" idx=\"1\"/>") +
                    TextShape(4, "Number", 3884613, 8685213, 2971800, 458787, ["7"], "<p:ph type=\"sldNum\" idx=\"10\"/>") +
                    "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
            }
        }

        private static NotesMasterPart CreateNotesMaster(PresentationPart presentation)
        {
            var master = presentation.AddNewPart<NotesMasterPart>();
            master.NotesMaster = new P.NotesMaster(
                $"<p:notesMaster {Namespaces}><p:cSld><p:spTree>{GroupHeader}" +
                TextShape(2, "Notes Master", 685800, 4400550, 5486400, 3600450, ["備忘稿母片文字"], "<p:ph type=\"body\" idx=\"1\"/>") +
                "</p:spTree></p:cSld>" +
                "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/></p:notesMaster>");
            return master;
        }

        private static string ChartXml(string? title, string[] categories, (string Name, double[] Values)[] series)
        {
            var titleXml = title is null
                ? string.Empty
                : $"<c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>{Escape(title)}</a:t></a:r></a:p></c:rich></c:tx></c:title>";

            var sb = new StringBuilder();
            for (var s = 0; s < series.Length; s++)
            {
                sb.Append($"<c:ser><c:idx val=\"{s}\"/><c:order val=\"{s}\"/>");
                sb.Append($"<c:tx><c:strRef><c:f>Sheet1!$B$1</c:f><c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>{Escape(series[s].Name)}</c:v></c:pt></c:strCache></c:strRef></c:tx>");
                sb.Append($"<c:cat><c:strRef><c:f>Sheet1!$A$2</c:f><c:strCache><c:ptCount val=\"{categories.Length}\"/>");
                for (var i = 0; i < categories.Length; i++)
                {
                    sb.Append($"<c:pt idx=\"{i}\"><c:v>{Escape(categories[i])}</c:v></c:pt>");
                }

                sb.Append("</c:strCache></c:strRef></c:cat>");
                sb.Append($"<c:val><c:numRef><c:f>Sheet1!$B$2</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"{series[s].Values.Length}\"/>");
                for (var i = 0; i < series[s].Values.Length; i++)
                {
                    sb.Append($"<c:pt idx=\"{i}\"><c:v>{series[s].Values[i]:0.##}</c:v></c:pt>");
                }

                sb.Append("</c:numCache></c:numRef></c:val></c:ser>");
            }

            return "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                   $"<c:chart>{titleXml}<c:plotArea><c:layout/><c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/>{sb}</c:barChart></c:plotArea></c:chart></c:chartSpace>";
        }

        private static string SmartArtXml(IReadOnlyList<(string Id, string? ParentId, string Text)> nodes)
        {
            var points = new StringBuilder("<dgm:pt modelId=\"doc\" type=\"doc\"/>");
            var connections = new StringBuilder();
            var order = new Dictionary<string, int>();
            var counter = 0;
            foreach (var (id, parentId, text) in nodes)
            {
                points.Append($"<dgm:pt modelId=\"{id}\"><dgm:prSet/><dgm:spPr/><dgm:t><a:bodyPr/><a:p><a:r><a:t>{Escape(text)}</a:t></a:r></a:p></dgm:t></dgm:pt>");
                var parent = parentId ?? "doc";
                var position = order.GetValueOrDefault(parent);
                order[parent] = position + 1;
                connections.Append($"<dgm:cxn modelId=\"c{counter++}\" srcId=\"{parent}\" destId=\"{id}\" srcOrd=\"{position}\" destOrd=\"0\"/>");
            }

            // Presentation-only points and connections that must be ignored.
            points.Append("<dgm:pt modelId=\"pres1\" type=\"pres\"><dgm:prSet/><dgm:spPr/><dgm:t><a:bodyPr/><a:p><a:r><a:t>版面點不該出現</a:t></a:r></a:p></dgm:t></dgm:pt>");
            connections.Append($"<dgm:cxn modelId=\"cp\" type=\"presOf\" srcId=\"{nodes[0].Id}\" destId=\"pres1\" srcOrd=\"0\" destOrd=\"0\"/>");

            return "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                   $"<dgm:ptLst>{points}</dgm:ptLst><dgm:cxnLst>{connections}</dgm:cxnLst></dgm:dataModel>";
        }
    }
}
