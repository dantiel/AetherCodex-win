using System.Text.RegularExpressions;

namespace AetherCodex.Editor;

/// <summary>Regex-based token rules for a language, mapped to TextMate scope names.</summary>
public sealed class LanguageDefinition
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string[] Extensions { get; init; }
    public string[] LineComments { get; init; } = Array.Empty<string>();
    public (string Start, string End)[] BlockComments { get; init; } = Array.Empty<(string, string)>();
    public char[] StringQuotes { get; init; } = { '"', '\'' };
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public string[] Types { get; init; } = Array.Empty<string>();
    public string[] Constants { get; init; } = Array.Empty<string>();
    public string[] Builtins { get; init; } = Array.Empty<string>();

    private Regex? _keyword;
    private Regex? _type;
    private Regex? _constant;
    private Regex? _builtin;
    private Regex? _number;
    private Regex? _functionCall;
    private Regex? _comment;

    public Regex KeywordRegex => _keyword ??= BuildAlternation(Keywords, "keyword");
    public Regex TypeRegex => _type ??= BuildAlternation(Types, "type");
    public Regex ConstantRegex => _constant ??= BuildAlternation(Constants, "constant.language");
    public Regex BuiltinRegex => _builtin ??= BuildAlternation(Builtins, "support.function");
    public Regex NumberRegex => _number ??= new Regex(@"\b(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(\.\d[\d_]*)?([eE][+-]?\d+)?)\b", RegexOptions.Compiled);
    public Regex FunctionCallRegex => _functionCall ??= new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*(?=\s*\()", RegexOptions.Compiled);
    public Regex CommentRegex => _comment ??= BuildCommentRegex();

    private static Regex BuildAlternation(string[] words, string _)
    {
        if (words.Length == 0) return new Regex(@"\b\B", RegexOptions.Compiled); // never matches
        var escaped = words
            .Select(Regex.Escape)
            .OrderByDescending(w => w.Length)
            .ToArray();
        return new Regex($@"\b(?:{string.Join('|', escaped)})\b", RegexOptions.Compiled);
    }

    private Regex BuildCommentRegex()
    {
        if (LineComments.Length == 0) return new Regex(@"\b\B", RegexOptions.Compiled);
        var escaped = LineComments.Select(Regex.Escape).ToArray();
        return new Regex($"(?:{string.Join('|', escaped)}).*", RegexOptions.Compiled);
    }
}

