using System.Diagnostics;
using System.Reflection;
using Diz.Utils;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
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
    /// </summary>
    public static class AsyncWorkerTiming
    {
        public static double UpdateDrainMs;
        public static double FixedDrainMs;

        /// <summary>FixedUpdate drains suppressed this frame - confirms the skip is actually firing.</summary>
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
    /// Times the FixedUpdate drain, and optionally suppresses it.
    ///
    /// Both Update and FixedUpdate call CheckForFinishedTasks, and Unity runs the FixedUpdate phase before
    /// Update - so on any frame that owes a physics step the queue is drained inside physics, and otherwise
    /// in Update. That is the entire reason the same stall shows up as an fuFPS spike sometimes and a
    /// gameUpdate spike other times.
    ///
    /// Suppressing this call makes completions drain once per frame in Update instead, which takes a
    /// multi-hundred-millisecond callback out of the physics step and stops it feeding Unity's catch-up
    /// logic. It does not make the stall smaller - the same work runs either way, one phase later.
    /// </summary>
    internal class AsyncWorkerFixedUpdatePatch : ModulePatch
    {
        private static long _start;
        private static bool _skipped;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AsyncWorker), nameof(AsyncWorker.FixedUpdate));
        }

        [PatchPrefix]
        private static bool Prefix()
        {
            _skipped = Plugin.DrainInUpdateOnly.Value;
            if (_skipped)
            {
                AsyncWorkerTiming.FixedSkips++;
                return false;
            }

            _start = Stopwatch.GetTimestamp();
            return true;
        }

        [PatchPostfix]
        private static void Postfix()
        {
            // Postfixes still run when a prefix skips the original, so the timer must not be read then.
            if (_skipped)
            {
                return;
            }

            AsyncWorkerTiming.FixedDrainMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }
}
