using System.Windows.Documents;

namespace AetherCodex.Models;

/// <summary>
/// One open buffer in the tab bar. A document is either a code/plain-text buffer
/// (held in <see cref="Text"/>) or a rich/markdown buffer (held in <see cref="Rich"/>);
/// the editor surface renders whichever is active.
/// </summary>
public sealed class OpenDocument
{
    public string Path { get; set; } = "";
    public bool IsRich { get; set; }
    public string Language { get; set; } = "Plain Text";
    public string Text { get; set; } = "";
    public FlowDocument? Rich { get; set; }
    /// <summary>Raw markdown source — canonical for .md buffers; <see cref="Rich"/> is its rendered cache.</summary>
    public string Markdown { get; set; } = "";
    public bool Dirty { get; set; }

    /// <summary>Read-only surface (diff views); the editor disables edits.</summary>
    public bool IsReadOnly { get; set; }
    /// <summary>A git diff buffer, not a real file on disk — never saved.</summary>
    public bool IsDiff { get; set; }
    /// <summary>Tab caption for diff buffers.</summary>
    public string DiffTitle { get; set; } = "";

    public bool IsMarkdown => Language == "Markdown";

    public string DisplayName =>
        IsDiff
            ? "⇄ " + DiffTitle
            : (Dirty ? "● " : "") + (Path.Length == 0 ? "untitled" : System.IO.Path.GetFileName(Path));
}