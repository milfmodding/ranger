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
    /// Times BotOwner.UpdateManual, split by whether the bot was paused on
    /// entry.
    ///
    /// UpdateManual is the gate on the 22 subsystem ticks that stand-by
    /// exists to skip, and nothing has ever timed it. It is not inside
    /// `aiTotal` - that is BotsController.method_0, the brain tick - and it
    /// has no player-loop phase of its own. Every price we have put on
    /// keeping a bot awake came from regressing frame time on bots.awake
    /// across legs, or, for this term, from one significant figure
    /// back-derived from a docstring.
    ///
    /// THE WHOLE METHOD IS TIMED AND THE SPLIT DOES THE WORK. A paused bot
    /// runs StandBy.Update() and stops; an awake bot runs that plus the 22
    /// ticks. So
    ///
    ///     awakeMs/awakeCalls - pausedMs/pausedCalls
    ///
    /// is the marginal cost of keeping one bot awake, measured on the same
    /// bots in the same frames rather than as a slope across legs.
    /// Instrumenting the inner block instead would need a transpiler to find
    /// it, and would still have to be differenced against something.
    ///
    /// Harmony's prefix/postfix overhead lands on both buckets equally, so
    /// it inflates both totals and very nearly cancels in the difference.
    /// **Read the difference. Treat the two absolutes as upper bounds.**
    ///
    /// The means are deliberately not computed here: a derived number in the
    /// log is one that can go stale against the inputs beside it.
    /// </summary>
    public static class UpdateManualTiming
    {
        private static long _awakeTicks;
        private static long _pausedTicks;
        private static int _awakeCalls;
        private static int _pausedCalls;

        /// <summary>
        /// Calls whose prefix never ran, so there was no start stamp to
        /// subtract.
        ///
        /// SleepingBotStandByPumpPatch also prefixes UpdateManual and
        /// returns false on its own path; a prefix returning false skips
        /// every later prefix but NOT the postfixes. HarmonyPriority.First
        /// is what keeps ours in front of it, and this counter is the check
        /// on that - the failure it guards is invisible otherwise, because a
        /// postfix reading a stale static start stamp returns a plausible
        /// duration for a call it never timed. Dropping the sample and
        /// counting it is the difference between a number we know is
        /// incomplete and one we cannot tell is wrong.
        /// </summary>
        private static int _unstampedCalls;

        private static int _deadCalls;
        private static long _deadTicks;

        internal static void Add(long ticks, bool paused)
        {
            if (paused)
            {
                _pausedTicks += ticks;
                _pausedCalls++;
                return;
            }

            _awakeTicks += ticks;
            _awakeCalls++;
        }

        internal static void AddUnstamped()
        {
            _unstampedCalls++;
        }

        /// <summary>
        /// Calls made by a bot that is already dead, which is a SUBSET of the
        /// counts above rather than a fourth bucket - the ticks are still in
        /// awakeMs, so no existing log changes meaning.
        ///
        /// They are awake by every test we have: BotsClass.UpdateByUnity ticks
        /// every bot on its roster with no liveness check, and a corpse keeps
        /// StandByType_1 == active. Their cost is near zero because the guard
        /// inside UpdateManual drops them, so they dilute awakeMs/awakeCalls
        /// downward and the dilution grows with the body count.
        ///
        /// Subtract before quoting a per-bot cost. A raid that ran long with
        /// many deaths is not cheaper per live bot than a short one - it has
        /// more corpses in the denominator.
        /// </summary>
        internal static void AddDead(long ticks)
        {
            _deadTicks += ticks;
            _deadCalls++;
        }

        public static void Append(StringBuilder sb)
        {
            sb.Append("{\"awakeMs\":").Append(Ms(_awakeTicks))
              .Append(",\"awakeCalls\":").Append(_awakeCalls)
              .Append(",\"pausedMs\":").Append(Ms(_pausedTicks))
              .Append(",\"pausedCalls\":").Append(_pausedCalls)
              .Append(",\"unstampedCalls\":").Append(_unstampedCalls)
              .Append(",\"deadCalls\":").Append(_deadCalls)
              .Append(",\"deadMs\":").Append(Ms(_deadTicks))
              .Append('}');
        }

        /// <summary>
        /// InvariantCulture is load-bearing, not tidiness: a comma-decimal
        /// locale turns `"awakeMs":2.5` into `"awakeMs":2,5` and every window
        /// in the file stops parsing. Telemetry.Fmt and RaidInit.F both
        /// already do this; a third copy of that helper would cost more than
        /// the one call each site makes.
        /// </summary>
        private static string Ms(long ticks)
        {
            return AiTiming.ToMs(ticks).ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static void ResetWindow()
        {
            _awakeTicks = 0L;
            _pausedTicks = 0L;
            _awakeCalls = 0;
            _pausedCalls = 0;
            _unstampedCalls = 0;
            _deadCalls = 0;
            _deadTicks = 0L;
        }
    }

    internal class UpdateManualTimingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual));
        }

        /// <summary>
        /// __state rather than the static start stamp the sibling AI timer
        /// uses. UpdateManual is per-bot and non-reentrant, so a static would
        /// be safe on its own - but only while our prefix runs on every call,
        /// and the pump patch can stop that. __state defaults to 0 when the
        /// prefix is skipped, which is what lets the postfix tell.
        /// </summary>
        [PatchPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(BotOwner __instance, out long __state)
        {
            // Read the split at ENTRY, not at exit: UpdateManual is where a
            // bot wakes, so one that arrives paused and leaves awake did the
            // paused amount of work. Carried in the sign, so one out-param
            // holds both and the postfix needs no second read of an object
            // the call may have changed. Timestamps are large and positive,
            // so the sign is free.
            //
            // No "StandBy was null" bucket, unlike CountBots. That check
            // guards a census walking every BotOwner including ones
            // mid-teardown; the body we wrap here dereferences StandBy
            // unconditionally, so a null would have thrown in vanilla before
            // this postfix could run. A counter for a state the game cannot
            // reach is a moving part that only ever reads zero.
            bool paused = __instance.StandBy != null
                          && __instance.StandBy.StandByType_1 == BotStandByType.paused;

            long now = Stopwatch.GetTimestamp();
            __state = paused ? -now : now;
        }

        [PatchPostfix]
        private static void Postfix(BotOwner __instance, long __state)
        {
            if (__state == 0L)
            {
                UpdateManualTiming.AddUnstamped();
                return;
            }

            bool paused = __state < 0L;
            long start = paused ? -__state : __state;
            long ticks = Stopwatch.GetTimestamp() - start;
            UpdateManualTiming.Add(ticks, paused);

            // **Corpses tick too, and they read as awake.** BotsClass.
            // UpdateByUnity calls UpdateManual on every bot in its set with no
            // liveness test - the guard is INSIDE UpdateManual, so this
            // postfix has already run by the time it fails. A dead bot keeps
            // StandByType_1 == active (the census caught one still saying so
            // ten seconds after death), so it lands in the awake bucket at
            // near-zero cost for as long as it stays on the roster.
            //
            // Counted, never silently dropped: deadCalls is a SUBSET of
            // awakeCalls, so every existing log keeps the meaning it had and
            // the contamination becomes measurable instead of arriving as a
            // surprise in the ramp.
            bool dead = __instance != null && __instance.IsDead;
            if (dead)
            {
                UpdateManualTiming.AddDead(ticks);
            }

            // Age is excluded outright rather than counted, because a corpse
            // has no meaningful continuous-awake age and would land in a
            // bucket either way it is handled: Ended() drops it on death, so
            // Record would re-stamp it at age 0 and pile near-zero costs into
            // the YOUNGEST bucket - making young bots look cheap and inverting
            // the very finding this instrument exists to test.
            if (!paused && !dead)
            {
                AwakeAge.Record(__instance, ticks);
            }
        }
    }
}
