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

    public sealed class Entry
    {
        public List<string> Searches { get; set; } = new();
        public List<string> Regexes { get; set; } = new();
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