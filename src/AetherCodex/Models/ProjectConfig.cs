namespace AetherCodex.Models;

/// <summary>A single effective config key: the value the Ruby soul resolves plus
/// the layer that produced it (<c>env</c>, <c>project</c>, <c>home</c>,
/// <c>bundle</c>, <c>preset</c> or <c>default</c>).</summary>
public sealed record ProjectConfigValue(string? Value, string Source);

/// <summary>
/// Effective project config surfaced by the daemon's <c>config</c> command. This
/// is a read-side projection for the Settings "Project" tab — the daemon remains
/// the single authority for how a value is actually resolved.
/// </summary>
public sealed class ProjectConfig
{
    public string? ProjectRoot { get; set; }
    public string? Name { get; set; }
    public string? ConfigPath { get; set; }
    public string Format { get; set; } = "yaml";

    /// <summary>Curated keys (name/model/fast-model/api-type/api-url/api-key/port/
    /// dev_mode) → effective value + source. Order mirrors <c>CONFIG::CONFIG_KEYS</c>.</summary>
    public Dictionary<string, ProjectConfigValue> Values { get; set; } = new();
}
