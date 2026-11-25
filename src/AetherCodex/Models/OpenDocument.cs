using System.Windows.Documents;
using AetherCodex.Git;
using AetherCodex.Spreadsheet;

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

    /// <summary>A spreadsheet (Calc) buffer; rendered by <c>SheetEditor</c>.</summary>
    public bool IsSheet { get; set; }
    public Sheet? Sheet { get; set; }

    /// <summary>A browser tab; rendered by <c>BrowserView</c>.</summary>
    public bool IsBrowser { get; set; }
    /// <summary>The committed URL of the browser tab (kept fresh on tab switch).</summary>
    public string Url { get; set; } = "";
    /// <summary>Page title, shown as the tab caption when present.</summary>
    public string BrowserTitle { get; set; } = "";

    /// <summary>Read-only surface (diff views); the editor disables edits.</summary>
    public bool IsReadOnly { get; set; }
    /// <summary>A git diff buffer, not a real file on disk — never saved.</summary>
    public bool IsDiff { get; set; }
    /// <summary>Tab caption for diff buffers.</summary>
    public string DiffTitle { get; set; } = "";
    /// <summary>HEAD (old) text of a diff buffer.</summary>
    public string DiffOld { get; set; } = "";
    /// <summary>Working-tree (new) text of a diff buffer.</summary>
    public string DiffNew { get; set; } = "";
    /// <summary>Repo root the diff was computed against (for the git fallback).</summary>
    public string DiffRepoRoot { get; set; } = "";
    /// <summary>The git change backing a diff buffer (for the git fallback).</summary>
    public GitChange? DiffChange { get; set; }

    public bool IsMarkdown => Language == "Markdown";

    public string DisplayName
    {
        get
        {
            if (IsDiff) return "⇄ " + DiffTitle;
            if (IsBrowser)
                return "▣ " + (BrowserTitle.Length > 0 ? BrowserTitle : Url.Length > 0 ? Url : "New Tab");
            return (Dirty ? "● " : "") + (IsSheet ? "▦ " : "") + (Path.Length == 0 ? "untitled" : System.IO.Path.GetFileName(Path));
        }
    }
}