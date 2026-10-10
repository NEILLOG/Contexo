using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Contexo.CorpusGen.Content;

/// <summary>
/// Five multi-page regulations with a running header and a "Page N of M" footer.
/// No Chinese font is available on every machine, so the PDFs are written in English with the standard Helvetica fonts
/// (byte-identical on every platform). Chinese PDF handling is left to the manual checklist.
/// </summary>
internal static class PdfDocuments
{
    private const string Header = "Yaoyang Technology Co., Ltd. - Internal Regulation";
    private const int LinesPerPage = 40;
    private const int MaxLineLength = 84;

    private static readonly string[] Filler =
    [
        "Department heads are responsible for communicating this document to every member of their teams.",
        "Questions about the interpretation of this document should be sent to the Administration Office in writing.",
        "Exceptions require the written approval of the responsible vice president and must be recorded.",
        "This document is reviewed once a year and revised whenever the applicable laws or company needs change.",
        "Employees who find a gap or an error in the procedure are encouraged to report it to their supervisor.",
        "Contractors working on company premises are expected to follow the same rules as regular employees.",
        "Records created under this procedure must be complete, legible and signed by the person who created them.",
        "The Administration Office keeps the master copy of this document and distributes updated versions.",
        "Training on the content of this document is part of the orientation program for all new employees.",
        "Where this document conflicts with a statutory requirement, the statutory requirement prevails.",
        "Each department may add detailed work instructions as long as they are consistent with this document.",
        "Internal audits check compliance with these rules at least once a year and report to management.",
    ];

    public static void AddAll(CorpusBuilder corpus)
    {
        Create(corpus, "Employee_Code_of_Conduct.pdf", "Employee Code of Conduct", 5,
            "Gifts and Hospitality",
            "Any gift with a value above NT$1,500 must be reported to the Ethics Officer within five working days of receipt.");
        Create(corpus, "Workplace_Safety_Regulations.pdf", "Workplace Safety Regulations", 8,
            "Emergency Drills",
            "Fire drills are held twice a year, in April and in October. The assembly point is the north parking lot.");
        Create(corpus, "Visitor_Management_Policy.pdf", "Visitor Management Policy", 3,
            "Visitor Registration",
            "Every visitor must sign in at the front desk, wear a visitor badge at all times and be escorted by the host employee. The visitor log is kept for ninety days.");
        Create(corpus, "Remote_Work_Guidelines.pdf", "Remote Work Guidelines", 6,
            "Eligibility and Approval",
            "Employees may work remotely up to two days per week with the approval of their manager and must connect through the company VPN.");
        Create(corpus, "Records_Retention_Standard.pdf", "Records Retention Standard", 10,
            "Retention Periods",
            "Financial records are retained for ten years. Email is retained for three years. CCTV footage is overwritten after thirty days.");
    }

    private static void Create(CorpusBuilder corpus, string fileName, string title, int pageCount, string factHeading, string factText)
    {
        var random = new Random(StableSeed(fileName));
        var blocks = new List<(string Heading, List<string> Paragraphs)>();
        var estimatedLines = 4;
        var article = 1;
        while (estimatedLines < pageCount * LinesPerPage)
        {
            var paragraphs = new List<string>();
            for (var p = 0; p < 2; p++)
            {
                paragraphs.Add(string.Join(' ', Enumerable.Range(0, 3).Select(_ => Filler[random.Next(Filler.Length)])));
            }

            blocks.Add(($"Article {article++}. General Provisions {article - 1}", paragraphs));
            estimatedLines += 2 + paragraphs.Sum(p => (p.Length / MaxLineLength) + 2);
        }

        // The fact sits on roughly the second third of the document.
        blocks.Insert(Math.Min(blocks.Count, Math.Max(1, blocks.Count / 3)), ($"Article {article}. {factHeading}", [factText]));

        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        var pages = Paginate(title, blocks, pageCount);
        for (var number = 1; number <= pageCount; number++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText(Header, 9, new PdfPoint(72, 805), regular);
            page.AddText($"Page {number} of {pageCount}", 9, new PdfPoint(250, 40), regular);
            var y = 760.0;
            foreach (var (text, isHeading) in pages[number - 1])
            {
                if (text.Length > 0)
                {
                    page.AddText(text, isHeading ? 13 : 11, new PdfPoint(72, y), isHeading ? bold : regular);
                }

                y -= 16;
            }
        }

        corpus.AddFile(fileName, Office.PdfNormalizer.Normalize(builder.Build()));
    }

    private static List<(string Text, bool IsHeading)>[] Paginate(string title, List<(string Heading, List<string> Paragraphs)> blocks, int pageCount)
    {
        var lines = new List<(string Text, bool IsHeading)> { (title, true), ("", false) };
        foreach (var (heading, paragraphs) in blocks)
        {
            lines.Add((heading, true));
            foreach (var paragraph in paragraphs)
            {
                lines.AddRange(Wrap(paragraph).Select(l => (l, false)));
                lines.Add(("", false));
            }
        }

        var pages = new List<(string Text, bool IsHeading)>[pageCount];
        for (var i = 0; i < pageCount; i++)
        {
            pages[i] = lines.Skip(i * LinesPerPage).Take(LinesPerPage).ToList();
        }

        return pages;
    }

    private static IEnumerable<string> Wrap(string text)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > MaxLineLength && line.Length > 0)
            {
                yield return line;
                line = "";
            }

            line = line.Length == 0 ? word : line + " " + word;
        }

        if (line.Length > 0)
        {
            yield return line;
        }
    }

    private static int StableSeed(string text)
    {
        var hash = 17;
        foreach (var c in text)
        {
            hash = (hash * 31) + c;
        }

        return hash;
    }
}
