using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

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

        // ---- Telemetry config ---------------------------------------------------------------
        public static ConfigEntry<bool> TelemetryEnabled;
        public static ConfigEntry<string> RunTag;
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> ProtocolKey;
        public static ConfigEntry<bool> ProtocolAutoStart;
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> MarkKey;
        public static ConfigEntry<float> TelemetryWindow;
        public static ConfigEntry<float> SpikeEventMs;
        public static ConfigEntry<bool> ProfilePlayerLoop;
        public static ConfigEntry<string> ExpandPhase;

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
                "Telemetry", "Run tag", "41-gatefix-verify",
                "Stamped into the telemetry file name and every header, so a written note only needs " +
                "the tag to find its data. Default carries the cutover verification tag.");

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

            ProfilePlayerLoop = Config.Bind(
                "Telemetry", "Profile player loop", true,
                "Inject timing markers around every top-level Unity player-loop phase. This is what " +
                "locates work that falls outside the game's own Update/FixedUpdate/render counters.");

            ExpandPhase = Config.Bind(
                "Telemetry", "Do not expand phases", "",
                "Comma-separated player-loop phases NOT to break into their child systems. Blank - " +
                "the default - expands every phase. This is a blocklist. Read only inside Install(), " +
                "so a change takes effect on the NEXT raid load.");

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

            if (ProfilePlayerLoop.Value)
            {
                PlayerLoopProfiler.Install();
                PlayerLoopProfiler.ArmFrameGap();
            }

            // NOTE: the Telemetry sampler component itself is NOT added here yet. Telemetry.cs
            // still lives in Framesaver and still owns the ndjson until the capstone commit;
            // adding a second sampler now would double-write the file. Framesaver's Awake
            // keeps its AddComponent<Telemetry> for exactly that reason. See the class doc.

            LogSource.LogInfo("Ranger: telemetry lifecycle OWNER as of seam-5. Sampler core still Framesaver-side until capstone.");
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
