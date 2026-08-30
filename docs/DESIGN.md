# Telemetry-kit extraction — design draft (Echo, 2026-08-16)

> **Superseded in part (2026-08-29).** The extraction this draft sequenced is
> complete (capstone, 2026-08-19). The shipped bus API is larger than this draft's
> `Count`/`Event`/`Tag` framing - it adds `Sum`, seven registered-callback
> families, and two one-slot reader registrations - and publish sites are
> registered callbacks rather than the Framesaver-side `if (Enabled)` calls
> sketched here. The boundary rule ("the kit records facts; the mod produces
> them") stands; check the code and `docs/FIELD-MAPPING.md` for current shape.

For Sophia's item 3: "pull the telemetry code into its own mod, make Framesaver the
exemplar of how to use it." Draft for boundary sanity-check; implementation sequenced
after Tau's Telemetry.cs 4.1 port landed (collision rule, posted in room 03:41Z).

## 0. What moves and what stays (inventory from the strip-list B-bucket)

**Moves to the kit** (pure instrumentation, no Framesaver-feature logic):

- `Telemetry.cs` (2,533 ln) — NDJSON writer, per-window sampler, spike recorder
- `PlayerLoopProfiler.cs` (666), `GpuTelemetry.cs` (865), `ProtocolRunner.cs` (545)
- The ~16 measurement-only patches: AiTickTiming, AsyncWorkerTiming, LateUpdateTiming,
  UpdateManualTiming, StandByTransitionTiming, AwakeAgeTiming, ComponentCensus,
  BotLogPatches, SpawnAttemptPatches, DistanceGridSpawn, ProfileBuildPatches,
  BundleLoadPatches, BossSpawnGate, RaidInitPatches' measurement half
- 10 config entries: TelemetryEnabled, RunTag, ProtocolKey, ProtocolAutoStart, MarkKey,
  TelemetryWindow, SpikeEventMs, ProfilePlayerLoop, ExpandPhase, GpuTelemetryEnabled

**Stays in Framesaver** (shipping features, 22 entries / 7 files per strip list A-bucket).

## 1. The boundary problem, stated honestly

Today's Telemetry.cs reads Framesaver patch internals directly — `animCulled` counts,
`StandByType` transitions, asleep/awake rosters. Those facts are *produced by* shipping
patches that stay. So the dependency must INVERT during extraction: shipping patches
publish facts; the kit records them. Three consequences:

1. **Publishing points stay in Framesaver** as calls into a small static surface
   (section 2). When the kit is absent they must be no-ops — Sophia disables detailed
   telemetry for the Framesaver release, so no-kit operation is the DEFAULT case, not
   an edge case. Cheap guard: a static bool latched by the kit on load; each publish
   site is `if (TelemetryBus.Enabled) TelemetryBus.X(...)`. One branch, no alloc.
2. **Measurement patches that instrument game internals move whole** — they don't need
   Framesaver state, they hook engine/SPT types directly. Clean cut.
3. **The fields that mix both** (e.g. `agents` block emits `animCulled` next to
   roster counts) split: roster counting is kit-side (it walks the roster itself),
   cull-facts come from the bus.

## 2. Proposed kit API (the "exemplar" surface)

```csharp
// In the kit assembly. Framesaver references only this.
public static class TelemetryBus
{
    public static bool Enabled { get; }              // latched at kit Awake
    public static void Count(string key, int delta); // e.g. Count("animCulled", 1)
    public static void Event(string key, float ms);  // e.g. Event("drainBatch", 4.2f)
    public static void Tag(string key, string value);// e.g. Tag("protocolArm", "B2")
}
```

Deliberately three methods, not a rich interface: the kit's value is the RECORDER
(windows, spikes, NDJSON, protocol arms), not the vocabulary. Keys are strings owned by
the producer; the kit documents a convention (`<feature>.<fact>`) and stays out of
semantics. A consumer mod writes ~5 lines to have durable telemetry; that is the
"mod authors would appreciate this" bar.

Protocol/arming stays kit-side: `ProtocolRunner` + its key/config move with the kit, and
the kit's own config decides recording. Framesaver never gates telemetry config.

## 3. Packaging

- New GUID (Sophia to name it — suggest keeping her handle convention), BepInEx 5.4.23.5,
  net472/x64, same as both current mods.
- Framesaver declares `[BepInDependency(kitGuid, DependencyFlags.SoftDependency)]`:
  loads with or without the kit; the bus no-ops when absent. Hard dependency would make
  the "separate optional download" promise impossible.
- Distribution: two downloads — Framesaver (features) and the kit (instrumentation),
  with Framesaver-as-exemplar docs living in the kit repo pointing at the bus calls in
  Framesaver as reference code.

## 4. Sequencing and risks

1. Tau's 4.1 port of Telemetry.cs lands (their wave list includes it) — commit 1.
2. Mechanical extraction on top of ported code — commit 2: move files, add bus,
   repoint Framesaver's publish sites. No behaviour change; corpus comparisons
   (beta-build-fields / attribute-log) will see the `agents` block arrive unchanged
   if field names are preserved — preserve them.
3. Risks named now: (a) the untracked-six problem class — extraction must move files
   by `git mv` so history follows; (b) run-raid.ps1/analysis scripts hard-code
   Framesaver's log source? The log WRITER moves to the kit — check `attribute-log.py`
   bracket assumptions (its `deferToAiMods` heuristic reads a header field that must
   survive the move verbatim); (c) Sophia's Gate 2 applies: she must be able to maintain
   both mods unaided, so the kit README needs the same no-terminal usage story if she
   ever runs it directly — though default-off makes that rare.

## 5. Open questions for the boundary check (Tau) and Sophia

- Tau: does the port's Telemetry.cs refactor touch the counter-emission points (where
  `animCulled` etc. increment), or only type references? If the latter, extraction is
  purely mechanical after your commit; if the former, we should merge my publish-site
  list with your diff before any file moves.
- Sophia: kit name + repo home; whether the kit ships its own protocol .ini files or
  those stay per-project; and v1 posture for the decoupled-cull/GC knobs (C-bucket)
  since that decides what "features" even means at extraction time.

## Status update (2026-08-16, post-marathon)

Repo home settled: `github.com/milfmodding/ranger.git` (Sophia named it Ranger).
Marathon completed successfully (9 maps, md5-stability held throughout via the
post-marathon clean rebuild). Extraction started per Sophia's 22:04Z go-ahead.
