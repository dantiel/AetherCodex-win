using System.Text.Json;

namespace AetherCodex.Models;

/// <summary>
/// Per-project find-in-project memory. Each project root carries its own recent
/// search queries and regex patterns — the ætheric ledger of where the eye has
/// looked. Persisted to %APPDATA%\AetherCodex\search-history.json.
/// </summary>
public sealed class SearchHistory
{
    public Dictionary<string, Entry> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public LastState? Last { get; set; }

    public sealed class Entry
    {
        public List<string> Searches { get; set; } = new();
        public List<string> Regexes { get; set; } = new();
    }

    /// <summary>The most recent find-in-project run, restored when the window reopens.</summary>
    public sealed class LastState
    {
        public string Query { get; set; } = "";
        public bool Regex { get; set; }
        public bool CaseSensitive { get; set; }
        public bool WholeWord { get; set; }
        public int Scope { get; set; }
        public string? FolderPath { get; set; }
        public string FileTypes { get; set; } = "";
        public string Footer { get; set; } = "";
        public List<LastResult> Results { get; set; } = new();
    }

    public sealed class LastResult
    {
        public string Glyph { get; set; } = "";
        public string Header { get; set; } = "";
        public string Snippet { get; set; } = "";
        public string File { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
    }

    private const int MaxEntries = 50;

    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AetherCodex", "search-history.json");

    public static SearchHistory Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<SearchHistory>(File.ReadAllText(HistoryPath)) ?? new SearchHistory();
        }
        catch { /* fall through to defaults */ }
        return new SearchHistory();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    public IReadOnlyList<string> QueriesFor(string root, bool regex)
    {
        var entry = Projects.TryGetValue(root, out var existing) ? existing : null;
        var list = regex ? entry?.Regexes : entry?.Searches;
        return list ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    public void Remember(string root, string query, bool regex)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        if (!Projects.TryGetValue(root, out var entry))
            Projects[root] = entry = new Entry();
        var list = regex ? entry.Regexes : entry.Searches;
        list.RemoveAll(q => q.Equals(query, StringComparison.Ordinal));
        list.Insert(0, query);
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
    }
}