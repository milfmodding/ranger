using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
{
    /// <summary>
    /// Times the tail of LocalGame.vmethod_1 - the raid initialisation that resumes inline inside a
    /// /client/game/bot/generate completion callback.
    ///
    /// WHY THIS EXISTS. Every raid produces one 16-19s bot/generate callback, always for PMCs, and four
    /// mechanisms were excluded by direct measurement: profile construction (66-135ms of 16,700), the bundle
    /// prologue (272ms), payload size (a 502KB payload took 16.3s while a 1,977KB one took 688ms) and GC
    /// (suspending it moved 6%). The telemetry then ruled out PMCs as the variable outright - 10 PMCs cost
    /// 16.8s while 35 PMCs cost 3.1s, and the same shape decays 11.4 -> 8.7 -> 3.1 -> 2.9s across one client
    /// session. Cost does not track the response at all.
    ///
    /// It is not the response. LocalGame.vmethod_1 does:
    ///
    ///     await botsPresets.TryLoadBotsProfilesOnStart(list);   // <- the bot/generate requests
    ///     BotCreatorClass botCreatorClass = new BotCreatorClass(...);
    ///     BotZone[] array = LocationScene.GetAllObjects&lt;BotZone&gt;(false).ToArray();
    ///     this.botsController_0.Init(...);                      // the whole AI system
    ///     await this.wavesSpawnScenario_0.Run(BeforeGameStarted);
    ///     this.nonWavesSpawnScenario_0.Run();
    ///     this.bossSpawnScenario_0.Run(...);
    ///
    /// TaskCompletionSource.SetResult runs continuations inline, so everything after that await executes on
    /// the main thread inside whichever callback completes the LAST preset batch. Intermediate batches are
    /// cheap because BotsPresets.method_1 opens with `await Task.Delay(500)` and yields immediately; only the
    /// final batch falls out of the loop and returns into vmethod_1. That is exactly what the logs show - in
    /// one window the assault batch reads profileMs 598.0 / residual 1.9, and the PMC batch beside it reads
    /// profileMs 66.2 / residual 16,691.8. Same endpoint, same window; one is mid-loop, the other is last.
    ///
    /// Same trap already documented for /client/match/local/start, recurring one layer down: a drain
    /// callback's measured duration includes the whole synchronously resumed continuation chain.
    ///
    /// WHAT TO READ. `raidInitMs` on the worstCallbacks entry is the headline - if it accounts for the
    /// residual, the question is answered and the remaining work is deciding which section is sliceable.
    ///
    /// Sections nest the same way ProfileBuild's do. The four top-level spans (controllerInit, wavesRun,
    /// nonWavesRun, bossRun) sum into TotalMs and do not overlap each other. Everything under `inside` is a
    /// sub-span of controllerInit, so it must not be added to it.
    ///
    /// OVERLAP WARNING. The spawn scenarios create bots, so profileBuild and bundleLoad time can land inside
    /// these spans. `residualMs` is deliberately left computed as before (ms - profile - bundle) so it stays
    /// comparable with the logs already collected; raidInitMs is reported beside it rather than folded in.
    /// If raidInitMs and residualMs are close, the overlap is small and the attribution is clean. If
    /// raidInitMs materially exceeds residualMs, the sections are double-counting bot creation and the
    /// spawn-scenario spans are the ones to distrust.
    /// </summary>
    public static class RaidInit
    {
        /// <summary>Sum of the four top-level spans. This is what AsyncDrain snapshots per callback.</summary>
        public static double TotalMs;

        /// <summary>
        /// Non-zero while BotsController.Init is on the stack. The sub-spans below are gated on it because
        /// several of their targets have callers elsewhere - AICoversData.RestoreData is also reached from
        /// AIManualPointsHolder, BotEventDebug and StartCoverFinderTester - and time from those would inflate
        /// a sub-span past its parent, silently clamping otherMs to zero.
        /// </summary>
        internal static int Depth;

        /// <summary>BotsController.Init - cover data, zones, spawner, doors, loot clusters.</summary>
        public static double ControllerInitMs;

        /// <summary>
        /// Synchronous prologue of WavesSpawnScenario.Run only. It is `async Task`, so a prefix/postfix pair
        /// measures up to its first await and no further - the rest resumes elsewhere and is not counted here.
        /// Gated on OldSpawn, so expect 0 on maps using the new spawn system.
        /// </summary>
        public static double WavesRunMs;

        /// <summary>NonWavesSpawnScenario.Run - plain void, fully measured.</summary>
        public static double NonWavesRunMs;

        /// <summary>BossSpawnScenario.Run - plain void, fully measured.</summary>
        public static double BossRunMs;

        // --- sub-spans of ControllerInitMs ---

        /// <summary>AICoversData.RestoreData - the whole-map cover database. Prime warm-up suspect.</summary>
        public static double CoversRestoreMs;

        /// <summary>AICoversData.CachePoints.</summary>
        public static double CoversCacheMs;

        /// <summary>BotDoorsController.RefreshData.</summary>
        public static double DoorsMs;

        /// <summary>BotZone.Init, accumulated across every zone on the map.</summary>
        public static double ZoneInitMs;
        public static int Zones;

        /// <summary>BotsController.method_1 - builds the PatrolPoint -&gt; BotZone map.</summary>
        public static double PatrolMapMs;

        /// <summary>
        /// GClass636.Init, the cut controller. Counted as well as timed because BotsController.Init calls it
        /// TWICE - lines 264 and 266 of the decompile, with nothing in between that would invalidate the
        /// first. If cutCalls reads 2 the duplicate is confirmed in the running game, and half of cutMs is
        /// free to reclaim with a one-line prefix.
        /// </summary>
        public static double CutMs;
        public static int CutCalls;

        /// <summary>
        /// AILootPointsCluster.CollectActualSpawnedLoot, accumulated. Each call scans GameWorld.AllLoot, and
        /// Streets has a great many of both clusters and loot - a plausible quadratic.
        /// </summary>
        public static double LootScanMs;
        public static int LootClusters;

        // -----------------------------------------------------------------------------------------------
        // Pass 2: complete partition of BotsController.Init.
        //
        // Pass 1 measured seven specific calls and left 91% of Init in `otherMs` - and the raid 1 vs raid 2
        // comparison then showed that ALL of the session warm-up lives in that unmeasured 91%
        // (controllerInit 13,804 -> 4,059 ms while coversRestore went 802.9 -> 809.4, i.e. flat). Picking
        // more individual methods to time would risk the same outcome again.
        //
        // So this partitions instead of sampling. Each checkpoint marks "about to do X"; the time between
        // consecutive checkpoints is charged to the segment named by the earlier one. Init's statements run
        // in a fixed order, so the segments tile the whole method with no gap and no overlap - the sum of
        // SegMs is ControllerInitMs by construction, and there is nowhere for 12 seconds to hide.
        //
        // Segments are coarser than method timings on purpose: a segment covers every statement between two
        // checkpoints, so a fat one still needs reading against the decompile. That is the point - it says
        // which handful of lines to read rather than which method to guess.
        //
        // Robustness note: if a checkpoint's target stops being called, its segment simply merges into the
        // previous one rather than the measurement breaking.
        // -----------------------------------------------------------------------------------------------

        internal const int SegCount = 17;

        /// <summary>
        /// What each segment covers, keyed by the checkpoint that opens it. Read as "from this call up to
        /// the next checkpoint", so the name is the START of the span, not all of its contents.
        /// </summary>
        internal static readonly string[] SegNames =
        {
            "entry",          // Init prefix -> AICoversData.CreateOrFind: smethod_0 and field assignment
            "coversCreate",   // CreateOrFind itself
            "coversData",     // RestoreData + CachePoints
            "coverBounds",    // BotCoverBounds.DisableAllCoilliders - sweeps every cover collider on the map
            "doorsAndFinds",  // RefreshData, IBotGame singleton, PlantedMines, FindUnityObjectOfType
            "stationary",     // AIStationaryController.Init + AITaskManager
            "zoneLeaveCtor",  // ZoneLeaveControllerClass ctor, artillery zones, GClass620 settings
            "settingsRepo",   // BotSettingsRepoClass.Init + DebugBotData
            "eventsCtor",     // BotsEventsController ctor
            "method2",        // BotsController.method_2
            "gclass369",      // GClass369.Init
            "zoneInit",       // every BotZone.Init, then BotLocationModifier.Validate and the zone filter
            "patrolMap",      // BotsController.method_1
            "spawnerCtor",    // GClass1890 ctor, SpawnControlScenario, event subscriptions, Connections
            "coreActivate",   // AICoreController.Activate + EventsController.Activate
            "cutInit",        // both CutController.Init calls, PlantedMines.Activate, smoke vision
            "lootScan",       // the per-cluster loot scans and Init's tail
        };

        internal static readonly double[] SegMs = new double[SegCount];

        /// <summary>
        /// Gen-0 collections that completed inside each segment.
        ///
        /// Added after the GPU/GC pass established that in-raid collections are rare and individually
        /// catastrophic (16 of 22 produced a >100 ms spike) and that pause cost scales with heap. Segment
        /// times are wall-clock, so a stop-the-world collection lands on whichever segment was running -
        /// the same mechanism that made identically-sized bot/generate callbacks differ 5x and was only
        /// caught because allocKb went negative. With seventeen segments competing to explain 12.6 s, one
        /// contaminated segment could send the next fix in the wrong direction.
        ///
        /// A fat segment with gen0 0 is real work. A fat segment carrying collections needs re-measuring
        /// before anyone acts on it.
        /// </summary>
        internal static readonly int[] SegGen0 = new int[SegCount];

        /// <summary>Collections and heap delta across the whole of BotsController.Init.</summary>
        public static int InitGen0;
        public static double InitHeapDeltaMb;

        private static int _seg;
        private static long _segLast;
        private static int _segGen0Last;
        private static long _initHeapStart;

        /// <summary>
        /// Closes the running segment and opens the one named by <paramref name="slot"/>.
        ///
        /// Only ever advances. Several checkpoint targets are reachable from outside Init - BotZone.Init
        /// fires 21 times in a row, AICoversData.RestoreData is also called by AIManualPointsHolder - so a
        /// repeated mark must keep accumulating into the same segment and a stale one must not rewind the
        /// cursor and start charging early work to a late segment.
        /// </summary>
        internal static void Mark(int slot)
        {
            if (Depth <= 0)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            int gen0 = GC.CollectionCount(0);
            SegMs[_seg] += AiTiming.ToMs(now - _segLast);
            SegGen0[_seg] += gen0 - _segGen0Last;
            _segLast = now;
            _segGen0Last = gen0;

            if (slot > _seg)
            {
                _seg = slot;
            }
        }

        /// <summary>Called from BotsController.Init's prefix - opens segment 0.</summary>
        internal static void BeginSegments()
        {
            _seg = 0;
            _segLast = Stopwatch.GetTimestamp();
            _segGen0Last = GC.CollectionCount(0);
            _initGen0Start = _segGen0Last;
            _initHeapStart = GC.GetTotalMemory(false);
        }

        /// <summary>Called from BotsController.Init's postfix - closes the final segment.</summary>
        internal static void EndSegments()
        {
            int gen0 = GC.CollectionCount(0);
            SegMs[_seg] += AiTiming.ToMs(Stopwatch.GetTimestamp() - _segLast);
            SegGen0[_seg] += gen0 - _segGen0Last;

            InitGen0 += gen0 - _initGen0Start;

            // Net delta, so a negative value means collection reclaimed more than Init allocated - the same
            // tell that identified GC inside the bot/generate callbacks.
            InitHeapDeltaMb += (GC.GetTotalMemory(false) - _initHeapStart) / (1024d * 1024d);
        }

        private static int _initGen0Start;

        // -----------------------------------------------------------------------------------------------
        // Pass 2, bucket B: the tail of vmethod_1 outside BotsController.Init.
        //
        // This is the part that did NOT warm up - 3,494 ms cold, 4,416 ms warm - so it is a different
        // mechanism from the Init warm-up, and in the warm case it is now the larger of the two.
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// BotCreatorClass ctor through to BotsController.SetSettings. The only substantial statement
        /// between them is LocationScene.GetAllObjects&lt;BotZone&gt;(false).ToArray(), so this span is
        /// effectively that scene scan.
        ///
        /// Measured as a gap rather than by patching GetAllObjects, because that method returns a lazy
        /// SelectMany - a prefix/postfix pair around it would time the construction of an enumerator and
        /// report ~0 while the caller's ToArray does all the work.
        /// </summary>
        public static double PreInitMs;

        /// <summary>BotsEventsController.SpawnAction - the last statement of vmethod_1.</summary>
        public static double SpawnActionMs;

        /// <summary>Set by the BotCreatorClass ctor, consumed by the SetSettings checkpoint.</summary>
        internal static long PreInitStart;

        /// <summary>Everything in BotsController.Init not covered by a sub-span above.</summary>
        public static double ControllerOtherMs
        {
            get
            {
                double other = ControllerInitMs - CoversRestoreMs - CoversCacheMs - DoorsMs
                               - ZoneInitMs - PatrolMapMs - CutMs - LootScanMs;
                return other > 0d ? other : 0d;
            }
        }

        /// <summary>Emitted only when something was actually measured - this is a once-per-raid event.</summary>
        public static bool Any
        {
            get { return TotalMs > 0d; }
        }

        public static void Append(StringBuilder sb)
        {
            sb.Append("{\"totalMs\":").Append(F(TotalMs))
              .Append(",\"controllerInitMs\":").Append(F(ControllerInitMs))
              .Append(",\"wavesRunMs\":").Append(F(WavesRunMs))
              .Append(",\"nonWavesRunMs\":").Append(F(NonWavesRunMs))
              .Append(",\"bossRunMs\":").Append(F(BossRunMs))
              .Append(",\"inside\":{\"coversRestoreMs\":").Append(F(CoversRestoreMs))
              .Append(",\"coversCacheMs\":").Append(F(CoversCacheMs))
              .Append(",\"doorsMs\":").Append(F(DoorsMs))
              .Append(",\"zoneInitMs\":").Append(F(ZoneInitMs))
              .Append(",\"zones\":").Append(Zones)
              .Append(",\"patrolMapMs\":").Append(F(PatrolMapMs))
              .Append(",\"cutMs\":").Append(F(CutMs))
              .Append(",\"cutCalls\":").Append(CutCalls)
              .Append(",\"lootScanMs\":").Append(F(LootScanMs))
              .Append(",\"lootClusters\":").Append(LootClusters)
              .Append(",\"otherMs\":").Append(F(ControllerOtherMs))
              .Append('}');

            // Bucket B - the vmethod_1 tail outside Init. preInit is the BotZone scene scan.
            sb.Append(",\"preInitMs\":").Append(F(PreInitMs))
              .Append(",\"spawnActionMs\":").Append(F(SpawnActionMs));

            // The complete partition. These sum to controllerInitMs, so whichever one is fat IS the answer -
            // unlike `inside`, which sampled seven calls and missed 91% of it.
            sb.Append(",\"initGen0\":").Append(InitGen0)
              .Append(",\"initHeapDeltaMb\":").Append(F(InitHeapDeltaMb));

            sb.Append(",\"segments\":{");
            bool first = true;
            for (int i = 0; i < SegCount; i++)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;

                // Emitted as [ms, gen0] rather than two parallel objects so a segment's time and its GC
                // contamination cannot be read apart from each other by accident.
                sb.Append('"').Append(SegNames[i]).Append("\":[").Append(F(SegMs[i]))
                  .Append(',').Append(SegGen0[i]).Append(']');
            }

            sb.Append("}}");
        }

        /// <summary>Local copy of Telemetry's formatter so this block stays self-contained.</summary>
        private static string F(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "null";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static void ResetWindow()
        {
            TotalMs = 0d;
            ControllerInitMs = 0d;
            WavesRunMs = 0d;
            NonWavesRunMs = 0d;
            BossRunMs = 0d;
            CoversRestoreMs = 0d;
            CoversCacheMs = 0d;
            DoorsMs = 0d;
            ZoneInitMs = 0d;
            Zones = 0;
            PatrolMapMs = 0d;
            CutMs = 0d;
            CutCalls = 0;
            LootScanMs = 0d;
            LootClusters = 0;
            PreInitMs = 0d;
            SpawnActionMs = 0d;
            PreInitStart = 0L;

            InitGen0 = 0;
            InitHeapDeltaMb = 0d;

            for (int i = 0; i < SegCount; i++)
            {
                SegMs[i] = 0d;
                SegGen0[i] = 0;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Top-level spans. These sum into RaidInit.TotalMs.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The one that should account for most of it: cover database, every BotZone, the patrol map, the
    /// spawner, and a loot scan per cluster.
    /// </summary>
    internal class BotsControllerInitPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Depth++;
            RaidInit.BeginSegments();
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            RaidInit.EndSegments();
            RaidInit.Depth = 0;
            double ms = AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            RaidInit.ControllerInitMs += ms;
            RaidInit.TotalMs += ms;
        }
    }

    /// <summary>
    /// Synchronous prologue only - Run is `async Task`. Gated on Location.OldSpawn in the caller, so 0 here
    /// means the map uses the new spawn system rather than that the call was free.
    /// </summary>
    internal class WavesSpawnRunPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(WavesSpawnScenario), nameof(WavesSpawnScenario.Run));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            double ms = AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            RaidInit.WavesRunMs += ms;
            RaidInit.TotalMs += ms;
        }
    }

    internal class NonWavesSpawnRunPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(NonWavesSpawnScenario), nameof(NonWavesSpawnScenario.Run));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            double ms = AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            RaidInit.NonWavesRunMs += ms;
            RaidInit.TotalMs += ms;
        }
    }

    internal class BossSpawnRunPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BossSpawnScenario), nameof(BossSpawnScenario.Run));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            double ms = AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            RaidInit.BossRunMs += ms;
            RaidInit.TotalMs += ms;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Sub-spans of BotsController.Init. These nest inside ControllerInitMs - do not add them to it.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole-map cover database. Best fit for the session warm-up curve: the first raid of a client
    /// session pays 16s where the fifth pays 3s, which is what a cache being populated once looks like.
    /// </summary>
    internal class CoversRestorePatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AICoversData), nameof(AICoversData.RestoreData));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(2);
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (RaidInit.Depth <= 0)
            {
                return;
            }

            RaidInit.CoversRestoreMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    internal class CoversCachePointsPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AICoversData), nameof(AICoversData.CachePoints));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (RaidInit.Depth <= 0)
            {
                return;
            }

            RaidInit.CoversCacheMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    internal class BotDoorsRefreshPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotDoorsController), nameof(BotDoorsController.RefreshData));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(4);
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (RaidInit.Depth <= 0)
            {
                return;
            }

            RaidInit.DoorsMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    /// <summary>Per-zone, so the count matters as much as the time - it says whether this scales with the map.</summary>
    internal class BotZoneInitPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotZone), nameof(BotZone.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(11);
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (RaidInit.Depth <= 0)
            {
                return;
            }

            RaidInit.ZoneInitMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            RaidInit.Zones++;
        }
    }

    internal class PatrolZoneMapPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotsController), "method_1");
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(12);
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (RaidInit.Depth <= 0)
            {
                return;
            }

            RaidInit.PatrolMapMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    /// <summary>
    /// Counted because BotsController.Init calls this twice in nine lines. A count of 2 confirms the
    /// duplicate is real at runtime and not a decompiler artifact.
    /// </summary>
    internal class CutControllerInitPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GClass636), nameof(GClass636.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(15);
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (RaidInit.Depth <= 0)
            {
                return;
            }

            RaidInit.CutMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            RaidInit.CutCalls++;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Pass 2 checkpoints. These do no timing of their own - they only advance the segment cursor, so the
    // cost is one timestamp per call and they are all one-shot per raid.
    //
    // Every one is gated inside RaidInit.Mark on Init being on the stack, so targets that are also reachable
    // from menu or debug paths contribute nothing outside a raid load.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Segment 1: AICoversData.CreateOrFind, separate from the RestoreData that follows it.</summary>
    internal class CoversCreateCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AICoversData), nameof(AICoversData.CreateOrFind));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(1);
        }
    }

    /// <summary>
    /// Segment 3: BotCoverBounds.DisableAllCoilliders. Sweeps every cover collider placed on the map, which
    /// is the kind of whole-scene pass that would plausibly cost seconds on Streets and cheapen once Unity
    /// has the objects resident.
    /// </summary>
    internal class CoverBoundsCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotCoverBounds), nameof(BotCoverBounds.DisableAllCoilliders));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(3);
        }
    }

    /// <summary>
    /// Segment 5. Closes the span containing GClass870.FindUnityObjectOfType&lt;AIStationaryController&gt;(),
    /// a scene-wide Unity type search - patched as a boundary rather than directly, because the generic
    /// method would need a closed instantiation and shared generic code makes that unreliable.
    /// </summary>
    internal class StationaryInitCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AIStationaryController), nameof(AIStationaryController.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(5);
        }
    }

    /// <summary>Segment 6.</summary>
    internal class ZoneLeaveCtorCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return RaidInitTargets.SoleConstructor(typeof(ZoneLeaveControllerClass));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(6);
        }
    }

    /// <summary>Segment 7.</summary>
    internal class SettingsRepoCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotSettingsRepoClass), nameof(BotSettingsRepoClass.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(7);
        }
    }

    /// <summary>Segment 8.</summary>
    internal class EventsCtorCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return RaidInitTargets.SoleConstructor(typeof(BotsEventsController));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(8);
        }
    }

    /// <summary>Segment 9.</summary>
    internal class BotsControllerMethod2Checkpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotsController), "method_2");
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(9);
        }
    }

    /// <summary>Segment 10.</summary>
    internal class GClass369InitCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GClass369), nameof(GClass369.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(10);
        }
    }

    /// <summary>Segment 13: the BotSpawner constructor.</summary>
    internal class SpawnerCtorCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return RaidInitTargets.SoleConstructor(typeof(GClass1890));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(13);
        }
    }

    /// <summary>Segment 14.</summary>
    internal class CoreActivateCheckpoint : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AICoreControllerClass), nameof(AICoreControllerClass.Activate));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(14);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Pass 2, bucket B: the vmethod_1 tail outside Init.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the pre-Init span. This is the first statement after the await that resumes inside the drain
    /// callback, so it is the earliest point the tail can be observed from.
    /// </summary>
    internal class BotCreatorCtorPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return RaidInitTargets.SoleConstructor(typeof(BotCreatorClass));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.PreInitStart = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Closes the pre-Init span. SetSettings is called once from vmethod_1 between the scene scan and Init,
    /// but it is a public method that other code may call later in the raid - so the span is consumed
    /// one-shot, and a later call with no pending start contributes nothing.
    /// </summary>
    internal class SetSettingsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotsController), nameof(BotsController.SetSettings));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            if (RaidInit.PreInitStart == 0L)
            {
                return;
            }

            RaidInit.PreInitMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - RaidInit.PreInitStart);
            RaidInit.PreInitStart = 0L;
        }
    }

    /// <summary>The final statement of vmethod_1.</summary>
    internal class SpawnActionPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotsEventsController), nameof(BotsEventsController.SpawnAction));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            RaidInit.SpawnActionMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    /// <summary>
    /// Constructor lookup that does not spell out parameter lists. Every type targeted here has exactly one
    /// declared constructor, and their signatures run to nine parameters of obfuscated types - naming them
    /// would break on any BSG signature change while adding no safety.
    /// </summary>
    internal static class RaidInitTargets
    {
        internal static MethodBase SoleConstructor(System.Type type)
        {
            System.Collections.Generic.List<ConstructorInfo> ctors = AccessTools.GetDeclaredConstructors(type);
            return ctors != null && ctors.Count == 1 ? ctors[0] : null;
        }
    }

    /// <summary>
    /// One call per loot cluster, each scanning the whole of GameWorld.AllLoot. Reporting the cluster count
    /// alongside is what separates "many cheap scans" from "one quadratic".
    /// </summary>
    internal class LootClusterScanPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AILootPointsCluster),
                nameof(AILootPointsCluster.CollectActualSpawnedLoot));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            RaidInit.Mark(16);
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (RaidInit.Depth <= 0)
            {
                return;
            }

            RaidInit.LootScanMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            RaidInit.LootClusters++;
        }
    }
}
