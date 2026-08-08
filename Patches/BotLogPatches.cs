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

        /// <summary>
        /// The stand-by permission a bot was actually granted, written where
        /// the grant is DECIDED rather than where the bot is created.
        ///
        /// **`botSpawn.canStandBy` cannot answer this and never could.**
        /// `BotOwner.Create` is a static factory at `BotOwner.cs:1033`;
        /// `InitPoints` runs at `:1772`, inside `method_10` - the activation
        /// path. So when the spawn line is written the grant has not been
        /// decided, and the only thing available at that site was the role's
        /// declared `Mind.CAN_STAND_BY`. Property, not outcome, in the field
        /// that looked like it would answer.
        ///
        /// Why it matters more than tidiness: `forceAllRoles` is granted once
        /// at activation and never revoked, so **a bot carries its assignment
        /// for its whole life.** That makes a window-level contrast a mixture
        /// of bots assigned at different times, and a BOT-level contrast clean
        /// - but only if the assignment is recorded. Unrecorded, the latch is
        /// fatal; recorded, it is the property that makes the design work.
        ///
        /// `roleAllows` sits beside `effective`, never instead: under
        /// `forceAllRoles` a bot reads false on the property while holding a
        /// true grant, and that disagreement IS the measurement.
        /// </summary>
        internal static void StandByAssigned(BotStandBy standBy, BotOwner bot)
        {
            if (standBy == null || bot == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder(320);
            sb.Append("{\"type\":\"botStandBy\"");
            Common(sb, bot);

            sb.Append(",\"effective\":").Append(standBy.CanDoStandBy ? "true" : "false");

            sb.Append(",\"roleAllows\":");
            if (!BotStandByUpdatePatch.RoleStandByKnown(bot))
            {
                sb.Append("null");
            }
            else
            {
                sb.Append(BotStandByUpdatePatch.RoleAllowsStandBy(bot) ? "true" : "false");
            }

            // The arm this bot was assigned under, on the bot's own line.
            // cfg carries it per window, but a window is a mixture of bots
            // assigned at different times - which is the whole reason this
            // line exists.
            sb.Append(",\"forced\":")
              .Append(Plugin.ForceStandByForAllRoles.Value ? "true" : "false");

            sb.Append('}');
            Emit(sb.ToString());
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
            // A bot leaving the roster is neither a wake nor a sleep, so it
            // lands in no stand-by counter unless it is counted here. Routed
            // through AIData because that is the only handle a death event
            // has on the BotOwner, and a null one is exactly the case we want
            // skipped - it means the victim is Sophia.
            IAIData ai = victim.AIData;
            BotOwner died = ai != null ? ai.BotOwner : null;
            if (died != null && died.StandBy != null)
            {
                StandByTransitions.Died(died.StandBy.StandByType_1 != BotStandByType.paused);
            }

            // Ends the awake span whatever state it died in, and - the part
            // that matters - drops the BotOwner so a bot killed while awake
            // does not sit in that dictionary holding its whole graph.
            AwakeAge.Ended(died);

            StringBuilder sb = new StringBuilder(384);
            sb.Append("{\"type\":\"death\"");
            CommonPlayer(sb, victim);

            sb.Append(",\"damageType\":\"").Append(Esc(damage.DamageType.ToString())).Append('"');
            sb.Append(",\"bodyPart\":\"").Append(Esc(part.ToString())).Append('"');

            // The blow's own account of who struck, beside the game's account
            // of who is blamed. Usually the same value, so the field is cheap
            // when they agree - but artillery is where they must not be merged:
            // the handler can set LastAggressor from damageInfo.Player and then
            // null it again, leaving `killer` null while `damageBy` still names
            // someone.
            //
            // Emitting both makes the disagreement visible instead of decided
            // by whichever field we happened to read. `killer` stays
            // authoritative because it is the game's own judgement about
            // attribution, and attribution is what "did Sophia's fight cause
            // this" turns on.
            string damageBy = "";
            try
            {
                if (damage.Player != null && damage.Player.iPlayer != null)
                {
                    damageBy = damage.Player.iPlayer.ProfileId ?? "";
                }
            }
            catch (Exception)
            {
            }

            sb.Append(",\"damageBy\":");
            if (damageBy.Length == 0)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('"').Append(Esc(damageBy)).Append('"');
            }

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

        /// <summary>
        /// One line per resolved grid-spawn bot, pairing the requested ring/distance/position with
        /// the bot that got it. `id` is the same ProfileId `botSpawn` and death lines use, so this
        /// joins to them - `pos` here IS the bot's spawn position (Common/CommonPlayer already log
        /// it), repeated here so the ring/distance label sits on the same line as a position, rather
        /// than making a reader join two lines just to know which distance a spawn position was
        /// meant to be.
        /// </summary>
        internal static void GridSpawnResolved(BotOwner bot, int ring, float distance, int index, Vector3 requestedPos)
        {
            if (bot == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"type\":\"gridSpawnResolved\"");
            Common(sb, bot);
            sb.Append(",\"ring\":").Append(ring);
            sb.Append(",\"ringDistance\":").Append(F(distance));
            sb.Append(",\"ringIndex\":").Append(index);
            sb.Append(",\"requestedPos\":[").Append(F(requestedPos.x)).Append(',').Append(F(requestedPos.y))
              .Append(',').Append(F(requestedPos.z)).Append(']');
            sb.Append('}');
            Emit(sb.ToString());
        }

        /// <summary>
        /// A ring position that failed the pre-spawn NavMesh check and was skipped rather than
        /// handed to AddPosition. No bot involved, so no `id`/`pos` from Common - this is a request
        /// that never became a spawn, and the point of logging it is that it must NOT silently
        /// shrink the ring without saying so in the same file the resolved lines live in.
        /// </summary>
        internal static void GridSpawnSkipped(int ring, float distance, int index, Vector3 candidatePos)
        {
            StringBuilder sb = new StringBuilder(192);
            sb.Append("{\"type\":\"gridSpawnSkipped\"");
            Clock(sb);
            sb.Append(",\"ring\":").Append(ring);
            sb.Append(",\"ringDistance\":").Append(F(distance));
            sb.Append(",\"ringIndex\":").Append(index);
            sb.Append(",\"candidatePos\":[").Append(F(candidatePos.x)).Append(',').Append(F(candidatePos.y))
              .Append(',').Append(F(candidatePos.z)).Append(']');
            sb.Append('}');
            Emit(sb.ToString());
        }

        /// <summary>
        /// Fires for EVERY bot reaching Active, not only grid-spawned ones - a Postfix on the bot's
        /// own activation method (BotOwner.method_10, see BotActivationCanaryPatch below) has no way
        /// to know which bots came from a grid spawn, and singling them out would need the same
        /// order-pairing DistanceGridSpawn already does once, not twice. Cheap regardless: one line,
        /// same shape as every other BotLog line.
        ///
        /// **The whole point of this line is the join, not the line itself.** `pos` here is where
        /// the bot ACTUALLY is once fully active - compare against the `pos` on this same `id`'s
        /// `botSpawn` line (position at creation, which for a grid spawn equals the requested
        /// position exactly). A mismatch is the game's own PreActive NavMesh fallback having moved
        /// the bot to a random zone spawn point after ~1s - confirmed 2026-08-08 that this happens
        /// silently, with no error anywhere else in the log.
        /// </summary>
        internal static void ActivationCanary(BotOwner bot)
        {
            if (bot == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder(192);
            sb.Append("{\"type\":\"botActive\"");
            Common(sb, bot);
            sb.Append('}');
            Emit(sb.ToString());
        }
    }

    /// <summary>
    /// The single spawn hook. Every bot from all three spawn systems -
    /// BossSpawnerClass, WavesSpawnScenario and the NonWavesSpawnScenario
    /// trickle - passes through BotOwner.Create, so this is one patch rather
    /// than three.
    ///
    /// **The `source` field is absent, and the reason is a count I got wrong
    /// once already - do not re-derive it from the estimate.**
    ///
    /// Create's signature carries no creation data, and the bot cannot reach it
    /// afterwards either: SpawnProfileData.SpawnParams has TriggerType and
    /// Id_spawn set only on the wave path, and role does not discriminate,
    /// because exUsec and pmcUSEC come through the BOSS spawner without being
    /// bosses.
    ///
    /// The design is a table keyed on BotCreationDataClass identity, stamped at
    /// each spawn system's entry and read at BotSpawner.method_11 where bot and
    /// creation data are both live. I estimated three stamps. **There are nine
    /// construction sites**: BossSpawnerClass alone builds three separate
    /// instances - boss at :75, escorts at :291, Zryachiy's supports at :323 -
    /// and BotSpawner builds six more (:303, :316, :402, :534, :772, :806).
    ///
    /// Nine sites with no way to prove the list is complete makes `unknown` the
    /// likely majority rather than a safety net, and a source field that is
    /// right for bosses and silently wrong for their escorts is worse than no
    /// field, because it looks authoritative. **Not built on purpose.** The
    /// promising route is the single funnel `BotCreationDataClass.Create`,
    /// which all nine reach; it needs each caller checked for an intervening
    /// await before a stamp handed to it could be trusted.
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

    /// <summary>
    /// BotOwner's OWN activation method - unrelated to BotSpawner.method_10 despite the identical
    /// obfuscated name; different class, different job. This is the method that sets
    /// BotState = EBotState.Active and, earlier in its body, calls BotStandBy.InitPoints (see
    /// BotStandByInitPointsPatch). Postfixing it means __instance.Position is read only once the
    /// bot is fully active - after the PreActive loop's NavMesh gate and its silent teleport
    /// fallback have already had their say, which is the whole point. See BotLog.ActivationCanary.
    /// </summary>
    internal class BotActivationCanaryPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), "method_10");
        }

        [PatchPostfix]
        private static void Postfix(BotOwner __instance)
        {
            BotLog.ActivationCanary(__instance);
        }
    }
}
