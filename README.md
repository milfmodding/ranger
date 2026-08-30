# Ranger

The standalone telemetry kit for SPT performance work — extracted from Framesaver so any mod
can record durable per-window frame telemetry with a five-line integration, instead of building
its own recorder.

**Status: extraction complete.** The recorder core moved from Framesaver at the capstone
(2026-08-19): the NDJSON writer, per-window sampler, spike recorder, protocol runner, and the
measurement-only Harmony patches live in this repo (see `docs/EXTRACTION-PLAN.md` for the
order it happened in). Framesaver remains the reference consumer — its own telemetry calls
are the worked example of how a mod uses `TelemetryBus`.

## Design

- `docs/DESIGN.md` — the `TelemetryBus` API (`Count`/`Event`/`Tag`), the boundary rule between
  "the kit records facts" and "the mod produces them", packaging (soft dependency, two separate
  downloads), and the open questions this design still needs answered before extraction starts.
- `docs/STATUS-OVERLAY.md` — the confirmed MEASUREMENT/PROTOCOL/FPS overlay widget with
  mark-flash feedback on capture (Sophia's explicit "definitely include" call).
- `docs/LITE-MODE.md` — the passive, protocol-free distribution mode for building a community
  telemetry corpus across hardware the core team can't otherwise sample.

## Build

Same convention as Framesaver: `dotnet build -c Release` compiles only; add `-p:Deploy=true` to
copy the built DLL into the configured SPT install's `BepInEx/plugins/`. `SptDir` in
`Ranger.csproj` currently points at `F:\SPT\SPT-4.1`.

The dirty-tree deploy gate Framesaver carries (`RefuseDirtyDeploy`) was ported into
`Ranger.csproj` on 2026-08-19, once extraction landed real source history to protect.
