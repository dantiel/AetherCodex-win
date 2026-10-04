# ÆtherCodex — Windows (Hermetic Manifest)

Native Windows port of the macOS [ÆtherCodex](https://dantiel.github.io/AetherCodex/)
editor. One soul, three faces: the Ruby intelligence core (Pythia, Mnemosyne,
companions, instrumentarium, argonaut) is canonical in the **AetherOracle** gem;
this repo is the Windows *body* — C# / .NET 8 / WPF — that speaks the framed-JSON
wire protocol over STDIN/STDOUT.

## Invariants

- **The Ruby daemon (`Ruby/standalone_daemon.rb`) is the soul.** Never reimplement
  oracle logic in C#; bridge it. The wire protocol (`ready` / `stream` / `result` /
  `recover_suggestion` frames) is the contract both bridges code against.
- **Config is polymorphic.** `activate_context` walks up to the nearest `.aethercodex`
  and re-points `project_root` + `memory_db`; the window inherits whatever project
  it opens. One window, many projects.
- **One repo per native editor.** macOS = Swift/AppKit, Windows = C#/WPF, Linux =
  future. The soul lives in AetherOracle and is *pinned*, never copied.

## Guidance

- Idiomatic .NET 8 / WPF, no Electron. AvalonEdit for the editor.
- Keep the C# bridge a faithful port of `RubyBridge.swift`.
- History sync is CLI-driven: Pythia mirrors Mnemosyne and rebuilds only when
  Mnemosyne advanced past the last rendered marker (never blindly).
- Themes/grammars ship under `Resources/`; the regex highlighter is a UI fallback
  until tree-sitter highlighting lands.
