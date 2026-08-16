using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
{
    /// <summary>
    /// Bundle loading, measured three ways, because the first attempt only caught part of it.
    ///
    /// `syncMs` on LoadBundlesAndCreatePools already covers more than it looks: method_1 is async, so
    /// everything before its first await runs synchronously on the caller. That prologue contains three
    /// quadratic scans:
    ///
    ///   pools.PoolsDictionary.Keys.All(...)   per candidate pool  -> O(candidates x existing pools)
    ///   Dictionary_4.ContainsValue(path)      per resource        -> O(resources x loaded resources)
    ///   list2.Contains(path)                  per resource        -> O(resources^2)
    ///
    /// Dictionary_4 accumulates across the raid, so the second one degrades as the raid goes on. That is a
    /// "gets worse over time" signature, which is what this investigation started from - worth watching
    /// `syncMs` against raid clock rather than only its total.
    ///
    /// What was missing is everything after the awaits: smethod_2, AddToken, and InitAndFillPools, which
    /// actually instantiates pooled GameObjects. Those resume on the main thread and are not in `syncMs`.
    /// `totalMs` (wall clock from call to task completion) and `poolFillMs` cover them.
    ///
    /// Note totalMs is elapsed time, not main-thread time: a load that waits on disk for 200ms is not
    /// costing 200ms of frame. Read it alongside `inFlight` - many concurrent long tasks are fine, a serial
    /// chain of them is not.
    /// </summary>
    public static class BundleLoad
    {
        public static int Calls;
        public static int Keys;
        public static int InFlightMax;

        /// <summary>
        /// Largest single call's resource count, and the synchronous prologue time of the worst call.
        ///
        /// Re-added 2026-07-26 after per-window data showed the loading stall tracks keys-per-call rather
        /// than keys in total: in one raid, 2,845 keys spread over 37 calls cost 147 ms while 3,877 keys in
        /// 2 calls cost 21 seconds. That is the signature of the quadratic scans in the prologue
        /// (list2.Contains per resource is O(resources^2)), which only bite when one call carries thousands.
        ///
        /// method_1 is async, so everything before its first await runs synchronously on the caller - which
        /// means the prefix-to-postfix window IS the prologue, and SyncMsMax is the number that should
        /// account for the stall if this theory is right.
        /// </summary>
        public static int KeysMax;
        public static double SyncMsMax;
        public static double SyncMsTotal;

        internal static int InFlight;

        public static void ResetWindow()
        {
            Calls = 0;
            Keys = 0;
            InFlightMax = 0;
            KeysMax = 0;
            SyncMsMax = 0d;
            SyncMsTotal = 0d;
        }
    }

    internal class BundleLoadPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            // 4.1: PoolManagerClass survives as EFT.ObjectsFactory, and LoadBundlesAndCreatePools now has
            // TWO overloads, so the target must be pinned - and pinned with the FULL parameter list:
            // a partial list matches nothing in AccessTools and Enable() throws, which aborts every
            // registration after it (that is exactly how raid 2 died - the first pin named three of
            // six parameters). The prefix reads `resources`, so this is the ICollection<ResourceKey>
            // overload, not the Pools/List one.
            return AccessTools.Method(
                typeof(EFT.ObjectsFactory),
                nameof(EFT.ObjectsFactory.LoadBundlesAndCreatePools),
                new[]
                {
                    typeof(EFT.ObjectsFactory.PoolsCategory),
                    typeof(EFT.ObjectsFactory.AssemblyType),
                    typeof(ICollection<EFT.ResourceKey>),
                    typeof(Diz.Jobs.YieldDelegate),
                    typeof(IProgress<EFT.InitLevelProgress>),
                    typeof(System.Threading.CancellationToken),
                });
        }

        [PatchPrefix]
        private static void Prefix(ICollection<ResourceKey> resources)
        {
            _start = Stopwatch.GetTimestamp();
            BundleLoad.Calls++;
            if (resources != null)
            {
                BundleLoad.Keys += resources.Count;
                if (resources.Count > BundleLoad.KeysMax)
                {
                    BundleLoad.KeysMax = resources.Count;
                }
            }
        }

        [PatchPostfix]
        private static void Postfix(Task __result)
        {
            // Everything before method_1's first await ran on this thread, so this is the prologue cost.
            double syncMs = AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            BundleLoad.SyncMsTotal += syncMs;
            if (syncMs > BundleLoad.SyncMsMax)
            {
                BundleLoad.SyncMsMax = syncMs;
            }

            if (__result == null)
            {
                return;
            }

            BundleLoad.InFlight++;
            if (BundleLoad.InFlight > BundleLoad.InFlightMax)
            {
                BundleLoad.InFlightMax = BundleLoad.InFlight;
            }

            __result.ContinueWith(t => BundleLoad.InFlight--, TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
