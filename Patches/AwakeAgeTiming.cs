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

        /// <summary>One window's calls for one bot, drained as a row.</summary>
        private struct Span
        {
            public long Ticks;
            public int Calls;
            public float AgeAtLast;
            public float SpanStart;
        }

        private static readonly Dictionary<BotOwner, Span> Live =
            new Dictionary<BotOwner, Span>();

        /// <summary>
        /// Starts an awake span, **if one is not already running.**
        ///
        /// Add-if-absent rather than assign, because this is driven from the
        /// StandByType setter and not every non-paused value is a wake:
        /// active -> goToSave -> active are all un-paused, and assigning would
        /// reset the age of a bot that never slept. Only Ended closes a span.
        /// </summary>
        internal static void Woke(BotOwner bot)
        {
            WokeAt(bot, Time.realtimeSinceStartup);
        }

        internal static void Ended(BotOwner bot)
        {
            // ReferenceEquals, not `== null`. BotOwner is a MonoBehaviour, so
            // `== null` is Unity's overload and answers TRUE for a destroyed
            // object - which would skip this Remove for exactly the bots most
            // in need of it, leaking the entry and the graph behind it. We are
            // asking "is there an object to key on", not "is its native peer
            // alive", and a destroyed bot must still be removable.
            if (!ReferenceEquals(bot, null))
            {
                Since.Remove(bot);
                Live.Remove(bot);
            }
        }

        /// <summary>
        /// Clock injected so the span logic can be driven on a bench. The
        /// alias this closes is the expensive one: a counter that FROZE on
        /// sleep instead of resetting would reproduce the raid's registered
        /// "second block opens at the first block's end value" branch exactly,
        /// and nothing after the fact could tell the instrument error from the
        /// finding.
        /// </summary>
        internal static void WokeAt(BotOwner bot, float now)
        {
            if (!ReferenceEquals(bot, null) && !Since.ContainsKey(bot))
            {
                Since[bot] = now;
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
            RecordAt(bot, ticks, Time.realtimeSinceStartup);
        }

        internal static void RecordAt(BotOwner bot, long ticks, float now)
        {
            if (ReferenceEquals(bot, null))
            {
                return;
            }

            float since;
            if (!Since.TryGetValue(bot, out since))
            {
                since = now;
                Since[bot] = now;
            }

            int bucket = Bucket(now - since);
            Ticks[bucket] += ticks;
            Calls[bucket]++;

            // Per-bot as well as per-bucket, because they are different
            // aggregations rather than one derived from the other: a bot whose
            // age crosses a boundary mid-window has its calls SPLIT across
            // buckets, while its row carries one age. Buckets answer the
            // pooled relation; rows are what a within-bot slope needs, and a
            // bucket comparison cannot give that - the arms wake different
            // populations, so old buckets fill with exemption roles and young
            // ones with transients, which is the composition artifact wearing
            // the fix's clothes.
            Span span;
            Live.TryGetValue(bot, out span);
            span.Ticks += ticks;
            span.Calls++;
            span.AgeAtLast = now - since;
            span.SpanStart = since;
            Live[bot] = span;
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
        /// <summary>
        /// One line per bot per window, drained after the sample so a reader
        /// meets the window before the rows inside it - same ordering as the
        /// bot ledger.
        ///
        /// `awakeS` is the bot's age at its last call in the window, which is
        /// the covariate a within-bot slope regresses against. `ms` and `n`
        /// are that bot's own share, so the rows sum to `awakeMs - deadMs`
        /// and the pooled number stays checkable against the disaggregated
        /// one rather than being trusted.
        ///
        /// **`spanS` identifies the span, and a reader needs it because a
        /// re-wake is not a continuation.** Two rows belong to the same
        /// continuous awake period only when `id` AND `spanS` both match.
        /// Inferring the break from a DECREASE in `awakeS` is nearly right
        /// and fails in one direction: a bot that sleeps and wakes early in a
        /// long window ends it OLDER than the previous row, so the reset is
        /// invisible and two spans get regressed as one. That case is not
        /// rare in the population the stand-by work moves, which is precisely
        /// the population where the artefact would correlate with the
        /// treatment.
        /// </summary>
        internal static void DrainRows(Action<string> emit, int window)
        {
            if (emit == null)
            {
                Live.Clear();
                return;
            }

            foreach (KeyValuePair<BotOwner, Span> entry in Live)
            {
                Span span = entry.Value;
                string id = "";
                string role = "";

                try
                {
                    BotOwner bot = entry.Key;
                    if (!ReferenceEquals(bot, null))
                    {
                        id = bot.ProfileId ?? "";
                        if (bot.Profile != null && bot.Profile.Info != null)
                        {
                            role = bot.Profile.Info.Settings.Role.ToString();
                        }
                    }
                }
                catch (Exception)
                {
                    // Identity is context; the numbers are the measurement.
                }

                emit("{\"type\":\"botWindow\",\"window\":" + window
                     + ",\"id\":\"" + id + "\",\"role\":\"" + role
                     + "\",\"spanS\":"
                     + span.SpanStart.ToString("0.##", CultureInfo.InvariantCulture)
                     + ",\"awakeS\":"
                     + span.AgeAtLast.ToString("0.##", CultureInfo.InvariantCulture)
                     + ",\"ms\":" + Ms(span.Ticks)
                     + ",\"n\":" + span.Calls + "}");
            }

            Live.Clear();
        }

        /// <summary>
        /// Zeroes the sums. **Does not touch Since** - an age is the quantity
        /// under test, not an accounting period. Live is cleared by DrainRows
        /// instead, so a drain that never ran cannot silently lose its rows.
        /// </summary>
        public static void ResetWindow()
        {
            Array.Clear(Ticks, 0, Ticks.Length);
            Array.Clear(Calls, 0, Calls.Length);
        }

        internal static void ResetForRaid()
        {
            Since.Clear();
            Live.Clear();
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
