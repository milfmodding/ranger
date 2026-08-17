using System.Diagnostics;
using System.Reflection;
using Diz.Utils;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Ranger
{
    /// <summary>
    /// Times AsyncWorker's completion drain, split by which phase it ran in.
    ///
    /// GClass1516.CheckForFinishedTasks empties the entire completion queue in one unbounded loop, and each
    /// callback is a TaskCompletionSource.SetResult that synchronously resumes whatever awaited it. AsyncWorker
    /// calls it from both Update and FixedUpdate, so a batch of background work finishing at the wrong moment
    /// lands its whole continuation inside a physics step.
    ///
    /// That is the shape of the unexplained spikes: ~800-1800ms frames spent almost entirely in FixedUpdate,
    /// with no change in bot count and allocation jumping from under 1.5MB/s to 123MB/s. Every RunOnBackgroundThread
    /// call site is resource-key or item work, which allocates heavily on completion.
    ///
    /// EXTRACTION SPLIT (ruled by Sophia 2026-08-17 05:13Z, same shape as the AsyncDrainPatch
    /// class-split the strip list already ruled): this is the TIMING half of Framesaver's
    /// AsyncWorkerTimingPatches.cs. The SUPPRESSION half - Prefix reading
    /// Plugin.DrainInUpdateOnly and skipping AsyncWorker.FixedUpdate - is the shipping
    /// "drain completions in Update only" lever and STAYS in Framesaver. At cutover,
    /// Framesaver's copy drops to suppression-only (prefix that just returns false when
    /// the lever is on) and increments FixedSkips here through a seam event rather than
    /// directly, so this counter keeps its NDJSON meaning without a config reference
    /// crossing the boundary. Until cutover, Framesaver's whole original file keeps
    /// running and this copy is inert - same deliberate-duplication state as every
    /// other moved file.
    /// </summary>
    public static class AsyncWorkerTiming
    {
        public static double UpdateDrainMs;
        public static double FixedDrainMs;

        /// <summary>FixedUpdate drains suppressed this frame - confirms the skip is actually firing.
        /// At cutover the increment comes from Framesaver's suppressor via a seam event (see the
        /// class doc above); it stays here so the counter and its NDJSON field keep one home.</summary>
        public static int FixedSkips;

        public static void Reset()
        {
            UpdateDrainMs = 0d;
            FixedDrainMs = 0d;
            FixedSkips = 0;
        }
    }

    internal class AsyncWorkerUpdatePatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AsyncWorker), nameof(AsyncWorker.Update));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            AsyncWorkerTiming.UpdateDrainMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    /// <summary>
    /// Times the FixedUpdate drain - TIMING ONLY. The suppression that used to live in this
    /// patch's Prefix (skip AsyncWorker.FixedUpdate when DrainInUpdateOnly is set) is the
    /// shipping lever and stays in Framesaver; see AsyncWorkerTiming's class doc for the
    /// split ruling and the cutover design.
    ///
    /// Both Update and FixedUpdate call CheckForFinishedTasks, and Unity runs the FixedUpdate phase before
    /// Update - so on any frame that owes a physics step the queue is drained inside physics, and otherwise
    /// in Update. That is the entire reason the same stall shows up as an fuFPS spike sometimes and a
    /// gameUpdate spike other times.
    /// </summary>
    internal class AsyncWorkerFixedUpdatePatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AsyncWorker), nameof(AsyncWorker.FixedUpdate));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            AsyncWorkerTiming.FixedDrainMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }
}
