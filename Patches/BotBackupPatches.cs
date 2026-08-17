using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
{
    /// <summary>
    /// Instruments the backup-profile system, which turns out to be both open questions at once.
    ///
    /// BotProfileBackuploader accumulates wave requests in _cacheToLoad and flushes them as a single
    /// /client/game/bot/generate:
    ///
    ///   AddProfileForBackup(data) -> _cacheToLoad.AddRange(data.PrepareToLoadBackend(1))
    ///                              -> if sum(Limit) > 10, fire DoLoad()
    ///   Update()                  -> every 2s, if anything pending and 50s since the last flush, fire
    ///   DoLoad()                  -> if (_loadsProcess <= 1) { take _cacheToLoad, clear it, request the lot }
    ///
    /// (4.1 IL re-verified 2026-08-16: trigger, guard and take-and-clear all identical to 4.0.13; only the
    /// names changed — was GClass684/List_1/method_1/Int_3, wave element was WaveInfoClass.)
    ///
    /// The trigger is a pending total above 10, yet requests of 75 bots
    /// (`assaultx54+assaultx17+marksmanx4`) were observed mid-raid. The `_loadsProcess <= 1` guard explains it: with
    /// two backup requests already in flight, DoLoad returns **without clearing _cacheToLoad**, so the pending
    /// list keeps growing while every new AddProfileForBackup re-fires a call that immediately bails. When a
    /// slot finally frees, the whole accumulation goes out as one request - and a bigger request means a
    /// longer stall, which keeps the slots busy longer. It feeds itself.
    ///
    /// DoLoad also sets _lastSendTime (the last-flush timestamp) *before* the _loadsProcess check, so a bailed
    /// call still resets the 50-second timer in Update.
    ///
    /// `bailed` versus `fired` is the number that confirms or kills this. If bails are rare, the guard is not
    /// the mechanism and the large requests come from somewhere else.
    /// </summary>
    public static class BotBackup
    {
        public static int Added;
        public static int Fired;
        public static int Bailed;
        public static int PendingMax;
        public static int LargestRequest;

        public static void ResetWindow()
        {
            Added = 0;
            Fired = 0;
            Bailed = 0;
            PendingMax = 0;
            LargestRequest = 0;
        }

        /// <summary>
        /// Ranger extraction (2026-08-16/17): publish-side addition, ADDITIVE. Publishes all five
        /// fields, not just the two (Fired/Bailed) the NDJSON "botBackup" block currently emits -
        /// Added/PendingMax/LargestRequest are real per-window facts this class already tracks and
        /// there is no reason the bus should see less than the class knows. Routed through
        /// RangerBridge rather than calling Ranger.TelemetryBus directly - see RangerBridge.cs.
        /// Called once per window from Telemetry.cs's Flush(), beside the existing "botBackup" block.
        /// </summary>
        internal static void PublishTelemetry()
        {
            if (!RangerBridge.Present)
            {
                return;
            }

            RangerBridge.PublishBotBackup(Added, Fired, Bailed, PendingMax, LargestRequest);
        }
    }

    internal class BotBackupAddPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotProfileBackuploader), nameof(BotProfileBackuploader.AddProfileForBackup));
        }

        [PatchPostfix]
        private static void Postfix(BotProfileBackuploader __instance)
        {
            BotBackup.Added++;

            int pending = Pending(__instance);
            if (pending > BotBackup.PendingMax)
            {
                BotBackup.PendingMax = pending;
            }
        }

        internal static int Pending(BotProfileBackuploader instance)
        {
            List<CountTypeBotWave> waves = instance != null ? instance._cacheToLoad : null;
            if (waves == null)
            {
                return 0;
            }

            int sum = 0;
            for (int i = 0; i < waves.Count; i++)
            {
                if (waves[i] != null)
                {
                    sum += waves[i].Limit;
                }
            }

            return sum;
        }
    }

    /// <summary>
    /// Counts flush attempts and, crucially, how many are refused by the in-flight guard.
    ///
    /// Read in a prefix: DoLoad is async, so by the time a postfix runs the list has already been taken and
    /// cleared. _loadsProcess is checked here rather than inferred afterwards for the same reason.
    /// </summary>
    internal class BotBackupFlushPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotProfileBackuploader), nameof(BotProfileBackuploader.DoLoad));
        }

        [PatchPrefix]
        private static void Prefix(BotProfileBackuploader __instance)
        {
            int pending = BotBackupAddPatch.Pending(__instance);

            // Same condition the method itself applies.
            if (__instance._loadsProcess <= 1)
            {
                BotBackup.Fired++;
                if (pending > BotBackup.LargestRequest)
                {
                    BotBackup.LargestRequest = pending;
                }
            }
            else
            {
                BotBackup.Bailed++;
            }
        }
    }
}
