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

**Deploy note for this session:** Framesaver HEAD is now `582afb1` (TickMath, on top of
the gate fix `886c4bd`); `bin/Release` holds `582afb1`'s build, so the `3F407D7A…` md5
quoted in-room for `886c4bd` is stale. Either build is valid for the verification raid
(both contain the gate fix); match the ndjson header's commit stamp to whichever is
deployed.
