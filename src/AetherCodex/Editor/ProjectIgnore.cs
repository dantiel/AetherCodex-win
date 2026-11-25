using System.Text;
using System.Text.RegularExpressions;

namespace AetherCodex.Editor;

/// <summary>The component a file/directory is being filtered for. Mirrors TextMate's
/// three consumers of the properties filtering variables: the File Browser, Open
/// Quickly (the Go-to-File chooser) and Find in Project (folder search).</summary>
public enum FilterComponent { Browser, FileChooser, FolderSearch }

/// <summary>
/// Project inclusion/exclusion rules, mirroring `.tm_properties` (and readable from
/// `.aethercodex` YAML or `.aether-properties` flat — whichever marks the context).
/// The full TextMate variable set is supported:
/// <code>
/// # general (any item, every component)
/// include / exclude
/// includeDirectories / excludeDirectories
/// includeFiles / excludeFiles
/// # File Browser
/// includeInBrowser / excludeInBrowser
/// includeDirectoriesInBrowser / excludeDirectoriesInBrowser
/// includeFilesInBrowser / excludeFilesInBrowser
/// # Open Quickly (Go-to-File)
/// includeInFileChooser / excludeInFileChooser
/// includeFilesInFileChooser
/// excludeDirectoriesInFileChooser / excludeFilesInFileChooser
/// # Find in Project
/// excludeInFolderSearch
/// excludeDirectoriesInFolderSearch / excludeFilesInFolderSearch
/// </code>
/// Values are comma-separated brace-expanded globs. A pattern containing a path
/// separator is matched against the path relative to the properties file; one
/// without a separator is also matched against the bare file/directory name at any
/// depth (so `*.pyc` and `node_modules` exclude everywhere). `include*` re-includes
/// an item that an `exclude*` would otherwise drop — e.g. exclude `vendor` but include
/// `vendor/keep.h` (the excluded subtree is still descended when an include pattern
/// points inside it). Applied by project search, Go-to-File and the file browser,
/// alongside the shared default directory ignores.
/// </summary>
public sealed class ProjectIgnore
{
    private enum ItemKind { Any, Directory, File }

    private sealed class Pattern
    {
        public Regex Regex = null!;
        public bool HasSeparator;
        public ItemKind Kind;
        public bool Literal;
        public string StaticPrefix = ""; // literal chars before the first wildcard, lowercased, '/'-separated
    }

