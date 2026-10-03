# ÆtherCodex — Windows

> *The window is a glass athanor — an alchemical furnace for the AI century.*

A **native Windows** port of the macOS [ÆtherCodex](https://dantiel.github.io/AetherCodex/)
editor. Same soul, new body: the Ruby intelligence core (Pythia, Mnemosyne,
companions, instrumentarium) runs unchanged; the AppKit UI is rebuilt natively
for Windows with **C# / .NET 8 / WPF**. No Electron — the editor *is* the Æther.

**Status: Alpha v0.1.0** — the body is standing, the soul is breathing.

## What it is

- **Native Windows** — C# + WPF (XAML), .NET 8, DPI-aware. No web wrapper.
- **The Ruby soul** — `Ruby/standalone_daemon.rb` speaks a framed JSON protocol
  over STDIN/STDOUT: `ready` / `stream` / `result` / `recover_suggestion` frames.
  The daemon is the exact engine the macOS app and the TextMate plugin use.
- **Pythia panel** — embedded AI chat with streaming status, tool-call result
  cards, and the companion roster (🦉 Eule, 🦊 Kitsune, 🔥 Phönix, …) shipped in
  the daemon's `ready` frame.
- **Code editor** — AvalonEdit with line numbers, incremental search (Ctrl+F),
  regex syntax highlighting mapped to the 123 bundled TextMate themes.
- **File browser** — lazy directory tree; **Go to File** (Ctrl+P) for
  fuzzy-filename navigation.
- **Markdown** — live preview pane (`◉ Preview` toggle) rendered to WPF flow docs.
- **Æther Debug window** — the daemon's stderr ring buffer with restart/clear.

## Build

Requires the **.NET 8 SDK** and **Ruby 3.x** (with the daemon's gems bundled in
`Ruby/.vendor_bundle`).

```bat
dotnet build src\AetherCodex\AetherCodex.csproj -c Debug
dotnet run --project src\AetherCodex\AetherCodex.csproj
```

The build copies `Ruby\` and `Resources\` (themes + tree-sitter grammars/queries)
next to `AetherCodex.exe`.

## Configure Pythia

Copy `.aethercodex.example` to `.aethercodex` (project root or `$HOME`) and set
your API credentials — identical to the macOS app and the TextMate plugin. The
daemon resolves the nearest `.aethercodex` when you open a folder, so the whole
window is polymorphic: opening any project inherits that project's context.

## Architecture

```
src/AetherCodex/
  Bridge/RubyBridge.cs      ← C# port of RubyBridge.swift (framed JSON over pipes)
  Bridge/DaemonFrames.cs    ← wire-protocol models
  Editor/                   ← themes, YAML loader, language rules, regex highlighter
  Panels/                   ← FileBrowser, PythiaPanel, MarkdownRenderer
  Models/                   ← FileItem, AppSettings
  MainWindow.xaml(.cs)      ← three-pane shell (browser / editor+preview / Pythia)
  GoToFileWindow.xaml(.cs)  ← Ctrl+P file navigator
  DebugWindow.xaml(.cs)     ← daemon log
Ruby/                       ← the daemon (soul), vendored gems, config
Resources/Syntax, Themes/   ← grammars, queries, 123 YAML themes
```

### Wire protocol

The C# `RubyBridge` launches `Ruby/standalone_daemon.rb` as a child process and
exchanges newline-delimited JSON frames:

```
inbound:   { "command": "...", "arguments": {...}, "request_id": N }
outbound:  { "type":"ready", ... }
           { "type":"stream", "method":"status", "payload":{ "type":..., "data":... } }
           { "type":"result", "request_id":N, "success":..., "result":... }
```

Commands mirrored from `RubyBridge.swift`: `ping`, `remember`, `recall`,
`history`, `oracle` (chat/conjuration), `companion`, `aegis`, `activate_context`,
`interrupt`, `cli`, `create_task`, `execute_task`, `evaluate_task`,
`list_tasks`, `remove_task`.

## Roadmap

Already standing: native shell, daemon bridge, editor + highlighting, themes,
file browser, go-to-file, search, markdown preview, Pythia chat, debug window.

Not yet ported from the macOS app:

- tree-sitter highlighting engine (currently regex fallback; the grammars and
  queries are already bundled in `Resources/`).
- code folding, incremental-replace, multi-tab editor.
- HTML/image preview panes.
- companion picker UI (roster is rendered; active-companion selection is wired
  in the bridge, not the panel).
- Mnemosyne cauldron view, project/scope settings dialogs.
- acrylic/glass window backdrop (WPF `DwmSetWindowAttribute`).

## License

MIT — © 2026 darographix. Descends from
[ÆtherCodex](https://github.com/dantiel/AetherCodex) and the
[ÆtherCodex TextMate plugin](https://github.com/dantiel/AetherCodex-TextMate).
