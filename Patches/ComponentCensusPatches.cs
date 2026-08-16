using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Framesaver.Patches
{
    /// <summary>
    /// Enumerates every component on a bot, alive and dead, so "does a corpse keep doing per-frame work"
    /// is answered once rather than one suspect at a time.
    ///
    /// Four one-shot samples per raid, all but one on a single GameObject:
    ///
    ///   alive        prefix on Player.OnDead   - the subject in life
    ///   dead0        postfix on Player.OnDead  - after the synchronous teardown
    ///   dead10       same object, ~10s later   - after method_98's coroutine and the dspTime release
    ///   aliveControl another live bot at dead0 - a check on `alive`, not a diff baseline
    ///
    /// Design notes that are load-bearing, all from spec review rather than hindsight:
    ///
    /// - Enumerate Component, not Behaviour, not MonoBehaviour. Renderer, Collider, Rigidbody and Cloth
    ///   all derive from Component directly, so a Behaviour census cannot see the ragdoll at all - and
    ///   would return a clean result while blind to it.
    /// - `enabled` is read by reflection rather than a list of known types. A curated list can only omit;
    ///   GetProperty returns null for a type that genuinely has none, which is exactly the distinction
    ///   between "no such property" and "switched off". Those must not collapse - Rigidbody reading
    ///   `false` would read as the ragdoll being disabled.
    /// - The samples are prefix/postfix rather than the OnPlayerDead event. The event fires at
    ///   Player.cs:7411; the animators are not disabled until 7454 and the corpse does not exist until
    ///   7521. An event-triggered sample would have reported Animator.enabled = true on a dead bot and
    ///   read as "corpses keep their animators" - inverting the finding this exists to test.
    /// </summary>
    internal static class Census
    {
        /// <summary>
        /// Components per line. 512 originally; 1024 when the enumeration widened to Component, which
        /// newly admits every hit collider and ragdoll body.
        ///
        /// 1024 was not enough either - `aliveControl` hit it in the first run that had two roots,
        /// dropping 272 of 1,296. That line is the check on whether `alive` was contaminated, and a
        /// truncated check compares a different set of components to the one it is checking. **The cap
        /// silently converted the control into a second, incomparable sample.**
        ///
        /// 4096 is a blowup guard, not a working limit: the largest census yet is 1,296, and if
        /// `dropped` is ever non-zero again that is a finding about the root, not a reason to raise
        /// this again.
        /// </summary>
        private const int MaxComponents = 4096;

        private const double Dead10DelayMs = 10000d;

        private static readonly Queue<string> Lines = new Queue<string>();

        /// <summary>Cached per type - GetProperty is not free and a player hierarchy repeats types heavily.</summary>
        private static readonly Dictionary<Type, PropertyInfo> EnabledProps =
            new Dictionary<Type, PropertyInfo>();

        private static bool _deathDone;
        private static Player _subject;
        private static long _deathAt;

        /// <summary>Clears every latch. Called at raid start, so a raid with no death shows as an
        /// absence rather than inheriting the previous raid's samples.</summary>
        internal static void ResetForRaid()
        {
            _deathDone = false;
            _subject = null;
            _deathAt = 0L;

            lock (Lines)
            {
                Lines.Clear();
            }
        }

        /// <summary>
        /// Drives the dead10 deadline. Called once per frame from Telemetry rather than from a coroutine:
        /// a coroutine lives on the subject and dies with it, which is precisely the case the
        /// "subject destroyed" error exists to report.
        /// </summary>
        internal static void Tick()
        {
            if (_deathAt == 0L)
            {
                return;
            }

            double elapsed = AiTiming.ToMs(Stopwatch.GetTimestamp() - _deathAt);
            if (elapsed < Dead10DelayMs)
            {
                return;
            }

            long at = _deathAt;
            _deathAt = 0L;

            // Unity's overloaded == is what makes a destroyed object detectable here; a plain null check
            // would miss it. Its destruction is itself the finding, so it is reported rather than skipped.
            if (_subject == null)
            {
                Enqueue("\"sample\":\"dead10\",\"error\":\"subject destroyed\"");
                _subject = null;
                return;
            }

            Capture("dead10", _subject, AiTiming.ToMs(Stopwatch.GetTimestamp() - at));
            _subject = null;
        }

        /// <summary>Called from the OnDead prefix - the subject in life, on its own GameObject.</summary>
        internal static void OnDeathPre(Player player)
        {
            if (_deathDone || player == null || !player.IsAI)
            {
                return;
            }

            Capture("alive", player, 0d);
        }

        /// <summary>
        /// Called from the OnDead postfix. Captures the subject after teardown, one live bot as a check
        /// that the prefix sample was uncontaminated, and arms the dead10 deadline.
        /// </summary>
        internal static void OnDeathPost(Player player)
        {
            if (_deathDone || player == null || !player.IsAI)
            {
                return;
            }

            _deathDone = true;

            Capture("dead0", player, 0d);
            CaptureControl(player);

            _subject = player;
            _deathAt = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// A live bot at the dead0 instant. Exists because a prefix guarantees nothing in OnDead's *body*
        /// has run, but not that nothing in the death sequence has: BotOwner.method_6 is registered on the
        /// same HealthController.DiedEvent and calls BotOwner.Dispose. Ours probably runs first, from
        /// reasoning about registration order - and "probably" is what this sample replaces with a
        /// measurement. If alive and aliveControl agree, the prefix sample is uncontaminated.
        /// </summary>
        private static void CaptureControl(Player subject)
        {
            try
            {
                if (!Singleton<IBotGame>.Instantiated)
                {
                    Enqueue("\"sample\":\"aliveControl\",\"error\":\"no bot game\"");
                    return;
                }

                BotsController controller = Singleton<IBotGame>.Instance.BotsController;
                IEnumerable<BotOwner> bots = controller != null && controller.Bots != null
                    ? controller.Bots.BotOwners
                    : null;

                if (bots != null)
                {
                    foreach (BotOwner bot in bots)
                    {
                        if (bot == null)
                        {
                            continue;
                        }

                        Player other = bot.GetPlayer;
                        if (other == null || other == subject
                            || other.HealthController == null || !other.HealthController.IsAlive)
                        {
                            continue;
                        }

                        Capture("aliveControl", other, 0d);
                        return;
                    }
                }

                Enqueue("\"sample\":\"aliveControl\",\"error\":\"no live control bot\"");
            }
            catch (Exception e)
            {
                Enqueue("\"sample\":\"aliveControl\",\"error\":\"" + Escape(e.GetType().Name) + "\"");
            }
        }

        /// <summary>
        /// The enumeration itself. GetComponentsInChildren, never GetComponents: the weapon - and so
        /// WeaponSoundPlayer, whose T+10 behaviour is the most interesting thing on the line - lives on
        /// _controllerObject, parented under PlayerBones.WeaponRoot. A non-recursive call returns a
        /// shorter list that looks entirely plausible.
        /// </summary>
        private static void Capture(string sample, Player player, double msSinceDeath)
        {
            StringBuilder sb = new StringBuilder(8192);

            try
            {
                // Two roots, because one is not enough and that assumption failed silently once.
                //
                // The weapon is NOT under the player. Player.ItemHandsController.smethod_4 positions
                // _controllerObject to the ribcage and never reparents it - the only SetParent calls are
                // inside `if (player.UsedSimplifiedSkeleton)`, zombies with knives or pistols. And the
                // hands controller itself is AddComponent'd onto player.gameObject (smethod_1:31787), so
                // rooting there would have re-enumerated the subtree we already had and still missed the
                // weapon, while growing the census enough to look like a fix.
                //
                // ControllerGameObject (Player.cs:31696) returns the weapon object itself rather than a
                // component on it, so discovery does not presuppose what we are looking for. BSG uses the
                // same object as a recursion root for exactly this purpose (Player.cs:28879).
                List<RootInfo> roots = new List<RootInfo>(2);
                List<Component> keep = new List<Component>(512);

                CollectRoot(player.gameObject, "Player", keep, roots);

                GameObject weapon = null;
                try
                {
                    Player.AbstractHandsController hands = player.HandsController;
                    weapon = hands != null ? hands.ControllerGameObject : null;
                }
                catch (Exception)
                {
                    weapon = null;
                }

                CollectRoot(weapon, "ControllerGameObject", keep, roots);

                // Sort BEFORE truncating. Unity's enumeration order is not guaranteed stable, so
                // truncating first would keep a different arbitrary 1024 each run - the cap would make
                // two censuses of the same object incomparable, silently.
                keep.Sort(CompareByGoThenName);

                int take = keep.Count > MaxComponents ? MaxComponents : keep.Count;
                int dropped = keep.Count - take;

                List<string> rows = new List<string>(take);
                for (int i = 0; i < take; i++)
                {
                    rows.Add(Row(keep[i]));
                }

                sb.Append("\"sample\":\"").Append(sample).Append('"');
                AppendSubject(sb, player, msSinceDeath);

                // Every root ATTEMPTED, including ones that resolved to null. A roots array listing only
                // successes is a field whose absence carries meaning - the failure this exists to stop.
                // A "path":null entry says "we looked for a weapon and there wasn't one", which is a
                // finding; omitting the entry says nothing and looks fine.
                //
                // `path` exists because of a number nobody can read without it: the first two-root run
                // had ControllerGameObject return 155 components on the subject and **1,053 on the
                // control bot**, against 243 for that bot's whole player subtree. A weapon larger than
                // the player carrying it is either a genuinely big prefab or a root that is not on the
                // bot at all - and `_controllerObject` comes from AssetPoolObject, so "not on the bot"
                // is a live possibility rather than a paranoid one. The path names which it is.
                sb.Append(",\"roots\":[");
                for (int i = 0; i < roots.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append("{\"label\":");
                    AppendStr(sb, roots[i].Label);
                    sb.Append(",\"path\":");
                    AppendStr(sb, roots[i].Path);
                    sb.Append(",\"n\":").Append(roots[i].Count).Append('}');
                }

                sb.Append(']');

                sb.Append(",\"fields\":[\"name\",\"go\",\"enabled\",\"activeInHierarchy\",\"cullingMode\"]");
                sb.Append(",\"n\":").Append(rows.Count);
                sb.Append(",\"truncated\":").Append(dropped > 0 ? "true" : "false");
                sb.Append(",\"dropped\":").Append(dropped);
                sb.Append(",\"components\":[");

                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(rows[i]);
                }

                sb.Append(']');
            }
            catch (Exception e)
            {
                Enqueue("\"sample\":\"" + sample + "\",\"error\":\"" + Escape(e.GetType().Name) + "\"");
                return;
            }

            Enqueue(sb.ToString());
        }

        /// <summary>
        /// Sorted as a string so ordering is by (name, go) textually - enumeration order is not stable
        /// and an unsorted list produces spurious diffs. Rows stay duplicated where types repeat, since
        /// the comparison is a multiset.
        /// </summary>
        /// <summary>One enumeration root: what was asked for, where it turned out to live, and how many
        /// components came from it. Recorded even when the root resolved to null.</summary>
        private struct RootInfo
        {
            public string Label;
            public string Path;
            public int Count;
        }

        /// <summary>
        /// Enumerates one root into the shared list and records what was attempted.
        ///
        /// A null root is recorded as an attempt with a null path and a zero count, never skipped -
        /// "we looked and found nothing" and "we did not look" must stay distinguishable.
        /// </summary>
        private static void CollectRoot(GameObject root, string label, List<Component> into,
                                        List<RootInfo> roots)
        {
            RootInfo info = new RootInfo();
            info.Label = label;

            if (root == null)
            {
                roots.Add(info);
                return;
            }

            // includeInactive, or "absent" and "disabled" become indistinguishable.
            Component[] all = root.GetComponentsInChildren<Component>(true);
            int added = 0;

            foreach (Component c in all)
            {
                // Transform is one per GameObject and carries no state we can read; including it would
                // dominate the count. The type test catches RectTransform too, correctly.
                if (c != null && !(c is Transform))
                {
                    into.Add(c);
                    added++;
                }
            }

            info.Path = PathOf(root.transform);
            info.Count = added;
            roots.Add(info);
        }

        /// <summary>Hierarchy path from the scene root, which is the only field that separates "this
        /// bot's weapon prefab is genuinely large" from "this root is not on this bot".</summary>
        private static string PathOf(Transform t)
        {
            StringBuilder sb = new StringBuilder(128);
            sb.Append(t.name);

            for (Transform p = t.parent; p != null; p = p.parent)
            {
                sb.Insert(0, '/').Insert(0, p.name);
            }

            return sb.ToString();
        }

        /// <summary>A JSON string or a bare null - the distinction the roots array is built on.</summary>
        private static void AppendStr(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"').Append(Escape(s)).Append('"');
        }

        /// <summary>Owning GameObject, then type name - the order the spec fixes so two censuses of the
        /// same object diff cleanly. Ordinal throughout; culture-sensitive compare would reorder rows
        /// between machines.</summary>
        private static int CompareByGoThenName(Component a, Component b)
        {
            int byGo = string.CompareOrdinal(a.gameObject.name, b.gameObject.name);
            return byGo != 0 ? byGo : string.CompareOrdinal(a.GetType().Name, b.GetType().Name);
        }

        private static string Row(Component c)
        {
            Type t = c.GetType();

            string enabled = "null";
            PropertyInfo prop;

            if (!EnabledProps.TryGetValue(t, out prop))
            {
                // Reflection rather than a list of known types. A list can only omit - and omission here
                // returns a clean, shorter result, which is the failure mode this instrument keeps
                // producing. A null PropertyInfo *is* "this type has no enabled".
                prop = t.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && (prop.PropertyType != typeof(bool) || !prop.CanRead))
                {
                    prop = null;
                }

                EnabledProps[t] = prop;
            }

            if (prop != null)
            {
                try
                {
                    enabled = (bool)prop.GetValue(c, null) ? "true" : "false";
                }
                catch (Exception)
                {
                    enabled = "null";
                }
            }

            string culling = "null";
            Animator anim = c as Animator;
            if (anim != null)
            {
                culling = "\"" + anim.cullingMode + "\"";
            }

            return "[\"" + Escape(t.Name) + "\",\"" + Escape(c.gameObject.name) + "\","
                   + enabled + "," + (c.gameObject.activeInHierarchy ? "true" : "false") + ","
                   + culling + "]";
        }

        private static void AppendSubject(StringBuilder sb, Player player, double msSinceDeath)
        {
            sb.Append(",\"subject\":{\"objId\":").Append(player.gameObject.GetInstanceID());

            bool alive = player.HealthController != null && player.HealthController.IsAlive;
            sb.Append(",\"alive\":").Append(alive ? "true" : "false");

            // null, not "" - "the object was gone" and "it held this value" must not collapse, which is
            // the same distinction as Rigidbody having no `enabled` at all. On a corpse BotOwner.Dispose
            // has already run, so a null BotStandBy is an expected reading rather than a defect, and a
            // stale-looking StandByType_1 on a dead0/dead10 line is likewise expected.
            string role = null;
            string standBy = null;

            try
            {
                // Read stand-by from the live object, never from SleepingBotAnimatorPatch.Sleeping -
                // that dictionary is our belief about the bot, not the bot's state.
                BotOwner owner = player.AIData != null ? player.AIData.BotOwner : null;
                if (owner != null)
                {
                    if (owner.Profile != null && owner.Profile.Info != null)
                    {
                        role = owner.Profile.Info.Settings.Role.ToString();
                    }

                    if (owner.StandBy != null)
                    {
                        standBy = owner.StandBy.standByType.ToString();
                    }
                }
            }
            catch (Exception)
            {
                // Role and stand-by are context, not the measurement.
            }

            AppendNullableString(sb, "role", role);
            AppendNullableString(sb, "standBy", standBy);
            sb.Append(",\"msSinceDeath\":").Append(
                msSinceDeath.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        /// <summary>Emits a JSON null for an absent value rather than an empty string, so "unreadable"
        /// and "read as empty" stay distinguishable.</summary>
        private static void AppendNullableString(StringBuilder sb, string key, string value)
        {
            sb.Append(",\"").Append(key).Append("\":");
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"').Append(Escape(value)).Append('"');
        }

        private static void Enqueue(string body)
        {
            lock (Lines)
            {
                Lines.Enqueue(body);
            }
        }

        /// <summary>Drained by Telemetry, which owns the writer and stamps the common context fields.</summary>
        internal static bool TryTakeLine(out string body)
        {
            lock (Lines)
            {
                if (Lines.Count > 0)
                {
                    body = Lines.Dequeue();
                    return true;
                }
            }

            body = null;
            return false;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            if (s.IndexOf('"') < 0 && s.IndexOf('\\') < 0)
            {
                return s;
            }

            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>
    /// Prefix and postfix on Player.OnDead. Filtered on IsAI; LocalPlayer.OnDead reaches this via
    /// base.OnDead, so the AI path is covered.
    ///
    /// A postfix rather than the OnPlayerDead event because every death event fires before the teardown -
    /// see the note on Census. Player.OnDead(EDamageType) references no obfuscated type, so the usual
    /// argument for preferring an event over a patch does not apply here.
    /// </summary>
    internal class PlayerOnDeadCensusPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.OnDead));
        }

        [PatchPrefix]
        private static void Prefix(Player __instance)
        {
            Census.OnDeathPre(__instance);
        }

        [PatchPostfix]
        private static void Postfix(Player __instance)
        {
            Census.OnDeathPost(__instance);
        }
    }
}
