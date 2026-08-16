# Ranger lite mode — Sophia's request (2026-08-16 18:54Z)

Her words: "a lite mode to Ranger — so none of the PresentMon stuff, just a BepInEx mod —
that just passively collects telemetry during raids and doesn't do any of the protocol
stuff. We could collect system information as well as map data, and then do as much
detailed timing of components as we possibly can, and then make an easy place for people to
upload samples for us to look into. That would probably move forward performance analysis
of this game by lightyears."

## Design shape (agreed in room 18:56Z)

- **Same codebase, build-configuration split**: bus/recorder core shared with full Ranger;
  the harness/PresentMon/protocol layer is the variable left out. Drop-in BepInEx mod, zero
  ceremony, no elevation.
- **What it records**: the existing header block (system, display, GPU, config snapshot —
  already captured today) + per-window samples + phase timings + map data. Every raid
  produces clean ndjson with zero user action.
- **The value**: a community corpus across *different* hardware — the axis a 3-person team
  can never sample — with the phase attribution already built.
- **Design guardrails from tonight's lessons**:
  1. Default-window discipline: default 30s windows; anything finer is opt-in. A community
     fleet running fine timers by default is the instrument-distorts-the-measured class.
  2. Privacy gate from day one: upload consent ("share my specs") + redaction pass (profile
     ids, character names in bot records). Header carries hardware + config + mod list —
     sensitive at fleet scale.
- **Open (her call, trails the mod)**: where uploaded samples land — infrastructure decision.

Status: queued into Ranger skeleton work (post-marathon). Companion docs: DESIGN.md (core),
STATUS-OVERLAY.md (widget, incl. confirmed mark-flash).
