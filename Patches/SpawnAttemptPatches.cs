using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
{
    /// <summary>
    /// Traces the bot-creation chain, to find out what drives ~7.5 attempts per second in a raid whose bot
    /// count never moves.
    ///
    /// The chain is:
    ///
    ///   BotSpawner.ActivateBotsByWave / ActivateBotsWithoutWave / SpawnBotByTypeForce / BossSpawner
    ///     -> BotCreationDataClass.Create(profileData, creator, count, token)
    ///        -> BotsPresets.FillCreationDataWithProfiles  -> GetNewProfile  (one pool lookup per Create)
    ///        -> LoadBundlesAndCreatePools
    ///     -> TryToSpawnInZoneInner / TrySpawnFreeAndDelay
    ///        -> BotOwner.Create  (an actual bot, finally)
    ///
    /// Because FillCreationDataWithProfiles takes exactly one profile from the pool per Create, the measured
    /// ~112 pool lookups per 15-second window means ~112 Create calls - about 7.5 spawn attempts a second,
    /// while the population sits flat.
    ///
    /// `creates` against `botOwners` is the ratio that matters. If attempts hugely outnumber the bots that
    /// result, the profile and bundle work behind them is waste, and the fix is upstream of everything
    /// measured so far. The per-entry-point counters then say which caller is responsible.
    /// </summary>
    public static class SpawnAttempts
    {
        public static int Creates;
        public static int ByWave;
        public static int WithoutWave;
        public static int ByTypeForce;
        public static int ZoneAttempts;
        public static int BotOwners;

        /// <summary>
        /// Cost and burst size of actual bot construction.
        ///
        /// BotCreatorClass.method_0 loops over data.Profiles and only awaits when the per-bot task has not
        /// already completed - so once profiles and bundles are ready it runs the whole batch in one frame,
        /// and BotSpawner.method_7 hands it every spawn point at once with its single Task.Yield() placed
        /// after the batch rather than between bots.
        ///
        /// `perFrameMax` is the number that decides whether spreading the burst is worth doing: a 700 ms
        /// Update spike at spawn-in is a very different problem if it is 15 bots at 45 ms than if it is 2.
        /// </summary>
        public static double CreateMsTotal;
        public static double CreateMsMax;
        public static int PerFrameMax;

        private static int _frame;
        private static int _thisFrame;

        /// <summary>BotCreatorClass.method_2 - the real per-bot construction cost.</summary>
        public static double BuildMsTotal;
        public static double BuildMsMax;
        public static int BuildPerFrameMax;

        private static int _buildFrame;
        private static int _buildThisFrame;

        internal static void NoteBuild(double ms)
        {
            BuildMsTotal += ms;
            if (ms > BuildMsMax)
            {
                BuildMsMax = ms;
            }

            int frame = UnityEngine.Time.frameCount;
            if (frame != _buildFrame)
            {
                _buildFrame = frame;
                _buildThisFrame = 0;
            }

            _buildThisFrame++;
            if (_buildThisFrame > BuildPerFrameMax)
            {
                BuildPerFrameMax = _buildThisFrame;
            }
        }

        internal static void NoteCreate(double ms)
        {
            CreateMsTotal += ms;
            if (ms > CreateMsMax)
            {
                CreateMsMax = ms;
            }

            int frame = UnityEngine.Time.frameCount;
            if (frame != _frame)
            {
                _frame = frame;
                _thisFrame = 0;
            }

            _thisFrame++;
            if (_thisFrame > PerFrameMax)
            {
                PerFrameMax = _thisFrame;
            }
        }

        public static void ResetWindow()
        {
            Creates = 0;
            ByWave = 0;
            WithoutWave = 0;
            ByTypeForce = 0;
            ZoneAttempts = 0;
            BotOwners = 0;
            CreateMsTotal = 0d;
            CreateMsMax = 0d;
            PerFrameMax = 0;
            BuildMsTotal = 0d;
            BuildMsMax = 0d;
            BuildPerFrameMax = 0;
        }
    }

    internal class SpawnCreateDataPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotCreationData), nameof(BotCreationData.Create));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            SpawnAttempts.Creates++;
        }
    }

    internal class SpawnByWavePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            // The SpawnWave overload - the normal wave path, not the boss one.
            return AccessTools.Method(
                typeof(BotSpawner),
                nameof(BotSpawner.ActivateBotsByWave),
                new[] { typeof(EFT.SpawnWave) });
        }

        [PatchPrefix]
        private static void Prefix()
        {
            SpawnAttempts.ByWave++;
        }
    }

    internal class SpawnWithoutWavePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotSpawner), nameof(BotSpawner.ActivateBotsWithoutWave));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            SpawnAttempts.WithoutWave++;
        }
    }

    internal class SpawnByTypeForcePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotSpawner), nameof(BotSpawner.SpawnBotByTypeForce));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            SpawnAttempts.ByTypeForce++;
        }
    }

    internal class SpawnZoneAttemptPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotSpawner), nameof(BotSpawner.TryToSpawnInZoneInner));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            SpawnAttempts.ZoneAttempts++;
        }
    }

    /// <summary>
    /// The only one of these counters that represents a bot the player can actually meet.
    /// </summary>
    /// <summary>
    /// Times BotCreatorClass.method_2 - the call that actually builds a bot, prefab and all - rather than
    /// BotOwner.Create, which turned out to be a 0.2 ms leaf with at most 4 per frame and could not possibly
    /// account for the ~700 ms spawn-in Update spike.
    ///
    /// method_2 is async, so prefix-to-postfix measures only its synchronous prologue. That is deliberate:
    /// the prologue is the part that runs on the caller's frame, and it is main-thread frame time we are
    /// hunting. If the spike is here, this number will show it; if it is not, the spawn path is exonerated
    /// entirely and the ~700 ms lives somewhere else in Update.
    /// </summary>
    internal class BotCreateWorkPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                AccessTools.TypeByName("BotCreatorClass"), "method_2");
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            SpawnAttempts.NoteBuild(AiTiming.ToMs(System.Diagnostics.Stopwatch.GetTimestamp() - _start));
        }
    }

    internal class BotOwnerCreatePatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), nameof(BotOwner.Create));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            SpawnAttempts.BotOwners++;
            SpawnAttempts.NoteCreate(AiTiming.ToMs(System.Diagnostics.Stopwatch.GetTimestamp() - _start));
        }
    }
}
