# Field mapping: pre-capstone flat paths -> post-capstone nested paths

Written at the capstone cutover commit (2026-08-19). For anyone maintaining
`harness/check-*.py` scripts (in either repo) that read Framesaver's old NDJSON
output by flat path name. (The sibling Framesaver `analysis/*.py` corpus was
deleted 2026-08-29 - commit 0588c35 - so this doc's remaining audience is the
harness checkers.)

## Why this exists

Before the capstone, one process (Framesaver) wrote one flat NDJSON object per
line. After the capstone, Ranger writes the ndjson, and every field a
Framesaver-owned shipping class contributes is nested under its own mod-guid
key rather than merged into the top level. This is Sophia's ruling (room,
2026-08-18 06:33Z-ish window): **new fields use the nested `"[modGuid]":{...}`
shape from the start, no flat-path back-compat for the migrated ones.**

`framesaver.ai.perf` is the nested key every Framesaver-contributed field now
lives under (`RangerBridge.FramesaverGuid`, matching Framesaver's own
`[BepInPlugin]` guid).

## What did NOT move (still flat, unaffected)

Everything Ranger's own capstone-coupled classes (`Telemetry`, `AwakeAge`,
`BotBackup`, `ProtocolRunner`, `BotLog`, `PlayerLoopProfiler`, `GpuTelemetry`,
`Census`, `StandByTransitions`, `TriggerSubscribers`, `UpdateManualTiming`,
`LateTiming`, the spawn/bundle/profile/raid-init family) always wrote is
**unchanged** — same flat field names, same shape, just now written by
Ranger's assembly instead of Framesaver's. If a script reads `standByRefused`,
`awakeAge`, `botBackup`, `protocol`, `census` lines, `spawn.*`, `bundleLoad.*`,
`raidInit.*`, `jobSchedulerLate`/`playerLate`/`playerTick`/`ambientLight`,
`profileBuild.*`, `qpc`, `qpcFrequency`, `gfx.*` — nothing to change.

## What DID move (old flat path -> new nested path)

All of these used to be flat top-level (or `cfg.*`-nested) fields written
directly by Framesaver's `Telemetry.cs`. They are now written by
`Framesaver/CapstoneCallbacks.cs`'s registered header/window/spike callbacks,
nested under `"framesaver.ai.perf":{...}`.

**Window line, from `CapstoneCallbacks.BuildWindow`:**

| Old flat path | New path |
|---|---|
| `snipersAwake` | `framesaver.ai.perf.snipersAwake` |
| `bossGroups.linked` | `framesaver.ai.perf.bossGroups.linked` |
| `bossGroups.heldAwake` | `framesaver.ai.perf.bossGroups.heldAwake` |
| `bots.animCulled` | `framesaver.ai.perf.botsAnim.animCulled` (see note below) |
| `bots.animCulledOffScreen` | `framesaver.ai.perf.botsAnim.animCulledOffScreen` |
| `bots.animCulledEngine` | `framesaver.ai.perf.botsAnim.animCulledEngine` |
| `agents.live` | `framesaver.ai.perf.agents.live` |
| `agents.pendingRemoval` | `framesaver.ai.perf.agents.pendingRemoval` |
| `agents.removedTotal` | `framesaver.ai.perf.agents.removedTotal` |
| `agents.slicing` | `framesaver.ai.perf.agents.slicing` |
| `agents.suppressSlicing` | `framesaver.ai.perf.agents.suppressSlicing` |
| `agents.tickedSum` | `framesaver.ai.perf.agents.tickedSum` |
| `agents.liveSum` | `framesaver.ai.perf.agents.liveSum` |
| `mods` (array) | `framesaver.ai.perf.mods` |
| `cfg.*` (~25 fields — windowSeconds/standBy/leakFix/brainPeriod/cullSleeping/cullAllBots/maxDelta/skipLate/skipTick/jobBudgetMs/jobSlowFrames/asyncBudgetMs/suspendGc/reclaimStandBy/deactivateSleeping/keepFighting/drainInUpdateOnly/drainDiagnostics/sleepDistance/wakeDistance/roleSleepDist/roleWakeDist/bossGroupWake/forceAllRoles/checkInterval/sleepImmediately/minBrainsPerFrame) | `framesaver.ai.perf.cfg.*` (same field names, same values) |
| `cfg.gcTimeSliceMs`/`gcDriveMs`/`gcSliceApplied` | `framesaver.ai.perf.cfg.gcTimeSliceMs`/etc |
| `gcDrive.*` (calls/pending/msTotal/msMax/sliceNs) | `framesaver.ai.perf.gcDrive.*` |
| `gcSuspended` | `framesaver.ai.perf.gcSuspended` |
| `worstCallbacks` (array) | `framesaver.ai.perf.worstCallbacks` |

