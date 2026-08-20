using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Ranger.Patches;

namespace Ranger
{
    /// <summary>
    /// Ranger — the standalone telemetry kit extracted from Framesaver.
    ///
    /// SEAM-5 CUTOVER (2026-08-17): as of this commit Ranger OWNS the telemetry
    /// lifecycle. Every measurement patch that lived in Framesaver is enabled HERE,
    /// and Framesaver's own Awake no longer enables any of them (its shipping
    /// features are untouched — stand-by, animator cull, brain scheduler, leak fix,
    /// AsyncDrain suppression). Both mods must be built from this pair of commits;
    /// launching with mismatched halves either double-instruments the game (both
    /// enable) or blinds it (neither does).
    ///
    /// Config is a FRESH start, not a migration of Framesaver's old "3. Telemetry"
    /// keys (Sophia's call, 2026-08-16 23:03Z). Defaults below were matched against
    /// the DEPLOYED cfg values as of cutover, not against Framesaver's code
    /// defaults, so behavior is unchanged from what Sophia has been playing:
    /// SpikeEventMs 30 (code default was 100), Window 30 (code default 60),
    /// MarkKey Mouse3, protocol key PageDown+Ctrl+Alt.
    ///
    /// What is NOT here yet, deliberately: the sampler core itself (Telemetry.cs
    /// still lives in Framesaver and still writes the ndjson — it moves at the
    /// capstone commit with BotLog). What this flip changes is WHO OWNS the
    /// measurement patches and their config, so the sampler's reads (which it
    /// repoints at the bus at capstone) have a live owner on this side.
    /// Translation for the raid right after this lands: Framesaver's Telemetry
    /// component keeps writing the ndjson exactly as before; the patches feeding
    /// it are now Ranger's. If the post-flip raid shows the same ndjson shape as
    /// the gate-fix verification raid, the flip is clean.
    /// </summary>
    [BepInPlugin("ranger.telemetry.kit", "Ranger", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        /// <summary>
        /// Ranger's own build identity, mirrored from Framesaver's Plugin.cs static
        /// constructor rather than shared with it: each assembly reports ITS OWN
        /// AssemblyInformationalVersion, and Ranger's is now the one Telemetry.cs's
        /// header line reads (capstone move - the sampler is Ranger-side and its
        /// header should identify the assembly it actually runs in, not Framesaver's).
        /// Same reasoning as the original: read once, safe body (no throw), empty
        /// rather than absent when the attribute is missing.
        /// </summary>
        public static readonly string BuildVersion;
        public static readonly string BuildCommit;

        static Plugin()
        {
            string informational = "";

            object[] attributes = typeof(Plugin).Assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (attributes.Length > 0)
            {
                informational =
                    ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0])
                    .InformationalVersion ?? "";
            }

            int plus = informational.IndexOf('+');
            BuildVersion = plus < 0 ? informational : informational.Substring(0, plus);
            BuildCommit = plus < 0 ? "" : informational.Substring(plus + 1);
        }

        // ---- Telemetry config ---------------------------------------------------------------
        public static ConfigEntry<bool> TelemetryEnabled;
        public static ConfigEntry<string> RunTag;
        public static ConfigEntry<string> InstallId;
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> ProtocolKey;
        public static ConfigEntry<bool> ProtocolAutoStart;
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> MarkKey;
        public static ConfigEntry<float> TelemetryWindow;
        public static ConfigEntry<float> SpikeEventMs;
        public static ConfigEntry<string> ExpandPhase;
        public static ConfigEntry<bool> ProfilePlayerLoop;

        // AsyncDrain diagnostics (worstCallbacks): read by AsyncDrainPatch's diagnostics half,
        // which is still in Framesaver until the class-split's cutover half. Declared here NOW
        // so the config surface moves once, not twice - Framesaver's Telemetry still reads its
        // own copy until the capstone, at which point only Ranger's is read.
        // NOT YET WIRED to any Ranger-side reader; see the seam-5 notes in EXTRACTION-PLAN.md.
        public static ConfigEntry<bool> AsyncDrainDiagnostics;

