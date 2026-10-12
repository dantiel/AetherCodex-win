namespace AetherCodex.Models;

/// <summary>A selectable AI model preset mirroring AetherOracle's <c>CONFIG::MODELS</c>.</summary>
public sealed record OracleModel(string Id, string FastModel, string ApiType, string ApiUrl);

/// <summary>A provider (DeepSeek, OpenAI, …) together with its known models.</summary>
public sealed record OracleProvider(string Key, string Name, IReadOnlyList<OracleModel> Models);

/// <summary>
/// Static mirror of AetherOracle's model registry — the same providers, models,
/// fast models, API types and endpoints the Ruby soul already knows. This exists
/// only to drive the settings UI; the daemon remains the single authority for
/// how a turn is actually routed.
/// </summary>
public static class OracleProviders
{
    public static readonly IReadOnlyList<OracleProvider> All = new OracleProvider[]
    {
        new("deepseek", "DeepSeek", new OracleModel[]
        {
            new("deepseek-chat",     "deepseek-chat", "deepseek", "https://api.deepseek.com/v1/chat/completions"),
            new("deepseek-reasoner", "deepseek-chat", "deepseek", "https://api.deepseek.com/v1/chat/completions"),
        }),
        new("openai", "OpenAI", new OracleModel[]
        {
            new("gpt-4o",      "gpt-4o-mini",  "openai", "https://api.openai.com/v1/chat/completions"),
            new("gpt-4o-mini", "gpt-4o-mini",  "openai", "https://api.openai.com/v1/chat/completions"),
            new("gpt-4.1",     "gpt-4.1-mini", "openai", "https://api.openai.com/v1/chat/completions"),
            new("gpt-4.1-mini","gpt-4.1-mini", "openai", "https://api.openai.com/v1/chat/completions"),
            new("gpt-5",       "gpt-5-mini",   "openai", "https://api.openai.com/v1/chat/completions"),
            new("gpt-5-mini",  "gpt-5-mini",   "openai", "https://api.openai.com/v1/chat/completions"),
        }),
        new("anthropic", "Anthropic", new OracleModel[]
        {
            new("claude-sonnet-4-5", "claude-sonnet-4-5", "anthropic", "https://api.anthropic.com/v1/messages"),
            new("claude-opus-4-5",   "claude-opus-4-5",   "anthropic", "https://api.anthropic.com/v1/messages"),
            new("claude-haiku-4-5",  "claude-haiku-4-5",  "anthropic", "https://api.anthropic.com/v1/messages"),
        }),
        new("gemini", "Gemini", new OracleModel[]
        {
            new("gemini-2.5-pro",   "gemini-2.5-flash", "gemini", "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"),
            new("gemini-2.5-flash", "gemini-2.5-flash", "gemini", "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"),
        }),
        new("custom", "Custom", Array.Empty<OracleModel>()),
    };

    public static OracleProvider? FindProvider(string? key) =>
        All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static OracleModel? FindModel(string? modelId) =>
        All.SelectMany(p => p.Models)
           .FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Map a saved model id back to its provider key (defaults to DeepSeek).</summary>
    public static string ProviderKeyForModel(string? modelId)
        => FindModel(modelId) is { } m
            ? All.First(p => p.Models.Contains(m)).Key
            : "deepseek";
}
