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
    /// class-split the strip list already ruled): this is the TIMING STORAGE half of what was
    /// Framesaver's AsyncWorkerTimingPatches.cs - the counters its NDJSON block reports. The
    /// SUPPRESSION half - Prefix reading Plugin.DrainInUpdateOnly and skipping
    /// AsyncWorker.FixedUpdate - is the shipping "drain completions in Update only" lever and
    /// STAYS in Framesaver.
    ///
    /// RESOLVED 2026-08-29: the two Ranger-side ModulePatch classes this file once carried were
    /// deleted. They were never Enable()'d - enabling them would have put a SECOND Harmony
    /// patch on AsyncWorker.Update/.FixedUpdate beside Framesaver's single live patch
    /// (re-enabled 2026-08-20 after a same-session delete/restore), which writes these
    /// statics directly through its RangerBridge. Storage only, by design.
    /// </summary>
    public static class AsyncWorkerTiming
    {
        public static double UpdateDrainMs;
        public static double FixedDrainMs;

        /// <summary>FixedUpdate drains suppressed this frame - confirms the skip is actually firing.
        /// The increment comes from Framesaver's suppressor path, written through its RangerBridge
        /// (see the class doc above); it stays here so the counter and its NDJSON field keep one
        /// home.</summary>
        public static int FixedSkips;

        public static void Reset()
        {
            UpdateDrainMs = 0d;
            FixedDrainMs = 0d;
            FixedSkips = 0;
        }
    }
}