        private void Awake()
        {
            LogSource = Logger;

            TelemetryEnabled = Config.Bind(
                "Telemetry", "Enabled", true,
                "Record telemetry. Off disables the sampling component, the measurement patches, " +
                "and every TelemetryBus publish (Count/Event/Tag/Sum) from any consumer mod - the " +
                "same posture as Ranger not being installed.");

            RunTag = Config.Bind(
                "Telemetry", "Run tag", "",
                "Stamped into the telemetry file name and every header, so a written note only needs " +
                "the tag to find its data. Per-run label - set it per run, leave empty for none. " +
                "(The default was briefly the cutover-verification tag; Tau's nit - a label must " +
                "not have an opinion about what run it is.)");

            // Generated ONCE, on this install's first boot, then read (never regenerated) on
            // every boot after - the opposite lifecycle from RunTag above, which the user sets
            // fresh each run. Sophia's ask (2026-08-20 16:12Z): a stable per-install identifier
            // so multiple runs from the same tester can be told apart from runs by someone else,
            // which RunTag alone cannot do (two testers can easily leave it at the same default,
            // or pick the same label independently, and it is not unique by construction).
            //
            // Config.Bind's own default only applies the FIRST time a key is written - once
            // BepInEx has persisted a value to the .cfg file, every later Bind call for the same
            // section+key returns what is on disk, not this default. So a fresh Guid literal
            // baked in here would not regenerate on every boot; it is captured by this specific
            // Bind call and then this Awake() never runs again against the same on-disk value
            // without going through this exact code path. Written out explicitly rather than
            // left implicit, because "why does this look like it changes every build" is a
            // reasonable question for whoever reads this next.
            InstallId = Config.Bind(
                "Telemetry", "Install id", Guid.NewGuid().ToString(),
                "A random identifier generated once when Ranger is first installed, then kept " +
                "unchanged on every later boot. Stamped into every header alongside the per-run " +
                "id, so runs from the same install can be grouped even across different Run tag " +
                "labels. Not tied to hardware or any personal identifier - delete this line from " +
                "the .cfg file to get a fresh one.");

            ProtocolKey = Config.Bind(
                "Telemetry", "Protocol step key",
                new BepInEx.Configuration.KeyboardShortcut(
                    UnityEngine.KeyCode.PageDown,
                    UnityEngine.KeyCode.LeftControl,
                    UnityEngine.KeyCode.LeftAlt),
                "Advances the measurement protocol one step (applies that step's config, closes the " +
                "current telemetry window, stamps the new arm). Does nothing if no protocol is loaded.");

            ProtocolAutoStart = Config.Bind(
                "Telemetry", "Auto-start protocol at raid start", false,
                "Advance the protocol to its first step automatically when a raid starts, instead of " +
                "waiting for the first key press. Leave off unless a timed run wants it.");

            MarkKey = Config.Bind(
                "Telemetry", "Mark key",
                new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.Mouse3),
                "Writes a 'mark' line saying you noticed choppiness just now, with the frames leading " +
                "up to the press. Numbered per raid and stamped with the map.");

            TelemetryWindow = Config.Bind(
                "Telemetry", "Window seconds", 30f,
                new ConfigDescription(
                    "How much wall time each summary line covers.",
                    (AcceptableValueBase)new AcceptableValueRange<float>(10f, 300f)));

            SpikeEventMs = Config.Bind(
                "Telemetry", "Spike event ms", 30f,
                new ConfigDescription(
                    "Write a separate line for every frame at least this slow, carrying that frame's " +
                    "own phase breakdown. 0 disables. Default 30 matches the deployed Framesaver " +
                    "value, not the old code default of 100.",
                    (AcceptableValueBase)new AcceptableValueRange<float>(0f, 2000f)));

            ExpandPhase = Config.Bind(
                "Telemetry", "Do not expand phases", "",
                "Comma-separated player-loop phases NOT to break into their child systems. Blank - " +
                "the default - expands every phase. This is a blocklist. Read only inside Install(), " +
                "so a change takes effect on the NEXT raid load.");

