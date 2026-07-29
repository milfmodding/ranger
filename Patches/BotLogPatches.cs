using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Framesaver.Patches
{
    /// <summary>
    /// One line per bot spawn and one per bot death.
    ///
    /// **This retires the class of fact only Sophia could supply.** "I did not
    /// see Gluhar" and "Gluhar did not spawn" are different propositions that
    /// no instrument we had could separate, and that a person cannot separate
    /// either - she would have had to walk to the garrison to answer it. The
    /// spawn log answers it better than she can.
    ///
    /// It would also have prevented a specific error we made: `exempt` pinned
    /// at 4 then draining 4 -> 3 -> 2 -> 1 could not distinguish Shturman plus
    /// three followers from Shturman plus two plus an unrelated PMC, because
    /// `exempt` counts every CAN_STAND_BY=false bot and every PMC is one. With
    /// named spawns and deaths that ambiguity cannot arise.
    ///
    /// A death log is also the first measure of COMBAT INTENSITY this project
    /// has had, which is the missing instrument for the residual: the theory
    /// is that per-agent cost rises during fights, through the cover search,
    /// and nothing we log distinguishes a fight from a quiet patrol.
    ///
    /// **This is a LEDGER and `bots.total` is a CENSUS.** They are two
    /// instruments over one population and must not be derived from each
    /// other, or the agreement becomes a tautology - the `tickedSum/liveSum`
    /// == 1.0000 shape. Nothing here reads the census and the census reads
    /// nothing here. Where they disagree the residual is the DESPAWN count,
    /// which ZonesLeaveController produces without killing anything and which
    /// is otherwise invisible to us.
    /// </summary>
    public static class BotLog
    {
        private static readonly Queue<string> Pending = new Queue<string>(64);

        /// <summary>
        /// Drained by Telemetry each window. A queue rather than a direct write
        /// so nothing here needs the Telemetry instance, and so a spawn storm
        /// during raid load cannot interleave with the window line it belongs
        /// to. Every line carries its own clock, so flushing late costs
        /// ordering in the file and nothing else.
        /// </summary>
        internal static void Drain(Action<string> write)
        {
            lock (Pending)
            {
                while (Pending.Count > 0)
                {
                    write(Pending.Dequeue());
                }
            }
        }

        private static void Emit(string line)
        {
            lock (Pending)
            {
                Pending.Enqueue(line);
            }
        }

        /// <summary>
        /// Cleared at raid start so a raid cannot inherit the previous one's
        /// unflushed tail. Deaths outlive their window but never their raid.
        /// </summary>
        public static void ResetForRaid()
        {
            lock (Pending)
            {
                Pending.Clear();
            }
        }

        private static bool _subscribed;

        /// <summary>
        /// Deaths arrive by event, not by patch. `Player.OnPlayerDeadStatic`
        /// carries victim, aggressor, damage info and body part in one place;
        /// the obvious alternative - BotOwner.Create's mirror,
        /// BotsController.BotDied - is a clean single path that passes only the
        /// BotOwner, so the obvious hook cannot answer the question the log
        /// exists for.
        ///
        /// The guard is not ceremony. A double subscription doubles every death
        /// line, and this is a LEDGER: a doubled ledger disagrees with the
        /// census by roughly the despawn count's magnitude, so it would be read
        /// as the residual we are hunting rather than as our own defect.
        /// </summary>
        public static void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }

            Player.OnPlayerDeadStatic += Death;
            _subscribed = true;
        }

        internal static void Spawn(BotOwner bot)
        {
            if (bot == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder(320);
            sb.Append("{\"type\":\"botSpawn\"");
            Common(sb, bot);

            // Role and CAN_STAND_BY captured HERE, because a dead bot is gone
            // and neither is recoverable afterwards. Read live from the bot's
            // own settings, never from a list we ship - the same reason
            // RoleAllowsStandBy reads the database per bot.
            //
            // Tri-state on purpose. null means "could not read the flag", which
            // is NOT false meaning "this role is exempt". Collapsing the two is
            // the confusion RoleStandByKnown exists to prevent, and it inflates
            // the exempt count with unknowns.
            sb.Append(",\"canStandBy\":");
            if (!BotStandByUpdatePatch.RoleStandByKnown(bot))
            {
                sb.Append("null");
            }
            else
            {
                sb.Append(BotStandByUpdatePatch.RoleAllowsStandBy(bot) ? "true" : "false");
            }

            sb.Append('}');
            Emit(sb.ToString());
        }

        /// <summary>
        /// `killerState` is authoritative and has THREE values. It must never
        /// collapse to two, and unknown must never resolve to the player.
        ///
        /// The field decides "did Sophia's fight cause this degradation", so a
        /// default-to-player would systematically overstate her, aimed exactly
        /// at the segmentation the log exists for. The game already asserts the
        /// distinction and we only have to carry it: Player's damage handler
        /// sets LastAggressor to a named iPlayer when damageInfo.Player is
        /// present, and to null EXPLICITLY otherwise - bleeding, falls,
        /// bot-on-bot with nothing recorded.
        ///
        /// **Artillery is the case that would fool a one-field read**: it sets
        /// a synthetic Aggressor stat and leaves LastAggressor null, so it
        /// arrives here looking like "no aggressor". `damageType` is what
        /// separates it, which is why state `none` carries a cause and is
        /// informative rather than merely absent.
        /// </summary>
        private static void Death(Player victim, IPlayer aggressor, DamageInfoStruct damage, EBodyPart part)
        {
            if (victim == null)
            {
                return;
            }

            // `death`, not `botDeath`: Player.OnDead raises this for EVERY
            // player, Sophia included. The narrower name would overstate what
            // the line brackets - the defect Shutter renamed generateMs over.
            //
            // **Consequence for the reconciliation: pair on `id` WHERE isAI is
            // true.** Her own death has no matching botSpawn by construction,
            // so an unfiltered pairing reports the missed-spawn-hook signature
            // every single raid.
            StringBuilder sb = new StringBuilder(384);
            sb.Append("{\"type\":\"death\"");
            CommonPlayer(sb, victim);

            sb.Append(",\"damageType\":\"").Append(Esc(damage.DamageType.ToString())).Append('"');
            sb.Append(",\"bodyPart\":\"").Append(Esc(part.ToString())).Append('"');

            if (aggressor == null)
            {
                sb.Append(",\"killerState\":\"none\",\"killer\":null");
            }
            else
            {
                string id = "";
                string role = "";
                bool isAi = true;
                bool read = true;

                try
                {
                    id = aggressor.ProfileId ?? "";
                    isAi = aggressor.IsAI;
                    role = aggressor.Profile != null && aggressor.Profile.Info != null
                           && aggressor.Profile.Info.Settings != null
                        ? aggressor.Profile.Info.Settings.Role.ToString()
                        : "";
                }
                catch (Exception)
                {
                    // An aggressor object we cannot interrogate is the third
                    // state, not the second. Saying "no aggressor" here would
                    // be a false statement about the raid rather than a gap.
                    read = false;
                }

                if (!read)
                {
                    sb.Append(",\"killerState\":\"unread\",\"killer\":null");
                }
                else
                {
                    sb.Append(",\"killerState\":\"named\",\"killer\":{\"id\":\"").Append(Esc(id))
                      .Append("\",\"role\":\"").Append(Esc(role))
                      .Append("\",\"isAI\":").Append(isAi ? "true" : "false").Append('}');
                }
            }

            sb.Append('}');
            Emit(sb.ToString());
        }

        private static void Common(StringBuilder sb, BotOwner bot)
        {
            Player player = bot.GetPlayer;
            if (player != null)
            {
                CommonPlayer(sb, player);
                return;
            }

            Clock(sb);
            sb.Append(",\"id\":\"\",\"role\":\"\",\"isAI\":true");
            Pos(sb, bot.Position);
        }

        private static void CommonPlayer(StringBuilder sb, Player player)
        {
            Clock(sb);

            string id = "";
            string role = "";
            try
            {
                id = player.ProfileId ?? "";
                if (player.Profile != null && player.Profile.Info != null
                    && player.Profile.Info.Settings != null)
                {
                    role = player.Profile.Info.Settings.Role.ToString();
                }
            }
            catch (Exception)
            {
            }

            // `id` is the profile id and it is the SAME value on this bot's
            // spawn
            // line and its death line. That is what lets the reconciliation
            // PAIR
            // events rather than only count them, and a death with no matching
            // spawn is the signature of a missed spawn hook.
            sb.Append(",\"id\":\"").Append(Esc(id)).Append('"');
            sb.Append(",\"role\":\"").Append(Esc(role)).Append('"');
            sb.Append(",\"isAI\":").Append(player.IsAI ? "true" : "false");
            Pos(sb, player.Position);
        }

        /// <summary>
        /// `qpc` and `raidElapsed` both, with the window ordinal alongside them
        /// rather than instead of them.
        ///
        /// **Join by containment, never by nearest.** The ordinal is a claim
        /// the containment check can verify. Nearest-start lands on the
        /// neighbouring window exactly at the boundaries where an event matters
        /// most - the same failure CORPUS records for the PresentMon join.
        ///
        /// `state` is carried so an event with no containing raid window is
        /// EMITTED AND MARKED rather than dropped. A spawn during loading is
        /// precisely the thing that would otherwise vanish and be inferred back
        /// as "did not spawn" - the proposition this log exists to settle.
        /// </summary>
        private static void Clock(StringBuilder sb)
        {
            sb.Append(",\"qpc\":").Append(GpuTelemetry.Qpc());
            sb.Append(",\"window\":").Append(Telemetry.CurrentWindow);
            sb.Append(",\"state\":\"").Append(Esc(Telemetry.CurrentStateName)).Append('"');

            double elapsed;
            if (Telemetry.TryGetRaidElapsed(out elapsed))
            {
                sb.Append(",\"raidElapsed\":").Append(elapsed.ToString("0.###", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(",\"raidElapsed\":null");
            }
        }

        private static void Pos(StringBuilder sb, Vector3 p)
        {
            // Position on BOTH events. Death positions locate the fight itself,
            // which is a better predictor than where the player was standing.
            sb.Append(",\"pos\":[").Append(F(p.x)).Append(',').Append(F(p.y))
              .Append(',').Append(F(p.z)).Append(']');
        }

        private static string F(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v)
                ? "null"
                : v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Esc(string v)
        {
            return v == null ? "" : v.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>
    /// The single spawn hook. Every bot from all three spawn systems -
    /// BossSpawnerClass, WavesSpawnScenario and the NonWavesSpawnScenario
    /// trickle - passes through BotOwner.Create, so this is one patch rather
    /// than three.
    ///
    /// The `source` field is deliberately absent: Create's signature carries no
    /// creation data, and the bot cannot reach it afterwards either -
    /// SpawnProfileData.SpawnParams has TriggerType and Id_spawn set only on
    /// the wave path, and role does not discriminate because exUsec and pmcUSEC
    /// come through the BOSS spawner without being bosses. Adding it needs a
    /// table keyed on BotCreationDataClass identity, read at
    /// BotSpawner.method_11. Scoped, not built, and not guessed at here.
    /// </summary>
    internal class BotSpawnLogPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), nameof(BotOwner.Create));
        }

        [PatchPostfix]
        private static void Postfix(BotOwner __result)
        {
            BotLog.Spawn(__result);
        }
    }
}
