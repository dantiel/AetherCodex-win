# Changelog

All notable changes to the ÆtherCodex Windows editor are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). The version
number lives in `src/AetherCodex/AetherCodex.csproj` (`<Version>`).

## [0.3.0] — 2026-10-16 — Third Alpha

The body learns to see: a Git view in the file browser with unified-diff
tabs, a rich-text ruler, a fully-drawn line-number gutter, HAML + Sass
highlighting, `.aether-properties` syntax mapping, incremental search, and
a scope-aware Find in Project.

### Added

- **Git view** — Files/Git mode toggle in the file browser; changed-files
  list backed by `GitService`, with unified diffs opened as read-only tabs.
- **Rich-text ruler** — centimeter scale with draggable first-line / hanging /
  right indentation markers over the rich surface.
- **Line-number gutter finishing** — per-line separators, a vertical divider to
  the code, and top/bottom edge lines framing the selected block.
- **HAML + Sass highlighting** — `.haml`, `.hamlc`, `.sass` in the language
  registry, with Sass `@mixin`/`@if`/`@media` directive colouring.
- **`.aether-properties`** — editor-level project config (`ProjectConfig.cs`)
  for per-project syntax overrides (e.g. `.umghaml → haml`, `.umgss → sass`).
- **Incremental search** — `Alt+S` / `Alt+Shift+S` for next/previous match.
- **Scope-aware Find in Project** — `Ctrl+F` (current file), `Ctrl+Shift+F`
  (project), `Ctrl+Alt+F` (folder); modeless and remembers last results.
- **`Alt+E`** — put the current selection into the find query.
- **Link Explorer to Active File** — expand + scroll to the active file on
  open and tab switch.

### Changed

- Go to File (`Ctrl+P`) restored to a floating modal window.
- Find in Project is modeless and persists its last result set.

## [0.2.0] — 2026-10-15 — Second Alpha

The body gains finesse: a live font-size zoom with a floating HUD, markup and
HAML highlighting, a per-item file-browser context menu, and the Go-to-File
modal dissolved into a bottom-docked quick-search overlay.

### Added

- **Font size zoom** — `Ctrl`+mouse wheel, `Ctrl`+`+`/`-`, and `Ctrl`+`0`,
  with a floating indicator (percent + absolute pt) and a Reset button.
- **Markup & HAML highlighting** — tag, `.class`, `#id`, and attribute
  colouring for HAML/HTML/XML in both the editor and markdown fenced code.
- **File browser context menu** — open, duplicate, new file/folder, refresh,
  reveal in Explorer, copy (relative) path, rename, delete (root protected).
- **Quick search overlay** — Go to File (`Ctrl`+`P`) is now a bottom-docked
  overlay, replacing the modal dialog.
- **Modeless Find in Project** (`Ctrl`+`Shift`+`F`) — a single reused window.
- **Haskell highlighting** + comment-inner markup (TODO/FIXME markers,
  `code`/**bold**/*italic* inside comments).
- **Custom gutter** — padded line numbers with active/selected-line tint.
- **Window transparency** — per-theme opacity with a user override
  (Settings → Appearance).
- **Tabbed settings dialog** (General / Editor / Appearance / AI Provider).
- **Theme contrast fixes** — sidebar + menu colours with a WCAG 4.5:1 floor.
- **Markdown table emphasis** — inline **bold**, `code`, links in table cells.
- **Theme-scope typography** — configurable Syne UI font (auto-install).

### Changed

- Go to File is no longer a modal; it is the quick-search overlay.

## [0.1.0] — 2026-10-12 — First Alpha

The body is standing, the soul is breathing: the first runnable native
Windows port of ÆtherCodex.

### Added

- Native Windows shell — C# / .NET 8 / WPF, DPI-aware, no Electron.
- Ruby intelligence core (`Ruby/standalone_daemon.rb`) bridged over a framed
  JSON protocol (`ready` / `stream` / `result` / `recover_suggestion`).
- **Pythia panel** — embedded AI chat with streaming status, tool-call result
  cards, and the companion roster (🦉 Eule, 🦊 Kitsune, 🔥 Phönix, …).
- **Code editor** — AvalonEdit with line numbers, incremental search (Ctrl+F),
  regex syntax highlighting mapped to the 123 bundled TextMate themes.
- **Tabbed documents** with per-language filetype glyphs, project-relative
  paths, and a syntax-highlighting dropdown in the titlebar.
- **Markdown** — rich-text editing with raw/rich "Source" toggle (moved into
  the titlebar) and a live preview pane.
- **File browser** — lazy directory tree; **Go to File** (Ctrl+P).
- **Find in Project** (Ctrl+Shift+F) and multi-cursor editing (Ctrl+W /
  Ctrl+Shift+W).
- **Æther Debug window** — daemon stderr ring buffer with restart/clear.
- Window chrome (native Win11 caption buttons), configurable application sigil
  (Settings → WINDOW → Sigil).
- Release tooling: `tools/make_release.ps1` (self-contained publish + zip +
  sha256 + git tag).

### Requirements

- Windows 10/11 x64.
- Ruby 3.3.x (x64) on PATH — the vendored gems are compiled for the
  `x64-mingw-ucrt` ABI (`Ruby/.vendor_bundle/ruby/3.3.0`).

[0.1.0]: https://github.com/dantiel/AetherCodex-win/releases/tag/v0.1.0