            // Capstone cutover (2026-08-19): moved from Framesaver's Plugin.cs, together with
            // Telemetry.cs and PlayerLoopProfiler.cs themselves (the seam-5 lesson - these three
            // cannot change owners independently). Same key name and default Framesaver's copy
            // used, so an existing BepInEx config file's saved value is NOT silently orphaned by
            // this move (Ranger's config section is "Telemetry", matching every other telemetry
            // key already here, not Framesaver's old "3. Telemetry" - a fresh key under a fresh
            // section, per Sophia's 2026-08-16 23:03Z ruling that Ranger's config is a fresh
            // start, not a migration).
            ProfilePlayerLoop = Config.Bind(
                "Telemetry", "Profile player loop", true,
                "Inject timing markers around every top-level Unity player-loop phase (Initialization, " +
                "EarlyUpdate, FixedUpdate, PreUpdate, Update, PreLateUpdate, PostLateUpdate). This is what " +
                "locates work that falls outside the game's own Update/FixedUpdate/render counters. Turn off " +
                "if you suspect the injection is causing trouble.");

            AsyncDrainDiagnostics = Config.Bind(
                "Experimental", "Async drain diagnostics", true,
                "Time each individual completion callback and report the slowest one per window, " +
                "resolved back to the call site that queued it. Turn off once a culprit is known.");

            TelemetryBus.Enabled = TelemetryEnabled.Value;

            // ---- Measurement patch lifecycle (moved from Framesaver's Awake) --------------
            //
            // TryEnable for diagnostic patches whose absence degrades a measurement rather than
            // breaking a fix - an unresolved checkpoint just merges its segment into the previous
            // one. Same reasoning as Framesaver's own TryEnable; confirmed fixes never go through
            // a swallow-and-continue path.
            TryEnable(new CoversCreateCheckpoint(), "CoversCreateCheckpoint");
            TryEnable(new CoverBoundsCheckpoint(), "CoverBoundsCheckpoint");
            TryEnable(new StationaryInitCheckpoint(), "StationaryInitCheckpoint");
            TryEnable(new ZoneLeaveCtorCheckpoint(), "ZoneLeaveCtorCheckpoint");
            TryEnable(new SettingsRepoCheckpoint(), "SettingsRepoCheckpoint");
            TryEnable(new EventsCtorCheckpoint(), "EventsCtorCheckpoint");
            TryEnable(new BotsControllerMethod2Checkpoint(), "BotsControllerMethod2Checkpoint");
            TryEnable(new GClass369InitCheckpoint(), "GClass369InitCheckpoint");
            TryEnable(new SpawnerCtorCheckpoint(), "SpawnerCtorCheckpoint");
            TryEnable(new CoreActivateCheckpoint(), "CoreActivateCheckpoint");
            TryEnable(new BotCreatorCtorPatch(), "BotCreatorCtorPatch");
            TryEnable(new SetSettingsPatch(), "SetSettingsPatch");
            TryEnable(new SpawnActionPatch(), "SpawnActionPatch");
            TryEnable(new PlayerOnDeadCensusPatch(), "PlayerOnDeadCensusPatch");

            new PlayerLateUpdateTimingPatch().Enable();
            new GameWorldPlayerTickPatch().Enable();
            new JobSchedulerLateUpdatePatch().Enable();
            new AmbientLightLateUpdatePatch().Enable();

            // ---- Capstone cutover (2026-08-19): the sampler core itself, and everything the
            // seam-5 comment above said was staying in Framesaver "until capstone". This IS that
            // commit. Four patch classes moved here WITH the static classes they instrument
            // (BotBackup, BotLog) per EXTRACTION-PLAN.md's "whole file, patches included" call -
            // Framesaver's Plugin.cs no longer enables any of these four.
            new BotBackupAddPatch().Enable();
            new BotBackupFlushPatch().Enable();
            new BotSpawnLogPatch().Enable();
            new BotActivationCanaryPatch().Enable();

