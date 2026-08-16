using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Ranger
{
    /// <summary>
    /// Ranger — the standalone telemetry kit extracted from Framesaver.
    ///
    /// IN PROGRESS as of this commit: PlayerLoopProfiler.cs, GpuTelemetry.cs and
    /// Patches/AiTickTimingPatches.cs have moved here with real git history preserved
    /// (git-filter-repo + merge --allow-unrelated-histories, not a copy). Telemetry.cs,
    /// ProtocolRunner.cs and the remaining measurement-only patches have not moved yet.
    /// See docs/EXTRACTION-PLAN.md for the inventory and what's still blocked.
    ///
    /// Config here is a FRESH start, not a migration of Framesaver's old "3. Telemetry"
    /// keys (Sophia's call, 2026-08-16 23:03Z: nothing shipped yet, only two of us run
    /// this, no need to carry old defaults forward). Entries move here one at a time as
    /// the code that reads them moves.
    /// </summary>
    [BepInPlugin("ranger.telemetry.kit", "Ranger", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        // ---- Config, moved so far -------------------------------------------------
        //
        // Only entries whose READING code has actually moved to this assembly. Do not
        // pre-declare the rest of the ten from the design doc's inventory ahead of the
        // code that uses them - an unread config entry is a promise nothing keeps.

        public static ConfigEntry<string> ExpandPhase;
        public static ConfigEntry<bool> GpuTelemetryEnabled;

        private void Awake()
        {
            LogSource = Logger;

            // Renamed from "Expand phase" in Framesaver when the meaning inverted from
            // allowlist to blocklist - preserved verbatim here, including the section
            // name, so PlayerLoopProfiler.cs's read of Plugin.ExpandPhase needs no change
            // beyond which Plugin class it resolves to.
            ExpandPhase = Config.Bind(
                "Telemetry", "Do not expand phases", "",
                "Comma-separated player-loop phases NOT to break into their child systems. Blank - the "
                + "default - expands every phase, which is what you almost always want. This is a "
                + "blocklist: an allowlist could only time phases someone had thought to name, so a "
                + "phase carrying a rare large spike went unmeasured while the output looked complete. "
                + "Read only inside Install(), so a change takes effect on the NEXT raid load. The "
                + "phases actually expanded are reported as `expandedPhases` on the telemetry header, "
                + "and entries matching no phase are logged.");

            GpuTelemetryEnabled = Config.Bind(
                "Telemetry", "GPU telemetry", true,
                "Sample VRAM budget vs usage (BSG's own DXGI query, twice a second), Unity's FrameTimingManager " +
                "and the render-submission profiler counters. This is the only view into the GPU side, which is " +
                "where the TimeUpdate presentation-wait spikes live. Sources that this build does not support " +
                "report themselves as unavailable and then stop costing anything.");

            LogSource.LogInfo("Ranger: loaded. Extraction in progress - see docs/EXTRACTION-PLAN.md for what's here so far.");
        }
    }
}
