using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Framesaver.Patches
{
    /// <summary>
    /// UpdateManual cost bucketed by how long the bot has been continuously
    /// awake.
    ///
    /// **The axis nothing we ship can see.** Every instrument we own is per
    /// frame and per population; this is per bot and per trajectory. It exists
    /// because raid 1's awake ms/call rose 0.0335 -> 0.1018 monotonically at a
    /// constant ~10 awake bots, and raid 1.5 did not replicate it - 34 windows,
    /// flat, and 6-10x cheaper - despite running three times longer with more
    /// deaths, which kills corpse and loot accumulation before anyone proposes
    /// it. What differed was the shape of the population: raid 1 held the same
    /// ~10 exemption-role bots awake for the whole raid, raid 1.5 had 1-4
    /// transients that woke near the player and slept again.
    ///
    /// If per-bot cost grows with continuous time awake, three things follow
    /// at once and this one counter prices all of them: the corpus 0.37 and
    /// raid 1.5's 0.22-0.25 are both right and differ only by the awake-age of
    /// the population each was fitted on; role-aware 350 m pays the ramped end
    /// rate because its bots are awake permanently by construction; and
    /// stand-by has a benefit nothing logs - recycling a bot through sleep
    /// resets the ramp.
    ///
    /// **Bucketed rather than pooled, and the buckets ARE the per-bot part.**
    /// Each bot's calls land in the bucket for ITS OWN age, so a window holding
    /// one old bot and ten young ones reports them separately - which a pooled
    /// mean cannot do, and which is the whole reason a pooled mean could not
    /// settle this from the corpus.
    ///
    /// Age survives a window boundary and is reset only by sleeping, dying or
    /// the raid ending. That is the quantity under test, not an accounting
    /// period.
    /// </summary>
    internal static class AwakeAge
    {
        /// <summary>
        /// Upper edges in seconds; the last bucket is everything above the
        /// final edge. Spread wide because the effect under test took thirteen
        /// minutes to triple, so the interesting contrast is minutes against
        /// tens of minutes rather than anything fine-grained.
        /// </summary>
        private static readonly float[] Bounds = { 60f, 150f, 300f, 600f, 1200f };

        private static readonly long[] Ticks = new long[Bounds.Length + 1];
        private static readonly int[] Calls = new int[Bounds.Length + 1];

        /// <summary>
        /// When each awake bot last became un-paused.
        ///
        /// Holds BotOwner references, which is the leak shape this mod exists
        /// to fix - so every exit is wired: sleeping removes, dying removes,
        /// and the raid boundary clears. A bot that dies awake would otherwise
        /// sit here holding its whole graph for the session, exactly as
        /// Sleeping did before ResetForRaid.
        /// </summary>
        private static readonly Dictionary<BotOwner, float> Since =
            new Dictionary<BotOwner, float>();

        internal static void Woke(BotOwner bot)
        {
            if (bot != null)
            {
                Since[bot] = Time.realtimeSinceStartup;
            }
        }

        internal static void Ended(BotOwner bot)
        {
            if (bot != null)
            {
                Since.Remove(bot);
            }
        }

        /// <summary>
        /// One awake UpdateManual call, charged to the bot's own age bucket.
        ///
        /// A bot we have not seen awake before is stamped now rather than
        /// dropped - it spawned awake, or it was already awake when we first
        /// looked. That undercounts its true age rather than misattributing
        /// it, which is the same trade CountBots makes with a null StandBy.
        /// </summary>
        internal static void Record(BotOwner bot, long ticks)
        {
            if (bot == null)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            float since;
            if (!Since.TryGetValue(bot, out since))
            {
                since = now;
                Since[bot] = now;
            }

            int bucket = Bucket(now - since);
            Ticks[bucket] += ticks;
            Calls[bucket]++;
        }

        private static int Bucket(float ageSeconds)
        {
            for (int i = 0; i < Bounds.Length; i++)
            {
                if (ageSeconds < Bounds[i])
                {
                    return i;
                }
            }

            return Bounds.Length;
        }

        /// <summary>
        /// `toS` is the bucket's upper edge in seconds, null for the tail, so
        /// a reader never has to know the edges from somewhere else.
        /// </summary>
        public static void Append(StringBuilder sb)
        {
            sb.Append('[');
            for (int i = 0; i < Calls.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"toS\":");
                sb.Append(i < Bounds.Length
                          ? Bounds[i].ToString("0.#", CultureInfo.InvariantCulture)
                          : "null");
                sb.Append(",\"ms\":").Append(Ms(Ticks[i]))
                  .Append(",\"n\":").Append(Calls[i]).Append('}');
            }

            sb.Append(']');
        }

        private static string Ms(long ticks)
        {
            return AiTiming.ToMs(ticks).ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Zeroes the sums. **Does not touch Since** - an age is the
        /// quantity under test, not an accounting period.</summary>
        public static void ResetWindow()
        {
            Array.Clear(Ticks, 0, Ticks.Length);
            Array.Clear(Calls, 0, Calls.Length);
        }

        internal static void ResetForRaid()
        {
            Since.Clear();
            ResetWindow();
        }
    }

    /// <summary>
    /// The longest ShootData.OnTriggerPressed invocation list on the roster.
    ///
    /// Here rather than in its own file because it measures the same axis:
    /// per-bot state that grows with time awake, which is the thing no
    /// per-frame instrument can see.
    ///
    /// GClass479.method_0 subscribes to that event on every EBotState.Active
    /// edge and only unsubscribes in Dispose, so under
    /// DeactivateSleepingBotState - which drives that edge on every wake,
    /// where vanilla drives it about twice per bot per raid - the list grows
    /// without bound. **This settles
    /// unbounded-but-cheap against bounded outright**, which no amount of
    /// timing can: the per-invocation work is two comparisons, so the cost is
    /// invisible long after the growth is real.
    ///
    /// A max rather than a mean: one bot with a thousand subscribers is the
    /// finding, and a mean over thirty bots would hide it.
    /// </summary>
    internal static class TriggerSubscribers
    {
        private static FieldInfo _backing;
        private static bool _looked;

        /// <summary>
        /// Read through the event's backing field, because an event cannot be
        /// read from outside its declaring type. Null when the field is not
        /// found, which is a real answer - a game update that renames it must
        /// read as "cannot tell", never as zero.
        ///
        /// Plain reflection rather than AccessTools: the field is declared on
        /// ShootData itself, so base-type walking buys nothing, and
        /// AccessTools' static constructor needs the Unity runtime - which
        /// would have made this the one number in the file that no test could
        /// reach.
        /// </summary>
        public static int Max()
        {
            if (!_looked)
            {
                _looked = true;
                _backing = typeof(ShootData).GetField(
                    "OnTriggerPressed", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_backing == null || !Singleton<IBotGame>.Instantiated)
            {
                return -1;
            }

            BotsController controller = Singleton<IBotGame>.Instance.BotsController;
            if (controller == null || controller.Bots == null || controller.Bots.BotOwners == null)
            {
                return -1;
            }

            int max = 0;
            foreach (BotOwner bot in controller.Bots.BotOwners)
            {
                if (bot == null || bot.IsDead || bot.ShootData == null)
                {
                    continue;
                }

                try
                {
                    // GetInvocationList allocates, so this runs once a window
                    // over ~30 bots rather than anywhere near a frame.
                    Delegate d = _backing.GetValue(bot.ShootData) as Delegate;
                    int n = d == null ? 0 : d.GetInvocationList().Length;
                    if (n > max)
                    {
                        max = n;
                    }
                }
                catch (Exception)
                {
                    // A bot mid-teardown must not take the window with it.
                }
            }

            return max;
        }
    }
}
