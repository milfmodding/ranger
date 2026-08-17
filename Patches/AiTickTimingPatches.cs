using System.Diagnostics;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
{
    /// <summary>
    /// Per-frame cost of the AI tick, broken into its two halves.
    ///
    /// The baseline run showed Update averaging ~3.5ms of a ~9.5ms gameUpdate, but carrying ~80-90% of the
    /// worst frames. Update contains every MonoBehaviour.Update in the game, so "how much of that is AI" was
    /// unanswerable from the HUD counters alone. These timers answer it directly.
    ///
    /// All three regions are main-thread and non-reentrant, so plain statics are safe and cheaper than
    /// Harmony's __state plumbing.
    /// </summary>
    public static class AiTiming
    {
        /// <summary>BotsController.UpdateByUnity (4.0.13: method_0) - the whole AI tick, driven from BaseLocalGame.Update.</summary>
        public static double TotalMs;



        public static double ToMs(long ticks)
        {
            return ticks * 1000d / Stopwatch.Frequency;
        }
    }

    internal class BotsControllerTickPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            // 4.1: was the obfuscated "method_0"; 4.1 gives it its real name. String-targeted, so the
            // compiler cannot catch a rename - this one killed the first 4.1 raid (Enable() threw and
            // silently dropped every registration after it, including telemetry).
            return AccessTools.Method(typeof(BotsController), "UpdateByUnity");
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            AiTiming.TotalMs = AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }
}
