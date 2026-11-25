using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherCodex.Models;

/// <summary>
/// A project bookmark shown in the Projects (★) sidebar. Favourites are global
/// (persisted in the user's home directory, independent of the open project);
/// ÆtherLink peers discovered by the Ruby soul are surfaced in the same view
/// with <see cref="Source"/> == "aetherlink" so they can be starred as well.
/// </summary>
public sealed class Favourite
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    // ── Runtime-only (never persisted) — populated when merging ÆtherLink peers ──
    [JsonIgnore] public string Source { get; set; } = "favourite"; // favourite | aetherlink
    [JsonIgnore] public bool Online { get; set; }
    [JsonIgnore] public int Port { get; set; }
    [JsonIgnore] public string? Description { get; set; }
    [JsonIgnore] public List<string> Tags { get; set; } = new();

    public string Glyph => Source == "aetherlink" ? "\uD83C\uDF10" : "\uD83D\uDCC1"; // 🌐 | 📁

    [JsonIgnore] public string ActionGlyph => Source == "aetherlink" ? "\u2605" : "\u2715"; // ★ | ✕
    [JsonIgnore] public string ActionToolTip => Source == "aetherlink"
        ? "Add to favourites"
        : "Remove from favourites";
}

/// <summary>
/// Global project favourites, persisted to <c>~/.aether-favourites</c> — a YAML
/// file (matching the <c>~/.aethercodex</c> convention) that is a *sibling* of the
/// <c>~/.aethercodex</c> config FILE, never a child of it. A single list shared
/// across every project the editor opens.
/// </summary>
public sealed class Favourites
{
    public List<Favourite> Items { get; set; } = new();

    private const string Header = "# === AETHER FAVOURITES (global project bookmarks) ========================";

    /// <summary>Absolute path to the global favourites YAML file in the user home dir.</summary>
    public static string FilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".aether-favourites");

    /// <summary>Read existing favourites; an absent/unparseable file yields an empty list
    /// (never throws). If the file already exists its contents are *used*, not replaced.</summary>
    public static Favourites Load()
    {
        var result = new Favourites();
        var path = FilePath();
        if (!File.Exists(path)) return result;
        try { result.Items = Parse(File.ReadAllLines(path)); }
        catch { result.Items = new List<Favourite>(); }
        return result;
    }

    /// <summary>Persist the list atomically (temp file + move-over). An existing file is
    /// overwritten with the merged contents — never left half-written, never recreated.</summary>
    public void Save()
    {
        var path = FilePath();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Render(Items));
        File.Move(tmp, path, overwrite: true);
    }

    // ── Minimal YAML (subset) — enough for our own shape, tolerant of hand edits ──

    private static string Render(List<Favourite> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine("favourites:");
        foreach (var item in items)
        {
            sb.AppendLine($"  - name: {JsonSerializer.Serialize(item.Name)}");
            sb.AppendLine($"    path: {JsonSerializer.Serialize(item.Path)}");
        }
        return sb.ToString();
    }

    private static List<Favourite> Parse(IEnumerable<string> lines)
    {
        var items = new List<Favourite>();
        Favourite? cur = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("- name:"))
            {
                cur = new Favourite { Name = ParseValue(line["- name:".Length..]) };
                items.Add(cur);
            }
            else if (cur is not null && line.StartsWith("path:"))
            {
                cur.Path = ParseValue(line["path:".Length..]);
            }
            // `favourites:` header and anything else are ignored.
        }
        return items.Where(f => f.Name.Length > 0 || f.Path.Length > 0).ToList();
    }

    private static string ParseValue(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(s) ?? ""; }
            catch { return s[1..^1]; }
        }
        // Plain scalar: drop a trailing " #" comment.
        var hash = s.IndexOf(" #");
        if (hash >= 0) s = s[..hash].TrimEnd();
        return s;
    }
}