**`bots` SPLIT, not just moved — the important one.** Pre-capstone, one flat
`"bots":{...}` object mixed Ranger-side counts (`awake`/`asleep`/`total`/
`exempt`/`standByRefused`/`roleUnknown`, computed via `CountBots()`) with
Framesaver-side counts (`animCulled`/`animCulledOffScreen`/`animCulledEngine`).
Post-capstone: `bots.awake`/`.asleep`/`.total`/`.exempt`/`.standByRefused`/
`.roleUnknown` stay flat at top level (Ranger's own `CountBots()`, unchanged).
The anim-cull triple moves to `framesaver.ai.perf.botsAnim.*` (new object
name, not `bots`) — per Sophia's no-legacy-flat-path ruling, this callback
never reconstructs the old merged shape at the code level. A script reading
the old `bots.animCulled*` fields needs to read `framesaver.ai.perf.botsAnim.*`
instead; a script reading `bots.awake` etc. is unaffected.

**Header line, from `CapstoneCallbacks.BuildHeader` (written once per session):**

| Old flat path | New path |
|---|---|
| `config.standByEnabled` | `framesaver.ai.perf.config.standByEnabled` |
| `config.sleepDistance` | `framesaver.ai.perf.config.sleepDistance` |
| `config.wakeDistance` | `framesaver.ai.perf.config.wakeDistance` |
| `config.checkInterval` | `framesaver.ai.perf.config.checkInterval` |
| `config.keepFightingBotsAwake` | `framesaver.ai.perf.config.keepFightingBotsAwake` |
| `config.sleepImmediately` | `framesaver.ai.perf.config.sleepImmediately` |
| `config.forceAllRoles` | `framesaver.ai.perf.config.forceAllRoles` |
| `config.fixAgentLeak` | `framesaver.ai.perf.config.fixAgentLeak` |
| `config.brainUpdatePeriod` | `framesaver.ai.perf.config.brainUpdatePeriod` |
| `config.minBrainsPerFrame` | `framesaver.ai.perf.config.minBrainsPerFrame` |
| `deferToAiMods` | `framesaver.ai.perf.deferToAiMods` |
| `roleSleep.roles` | `framesaver.ai.perf.roleSleep.roles` |

**Spike line, from `CapstoneCallbacks.BuildSpike` (only present on spike lines
that carry a completed GC collection):**

| Old flat path | New path |
|---|---|
| `gcSuspendsBefore` | `framesaver.ai.perf.gcSuspendsBefore` |
| `gcMsSinceSuspend` | `framesaver.ai.perf.gcMsSinceSuspend` |

## Migration recipe for a script

Old: `line["bots"]["animCulled"]`
New: `line["framesaver.ai.perf"]["botsAnim"]["animCulled"]`

Old: `line["cfg"]["maxDelta"]`
New: `line["framesaver.ai.perf"]["cfg"]["maxDelta"]`

Old: `line["config"]["sleepDistance"]` (header only)
New: `line["framesaver.ai.perf"]["config"]["sleepDistance"]`

A script that wants BOTH old and new corpora readable through one code path
should check for the nested key first and fall back to the flat path, rather
than the reverse — old logs never had the nested key, new logs never have the
flat one for these specific fields, and a fallback that tries flat-first would
silently prefer a KeyError over the real (nested) data on new logs.

## What is verified vs. what is asserted

This table was built by reading `CapstoneCallbacks.cs`'s actual field-by-field
content directly, not by re-deriving field names from memory. It has NOT yet
been verified against a live raid's actual NDJSON output byte-for-byte — that
is the verification raid's own job (see `EXTRACTION-PLAN.md`'s 7-point
criteria list). Treat this table as "what the code says it writes," correct
as of the commit that lands alongside it, and confirm it against a real log
before trusting it for a historical-corpus migration.

## The `bus` block (added 2026-08-29)

Every window row also carries a top-level `bus` object - the kit's generic record of
EVERYTHING published to `TelemetryBus` that window, from every registered producer:

    "bus": {
        "count": { "<key>": <int>, ... },
        "event": { "<key>": <number>, ... },
        "sum":   { "<key>": <number>, ... },
        "tag":   { "<key>": "<string>", ... }
    }

Facts already carried in dedicated blocks (e.g. `aiCoreController.tickedSum/liveSum`,
merged into `agents.*`; `standBy.woken/wokenMs/slept/sleptMs`, merged into
`standByTransitions`) appear in both places - the dedicated blocks remain
contractual, `bus` is the generic catch-all that makes ANY publish load-bearing
without the kit knowing producer field names. Values are window-scoped:
`TelemetryBus.ResetWindow` clears the dictionaries at the same boundary.
