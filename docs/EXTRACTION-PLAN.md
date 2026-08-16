# Extraction plan (Echo, 2026-08-16, started post-marathon)

Tracks the mechanical move of telemetry code from Framesaver into this repo. See DESIGN.md
for the "why" and the boundary rules; this doc is the "what, in what order" checklist.

## Prerequisites (all now satisfied)

- [x] Marathon complete, deployed binary's md5-stability constraint lifted.
- [x] Tau's post-marathon clean rebuild landed (fixes the dirty-tree-deploy stamp bug —
      `reg-dec-2026-08-16T213832` — as a side effect, this also unblocks the loading-freeze
      investigation's wall-clock anchor field).
- [x] Ranger repo skeleton exists (this commit): csproj, Plugin.cs stub, docs, .gitignore.
- [x] Sophia's go-ahead to start (22:04Z, room): "start looking at pulling out Ranger, since
      I won't have time to do raids for a little bit."

## Inventory (unchanged from DESIGN.md section 0)

**Moves to Ranger** (pure instrumentation):
- `Telemetry.cs`, `PlayerLoopProfiler.cs`, `GpuTelemetry.cs`, `ProtocolRunner.cs`
- ~16 measurement-only patches (see DESIGN.md for the full list)
- 10 telemetry-only config entries

**Stays in Framesaver** (shipping features): everything else — bot stand-by, brain
scheduler, sleeping-bot animator cull, leak fix, max-delta clamp, drain-in-Update-only,
role-sleep distance, boss group wake, long-range exemption, etc.

## Sequencing

1. **Skeleton (this commit)** — repo, csproj, docs. No Framesaver code touched yet.
2. **Boundary re-verification against current Framesaver source** — the DESIGN.md draft
   predates Tau's 4.1 port AND tonight's marathon protocol work (`ProtocolRunner` gained
   arm-tracking fields, `Telemetry.cs` gained the whole-population census fields used in
   tonight's cross-check). Re-read the actual current files before assuming the file
   inventory and publish-site list from the draft are still accurate — they were written
   against an earlier snapshot.
3. **Mechanical `git mv` extraction** — move the confirmed instrumentation-only files/patches
   into this repo with history, add `TelemetryBus`, repoint Framesaver's publish sites to
   the bus. No behaviour change; NDJSON field names preserved verbatim (corpus comparisons
   depend on this — the `agents` block and header fields must arrive unchanged).
4. **Framesaver repoints**: `[BepInDependency(rangerGuid, DependencyFlags.SoftDependency)]`,
   publish calls become `if (TelemetryBus.Enabled) TelemetryBus.X(...)`.
5. **Status overlay** (STATUS-OVERLAY.md) and **lite mode** (LITE-MODE.md) build on top of
   the extracted core once it's landed and verified against a real raid.

## Risks carried in from the design draft (still apply)

- The untracked-six problem class: use `git mv`, not delete+recreate, so history follows.
- `analysis/*.py` scripts may hard-code Framesaver's log source path or directory —
  check before the log writer moves.
- `attribute-log.py`'s `deferToAiMods` heuristic reads a header field that must survive
  the move verbatim.
- Sophia's Gate 2 (must maintain both mods unaided): Ranger's README needs the same
  no-terminal usage story Framesaver's does, if she ever runs it directly.

## Log

- 2026-08-16 22:0x — skeleton created (csproj, Plugin.cs, docs, .gitignore), repo cloned
  and confirmed empty/pushable. Next: re-verify the file inventory against current
  Framesaver source (step 2 above) before any `git mv`.
