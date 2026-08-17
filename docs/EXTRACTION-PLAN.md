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

## Test-suite coupling (found during the git-mv prep pass, 2026-08-16)

My first boundary check only looked at production `.cs` cross-references. `tests/unwrap/Program.cs`
(1,158 lines, reflection-based, loads `Framesaver.dll` and reflects into its types) is a second,
real coupling surface I hadn't checked. Re-verified against it directly:

- **`PlayerLoopProfiler` and `GpuTelemetry`: zero references anywhere in the test file.** Genuinely
  clean cuts, confirmed both at the production-code level and the test level.
- **`ProtocolRunner`: substantial reflection-based coverage.** The `@directives` section (around
  line 530) reflects into `Framesaver.ProtocolRunner`'s `Directive` method, `_defaultSeconds`
  static field, and nested `Step` type directly, plus `StripComment`/`TryParse` earlier in the
  file. There is also a separate "every protocol file on disk, against the shipped settings"
  section (~line 982) that walks `protocol-*.ini` files found relative to `Framesaver.csproj` and
  validates every key against config names resolved from the loaded assembly — and a third section
  checking that telemetry field names mentioned in protocol `.ini` prose actually appear in the
  assembly's emitted strings (this is the check that caught a real shipped misspelling,
  `animCullEngine` for `animCulledEngine`, per its own comment).

**Consequence**: moving `ProtocolRunner.cs` is not just a `git mv` — it needs a plan for this test
coverage too, since the whole file reflects into ONE assembly (`Framesaver.dll`) and tests a mix
of shipping-feature and measurement-only classes together. Options, not yet decided: (a) split
the test file along the same seam as the code move, with the Ranger-side tests loading
`Ranger.dll` and Framesaver-side tests staying against `Framesaver.dll`; (b) keep one test program
that loads both assemblies. Either way this is real additional work, not a pure mechanical
follow-on to the code move — flagging it here rather than discovering it mid-move.

**Sequencing decision**: move `PlayerLoopProfiler.cs` and `GpuTelemetry.cs` first (fully clean at
both the production and test level) as their own commit. `ProtocolRunner.cs` waits for a test
migration plan, staged with the `Telemetry.cs` surgery since both are "real work" commits rather
than mechanical ones.

## The real blocker, found while trying to delete the moved files from Framesaver (2026-08-16, late)

Copying `PlayerLoopProfiler.cs` and `GpuTelemetry.cs` into Ranger (with history) was the easy half.
Deleting them from Framesaver turns out not to be safe yet, and the reason matters for how the rest
of this extraction has to go.