            // ---- Wiring-gap fix (2026-08-19, editing pass): these ~26 classes' SOURCE FILES
            // already moved to Ranger in the earlier batch-1/2/3 git-filter-repo moves
            // (EXTRACTION-PLAN.md), but their .Enable() calls were never added here - so
            // Framesaver's OLD copies of the same classes kept firing into a static-data copy
            // nobody read (Telemetry.cs, the only reader, moved to Ranger at the capstone),
            // while these live, correct, Ranger-side copies sat permanently unpopulated. Found
            // via tests/unwrap + a raid-log audit showing updateManual/spawn/bundleLoad/
            // profileBuild all-zero in every sample line of the deployed capstone-verify raid.
            // This list mirrors Framesaver's own .Enable() list exactly (see that file's Awake,
            // "Capstone cutover" comment) for every class WHOSE SOURCE HAS ALREADY MOVED -
            // AsyncDrainPatch itself is deliberately excluded: its diagnostics half (which reads
            // ProfileBuild/BundleLoad/RaidInit) was ALSO planned to split into Ranger per
            // EXTRACTION-PLAN.md's capstone-sequence section, and that split never happened -
            // AsyncDrainPatch.cs is still 100% Framesaver-side. Splitting it is separate,
            // deliberate follow-on work, not folded into this mechanical wiring fix.
            new BotsControllerTickPatch().Enable();
            new UpdateManualTimingPatch().Enable();
            new BossWaveSettingsPatch().Enable();
            new BotControllerSettingsPatch().Enable();
            // AsyncWorkerUpdatePatch/AsyncWorkerFixedUpdatePatch deliberately NOT enabled here.
            // Found and fixed same-session: AsyncWorkerTimingPatches.cs is a MIXED file
            // (EXTRACTION-PLAN.md, Sophia's 2026-08-17 05:13Z ruling) - its FixedUpdate patch's
            // Prefix implements the shipping "Drain completions in Update only" lever
            // (Plugin.DrainInUpdateOnly), not just measurement. Deleting Framesaver's whole
            // file (this session's first pass) silently dropped that lever; restoring it means
            // Framesaver keeps the ONE Harmony patch on both AsyncWorker.Update/.FixedUpdate,
            // now writing into THIS class's statics via RangerBridge instead of a
            // Framesaver-local field. Enabling these two classes here as well would put a
            // SECOND Harmony patch on the same two methods - this class (AsyncWorkerTiming)
            // stays enabled as data storage only; its two ModulePatch classes stay disabled.
            new ProfileCtorPatch().Enable();
            new ProfileInventoryPatch().Enable();
            new BundleLoadPatch().Enable();
            new SpawnCreateDataPatch().Enable();
            new SpawnByWavePatch().Enable();
            new SpawnWithoutWavePatch().Enable();
            new SpawnByTypeForcePatch().Enable();
            new SpawnZoneAttemptPatch().Enable();
            new BotOwnerCreatePatch().Enable();
            new BotCreateWorkPatch().Enable();

            // Raid initialisation, which resumes inline inside the last bot/generate completion callback and
            // is the unexplained 16.7s. One-shot per raid, so no per-frame cost.
            new BotsControllerInitPatch().Enable();
            new WavesSpawnRunPatch().Enable();
            new NonWavesSpawnRunPatch().Enable();
            new BossSpawnRunPatch().Enable();
            new CoversRestorePatch().Enable();
            new CoversCachePointsPatch().Enable();
            new BotDoorsRefreshPatch().Enable();
            new BotZoneInitPatch().Enable();
            new PatrolZoneMapPatch().Enable();
            new CutControllerInitPatch().Enable();
            new LootClusterScanPatch().Enable();

            // Death-event subscription moves with BotLog - see BotLog.Subscribe's own doc
            // comment for why the guard against a double subscription matters here specifically.
            BotLog.Subscribe();

            // PlayerLoopProfiler install/arm, RESTORED here (reverses the seam-5 partial revert
            // above) - now safe because the sampler that reads PlayerLoopProfiler.Snapshot
            // (Telemetry, added below) lives in THIS SAME ASSEMBLY as of this commit. The seam-5
            // defect (ownership inverting in-raid between two assemblies each re-arming their own
            // copy) cannot recur once there is only one copy.
            if (ProfilePlayerLoop.Value)
            {
                PlayerLoopProfiler.Install();
                PlayerLoopProfiler.ArmFrameGap();
            }

            // The sampler component itself. Framesaver's Plugin.cs no longer adds this - see
            // that file's own Awake for the deletion. This is the moment Ranger starts writing
            // the ndjson instead of Framesaver.
            if (TelemetryEnabled.Value)
            {
                gameObject.AddComponent<Telemetry>();
            }

            LogSource.LogInfo("Ranger: telemetry lifecycle AND sampler core owner as of the capstone cutover. Framesaver no longer writes ndjson.");
        }

        private static void TryEnable(SPT.Reflection.Patching.ModulePatch patch, string name)
        {
            try
            {
                patch.Enable();
            }
            catch (Exception ex)
            {
                LogSource.LogWarning("Ranger: diagnostic patch " + name + " did not resolve - "
                                     + ex.Message + ". Its segment will merge into the previous one.");
            }
        }
    }
}
