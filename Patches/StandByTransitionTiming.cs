using System.Globalization;
using System.Text;

namespace Framesaver.Patches
{
    /// <summary>
    /// Gross stand-by transitions per window, split by direction, with the
    /// time spent inside each path.
    ///
    /// Everything we measure is per FRAME. If a transition costs something,
    /// that cost is invisible to all of it - it would show up smeared across
    /// whichever frames happened to contain transitions, which is a tail and
    /// not a level. That is the axis this adds.
    ///
    /// **Counted transitions, not calls, and the difference is the whole
    /// number.** Wake() runs every check interval for every bot an exemption
    /// holds awake - snipers by rank, followers by group, anything with a goal
    /// enemy - and does nothing when the bot is already awake. Counting calls
    /// would report the exempt population times the check rate and label it
    /// churn, which is a large number that means nothing and would agree with
    /// the hypothesis for the wrong reason.
    ///
    /// Read `wokenMs / woken` as the cost of one transition. Alpha's proxy for
    /// this was |awake[i] - awake[i-1]|, which is NET where this is GROSS, so
    /// the proxy is a strict lower bound and these counts should come out
    /// higher. Three things bound what the correlation behind this can claim,
    /// and they are not repaired by measuring the cost properly: n is 22 in
    /// the stratified row and mostly a zero-versus-one contrast, the proxy
    /// understates so the true effect is larger rather than more precise, and
    /// a third variable driving both churn and the tail is not excluded by
    /// stratifying on the awake level.
    ///
    /// **A flat, small per-transition cost does not exonerate churn**, which
    /// is worth saying because it is the obvious reading of a null result
    /// here. GClass479.method_0 subscribes to ShootData.OnTriggerPressed on
    /// every EBotState.Active edge and only unsubscribes in Dispose, so what a
    /// wake leaves behind is a permanently longer invocation list. That cost
    /// is paid per trigger press, by a bot that woke earlier - so it lands in
    /// neither number here.
    /// </summary>
    public static class StandByTransitions
    {
        private static long _wakeTicks;
        private static long _sleepTicks;
        private static int _woken;
        private static int _slept;

        internal static void Woken(long ticks)
        {
            _wakeTicks += ticks;
            _woken++;
        }

        internal static void Slept(long ticks)
        {
            _sleepTicks += ticks;
            _slept++;
        }

        public static void Append(StringBuilder sb)
        {
            sb.Append("{\"woken\":").Append(_woken)
              .Append(",\"wokenMs\":").Append(Ms(_wakeTicks))
              .Append(",\"slept\":").Append(_slept)
              .Append(",\"sleptMs\":").Append(Ms(_sleepTicks))
              .Append('}');
        }

        /// <summary>
        /// InvariantCulture for the same reason UpdateManualTiming.Ms gives:
        /// a comma-decimal locale turns 2.5 into 2,5 and every window in the
        /// file stops parsing.
        /// </summary>
        private static string Ms(long ticks)
        {
            return AiTiming.ToMs(ticks).ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static void ResetWindow()
        {
            _wakeTicks = 0L;
            _sleepTicks = 0L;
            _woken = 0;
            _slept = 0;
        }
    }
}