    /// <summary>Build-system / VCS noise that is skipped everywhere (before project rules).</summary>
    public static readonly HashSet<string> DefaultIgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".aether", ".vendor_bundle", ".bundle", "node_modules", "bin", "obj", ".vs", "vendored" };

    public string BaseDir { get; }

    private readonly Dictionary<FilterComponent, List<Pattern>> _exclude = new()
    {
        [FilterComponent.Browser] = new(),
        [FilterComponent.FileChooser] = new(),
        [FilterComponent.FolderSearch] = new(),
    };
    private readonly Dictionary<FilterComponent, List<Pattern>> _include = new()
    {
        [FilterComponent.Browser] = new(),
        [FilterComponent.FileChooser] = new(),
        [FilterComponent.FolderSearch] = new(),
    };

    public bool HasRules => _exclude.Values.Any(l => l.Count > 0) || _include.Values.Any(l => l.Count > 0);

    private static readonly FilterComponent[] All =
    { FilterComponent.Browser, FilterComponent.FileChooser, FilterComponent.FolderSearch };

    private static readonly Dictionary<string, (FilterComponent[] Components, bool Include, ItemKind Kind)> RuleMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── general (any item, every component) ──────────────────────────
            ["include"] = (All, true, ItemKind.Any),
            ["exclude"] = (All, false, ItemKind.Any),
            ["includeDirectories"] = (All, true, ItemKind.Directory),
            ["excludeDirectories"] = (All, false, ItemKind.Directory),
            ["includeFiles"] = (All, true, ItemKind.File),
            ["excludeFiles"] = (All, false, ItemKind.File),
            // ── File Browser ──────────────────────────────────────────────────
            ["includeInBrowser"] = (new[] { FilterComponent.Browser }, true, ItemKind.Any),
            ["excludeInBrowser"] = (new[] { FilterComponent.Browser }, false, ItemKind.Any),
            ["includeDirectoriesInBrowser"] = (new[] { FilterComponent.Browser }, true, ItemKind.Directory),
            ["excludeDirectoriesInBrowser"] = (new[] { FilterComponent.Browser }, false, ItemKind.Directory),
            ["includeFilesInBrowser"] = (new[] { FilterComponent.Browser }, true, ItemKind.File),
            ["excludeFilesInBrowser"] = (new[] { FilterComponent.Browser }, false, ItemKind.File),
            // ── Open Quickly (Go-to-File chooser) ─────────────────────────────
            ["includeInFileChooser"] = (new[] { FilterComponent.FileChooser }, true, ItemKind.Any),
            ["excludeInFileChooser"] = (new[] { FilterComponent.FileChooser }, false, ItemKind.Any),
            ["includeFilesInFileChooser"] = (new[] { FilterComponent.FileChooser }, true, ItemKind.File),
            ["excludeDirectoriesInFileChooser"] = (new[] { FilterComponent.FileChooser }, false, ItemKind.Directory),
            ["excludeFilesInFileChooser"] = (new[] { FilterComponent.FileChooser }, false, ItemKind.File),
            // ── Find in Project (folder search) ───────────────────────────────
            ["excludeInFolderSearch"] = (new[] { FilterComponent.FolderSearch }, false, ItemKind.Any),
            ["excludeDirectoriesInFolderSearch"] = (new[] { FilterComponent.FolderSearch }, false, ItemKind.Directory),
            ["excludeFilesInFolderSearch"] = (new[] { FilterComponent.FolderSearch }, false, ItemKind.File),
        };

    private ProjectIgnore(string baseDir) => BaseDir = baseDir;

    public static ProjectIgnore Load(string? startPath)
    {
        var file = ProjectProperties.LocateFilterFile(startPath);
        var baseDir = file is null
            ? (Directory.Exists(startPath) ? startPath! : Path.GetDirectoryName(startPath) ?? Directory.GetCurrentDirectory())
            : Path.GetDirectoryName(file)!;
        var ignore = new ProjectIgnore(baseDir);
        if (file is null) return ignore;

        foreach (var (key, value) in ReadEntries(file))
        {
            if (value.Length == 0 || !RuleMap.TryGetValue(key, out var rule)) continue;
            foreach (var expanded in ExpandList(value))
            {
                var pattern = Compile(expanded, rule.Kind);
                foreach (var component in rule.Components)
                    (rule.Include ? ignore._include : ignore._exclude)[component].Add(pattern);
            }
        }
        return ignore;
    }

    /// <summary>Read `key → value` entries from the filter file, dispatching on format:
    /// `.aethercodex` is YAML (top-level scalars only), the rest are flat key=value.</summary>
    private static IEnumerable<(string Key, string Value)> ReadEntries(string file)
    {
        if (string.Equals(Path.GetFileName(file), ".aethercodex", StringComparison.OrdinalIgnoreCase))
            return ReadYamlScalars(file);
        return ReadFlat(file);
    }

    private static IEnumerable<(string Key, string Value)> ReadFlat(string file)
    {
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '!' or ';') continue;
            int eq = line.IndexOfAny(new[] { '=', ':' });
            if (eq <= 0) continue;
            yield return (line[..eq].Trim(), line[(eq + 1)..].Trim().Trim('\'', '"'));
        }
    }

    /// <summary>Minimal YAML scalar reader: top-level `key: value` lines only, with
    /// quoted values unquoted. Nested (indented) blocks, document markers and comments
    /// are skipped — the filtering variables are always flat scalars.</summary>
    private static IEnumerable<(string Key, string Value)> ReadYamlScalars(string file)
    {
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw;
            if (line.Length == 0 || line.StartsWith("---") || line.StartsWith("...")) continue;
            if (line[0] is ' ' or '\t' or '#' or '!') continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            if (key.Length == 0) continue;
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0) continue; // nested map marker (e.g. `token-usage:`)
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            yield return (key, value);
        }
    }

    /// <summary>Path relative to the properties file (forward slashes); falls back to the
    /// bare name when <paramref name="path"/> lies outside the base directory.</summary>
    public string RelativePath(string path)
    {
        if (path.Length >= BaseDir.Length &&
            path.StartsWith(BaseDir, StringComparison.OrdinalIgnoreCase) &&
            (path.Length == BaseDir.Length || path[BaseDir.Length] is '\\' or '/'))
            return path.Substring(BaseDir.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        return (Path.GetFileName(path) ?? path).Replace('\\', '/');
    }

    /// <summary>True when a path matches an exclude pattern (single path; ancestor exclusion
    /// is threaded through by the caller as an inherited flag).</summary>
    public bool MatchesExclude(string relPath, bool isDir, FilterComponent component = FilterComponent.Browser) =>
        _exclude[component].Any(p => Matches(p, relPath, isDir));

    public bool MatchesInclude(string relPath, bool isDir, FilterComponent component = FilterComponent.Browser) =>
        _include[component].Any(p => Matches(p, relPath, isDir));

    /// <summary>Whether an excluded directory must still be descended because an include
    /// pattern targets something inside (or re-includes the directory itself).</summary>
    public bool HasIncludeInside(string dirRel, FilterComponent component = FilterComponent.Browser)
    {
        foreach (var inc in _include[component])
        {
            if (inc.StaticPrefix.StartsWith(dirRel + "/", StringComparison.OrdinalIgnoreCase)) return true;
            if (inc.Literal && string.Equals(inc.StaticPrefix, dirRel, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsDefaultIgnored(string name) => DefaultIgnoredDirs.Contains(name);

    private static bool Matches(Pattern p, string relPath, bool isDir)
    {
        if (p.Kind == ItemKind.Directory && !isDir) return false;
        if (p.Kind == ItemKind.File && isDir) return false;
        if (p.HasSeparator) return p.Regex.IsMatch(relPath);
        var name = relPath[(relPath.LastIndexOf('/') + 1)..];
        return p.Regex.IsMatch(relPath) || p.Regex.IsMatch(name);
    }

    // ── glob-list parsing ──────────────────────────────────────────────────────

    private static IEnumerable<string> ExpandList(string value)
    {
        foreach (var part in SplitTopLevel(value))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            foreach (var expanded in ExpandBraces(trimmed))
                if (expanded.Trim().Length > 0)
                    yield return expanded.Trim();
        }
    }

    /// <summary>Split on commas that are not nested inside a brace group.</summary>
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{') depth++;
            else if (c == '}') { if (depth > 0) depth--; }
            else if (c == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    /// <summary>Repeatedly expand the first (leftmost) brace group until none remain,
    /// so `{*.{o,pyc},tmp}` yields `*.o`, `*.pyc` and `tmp`.</summary>
    private static IEnumerable<string> ExpandBraces(string s)
    {
        var results = new List<string> { s };
        bool changed;
        do
        {
            changed = false;
            var next = new List<string>();
            foreach (var r in results)
            {
                var expanded = ExpandFirstBrace(r);
                if (expanded is null) next.Add(r);
                else { next.AddRange(expanded); changed = true; }
            }
            results = next;
        } while (changed);
        return results;
    }

    private static string[]? ExpandFirstBrace(string s)
    {
        int open = -1;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '{') { open = i; break; }
        if (open < 0) return null;

        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var inner = s.Substring(open + 1, i - open - 1);
                    return SplitTopLevel(inner).Select(alt => s[..open] + alt + s[(i + 1)..]).ToArray();
                }
            }
        }
        return null; // unbalanced — treat literally
    }

    // ── glob → regex ───────────────────────────────────────────────────────────

    private static Pattern Compile(string raw, ItemKind kind)
    {
        var pattern = raw.Replace('\\', '/');
        if (pattern.Length > 1 && pattern.EndsWith('/'))
        {
            kind = ItemKind.Directory; // a trailing slash pins the pattern to directories
            pattern = pattern.TrimEnd('/');
        }
        bool hasSeparator = pattern.Contains('/');

        var rx = new StringBuilder("^");
        bool hasWildcard = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '*':
                    hasWildcard = true;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; rx.Append("(?:.*/)?"); }
                        else rx.Append(".*");
                    }
                    else rx.Append("[^/]*");
                    break;
                case '?':
                    hasWildcard = true;
                    rx.Append("[^/]");
                    break;
                case '[':
                    hasWildcard = true;
                    int j = i + 1;
                    if (j < pattern.Length && pattern[j] == '!') j++;
                    while (j < pattern.Length && pattern[j] != ']') j++;
                    if (j < pattern.Length)
                    {
                        rx.Append(pattern.Substring(i, j - i + 1).Replace("[!", "[^"));
                        i = j;
                    }
                    else rx.Append("\\[");
                    break;
                default:
                    if ("\\.^$+(){}|".Contains(c)) rx.Append('\\');
                    rx.Append(c);
                    break;
            }
        }
        rx.Append('$');

        return new Pattern
        {
            Regex = new Regex(rx.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            HasSeparator = hasSeparator,
            Kind = kind,
            Literal = !hasWildcard,
            StaticPrefix = LiteralPrefix(pattern),
        };
    }

    private static string LiteralPrefix(string pattern)
    {
        var sb = new StringBuilder();
        foreach (var c in pattern)
        {
            if (c is '*' or '?' or '[' or '{') break;
            sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }
}
