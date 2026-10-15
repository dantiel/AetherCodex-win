using System.Text.RegularExpressions;

namespace AetherCodex.Editor;

/// <summary>
/// Polymorphic project-wide content search. Files are walked with the same ignore
/// set as Go-to-File; each file is matched according to its language (glyph + whole-word
/// identifier semantics), and the engine morphs between plain / whole-word / regex modes.
/// </summary>
public static class ProjectSearch
{
    public sealed class Options
    {
        public string Query { get; }
        public bool Regex { get; }
        public bool CaseSensitive { get; }
        public bool WholeWord { get; }
        public Options(string query, bool regex, bool caseSensitive, bool wholeWord)
        {
            Query = query;
            Regex = regex;
            CaseSensitive = caseSensitive;
            WholeWord = wholeWord;
        }
    }

    public const int MaxResults = 2000;
    public const int MaxLine = 400;

    public sealed record SearchMatch(string File, int Line, int Column, int Length, string LineText, string Glyph);

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".aether", ".vendor_bundle", ".bundle", "node_modules", "bin", "obj", ".vs", "vendored" };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".psd",
        ".exe", ".dll", ".pdb", ".obj", ".lib", ".so", ".dylib", ".a", ".sys",
        ".zip", ".gz", ".tar", ".7z", ".rar", ".xz", ".bz2", ".zst",
        ".mp3", ".mp4", ".wav", ".ogg", ".flac", ".aac", ".avi", ".mkv", ".mov", ".webm",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".sqlite", ".db", ".sqlite3", ".bin", ".dat", ".pak", ".rlib",
        ".jar", ".war", ".class", ".o", ".pyc", ".pyo", ".nupkg", ".snupkg",
        ".wasm", ".aotdata", ".resources",
    };

    public static IEnumerable<string> EnumerateFiles(string root, IReadOnlyList<string> globs, CancellationToken token = default)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested) yield break;
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (IgnoredDirs.Contains(name)) continue;
                try
                {
                    if (Directory.Exists(entry)) stack.Push(entry);
                }
                catch { continue; }
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }
            foreach (var file in files)
            {
                if (token.IsCancellationRequested) yield break;
                var name = Path.GetFileName(file);
                if (IgnoredDirs.Contains(name)) continue;
                if (GlobsMatch(file, globs)) yield return file;
            }
        }
    }

    private static bool GlobsMatch(string file, IReadOnlyList<string> globs)
    {
        if (globs.Count == 0 || (globs.Count == 1 && globs[0] == "*")) return true;
        var name = Path.GetFileName(file);
        foreach (var g in globs)
        {
            if (g == "*") return true;
            if (g.StartsWith('*'))
            {
                var suffix = g.Substring(1);
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (name.Equals(g, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private const int BinaryProbeBytes = 8192;

    /// <summary>
    /// Cheap binary sniff: known binary extensions plus a NUL-byte probe over the
    /// first 8 KiB. Text never carries a NUL in its prefix; UTF-16/UTF-32 BOMs are
    /// exempted because their zero bytes are structural, not binary.
    /// </summary>
    public static bool IsBinaryFile(string path)
    {
        if (BinaryExtensions.Contains(Path.GetExtension(path))) return true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) return false;
            var probe = new byte[Math.Min((int)stream.Length, BinaryProbeBytes)];
            int read = stream.Read(probe, 0, probe.Length);
            if (read >= 2 && ((probe[0] == 0xFF && probe[1] == 0xFE) || (probe[0] == 0xFE && probe[1] == 0xFF)))
                return false; // UTF-16 LE/BE BOM
            if (read >= 4 && probe[0] == 0xFF && probe[1] == 0xFE && probe[2] == 0x00 && probe[3] == 0x00)
                return false; // UTF-32 LE BOM
            for (int i = 0; i < read; i++)
                if (probe[i] == 0) return true;
            return false;
        }
        catch
        {
            return true; // unreadable → skip
        }
    }

    public static IEnumerable<SearchMatch> MatchFile(string path, Options options, CancellationToken token = default)
    {
        if (IsBinaryFile(path)) yield break;
        var glyph = GlyphFor(path);
        var query = options.Query;
        int lineNo = 0;

        if (options.Regex)
        {
            Regex rx;
            try
            {
                rx = new Regex(query,
                    RegexOptions.CultureInvariant | (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));
            }
            catch (ArgumentException) { yield break; }

            foreach (var line in File.ReadLines(path))
            {
                if (token.IsCancellationRequested) yield break;
                lineNo++;
                if (line.Length == 0) continue;
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(line))
                {
                    if (m.Length == 0) continue;
                    yield return new SearchMatch(path, lineNo, m.Index + 1, m.Length, Clip(line), glyph);
                }
            }
        }
        else
        {
            var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var line in File.ReadLines(path))
            {
                if (token.IsCancellationRequested) yield break;
                lineNo++;
                if (line.Length == 0) continue;
                int idx = 0;
                while (true)
                {
                    int found = line.IndexOf(query, idx, comparison);
                    if (found < 0) break;
                    if (!options.WholeWord || IsWholeWord(line, found, found + query.Length))
                        yield return new SearchMatch(path, lineNo, found + 1, query.Length, Clip(line), glyph);
                    idx = found + Math.Max(1, query.Length);
                }
            }
        }
    }

    private static string Clip(string line) =>
        line.Length <= MaxLine ? line.Trim() : line.Substring(0, MaxLine).Trim() + "…";

    private static bool IsWholeWord(string line, int start, int end)
    {
        bool left = start == 0 || !IsWordChar(line[start - 1]);
        bool right = end >= line.Length || !IsWordChar(line[end]);
        return left && right;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static string GlyphFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".rb" => "◆", ".cs" => "♯", ".swift" => "🕊", ".py" => "🐍",
        ".js" or ".ts" or ".jsx" or ".tsx" => "§", ".json" or ".yml" or ".yaml" => "≡",
        ".md" => "✎", ".html" or ".css" or ".scss" => "◇", _ => "·",
    };
}