using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AetherCodex.Editor;

/// <summary>
/// Registry + dispatcher for the rich-text editor. Each rich format (RTF, DOCX,
/// ODT, legacy DOC) registers its extensions and a load/save handler so the rest
/// of the app treats them uniformly. RTF and plain text are WPF-native
/// (<see cref="TextRange"/>); DOCX, ODT and DOC are parsed directly against their
/// zip/binary packages using only the BCL (no external dependencies).
/// </summary>
public static class DocumentIO
{
    /// <summary>A registered rich-text format: which extensions it owns and how to load/save it.</summary>
    public sealed record RichTextFormat(
        string Key,
        string DisplayName,
        string[] Extensions,
        Func<string, FlowDocument> Load,
        Action<string, FlowDocument>? Save);

    public static readonly IReadOnlyList<RichTextFormat> Formats = new[]
    {
        new RichTextFormat("rtf",  "Rich Text Format",  new[] { ".rtf" },  LoadRtf,  SaveRtf),
        new RichTextFormat("docx", "Word Document",     new[] { ".docx" }, DocxFormat.Load, DocxFormat.Save),
        new RichTextFormat("odt",  "OpenDocument Text", new[] { ".odt" },  OdtFormat.Load,  OdtFormat.Save),
        // Legacy binary Word is import-only: WPF cannot emit OLE2 .doc, and the
        // reader reconstructs text + paragraph structure (no round-trip write).
        new RichTextFormat("doc",  "Word 97-2003",      new[] { ".doc" },  DocFormat.Load,  null),
    };

    private static readonly Dictionary<string, RichTextFormat> ByExtension = BuildIndex();

    private static Dictionary<string, RichTextFormat> BuildIndex()
    {
        var map = new Dictionary<string, RichTextFormat>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Formats)
            foreach (var e in f.Extensions)
                map[e] = f;
        return map;
    }

    public static RichTextFormat? FormatFor(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var f) ? f : null;

    public static bool IsRichDocument(string path) => FormatFor(path) is not null;

    public static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    public static bool IsPlainText(string path) =>
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public static FlowDocument Load(string path)
    {
        if (FormatFor(path) is { } fmt) return fmt.Load(path);
        return LoadText(path);
    }

    public static void Save(string path, FlowDocument doc)
    {
        if (IsMarkdown(path)) { SaveMarkdown(path, doc); return; }

        if (FormatFor(path) is { } fmt)
        {
            if (fmt.Save is { } save) { save(path, doc); return; }
            var ext = Path.GetExtension(path).TrimStart('.');
            throw new NotSupportedException(
                $".{ext} is import-only in ÆtherCodex — use File ▸ Save As to ODT, DOCX or RTF.");
        }

        SaveText(path, doc);
    }

    // ── RTF / TXT / Markdown ───────────────────────────────────────────────────

    private static FlowDocument LoadRtf(string path)
    {
        var doc = new FlowDocument();
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var fs = File.OpenRead(path);
        range.Load(fs, DataFormats.Rtf);
        return doc;
    }

    private static void SaveRtf(string path, FlowDocument doc)
    {
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var fs = File.Create(path);
        range.Save(fs, DataFormats.Rtf);
    }

    private static FlowDocument LoadText(string path)
    {
        var doc = new FlowDocument();
        var para = new Paragraph();
        doc.Blocks.Add(para);
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) para.Inlines.Add(new LineBreak());
            para.Inlines.Add(new Run(lines[i]));
        }
        return doc;
    }

    private static void SaveText(string path, FlowDocument doc)
    {
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var fs = File.Create(path);
        range.Save(fs, DataFormats.Text);
    }

    private static void SaveMarkdown(string path, FlowDocument doc)
    {
        File.WriteAllText(path, MarkdownSerializer.ToMarkdown(doc), new UTF8Encoding(false));
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    /// <summary>Parse a "#RRGGBB" / "RRGGBB" / named colour into a brush; null when unset/auto.</summary>
    internal static Brush? ParseColor(string? val)
    {
        if (string.IsNullOrEmpty(val) || val == "auto") return null;
        string? hex = null;
        if (val.StartsWith('#')) hex = val;
        else if (val.Length == 6 && val.All(Uri.IsHexDigit)) hex = "#" + val;
        else
        {
            var named = new Dictionary<string, string>
            {
                ["black"] = "#000000", ["white"] = "#FFFFFF", ["gray"] = "#808080",
                ["red"] = "#FF0000", ["green"] = "#008000", ["blue"] = "#0000FF",
                ["yellow"] = "#FFFF00", ["cyan"] = "#00FFFF", ["magenta"] = "#FF00FF"
            };
            if (named.TryGetValue(val.ToLowerInvariant(), out var v)) hex = v;
        }
        if (hex is null) return null;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    /// <summary>Render a colour as standard 6-digit "#RRGGBB" (OOXML/ODF require 6 hex,
    /// not WPF's 8-digit "#AARRGGBB").</summary>
    internal static string ToHex(Color c) =>
        "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    /// <summary>Write a UTF-8 string entry into a zip package (shared by DOCX/ODT writers).</summary>
    internal static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var sw = new StreamWriter(s, new UTF8Encoding(false));
        sw.Write(content);
    }

    /// <summary>Clear auto text-colour (pure black/white) so documents inherit the
    /// active theme foreground instead of baking in Word's default black.</summary>
    public static void NormalizeAutoColors(FlowDocument doc)
    {
        foreach (var run in EnumerateRuns(doc))
        {
            if (run.Foreground is SolidColorBrush fg && (fg.Color == Colors.Black || fg.Color == Colors.White))
                run.ClearValue(TextElement.ForegroundProperty);
        }
    }

    internal static IEnumerable<Run> EnumerateRuns(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
            foreach (var run in RunsInBlock(block))
                yield return run;
    }

    internal static IEnumerable<Run> RunsInBlock(Block block)
    {
        switch (block)
        {
            case Paragraph p: return RunsInInlines(p.Inlines);
            case List l: return l.ListItems.SelectMany(li => li.Blocks.SelectMany(RunsInBlock));
            case Section s: return s.Blocks.SelectMany(RunsInBlock);
            case Table t: return t.RowGroups.SelectMany(g => g.Rows)
                .SelectMany(row => row.Cells).SelectMany(c => c.Blocks.SelectMany(RunsInBlock));
            default: return Enumerable.Empty<Run>();
        }
    }

    private static IEnumerable<Run> RunsInInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run: yield return run; break;
                // Hyperlink derives from Span, so Span.Inlines already covers links.
                case Span span:
                    foreach (var r in RunsInInlines(span.Inlines)) yield return r;
                    break;
            }
        }
    }
}