/// <summary>Extension → language registry (the ÆtherCodex language map).</summary>
public static class Languages
{
    public static readonly IReadOnlyList<LanguageDefinition> All = new[]
    {
        new LanguageDefinition
        {
            Key = "ruby", Name = "Ruby",
            Extensions = new[] { ".rb", ".rake", ".gemspec", "Gemfile", "Rakefile" },
            LineComments = new[] { "#" },
            BlockComments = new[] { ("=begin", "=end") },
            Keywords = "def end class module if elsif else unless while until for do return yield begin rescue ensure case when then and or not nil true false self super break next redo retry in".Split(' '),
            Types = "String Integer Float Array Hash Symbol Proc Object".Split(' '),
            Constants = "true false nil".Split(' '),
            Builtins = "puts print p require require_relative include extend attr_accessor attr_reader attr_writer raise lambda proc".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "python", Name = "Python",
            Extensions = new[] { ".py", ".pyw" },
            LineComments = new[] { "#" },
            StringQuotes = new[] { '"', '\'' },
            Keywords = "def class if elif else while for in return yield import from as try except finally with lambda global nonlocal pass break continue raise assert del not and or is None True False async await match case".Split(' '),
            Types = "int float str bool list dict tuple set bytes".Split(' '),
            Constants = "True False None".Split(' '),
            Builtins = "print len range enumerate zip map filter open isinstance".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "javascript", Name = "JavaScript",
            Extensions = new[] { ".js", ".jsx", ".mjs", ".cjs" },
            LineComments = new[] { "//" },
            BlockComments = new[] { ("/*", "*/") },
            Keywords = "function const let var if else for while do return new class extends super this typeof instanceof in of try catch finally throw switch case default break continue import export from as async await yield null undefined true false delete void".Split(' '),
            Types = "Number String Boolean Object Array Function Promise Map Set Symbol".Split(' '),
            Constants = "true false null undefined NaN Infinity".Split(' '),
            Builtins = "console.log Math JSON Date parseInt parseFloat".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "typescript", Name = "TypeScript",
            Extensions = new[] { ".ts", ".tsx", ".mts", ".cts" },
            LineComments = new[] { "//" },
            BlockComments = new[] { ("/*", "*/") },
            Keywords = "function const let var if else for while do return new class extends super this typeof instanceof in of try catch finally throw switch case default break continue import export from as async await yield null undefined true false delete void interface type enum implements namespace declare readonly public private protected abstract static keyof infer never unknown any".Split(' '),
            Types = "number string boolean object array function promise map set symbol void any unknown never".Split(' '),
            Constants = "true false null undefined NaN Infinity".Split(' '),
            Builtins = "console.log Math JSON Date parseInt parseFloat".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "csharp", Name = "C#",
            Extensions = new[] { ".cs" },
            LineComments = new[] { "//" },
            BlockComments = new[] { ("/*", "*/") },
            Keywords = "using namespace class struct interface enum delegate event public private protected internal static readonly const virtual override abstract sealed async await new this base return if else for foreach while do switch case default break continue try catch finally throw in out ref get set var null true false is as lock typeof nameof".Split(' '),
            Types = "int long short byte uint ulong ushort float double decimal bool char string object void dynamic".Split(' '),
            Constants = "true false null".Split(' '),
            Builtins = "Console.WriteLine Console.ReadLine Math String Int32".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "c", Name = "C",
            Extensions = new[] { ".c", ".h" },
            LineComments = new[] { "//" },
            BlockComments = new[] { ("/*", "*/") },
            Keywords = "if else for while do switch case default break continue return struct union enum typedef static extern const volatile register signed unsigned sizeof goto void char int long short float double auto inline restrict _Bool _Complex _Imaginary".Split(' '),
            Types = "int long short float double char void unsigned signed size_t ssize_t".Split(' '),
            Constants = "NULL true false".Split(' '),
            Builtins = "printf scanf malloc free memcpy memset".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "cpp", Name = "C++",
            Extensions = new[] { ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx" },
            LineComments = new[] { "//" },
            BlockComments = new[] { ("/*", "*/") },
            Keywords = "if else for while do switch case default break continue return struct union enum typedef static extern const volatile register signed unsigned sizeof goto void char int long short float double auto class template typename namespace using public private protected virtual override final friend new delete this try catch throw constexpr noexcept decltype".Split(' '),
            Types = "int long short float double char void unsigned signed size_t string vector map".Split(' '),
            Constants = "nullptr true false NULL".Split(' '),
            Builtins = "std cout cin printf malloc free".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "json", Name = "JSON",
            Extensions = new[] { ".json", ".jsonc" },
            LineComments = new[] { "//" },
            BlockComments = new[] { ("/*", "*/") },
            Keywords = "true false null".Split(' '),
            Constants = "true false null".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "xml", Name = "XML/HTML",
            Extensions = new[] { ".xml", ".html", ".htm", ".xaml", ".svg", ".plist" },
            LineComments = Array.Empty<string>(),
            BlockComments = new[] { ("<!--", "-->") },
            Keywords = Array.Empty<string>(),
            Types = Array.Empty<string>(),
            Constants = Array.Empty<string>(),
        },
        new LanguageDefinition
        {
            Key = "css", Name = "CSS",
            Extensions = new[] { ".css", ".scss", ".sass", ".less" },
            LineComments = new[] { "//" },
            BlockComments = new[] { ("/*", "*/") },
            Keywords = "important".Split(' '),
            Constants = "true false null".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "shell", Name = "Shell",
            Extensions = new[] { ".sh", ".bash", ".zsh", ".fish" },
            LineComments = new[] { "#" },
            Keywords = "if then else elif fi for while do done case esac function return exit source export local readonly echo cd".Split(' '),
            Constants = "true false".Split(' '),
            Builtins = "echo printf cd ls grep sed awk".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "markdown", Name = "Markdown",
            Extensions = new[] { ".md", ".markdown", ".mdown" },
            LineComments = Array.Empty<string>(),
            Keywords = Array.Empty<string>(),
        },
        new LanguageDefinition
        {
            Key = "yaml", Name = "YAML",
            Extensions = new[] { ".yml", ".yaml" },
            LineComments = new[] { "#" },
            Keywords = "true false null".Split(' '),
            Constants = "true false null".Split(' '),
        },
        new LanguageDefinition
        {
            Key = "plain", Name = "Plain Text",
            Extensions = new[] { ".txt" },
        },
    };

    private static readonly Dictionary<string, LanguageDefinition> ByExtension = BuildIndex();

    private static Dictionary<string, LanguageDefinition> BuildIndex()
    {
        var index = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in All)
            foreach (var ext in lang.Extensions)
                index[ext] = lang;
        return index;
    }

    public static LanguageDefinition FromPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && ByExtension.TryGetValue(ext, out var byExt))
            return byExt;
        var fileName = Path.GetFileName(path);
        if (ByExtension.TryGetValue(fileName, out var byName))
            return byName;
        return ByExtension[".txt"];
    }
}