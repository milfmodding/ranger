# Ranger status overlay — Sophia's Galaxy-Brain request (2026-08-16)

Her words: "if we could add something to Ranger that just gives a small GUI widget in the
upper left or something to show: MEASUREMENT: ON / PROTOCOL: [name], [STARTED/STOPPED/DONE]
/ [maybe multiple protocols listed here?] / FPS: — Make it a config option to show, or give
it a hotkey, with just a simple text+red/green/yellow circles for status overlay, and it'd
make future measurements a lot easier."

**CONFIRMED feature, not just an idea — Sophia 18:39Z: "OH that's such a good idea for the
mouse3 flash. Love it, let's definitely include that."** Mark-flash is in on her explicit
call; the whole overlay is adopted.

## Why this is a strong fit (design notes for the Ranger skeleton)

1. **It's the artifact-check loop made human.** Tonight proved the pattern: every raid-start
   needed one of us to verify from disk that (a) telemetry registered, (b) protocol started,
   (c) arms advancing. The overlay puts exactly those three facts on her screen — the operator
   self-verifies without a seat in the loop. Tonight's auto-start failure (dirty build) would
   have shown "PROTOCOL: STOPPED" the moment she loaded in.
2. **Data sources already exist**: MEASUREMENT = the TelemetryBus.Enabled latch (the kit's
   own state); PROTOCOL = ProtocolRunner's step/arm state (the same block emitted per-window
   in ndjson); FPS = frame timing the kit already samples. No new instrumentation — this is a
   *view* over existing state, which is why it belongs in Ranger not Framesaver.
3. **Multiple protocols**: the current one-protocol-per-raid model means the list is usually
   one entry; the UI shape should accommodate the list anyway (future concurrent protocols —
   the shared-PageDown-key decision — would light this up).
4. **Config toggle + hotkey both** — matches Framesaver's existing config/hotkey conventions
   (ProtocolKey, MarkKey, GridSpawnKey are all configurable chords; ConfigurationManager is
   already a dependency of the install).
5. **Implementation notes for whoever builds it**: OnGUI text overlay (BepInEx 5 conventions,
   TextRenderingModule reference needed — DRIP.TexturePreview already proved the reference set
   on this BepInEx version) or IMGUI; colored-circle glyphs as plain Unicode (●/○) colored — no
   textures needed. Default OFF (her "config option to show").
6. **Mark-key flash (CONFIRMED)**: widget flashes when Mouse3 marks land — confirmed-capture
   feedback so she knows a hitch was captured without leaving the game. Her favorite part, per
   18:39Z.

Status: queued for the Ranger skeleton work (post-marathon). Not started as of the skeleton
commit — see EXTRACTION-PLAN.md for sequencing relative to the core bus/recorder move.
