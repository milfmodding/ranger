using BepInEx;
using BepInEx.Logging;

namespace Ranger
{
    /// <summary>
    /// Ranger — the standalone telemetry kit extracted from Framesaver.
    ///
    /// SKELETON ONLY as of this commit. TelemetryBus, the recorder core
    /// (Telemetry.cs/PlayerLoopProfiler.cs/GpuTelemetry.cs/ProtocolRunner.cs) and the
    /// ~16 measurement-only patches have not moved here yet — that is the next commit,
    /// via `git mv` so history follows. See docs/EXTRACTION-PLAN.md for the inventory
    /// and sequencing, and docs/DESIGN.md for the bus API and boundary rules this was
    /// built against.
    /// </summary>
    [BepInPlugin("ranger.telemetry.kit", "Ranger", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("Ranger: skeleton loaded. No telemetry surface yet — see docs/EXTRACTION-PLAN.md.");
        }
    }
}
