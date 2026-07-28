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
    /// GClass684 accumulates wave requests in List_1 and flushes them as a single /client/game/bot/generate:
    ///
    ///   AddProfileForBackup(data)  -> List_1.AddRange(data.PrepareToLoadBackend(1))
    ///                              -> if sum(Limit) > 10, fire method_1()
    ///   Update()                   -> every 2s, if anything pending and 50s since the last flush, fire
    ///   method_1()                 -> if (Int_3 &lt;= 1) { take List_1, clear it, request the lot }
    ///
    /// The trigger is a pending total above 10, yet requests of 75 bots
    /// (`assaultx54+assaultx17+marksmanx4`) were observed mid-raid. The `Int_3 &lt;= 1` guard explains it: with
    /// two backup requests already in flight, method_1 returns **without clearing List_1**, so the pending
    /// list keeps growing while every new AddProfileForBackup re-fires a call that immediately bails. When a
    /// slot finally frees, the whole accumulation goes out as one request - and a bigger request means a
    /// longer stall, which keeps the slots busy longer. It feeds itself.
    ///
    /// method_1 also sets Float_2 (the last-flush timestamp) *before* the Int_3 check, so a bailed call still
    /// resets the 50-second timer in Update.
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
    }

    internal class BotBackupAddPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GClass684), nameof(GClass684.AddProfileForBackup));
        }

        [PatchPostfix]
        private static void Postfix(GClass684 __instance)
        {
            BotBackup.Added++;

            int pending = Pending(__instance);
            if (pending > BotBackup.PendingMax)
            {
                BotBackup.PendingMax = pending;
            }
        }

        internal static int Pending(GClass684 instance)
        {
            List<WaveInfoClass> waves = instance != null ? instance.List_1 : null;
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
    /// Read in a prefix: method_1 is async, so by the time a postfix runs the list has already been taken and
    /// cleared. Int_3 is checked here rather than inferred afterwards for the same reason.
    /// </summary>
    internal class BotBackupFlushPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GClass684), "method_1");
        }

        [PatchPrefix]
        private static void Prefix(GClass684 __instance)
        {
            int pending = BotBackupAddPatch.Pending(__instance);

            // Same condition the method itself applies.
            if (__instance.Int_3 <= 1)
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
