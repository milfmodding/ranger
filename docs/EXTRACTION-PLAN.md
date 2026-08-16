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

## Boundary re-verification findings (2026-08-16, against current Framesaver source)

Checked every `using Framesaver.Patches` and every bare `Patches.X` reference across the four
core files.

**Clean cuts — no coupling into shipping-feature classes, move whole with zero surgery:**
- `PlayerLoopProfiler.cs` — no Framesaver usings at all.
- `ProtocolRunner.cs` — no Framesaver usings at all.
- `GpuTelemetry.cs` — carries `using Framesaver.Patches;` but it's vestigial: grepped every
  known Patches class name against the file, zero actual references. Safe to move as-is
  (drop the unused using in the same commit).

**Needs real surgery — `Telemetry.cs` (2,533 ln), confirmed by line-level grep:**

Directly calls into THREE shipping-feature classes that stay in Framesaver (the boundary
inversion DESIGN.md section 1 describes, now with exact line numbers as of this commit):
- `SleepingBotAnimatorPatch.{ResetForRaid, ReadAndReset, CulledLastFrame, CulledOffScreen,
  CulledEngine}` — lines 416, 1060, 1501, 1507, 1515. This is the shipping anim-cull feature
  (A-bucket, stays); Telemetry reads its counters for the `animCulled`/etc NDJSON fields.
- `RoleSleepDistance.{Effective, EffectiveWake, RoleNames}` — lines 1736, 1737, 2415. Shipping
  feature (role-sleep distance), Telemetry reads its config-derived values for NDJSON.
- `BossGroupWake.Counts` — line 1495. Shipping feature (boss group wake), Telemetry reads a
  linked/held count pair.

Also calls into several patch classes that DO move (measurement-only, confirmed): `Census`
(363, 417, 2120), `BotLog` (418, 1773), `AwakeAge` (419, 1380, 1779, 2258), `DistanceGridSpawn`
(435, 437 — NOTE: strip-list v2 already sorted DistanceGridSpawn into bucket B, i.e. "grid-spawn
4" — cross-check against the strip list before assuming this one moves), `UpdateManualTiming`
(1366, 2258), `StandByTransitions` (1373, 2259), `TriggerSubscribers` (1385), `BossSpawnGate`
(1416, 1419 — measurement-only per DESIGN.md, but the name is close enough to BossGroupWake
that it's worth double-checking these aren't secretly the same coupling).

**Consequence for sequencing**: the three clean files can move in one mechanical `git mv` +
using-cleanup commit with essentially no risk. `Telemetry.cs` needs the actual bus-inversion
work DESIGN.md section 1 describes — replacing those 3 shipping-class call sites with
`TelemetryBus.Count/Event/Tag` calls made FROM the shipping classes themselves (they publish),
rather than Telemetry.cs reaching into them (it currently pulls) — before it can move. That is
real surgery on production code Sophia must be able to maintain, not a mechanical move, and it
touches the highest-line-count, most load-bearing file in the mod. Doing it carefully, in its
own reviewable commit, separate from the clean-file move, is worth the extra round trip.

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