**Framesaver's `Plugin.cs` and `Telemetry.cs` call these classes BY NAME, directly, unqualified**
(`PlayerLoopProfiler.Install()`, `GpuTelemetry.Sample()`, etc. — dozens of call sites, confirmed by
grep). There is no abstraction between Framesaver and these classes today; `TelemetryBus` is a
design, not code that exists yet. So the only way to delete the source files from Framesaver and
keep it compiling is to give `Framesaver.csproj` an actual project/assembly reference to
`Ranger.dll` — which in .NET/BepInEx is a HARD load-time dependency: if `Ranger.dll` is missing,
`Framesaver.dll` fails to load AT ALL, not just the telemetry parts. That is exactly the outcome
Sophia said she does not want (room, 2026-08-16 23:03Z: "I don't want Ranger to be a hard
requirement for Framesaver").

**The actual sequencing this implies**: the `TelemetryBus` indirection layer (or an equivalent
soft-load mechanism) has to be BUILT AND WIRED INTO every one of these call sites BEFORE any
source file still called directly from Framesaver can be deleted from Framesaver — not after, and
not alongside as a nice-to-have. Until that wiring exists, the honest state is: Ranger holds a
second COPY of `PlayerLoopProfiler.cs`/`GpuTelemetry.cs` with real history, Framesaver still holds
and runs its own originals, and the two are not yet the same object. That is not the finished
extraction, but it is not wrong or unsafe either — both mods build and run independently right now,
nothing is broken, and no half-migrated state has shipped.

**Stopping here for tonight rather than building the bus under time pressure.** The bus is real
work — a static class, a load-order-safe `Enabled` latch, and rewriting every one of Framesaver's
direct calls (`PlayerLoopProfiler.X`, `GpuTelemetry.X`, and eventually the `Telemetry.cs` surgery's
own three shipping-class coupling points) to go through it. Worth doing carefully in its own
session rather than rushed at the end of a long one where two design corrections already happened.

## The read-direction reframe (2026-08-16, later still)

Went looking to design a "read API" so `Telemetry.cs` could pull facts back out of
`TelemetryBus` the way `PlayerLoopProfiler`/`GpuTelemetry` currently get read directly. Checked
which of `Telemetry.cs`'s own methods every one of those ~38 call sites sits inside, expecting a
mix of sampler-core code and unrelated code. **It is not a mix.** Every single call site sits
inside one of: `Update`, `Sample`, `EmitSpikeEvent`, `Flush`, `WriteHeader`, `WriteMark`,
`WriteGridSpawnMarker`, `DrainCensus`, `OnDestroy`, `ResetWindow` — i.e. the sampler/window/
protocol-arm lifecycle itself, not scattered elsewhere in Framesaver.

That means "design a read API so `Telemetry.cs` can query `PlayerLoopProfiler`/`GpuTelemetry`
through `TelemetryBus`" was the WRONG shape for this specific coupling. There is nothing to
bridge: the sampler core (all of `Telemetry.cs` minus its three genuine shipping-class reads —
`SleepingBotAnimatorPatch`/`RoleSleepDistance`/`BossGroupWake`, DESIGN.md section 1) and
`PlayerLoopProfiler`/`GpuTelemetry` are ONE COHESIVE UNIT that happens to be split across two
files today. This is exactly what Sophia's ruling already said ("Ranger should own the whole
loop", 22:18Z) — confirmed now at the level of individual call sites rather than as a design
preference. `TelemetryBus`'s `Count`/`Event`/`Tag`/`TryGet*` surface is still correct for the
THREE shipping-class reads, which are a genuinely different relationship (external features
publishing facts INTO the sampler, not the sampler's own internals talking to itself).

**Revised plan**: move the sampler core (`Telemetry.cs` minus the 3 shipping-class touch
points) to Ranger as ONE UNIT alongside `PlayerLoopProfiler`/`GpuTelemetry`, using the same
git-filter-repo history-preserving technique. The 3 shipping-class touch points become
`TelemetryBus.Count`/`Event`/`Tag` calls FROM `SleepingBotAnimatorPatch`/`RoleSleepDistance`/
`BossGroupWake` (which stay in Framesaver), consumed by the now-Ranger-side sampler via
`TryGet*`. This is smaller and more mechanical than "split `Telemetry.cs` down the middle" —
it's "move the whole file, then cut exactly 3 threads that cross the boundary and reconnect
them through the bus."

## Correction to the read-direction reframe above (2026-08-17, fuller audit)

The reframe above checked only `PlayerLoopProfiler`/`GpuTelemetry` call sites and concluded
"only 3 shipping-class touch points." **That conclusion was built on an incomplete audit and was
wrong.** A full pass over every class `Telemetry.cs` references (27 total, not 2) finds it reads
directly from AT LEAST 9 genuinely shipping-feature classes, not 3:

- `SleepingBotAnimatorPatch`, `RoleSleepDistance`, `BossGroupWake` — the original 3, still correct.
- `AICoreControllerUpdatePatch` — the AI brain scheduler (reads `LastBrainsTicked`,
  `LiveAgents`, `PendingRemoval`, `RemovedTotal` for the `agents` NDJSON block).
- `BotStandByUpdatePatch` — the core stand-by system itself (reads `RoleStandByKnown`,
  `RoleAllowsStandBy` per-bot).
- `LongRangeExemption` — reads `.Count` for `snipersAwake`.
- `ModCompat` — the compatibility-guard system (reads `SuppressSlicing`, calls
  `AppendDetected`).
- `BotBackup` (`BotBackupPatches.cs`) — reads `Fired`/`Bailed`, calls `ResetWindow`.
- `AsyncDrain` (`AsyncDrainPatch.cs`) — the drain-budget lever, already known from the
  `AiTiming.ToMs` finding, but it ALSO surfaces `WorstCallbackMs`/`WorstCallbackName` etc. into
  Telemetry's own output, not just the one utility call.

Correcting the record rather than quietly fixing the plan: this is NOT "move the whole sampler
(minus 3 threads) and wire 3 bus calls." It is closer to the shape DESIGN.md section 1 already
described in general terms — SEVERAL shipping features each need a small publish-side change
(`TelemetryBus.Count/Event/Tag` calls added to their own code) before the sampler core that
reads them can move. This is real, multi-file surgery across the shipping half of the mod, not
a narrow 3-class exception.

**Practical consequence for sequencing**: the "move the sampler core in one commit" plan from
the previous section is too optimistic. The actual order has to be: (1) add publish calls to
EACH of the ~9 shipping classes first (this touches shipping code, needs care, one class at a
time is safer than all nine in one commit), verifying Framesaver still builds and its NDJSON
output is byte-identical after each; (2) only once every shipping-side read Telemetry.cs
currently does directly has a bus equivalent, THEN move the sampler core and repoint its reads
from direct class access to `TelemetryBus.TryGet*`; (3) delete the moved files from Framesaver.
That is three real phases, not one.

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

## Phase 2 boundary audit — COMPLETE (2026-08-17 ~04:45Z, post-fix session)

With Phase 1 (the 9 publish wrappers + the Present-gate fix, Framesaver `886c4bd`)
landed, the full bidirectional audit of every measurement-only candidate against every
staying file (Plugin.cs, the 8 shipping classes, RangerBridge, the staying half of
AsyncDrainPatch) is done. Class-name level, both directions, comments distinguished from
code. Results:

**Fully clean, move whole with zero surgery (production level; test level still needs the
unwrap/Program.cs pass the plan already flags):**
- `AsyncWorkerTimingPatches.cs`, `BossSpawnGatePatches.cs`, `LateUpdateTimingPatches.cs`
  (LateTiming), `SpawnAttemptPatches.cs`, `UpdateManualTimingPatches.cs`,
  `ComponentCensusPatches.cs` (its SleepingBotAnimatorPatch mention is a comment),
  `DistanceGridSpawn.cs` (RoleSleepDistance mention is a comment).

**Resolved this session:** `AsyncDrainPatch` no longer references `AiTiming` — local
`TickMath.ToMs` one-liner per the 2026-08-16 23:13Z ruling (Framesaver `582afb1`).

**Real remaining seams, all one family — shipping code EMITS into measurement** (the
same direction as the 9 publish wrappers, so TelemetryBus.Event is the right seam for
each; what's new is they need richer payloads than (name, float)):
1. `SleepingBotAnimatorPatch` → `AwakeAge.Ended/Woke(owner)` (lines ~551/555): shipping
   notifies per-bot sleep/wake. Bus: per-bot event carrying bot identity.
2. `BotStandByUpdatePatch` → `StandByTransitions.Woken/Slept(duration)` (~222/389):
   transition cost events. Bus: Event(name, ms) — fits the existing shape directly.
3. `BotStandByInitPointsPatch` → `BotLog.StandByAssigned(standBy, owner)` (×2): per-bot
   assignment events. BotLog then reads `RoleStandByKnown/RoleAllowsStandBy` (shipping
   predicates) to enrich the line — fold both booleans into the event payload so the
   measurement side never calls back into shipping. **This is the one genuine move→stay
   read left in the audit** and the payload widening removes it.
4. `AsyncDrainPatch` diagnostics block → `ProfileBuild.TotalMs`, `BundleLoad.SyncMsTotal`,
   `RaidInit.TotalMs` deltas around each drain: measurement embedded in the mixed file.
   Resolves via the class-split the strip list already ruled for AsyncDrainPatch.cs —
   the diagnostics half (including these reads) moves; a postfix on the drain method
   replaces the inline instrumentation, and the staying budget lever keeps none of it.
5. `Plugin.cs` → `BotLog.Subscribe()`, `PlayerLoopProfiler.Install()/ArmFrameGap()`,
   grid-spawn/census config binds: the telemetry lifecycle + config block the plan
   already schedules to move into Ranger's Plugin.cs (fresh config per Sophia's
   2026-08-16 23:03Z ruling, no migration).

**Sequencing consequence:** Phase 2 is now unblocked for the clean seven as pure
git-filter-repo moves (test-coupling pass first for each). The five seams above are the
complete list of non-mechanical work between here and moving `Telemetry.cs` itself.
None of them require a read API: every remaining crossing is an event emission or a
config/lifecycle hook. The `TryGet*` read direction the earlier plan sections worried
about is NOT needed for any current crossing — the 9 publish wrappers already inverted
the reads at flush cadence.

### Test-coupling pass over the clean seven (2026-08-17 ~04:50Z)

`tests/unwrap/Program.cs` (reflection-based, loads Framesaver.dll) grepped per class:

- **Zero test references (fully clean at both levels now):** AsyncWorkerTiming,
  LateTiming, SpawnAttempts, Census, DistanceGridSpawn.
- **Small self-contained behavioral test blocks:** UpdateManualTiming (bucket arithmetic,
  drives the statics directly), BossSpawnGate (forced-but-excluded intersection and the
  null-vs-empty distinction). Each is one `Console.WriteLine` section with its own
  `asm.GetType` — migration shape is option (b) from the earlier section: ONE test
  program that loads both assemblies, moved classes' blocks repointed at
  `Assembly.LoadFrom("Ranger.dll")`. Concrete now, not an open option.
- For later seams: BotLog (4 refs), AwakeAge (2 refs), StandByTransitions (2 refs) have
  similar small blocks plus NDJSON-literal string checks (`",\"awakeAge\":"` etc.) — the
  literal checks must follow whichever assembly emits the string after the move.

### Seam 2 DONE — with a correction to this doc's own earlier claim (2026-08-17 ~04:55Z)

**Correction:** seam 2 was described above as "fits the existing Event(name, ms) shape
directly." That was WRONG: `TelemetryBus.Event` is last-write-wins per window, while
StandByTransitions' semantic is `wokenMs / woken` = cost of ONE wake — count AND summed
duration must both accumulate, and a single Event per transition would silently keep
only the final transition's length while looking like a total. Caught while writing the
call sites, before anything shipped. Same lesson shape as the JIT-gate retraction: read
the actual surface before claiming a fit.

**Landed:**
- Ranger `f20a180`: `TelemetryBus.Sum(key, delta)` added — the accumulating double
  counterpart to Count (with `TryGetSum` + ResetWindow coverage). Event keeps
  last-write-wins; the distinction is documented on Sum itself.
- Framesaver `e26ec47`: `RangerBridge.PublishStandByTransition(woken, ms)` + both call
  sites in BotStandByUpdatePatch (wake ~line 222, sleep ~line 389), gated on Present,
  one timestamp read per site feeding the direct call (ticks) and the publish (ms via
  TickMath). Additive, zero NDJSON change. Both mods build clean.

**Seam status:** 1 of 5 closed (seam 2). Remaining: AwakeAge (per-bot events, needs
bot-identity payload), BotLog.StandByAssigned (needs role-predicate booleans folded into
the payload — the one genuine move→stay read), AsyncDrain diagnostics (class-split),
Plugin.cs lifecycle/config (moves to Ranger's Plugin.cs).

### Seam-3 ordering correction (2026-08-17 ~05:00Z)

Reading `AwakeAgeTiming.cs` before wiring it surfaced an ordering constraint the list
above glossed over: `AwakeAge.Woke/Ended` take a **`BotOwner` reference** (the class keys
two dictionaries by it), so this seam cannot be pre-wired through the generic bus the
way seam 2 was — the natural seam is a typed bridge call
(`RangerBridge.NotifyAwakeAge…` → `Ranger.AwakeAge.Woke(owner)`), but a bridge method
cannot name a Ranger-side type that doesn't exist until the file actually moves.

**Correct order:** seams 3 and 4 (AwakeAge, BotLog) are wired *inside* their move
commits — bridge method + gated call site added in Framesaver in the same commit that
lands the Ranger-side copy — not as pre-move additive publishes. Seam 2 was pre-wireable
only because its payload was a plain (bool, double) that the existing bus could carry.
This also means the move commits for those two files carry behavior the raid must
re-verify (not merely additive), unlike the clean-file moves.

Sequencing stays: clean-seven copies first (establishes the repeatable move procedure,
zero Framesaver-side edits), then seam-bearing moves one at a time, each followed by a
verification raid before the next.

### Mixed-file finding: AsyncWorkerTimingPatches is NOT clean (2026-08-17 ~05:10Z)

The first batch move attempt (AsyncWorkerTimingPatches.cs) built the merge cleanly and
then FAILED TO COMPILE in Ranger, exposing an audit blind spot: the Phase-2 audit checked
candidate→shipping-class references but not candidate→`Plugin` config references. Full
re-audit of all fourteen candidates against `Plugin.*`:

- **`AsyncWorkerTimingPatches.cs` — MIXED FILE, same shape as AsyncDrainPatch.cs.**
  `AsyncWorkerFixedUpdatePatch`'s Prefix implements the `DrainInUpdateOnly` shipping
  lever's actual behavior: when the flag is set it skips `AsyncWorker.FixedUpdate`
  entirely (the "drain completions in Update only" feature, C3→A-bucket keep). The
  timing half is measurement; the suppression half is shipping. Strip-list precedent
  (the explicitly-ruled AsyncDrainPatch class-split) says: suppression stays in
  Framesaver, timing moves. **Extending that split to this second file is ruling-shaped
  — flagged to Sophia 2026-08-17 ~05:15Z; do not cut shipping behavior on extrapolation
  (the C4 lesson, applied to the audit that missed it).**
- `BotLogPatches.cs` reads `Plugin.ForceStandByForAllRoles` (logs it into the event
  line) and `DistanceGridSpawn.cs` reads the GridSpawn* keys — both read-only, both
  B-bucket config that moves with its file per the strip list; they fold into the seam-5
  config-block move, not new seams.
- Everything else: clean.

**Git state:** merge `e616a3a` landed the file's history into Ranger; revert `9532c71`
removed it from the tree without losing history. When the split is ruled, re-landing is
`git checkout e616a3a -- Patches/AsyncWorkerTimingPatches.cs` + the split itself — the
history merge never needs redoing.

**Corrected clean list (six, was seven):** AsyncWorkerTimingPatches moves OUT;
LateUpdateTimingPatches, SpawnAttemptPatches, ComponentCensusPatches,
DistanceGridSpawn (config reads noted above), UpdateManualTimingPatches, BossSpawnGate
move whole. First-batch procedure (filter-repo via `python -m git_filter_repo` — not on
git's PATH on this machine — then unrelated-histories merge, then a Ranger-side namespace
switch commit) is proven end-to-end by this attempt.

### Batch 1 LANDED (2026-08-17 ~05:20Z): five files moved with history

**Moved** (merge `d3303b3` + namespace switch `dfd7e16`, Ranger builds clean):
`SpawnAttemptPatches.cs`, `BossSpawnGatePatches.cs`, `ComponentCensusPatches.cs`,
`StandByTransitionTiming.cs`, `AiTickTimingPatches.cs` — nine commits of history each,
including the 4.1-port lineage. All five verified zero-coupling on all three audit axes
(shipping classes, Plugin config, Telemetry). Copies are INERT until Ranger's Plugin.cs
wires them; Framesaver's originals still run. Same deliberate-duplication state as
PlayerLoopProfiler/GpuTelemetry.

**Procedure note that cost one retry:** filter-repo path arguments need FORWARD slashes;
Windows backslashes silently match nothing and produce an empty filter with no commits
(the merge then refuses on a branchless scratch — clean failure, nothing landed wrong).

**Not in batch 1, with reasons recorded:**
- `LateUpdateTimingPatches.cs`, `DistanceGridSpawn.cs`, `BotBackupPatches.cs`: Telemetry
  mentions are likely doc comments but need code-vs-comment verification before moving
  (DistanceGridSpawn additionally reads Plugin.GridSpawn* — seam-5 config).
- `UpdateManualTimingPatches.cs`: `Telemetry.Fmt` reference needs the same verification
  (Fmt is a private static on Telemetry; if real code, the file moves with the sampler
  core unit).
- `AwakeAgeTiming.cs`, `BotLogPatches.cs`: seam-3/4 ordering correction above (wire
  inside move commits; BotLog additionally has four real Telemetry-static reads — it
  moves WITH Telemetry.cs).
- `AsyncWorkerTimingPatches.cs`: split ruling pending (this doc, above).
- `ProtocolRunner.cs`: test-migration plan still open (earlier section).

**Net:** 7 of ~17 measurement files now live in Ranger (2 skeleton-era + 5 batch-1),
all inert-by-design until seam 5 wires the lifecycle.

### Batch 2 LANDED (2026-08-17 ~05:25Z): +3 files, and a FOURTH audit axis

Moved (merges `0e37306` + `c2f621f`, namespace switch `6b7558d`, builds clean):
`LateUpdateTimingPatches.cs`, `UpdateManualTimingPatches.cs`, and — landed as an
inert dependency — `AwakeAgeTiming.cs`.

**Fourth audit axis, found by the compiler:** candidate→candidate references.
`UpdateManualTiming` line 263 reads `AwakeAge` (both measurement files; no earlier
axis checked measurement-referencing-measurement). AwakeAge's copy follows the
PlayerLoopProfiler precedent — inert duplicate now, seam-3 wiring (BotStandBy-
UpdatePatch's Ended/Woke calls through the bridge) still at cutover, NOT now.
Also verified in this pass: LateTiming/UpdateManualTiming/DistanceGridSpawn's
`Telemetry` mentions are all doc comments (code-clean); `BotBackupPatches` is
**reclassified seam-bearing** (its file contains one of the nine PublishTelemetry
wrappers → references Framesaver's RangerBridge; the Ranger copy must drop or
repoint that wrapper when it moves); `DistanceGridSpawn` is **blocked on seam-5
config** (its real Plugin.GridSpawn* reads won't compile in Ranger until those
keys exist in Ranger's Plugin.cs).

**Net after batch 2: 10 of ~17 measurement files Ranger-side** (2 skeleton-era,
5 batch-1, 3 batch-2), all inert-by-design. Remaining: DistanceGridSpawn (seam-5),
BotBackup (seam-bearing reclass), BotLog (moves WITH Telemetry.cs),
AsyncWorkerTiming (split ruling), ProtocolRunner (test plan), AsyncDrain +
SleepingBot mixed-file halves (splits), and Telemetry.cs itself.

### AsyncWorkerTiming split EXECUTED (2026-08-17 ~05:30Z, Ranger `0bf3278`)

Sophia ruled at 05:13Z ("timing half moving to Ranger is a good idea"): re-landed
from preserved history `e616a3a` and split. Ranger keeps the timing statics + both
timing patches; the suppression half (`Plugin.DrainInUpdateOnly` read, skip return,
`FixedSkips` increment) is the shipping lever and stays in Framesaver.
`FixedSkips` keeps its home in Ranger's `AsyncWorkerTiming` — at cutover the
increment arrives as a seam event from Framesaver's suppressor, so the counter and
its NDJSON field keep one home without config crossing the boundary. Framesaver's
original file is untouched until cutover (drops to suppression-only then). Builds
clean. **Net: 11 of ~17 measurement files Ranger-side.**

**Deploy note for this session:** Framesaver HEAD is now `582afb1` (TickMath, on top of
the gate fix `886c4bd`); `bin/Release` holds `582afb1`'s build, so the `3F407D7A…` md5
quoted in-room for `886c4bd` is stale. Either build is valid for the verification raid
(both contain the gate fix); match the ndjson header's commit stamp to whichever is
deployed.

## Seam-5 flip, defect, and fix (2026-08-17 07:04Z–07:26Z) — see register

Full story lives in the room and the register (`reg-dec-2026-08-17T071420`,
`reg-dec-2026-08-17T072603`, `reg-dec-2026-08-17T074415`), not duplicated here. Short
version for anyone reading this doc cold: Ranger took ownership of the telemetry
lifecycle (config + patch enables); a defect was found and fixed where the
PlayerLoopProfiler's install/arm had to be PARTIALLY REVERTED to Framesaver, because
it and the sampler that reads its `Snapshot` are statically coupled within one
assembly and cannot change owners independently. **That defect is this section's whole
lesson, generalised below**, because it turned out to describe most of what remains.

## Why seam-3 (AwakeAge) and BotBackup are BOTH capstone-coupled, not independently
wireable (2026-08-17 ~07:46Z)

After the flip verified clean, the plan called for two more "pre-capstone seams":
AwakeAge (per-bot wake/sleep events) and BotBackup (already publish-wired via the bus,
seemed like a pure move). Checked both against `Telemetry.cs`'s actual read pattern
before touching code, and both hit the SAME wall the profiler did:

- **AwakeAge**: `Telemetry.Flush()` doesn't just publish AwakeAge facts — it reads
  `AwakeAge`'s bucketed histogram (`Ticks[]`/`Calls[]`, 6 buckets) via `Append(sb)` for
  its own NDJSON block, and drains per-bot span rows via `DrainRows(Append, _window)`
  every window. Neither is a simple two-value event; a histogram and a per-bot row set
  don't fit `TelemetryBus.Count/Event/Sum/Tag` without either serialising an array
  through `Tag` (defeats the bus's typed-value purpose) or widening the bus's
  vocabulary for one caller. `Ranger.AwakeAge` is also `internal static` — cross-assembly
  access needs it `public` first, same problem the bus was built to route around.
- **BotBackup**: already has a clean bus publish (`RangerBridge.PublishBotBackup`, 5
  plain ints, seam-2-shaped) — but `Telemetry.Flush()` ALSO reads `BotBackup.Fired`/
  `.Bailed` directly for its own `"botBackup"` NDJSON block, and calls
  `BotBackup.ResetWindow()` in its own window-reset sequence. The bus publish and the
  direct read are two different relationships to the same class; only the publish half
  is bridgeable, and the direct-read half is exactly the profiler's coupling.

**The general rule, stated once so it doesn't need re-discovering per class:** a
measurement class is independently seam-wireable ONLY if `Telemetry.cs` never reads its
state or calls its lifecycle methods (`ResetWindow`, `Append`, `DrainRows`, etc.)
directly — i.e. only if the class's sole relationship to the sampler is "publishes a
fact, Telemetry never reads it back." `StandByTransitions` (seam-2) qualified. AwakeAge,
BotBackup, BotLog (four direct `Telemetry.cs` reads per the earlier audit), and the
profiler do not — Telemetry reads or calls into all of them directly, which means they
are part of the sampler's own internal object graph, not external publishers to it.
**Every one of those moves AT the capstone, together with `Telemetry.cs`, or not at
all before it.**

Consequence: the "5 seams, wire pre-capstone" framing from earlier in this doc was
optimistic. Two of five (StandByTransitions, and the profiler before its ownership bug)
were genuinely independent. The rest were always capstone-coupled; the plan just hadn't
checked each one against `Telemetry.cs`'s actual reads yet. **Nothing more is landable
before the capstone** except pure administrative work (test migration, below) and the
remaining fully-clean file moves already done.

## ProtocolRunner test-migration plan (2026-08-17 ~07:50Z, design only — no code changed)

`ProtocolRunner.cs` has not moved yet (still 100% Framesaver-side; DESIGN.md's inventory
lists it as a mover but nothing has touched it). Its test coupling, found by direct
read of `tests/unwrap/Program.cs`:

**Two reflection blocks, both assembly-qualified by string name:**
1. `StripComment`/`TryParse` (lines ~76–99): pure parsing helpers, no BepInEx/Unity
   dependency, no state. `asm.GetType("Framesaver.ProtocolRunner")` then
   `GetMethod(..., BindingFlags.NonPublic | BindingFlags.Static)`.
2. `Directive`/`_defaultSeconds`/`Step` (lines ~535–568): the `@directive` parser —
   also pure (comment explicitly notes `Load()` needs BepInEx config and `Due` needs a
   Unity clock, so neither is tested here; only the parseable half is). Reflects into
   the nested `Step` type and a private static field alongside the method.

**The same file also carries the AwakeAge bucket test** (lines ~578–600,
`asm.GetType("Framesaver.Patches.AwakeAge")`, `Bucket`/`Ticks`/`Calls`/`Append`/
`ResetWindow`) — relevant here only because it's proof the migration shape below has to
handle more than one assembly per test run already, not a new problem ProtocolRunner
introduces.

**The migration shape (recommended): one test program, `Assembly.LoadFrom` both DLLs.**
Not two test programs — the file already interleaves checks across logical units
(protocol parsing, AwakeAge buckets, StandByTransitions emission, in one linear `Check()`
sequence with a running pass/fail tally) and splitting it would either duplicate the
tally/`Check` harness or lose the single "N of M passed" summary the file's own bottom
line reports. Concretely:
- `var asm = Assembly.LoadFrom("Framesaver.dll")` stays for the classes that stay
  (StandByTransitions et al., once wherever they land).
- Add `var rangerAsm = Assembly.LoadFrom("Ranger.dll")` once, near the top, alongside
  the existing `asm` load.
- Each moved class's `asm.GetType("Framesaver.X")` becomes
  `rangerAsm.GetType("Ranger.X")` — mechanical, one line per reflection call site, since
  the namespace switch (`Framesaver.Patches` → `Ranger`) is already the pattern every
  file move in this doc has used.
- `BindingFlags` and method/field names are UNCHANGED — the moved classes' internals
  (checked against every file moved so far) don't rename members, only their namespace.

**CORRECTION, same turn: ProtocolRunner is ALSO capstone-coupled.** Said above "nothing
found reading ProtocolRunner's STATE directly" as a hopeful placeholder pending the
audit — then ran the audit before publishing this as settled, and it was wrong. Grep of
`Telemetry.cs` for `ProtocolRunner\.` (11 hits) shows the same split as everywhere else:
`ResetForRaid()`, `Due`, `AutoStartDue`, `CanAdvance`, `Advance()` are lifecycle control
(fine, would bridge) — but lines 1699–1709 are `Flush()` reading `ProtocolRunner.Name`/
`.StepIndex`/`.StepCount`/`.StepSeconds`/`.Arm` DIRECTLY to build its own `"protocol"`
NDJSON block, the identical shape to `BotBackup.Fired`/`.Bailed` and AwakeAge's
histogram. **ProtocolRunner moves at the capstone too.** Three of the general rule's
exceptions accounted for now (AwakeAge, BotBackup, ProtocolRunner), zero found that
weren't — worth treating that as the base rate going forward rather than re-hoping per
file: assume capstone-coupled until an actual grep says otherwise, not the reverse.

**What survives from this unit despite the correction:** the TEST migration shape
(one program, `Assembly.LoadFrom` both DLLs, mechanical `asm.GetType` →
`rangerAsm.GetType` per moved reflection call) is still correct and still useful — it
just applies to the WHOLE capstone's test surface (ProtocolRunner's two blocks +
AwakeAge's bucket block, all moving together) rather than to a ProtocolRunner-only
pre-capstone move. No separate design work needed later; this section already covers it.

**Status: fully capstone-coupled, nothing pre-landable here either.** Confirmed 2026-08-17
~07:52Z by direct grep, not left as an assumption.

## ProtocolRunner's cross-assembly reflection into Plugin (2026-08-17 ~08:04Z, found during
capstone commit-sequence planning)

`ProtocolRunner.BuildEntryMap()` reflects `typeof(Plugin)` UNQUALIFIED, and `ProtocolRunner`
lives in `namespace Framesaver` — so this resolves to `Framesaver.Plugin`, not some Ranger-side
equivalent. This is not read-only: `Advance()` writes `a.Key.BoxedValue = a.Value` into whatever
`ConfigEntryBase` the protocol `.ini` names. Checked all 9 deployed `protocol-*.ini` files: real
protocols assign genuine SHIPPING settings, not just telemetry knobs — `Brain update period`
(the AI-slicing lever), `Cull sleeping bot animators`, `Force for all roles`,
`Drain completions in Update only`, `Max delta time`. Three shipping features, toggled by an
A/B protocol file, by design — that IS the point of the class (see its own doc comment on why
a keypress-driven arm exists at all).

**Consequence for the capstone:** ProtocolRunner cannot become "Ranger reflects into Ranger's
own Plugin" when it moves — its whole purpose requires it to keep reflecting into
`Framesaver.Plugin`, cross-assembly, after the move. This is NOT a blocker (public static
fields, which `Plugin`'s config entries already are for BepInEx binding, are reachable by
reflection across assemblies same as within one), but it IS a different, looser-coupled
relationship than every other capstone-coupled class: those move BECAUSE Telemetry.cs reads
their state directly (a same-assembly problem the bus or the move itself fixes); this one has
an *additional*, orthogonal cross-assembly reflection dependency that survives the move
unchanged and needs its own explicit statement rather than being silently assumed away.

**What the capstone commit must do for this specifically:**
- `BuildEntryMap()`'s `typeof(Plugin)` must become an explicit, guarded resolution of
  `Framesaver.Plugin` by assembly-qualified name (e.g.
  `Type.GetType("Framesaver.Plugin, Framesaver")` or an equivalent Chainloader-based lookup),
  not a bare `typeof(Plugin)` that would silently bind to `Ranger.Plugin` instead once
  `ProtocolRunner` lives in `namespace Ranger`. **This is a real behavior change hiding inside
  what looks like a namespace-only move** — every other file moved so far kept `typeof(X)`
  references pointed at classes that moved WITH them; this is the first one pointing at a class
  that does NOT move.
- Needs a null/missing-type guard for the case Framesaver is somehow absent while Ranger runs
  standalone — same soft-dependency posture as everything else, but worth its own explicit
  check since a silently-empty entry map would make every protocol file "parse successfully,
  fail every key" rather than fail loudly the way an unknown key already does today.
- The capstone verification raid needs one concrete check added to its list: load a real
  protocol file (e.g. `protocol-brain-slice.ini`) post-move and confirm `Brain update period`
  still actually changes when the protocol key is pressed — a functional check, not just an
  ndjson-shape comparison, because this is the one piece of the capstone that changes *what a
  string resolves to* rather than only *which assembly a class lives in*.

## The capstone commit sequence (2026-08-17 ~08:10Z, planned before any code touched)

Full re-audit of every file and call site done (direct read of `Telemetry.cs`,
`AwakeAgeTiming.cs`, `BotBackupPatches.cs`, `ProtocolRunner.cs`, `BotLogPatches.cs`,
`RangerBridge.cs`, `TelemetryBus.cs`, `Ranger/Plugin.cs`, `Framesaver/Plugin.cs`,
`tests/unwrap/Program.cs`, plus every deployed `protocol-*.ini`). What follows is the
concrete plan, not a restatement of findings already above.

### What moves, in one commit pair (Ranger + Framesaver, paired, deployed together)

**Into Ranger** (namespace switch `Framesaver` → `Ranger`, mechanical per every prior
move in this doc):
- `Telemetry.cs` (the sampler/window/flush/spike core — all of it, not a split)
- `AwakeAgeTiming.cs` — NOTE: already a Ranger-side INERT COPY since batch 2
  (`6b7558d`). Capstone re-lands its `Woke`/`Ended`/`RecordAt` as the LIVE copy and the
  Framesaver original is deleted, not moved again — the merge-with-history step already
  happened; this is a namespace-switch + delete, same shape as every other file's
  second half.
- `BotBackupPatches.cs` (the `BotBackup` static class AND its two Harmony patches,
  `BotBackupAddPatch`/`BotBackupFlushPatch` — the whole file, patches included, since
  nothing in DESIGN.md's inventory or this audit found a shipping-feature reason to
  split the patches from the statics they instrument)
- `ProtocolRunner.cs` — whole file, WITH the `BuildEntryMap` fix above (assembly-
  qualified cross-reference to `Framesaver.Plugin`, guarded for absence)
- `BotLogPatches.cs` (the `BotLog` static class AND `BotSpawnLogPatch`/
  `BotActivationCanaryPatch` — whole file, same reasoning as BotBackup)
- `PlayerLoopProfiler.cs` — already a Ranger-side inert copy (skeleton-era). Capstone
  makes it live: Ranger's `Plugin.cs` regains the `Install()`/`ArmFrameGap()` call the
  seam-5 follow-up deliberately reverted OUT of Ranger, and Framesaver's copy is
  deleted (not just its call site — the file itself, since nothing else in Framesaver
  references `PlayerLoopProfiler` once `Telemetry.cs` moves with it).

**Stays in Framesaver, unchanged:**
- `SleepingBotAnimatorPatch`, `RoleSleepDistance`, `BossGroupWake`,
  `AICoreControllerUpdatePatch`, `BotStandByUpdatePatch`, `LongRangeExemption`,
  `ModCompat`, `AsyncDrainPatch` (suppression half), `GcControl.cs` — all shipping
  features, all already publish through `RangerBridge`/`TelemetryBus` for the facts
  Telemetry.cs reads back.
- `TriggerSubscribers` (in `AwakeAgeTiming.cs` today, alongside `AwakeAge` — see "one
  file, two classes" note below).
- `GpuTelemetry.cs` — already fully split (Ranger owns the archived-instruments
  shell + Qpc/gfx; Framesaver has no copy at all post-split, confirmed this session).

### One thing the audit above did not have a section for: `TriggerSubscribers`

`AwakeAgeTiming.cs` contains TWO classes: `AwakeAge` (capstone-coupled, moves) and
`TriggerSubscribers` (a different instrument — max ShootData subscriber count —
referenced from `Telemetry.cs` line 1379 the same direct-read way). Grep says
`TriggerSubscribers` has NO Framesaver-shipping-class references itself and IS read
directly by `Telemetry.cs`, so it is capstone-coupled by the same rule as everything
else in this file — it moves WITH `AwakeAge`, same file, same commit. Confirmed: no
separate finding needed, the file moves as one unit exactly as DESIGN.md's original
inventory said ("AwakeAgeTiming" as one line item, never split).

### The three genuine bridge rewires this commit must also do

These are shipping-code edits (Framesaver), not just deletions, because
`BotStandByUpdatePatch.Wake()`/`GoToSleep()` today call `StandByTransitions.Woken()`/
`.Slept()` DIRECTLY (ticks-based, for the immediate wake/sleep cost) in ADDITION to the
additive `RangerBridge.PublishStandByTransition` call already landed at seam-2. Once
`StandByTransitions` (already Ranger-side since batch 1, currently an inert duplicate)
becomes the ONLY copy, the direct call in `BotStandByUpdatePatch.cs` has nothing to call
— `Framesaver.Patches.StandByTransitions` stops existing. **This is the one place the
capstone touches live shipping code, not just the sampler files.** Fix: delete the two
direct calls (`StandByTransitions.Woken(wakeTicks)` / `.Slept(sleepTicks)`) from
`BotStandByUpdatePatch.cs`, keep the existing `RangerBridge.PublishStandByTransition`
calls exactly as they are (already gated, already correct) — the bus publish becomes
the ONLY path, which is exactly the shape seam-2 built and verified weeks before the
capstone needed it. Zero new code here, only a deletion.

Same shape does NOT apply to `AwakeAge.Woke`/`.Ended` (called from
`SleepingBotAnimatorPatch` per the original Phase-2 audit's seam 1) or `BotLog.
StandByAssigned` (called from `BotStandByInitPointsPatch`, seam 3) — THOSE calls were
never bridged additively the way seam-2 was (the seam-3 ordering correction above
explains why: bot-identity payloads can't go through the generic bus, so they need a
typed `RangerBridge` method same-commit). **This commit must ADD**:
- `RangerBridge.NotifyAwakeAgeWoke(BotOwner)` / `NotifyAwakeAgeEnded(BotOwner)` —
  NoInlining-wrapped per the class's own established pattern, calling
  `global::Ranger.AwakeAge.Woke(bot)`/`.Ended(bot)` directly (BotOwner is a Framesaver-
  visible EFT type, not a Ranger type, so passing it across the bridge is fine —
  RangerBridge already takes typed non-Ranger arguments in half its methods).
- `SleepingBotAnimatorPatch`'s two call sites (~lines 551/555 per the Phase-2 audit)
  switch from whatever they call today (need to re-check at commit time — the Phase-2
  audit describes this as `AwakeAge.Ended/Woke(owner)` still being CALLED DIRECTLY, not
  yet bridged, unlike StandByTransitions) to `RangerBridge.NotifyAwakeAgeWoke/Ended`.
- `BotLogPatches.cs`'s own `Death()` method calls `AwakeAge.Ended(died)` directly (found
  in this session's grep, line ~in the Death handler) — but `BotLog` and `AwakeAge` are
  moving in the SAME commit, so this call needs no bridge at all: it becomes an ordinary
  same-assembly call once both are Ranger-side. No bridge method needed for this one
  specific call site — flagging so nobody builds one that turns out to be dead code.
- `BotStandByInitPointsPatch`'s `StandByAssigned` call: per the Phase-2 audit this is
  "the one genuine move→stay read left" — `BotLog.StandByAssigned` reads
  `BotStandByUpdatePatch.RoleStandByKnown/RoleAllowsStandBy` (shipping predicates) to
  enrich its own event line. Since `BotLog` moves to Ranger this commit, that read
  becomes cross-assembly. Fix per the audit's own resolution: fold both booleans into
  the call's payload — `RangerBridge.NotifyBotLogStandByAssigned(bool effective, bool?
  roleAllows, bool forced, string profileId, ...)` (exact signature TBD at write time,
  matching whatever `StandByAssigned` needs to build its NDJSON line), computed in
  `BotStandByInitPointsPatch.cs` (which already has both predicates in scope) and
  passed in, rather than `BotLog.StandByAssigned` reaching back into
  `BotStandByUpdatePatch` cross-assembly.

### Config and lifecycle (Plugin.cs both sides)

- Ranger's `Plugin.cs` gains: `PlayerLoopProfiler.Install()` + `.ArmFrameGap()` calls
  (moving back from the seam-5 partial-revert), `gameObject.AddComponent<Telemetry>()`
  (the sampler component itself — Ranger's `GameObject` needs identifying; check
  whether `BaseUnityPlugin` gives one for free the way Framesaver's does), and the
  `AsyncDrainDiagnostics` config entry is ALREADY declared Ranger-side (seam-5) but
  unwired — this commit is where it finally gets read by something (the diagnostics
  half of `AsyncDrainPatch`, whenever that class-split lands — NOTE this is likely
  still open after the capstone; check the strip-list's AsyncDrainPatch ruling before
  assuming it's in scope for THIS commit specifically).
- Framesaver's `Plugin.cs` DROPS: the `PlayerLoopProfiler.Install/.ArmFrameGap` block
  (the comment already marks it as capstone-bound), the
  `gameObject.AddComponent<Telemetry>()` line, and `BotLog.Subscribe()` (moves with
  BotLog — check whether Ranger's `Plugin.Awake()` needs to call `Ranger.BotLog.
  Subscribe()` in its place, since the death-event subscription has to happen
  somewhere and BotLog is the class that owns `_subscribed`).
- Framesaver's `Plugin.cs` LOSES four more `Enable()` lines: `new BotBackupAddPatch().
  Enable()`, `new BotBackupFlushPatch().Enable()`, `new BotSpawnLogPatch().Enable()`,
  `new BotActivationCanaryPatch().Enable()`. Since the PATCH CLASSES move to Ranger
  along with the static classes they instrument (`BotBackup`, `BotLog`), their
  `Enable()` calls move too — Ranger's `Plugin.Awake()` gains these four lines.

### `tests/unwrap/Program.cs` — same commit, per the migration shape already designed

Add `var rangerAsm = Assembly.LoadFrom(rangerDll)` near the top (needs a second CLI arg
or a `FindUp`-style default, mirroring the existing `dll` argument handling at the top
of `Main`). Mechanical per-line changes, all confirmed by this session's grep of the
test file:
- Line 76/536: `asm.GetType("Framesaver.ProtocolRunner")` →
  `rangerAsm.GetType("Ranger.ProtocolRunner")` (both reflection blocks touch the same
  type, fix once, applies to both).
- Line 580: `asm.GetType("Framesaver.Patches.AwakeAge")` →
  `rangerAsm.GetType("Ranger.AwakeAge")`.
- Line 330: `asm.GetType("Framesaver.Patches.BotLog")` → `rangerAsm.GetType("Ranger.
  BotLog")`.
- Line 460: `asm.GetType("Framesaver.Patches.StandByTransitions")` → already
  Ranger-side since batch 1 but the TEST reference was never updated (batch 1's
  own log says StandByTransitionTiming was "fully clean" at the test level with
  ZERO test references — re-check this at commit time, this line 460 hit may be
  stale/wrong in my grep, or batch 1's clean-test claim may have missed it. VERIFY
  BEFORE ASSUMING, per this session's whole lesson.)
- Line 737: `asm.GetType("Framesaver.Patches.TriggerSubscribers")` →
  `rangerAsm.GetType("Ranger.TriggerSubscribers")`.
- `BindingFlags`, method names, field names: unchanged, per every prior move.
- Lines 384/432 (`RoleSleepDistance`, `BossGroupWake`) and 775 (`SleepingBotAnimatorPatch`)
  and 885/919/921 (`Framesaver.Plugin` field checks) stay on `asm` (Framesaver) —
  those classes do not move.

### Sequencing within "one commit pair"

Given the size, "one commit pair" likely means one Ranger commit + one Framesaver
commit landed atomically (same GO, same deploy, same raid) rather than one git commit
each containing everything — splitting Ranger's commit into "the 5 file moves" and
"the Plugin.cs wiring" as two sequential Ranger commits (both required before
Framesaver's half can build) is fine and probably clearer to review, AS LONG AS
neither side is deployed/tested until both are complete, matching the "don't launch
between them" discipline already used for seam-5. `tests/unwrap/Program.cs` change
rides with whichever commit is easiest to verify it against (probably last, once both
DLLs exist to build against).

### Verification raid criteria (extends the seam-5/split pattern already proven)

1. NDJSON shape byte-identical to the last verified raid (`41-split-verify`) for every
   field NOT touched by this move — field names, not just presence, since this is the
   commit most likely to introduce a silent rename.
2. Zero new exceptions in Player.log (same bar every prior raid in this arc used).
3. Install/ownership log lines: Ranger's boot log should now show the profiler +
   sampler install (`Framesaver: telemetry...` lines should be gone entirely, since
   Framesaver no longer runs a sampler at all — different bar than seam-5's "install
   lines Framesaver-only", this is now the opposite: Ranger-only, because the whole
   sampler moved, not just the lifecycle).
4. **New, specific to this commit**: a protocol file's arm actually changes shipping
   behavior post-move (the `BuildEntryMap` cross-assembly check above) — load
   `protocol-brain-slice.ini`, press the protocol key, confirm the NDJSON `protocol`
   block's `arm` field changes AND `Brain update period`'s effect is visible (slicing
   on/off, same as any protocol-file A/B already run this project).
5. StandByTransitions numbers (`woken`/`wokenMs`/`slept`/`sleptMs`) still populate —
   confirms the seam-2 bus publish, now the ONLY path, still works after the direct-call
   deletion in `BotStandByUpdatePatch.cs`.
6. AwakeAge buckets and per-bot `botWindow` rows still populate — confirms the new
   `RangerBridge.NotifyAwakeAgeWoke/Ended` bridge methods work.
7. `botStandBy` event lines still carry `roleAllows`/`forced` correctly — confirms the
   payload-widening fix for `BotLog.StandByAssigned`.

### What is NOT in this commit (deliberately deferred)

- Phase 3 (deleting Framesaver's now-empty originals) happens AS PART of this commit
  for the 5 moved files (git mv semantics, not a separate later deletion) — unlike the
  clean-seven moves, which stayed duplicated for a long window by design. There is no
  reason to duplicate Telemetry.cs/AwakeAge/BotBackup/ProtocolRunner/BotLog/profiler
  the way the clean files were duplicated, because (unlike those) NOTHING in Framesaver
  can call them once the bridge rewires land — a lingering Framesaver original would be
  dead code from the moment this commit lands, not a safety net.
- `AsyncDrainPatch.cs`'s class-split (diagnostics half to Ranger, suppression half
  stays) is a SEPARATE unit, not folded into this commit — confirmed by re-reading the
  strip-list ruling reference above; it is its own seam with its own class-split
  precedent (same shape as `AsyncWorkerTimingPatches`), not capstone-coupled by the
  general rule (Telemetry.cs does not read AsyncDrain's diagnostic state directly the
  way it reads AwakeAge/BotBackup/ProtocolRunner — it reads `AsyncDrain.Drained`/
  `.GcSuspended` etc. which are ALREADY bridged via `RangerBridge.PublishAsyncDrain`).
  Worth a follow-up unit after the capstone lands and verifies clean, not before.
- `DistanceGridSpawn.cs` was archived entirely (Sophia's ruling), not part of any
  remaining move.
- The Ranger README's no-terminal usage story (DESIGN.md's Gate-2 risk) — still open,
  still not blocking, still worth doing before Sophia needs to touch Ranger directly.

## CORRECTION, found immediately after history-merge (2026-08-17 ~08:18Z): the plan
above is missing the reverse-direction dependency and cannot be executed as written

The commit sequence above only accounted for the 4 capstone-coupled classes
(AwakeAge/BotBackup/ProtocolRunner/BotLog) and treated everything else `Telemetry.cs`
touches as "already bridged via the 9 `RangerBridge.PublishX()` calls, so it's fine."
**That conflates two different relationships that happen to share the same 9 classes:**
the PUBLISH calls (`RangerBridge.PublishAnimatorCull` etc.) are shipping classes handing
facts OUTWARD to the bus, additive, already correct, already verified live. Separately,
**`Telemetry.cs` itself directly reads all 9 shipping classes' OWN STATE** to build its
NDJSON — `Framesaver.Patches.SleepingBotAnimatorPatch.CulledLastFrame`,
`.BossGroupWake.Counts(...)`, `AICoreControllerUpdatePatch.LiveAgents`,
`LongRangeExemption.Count`, `ModCompat.SuppressSlicing`,
`BotStandByUpdatePatch.RoleStandByKnown/RoleAllowsStandBy`, `AsyncDrain.Drained` —
29 direct `Framesaver.Patches.*`/bare-class references total (grepped against Ranger's
now-merged copy of `Telemetry.cs`, 2026-08-17 ~08:16Z).

**`Ranger.csproj` has NO project/assembly reference to `Framesaver.dll`, and never has**
(checked — deliberately absent, matching "Ranger must never be a hard dependency of
Framesaver," which until now was read as one-directional: Framesaver→Ranger soft,
Ranger never needed anything FROM Framesaver). Once `Telemetry.cs` is Ranger's only
copy, these 29 references do not compile — there is nothing for Ranger to resolve
`Framesaver.Patches.SleepingBotAnimatorPatch` against. **This is the identical hazard
`RangerBridge.cs`'s own doc comment describes** (the JIT resolves every type referenced
in a method's IL when that method compiles, not only the branch taken) — pointed the
opposite direction from every case handled so far in this whole extraction.

**This was missed because every previous seam in this doc checked "does Telemetry.cs
read the MOVING class's state" (the capstone-coupling rule) but never checked "does
Telemetry.cs read a STAYING class's state" — which was assumed covered by the publish
wrappers alone. It is not: the wrappers make the shipping class's facts available to
the bus, but Telemetry.cs's OWN CODE, sitting right next to each wrapper call, still
reads the class directly on the SAME LINE RANGE, unchanged.** Concretely: line 1497
calls `LongRangeExemption.PublishTelemetry()` (fine, additive, already correct) but
line 1492, three lines above it, reads `LongRangeExemption.Count` directly into the
NDJSON — and THAT read is what breaks once Telemetry.cs is Ranger-side with no
reference to Framesaver.

**Two candidate shapes, not yet chosen:**
(a) Replace every one of Telemetry.cs's direct reads with `TelemetryBus.TryGet*` —
requires each of the 9 shipping classes to ALSO publish the SPECIFIC values Telemetry
currently reads directly (not just what each `PublishTelemetry()` already sends), which
is more shipping-code surgery than this doc has scoped anywhere so far: 9 classes,
roughly one new bus key per NDJSON field currently read directly (`animCulled`,
`animCulledOffScreen`, `animCulledEngine`, `bossGroups.linked`, `bossGroups.heldAwake`,
`agents.live/pendingRemoval/removedTotal`, `snipersAwake`, `suppressSlicing`,
`roleSleepDist`/`roleWakeDist`, plus the two `RoleStandByKnown`/`RoleAllowsStandBy`
predicates called per-bot inside `CountBots()`, which is a loop — a `TryGet` per bot
per window is a different cost shape than a static field read and needs its own look).
(b) A `FramesaverBridge` mirror of `RangerBridge` — NoInlining-wrapped, assembly-
qualified `Type.GetType`/reflection-based resolution of `Framesaver.Patches.X`, guarded
for Framesaver's absence — living in RANGER, isolating these 29 reads the same way
`RangerBridge` isolates Framesaver's 9 publish sites today. Cheaper to write (mechanical,
same shape already proven) but changes Ranger's posture: today Ranger has no
dependency on Framesaver at all, even a soft/reflection one; this would give it one,
just for `Telemetry.cs`'s read half. Needs Sophia's read on whether that's acceptable
given Ranger is meant to be a standalone kit — a `Telemetry.cs` that reads Framesaver
reflectively is not "standalone" in the same sense GpuTelemetry/PlayerLoopProfiler are.

**Not deciding here.** Flagged to the room 2026-08-17 ~08:18Z. The safe state right now:
only the additive history-merge (Telemetry.cs/BotBackupPatches.cs/ProtocolRunner.cs/
BotLogPatches.cs into Ranger, commit `4c3ae81`) has landed — no namespace switch, no
shipping-code edits, nothing deployed. Both mods still build and run exactly as before;
Ranger's merged copies are inert duplicates, same posture as every prior batch before
its namespace-switch commit. **Stopping here to get a ruling on (a) vs (b) before any
further code**, same discipline as every other design fork this session.
