using System;
using System.Collections.Generic;
using System.Text;
using EFT;

namespace Ranger
{
    /// <summary>
    /// The publish-side surface a consumer mod calls to hand Ranger a fact, per
    /// docs/DESIGN.md section 2. Deliberately three methods, not a rich interface: the
    /// kit's value is the RECORDER (windows, spikes, NDJSON, protocol arms), not the
    /// vocabulary. Keys are strings owned by the producer; convention is
    /// `&lt;feature&gt;.&lt;fact&gt;` (e.g. "animCull.culledLastFrame"), documented not enforced.
    ///
    /// THIS SOLVES ONE DIRECTION OF THE BOUNDARY, NOT BOTH. It is for shipping features
    /// PUBLISHING facts the kit should record (Framesaver's SleepingBotAnimatorPatch,
    /// RoleSleepDistance, BossGroupWake are the three concrete callers named in
    /// docs/DESIGN.md section 1 - none are wired yet, that is the next step after this
    /// class exists).
    ///
    /// It does NOT solve the opposite direction: Framesaver's Telemetry.cs currently
    /// READS FROM PlayerLoopProfiler/GpuTelemetry (kit-side instruments) to build its own
    /// NDJSON output - dozens of call sites, found while trying to delete those files from
    /// Framesaver on 2026-08-16. That is a query surface, not a publish surface, and needs
    /// its own design - most likely resolved by moving the reading code (the rest of
    /// Telemetry.cs, eventually the whole sampler loop per Sophia's "Ranger owns the whole
    /// loop" ruling) rather than adding read methods here. See docs/EXTRACTION-PLAN.md
    /// "The real blocker" for the full account.
    ///
    /// Enabled is latched once at Awake, matching the pattern Framesaver's own config
    /// reads use (read once, cached, no locking needed because nothing hot-swaps a
    /// BepInEx plugin mid-session). A caller that checks Enabled before every Count/
    /// Event/Tag call pays one static bool read and nothing else when Ranger's own
    /// telemetry is switched off - the same "no-kit is the default case" requirement
    /// DESIGN.md section 1 states for the no-Ranger-installed case, extended to cover
    /// Ranger-installed-but-disabled too.
    /// </summary>
    public static class TelemetryBus
    {
        /// <summary>
        /// True once Ranger's Plugin has run Awake and its own telemetry config says to
        /// record. False for the whole window before Awake runs (BepInEx plugin load
        /// order is not guaranteed relative to Framesaver, so a caller MUST check this
        /// rather than assume Ranger has initialised) and false whenever Ranger is
        /// present but its own "Enabled" setting says not to record.
        /// </summary>
        public static bool Enabled { get; internal set; }

        // Per-window accumulators. Cleared by ResetWindow(), called by whichever side
        // owns window boundaries - today that is still Framesaver's Telemetry.cs, since
        // the sampler loop has not moved yet. This is intentionally the simplest
        // structure that could work: a dictionary per fact kind, keyed by the
        // producer's own string. No allocation avoidance work has been done yet because
        // nothing calls this in a hot path today - the three named callers
        // (SleepingBotAnimatorPatch, RoleSleepDistance, BossGroupWake) each fire at most
        // a few times per frame, not per-bot-per-frame.

        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private static readonly Dictionary<string, double> _events = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> _sums = new Dictionary<string, double>();
        private static readonly Dictionary<string, string> _tags = new Dictionary<string, string>();

        /// <summary>Accumulate a delta under `key`. Call once per occurrence; the bus sums.</summary>
        public static void Count(string key, int delta)
        {
            if (!Enabled) return;
            _counts.TryGetValue(key, out int current);
            _counts[key] = current + delta;
        }

        /// <summary>
        /// Accumulate a double delta under `key`. Call once per occurrence; the bus sums.
        ///
        /// NOT the same shape as Event, and the difference is load-bearing: Event is
        /// LAST WRITE WINS per window ("what is the current value of this fact"), while
        /// Sum accumulates ("what is the total of all occurrences"). A duration that
        /// occurs several times per window - a stand-by transition, a callback - read
        /// through Event would silently report only the last occurrence's length and
        /// LOOK like a total. First concrete caller: Framesaver's StandByTransitions
        /// seam (woken/slept counts beside their tick sums, `wokenMs / woken` = cost of
        /// one wake, which requires both halves to survive). Mirror of Count for the
        /// duration/int split: Count carries the occurrence, Sum carries the magnitude.
        /// </summary>
        public static void Sum(string key, double delta)
        {
            if (!Enabled) return;
            _sums.TryGetValue(key, out double current);
            _sums[key] = current + delta;
        }

        /// <summary>Record a duration (or any float measurement) under `key`. Last write wins per window.</summary>
        public static void Event(string key, float ms)
        {
            if (!Enabled) return;
            _events[key] = ms;
        }

        /// <summary>Record a label under `key`. Last write wins per window.</summary>
        public static void Tag(string key, string value)
        {
            if (!Enabled) return;
            _tags[key] = value;
        }

        /// <summary>
        /// Read-side accessors, for whatever builds the NDJSON output (today: Framesaver's
        /// Telemetry.cs, unmoved). Returns the accumulated value or the given default if the
        /// key was never touched this window - "never published" and "published as zero" are
        /// different facts and callers should not conflate them by defaulting silently to 0
        /// without checking TryGet first if that distinction matters to them.
        /// </summary>
        public static bool TryGetCount(string key, out int value) => _counts.TryGetValue(key, out value);
        public static bool TryGetEvent(string key, out double value) => _events.TryGetValue(key, out value);
        public static bool TryGetSum(string key, out double value) => _sums.TryGetValue(key, out value);
        public static bool TryGetTag(string key, out string value) => _tags.TryGetValue(key, out value);

        /// <summary>Clears all accumulated facts. Call at window close, before the next window's publishers fire.</summary>
        public static void ResetWindow()
        {
            _counts.Clear();
            _events.Clear();
            _sums.Clear();
            _tags.Clear();
        }

        // ---- Registered callbacks (2026-08-17, Sophia's design) --------------------------
        //
        // The Count/Event/Sum/Tag surface above solves ONE direction of the boundary:
        // a shipping feature pushing a simple fact outward. It cannot solve the other
        // direction Telemetry.cs's own capstone move exposed - the sampler core wanting
        // to build a whole NDJSON FRAGMENT out of a shipping class's internals (multiple
        // fields, some requiring the class's own formatting logic, some per-bot loops).
        // Widening Count/Event/Sum/Tag to cover every such field was one option (more
        // bus vocabulary, mechanical but unbounded); a reflection-based read bridge was
        // the other (works, but gives Ranger a soft dependency on Framesaver it never
        // had before, and needs its own JIT-safety proof same as RangerBridge did).
        //
        // Sophia's proposal is neither: a REGISTERED CALLBACK. The registering mod passes
        // a delegate whose BODY is compiled inside ITS OWN assembly, where its own types
        // (SleepingBotAnimatorPatch, RoleSleepDistance, ...) are ordinary in-assembly
        // references - never something Ranger's code has to resolve. Ranger holds and
        // invokes an opaque Action; type erasure through the delegate is the same
        // JIT-safety property RangerBridge's NoInlining isolation gets by a different
        // route, and it costs zero new vocabulary and zero reflection.
        //
        // Keyed by the registering mod's own GUID, per Sophia's "namespace by mod guid to
        // avoid collisions" instruction - one caller's fragment cannot silently overwrite
        // another's, and the NDJSON output nests each mod's block under its own key
        // (`"[guid]":{...}`) rather than flattening every registrant's fields into one
        // shared namespace the way Count/Event/Sum/Tag's string keys do.
        //
        // A callback that throws is ROLLED BACK, not allowed to cost the whole line -
        // same posture GpuTelemetry.Guarded already established for exactly this reason
        // (a half-written StringBuilder segment is invalid JSON and costs the WHOLE
        // window, which is a worse failure than one mod's fragment going missing).

        private static readonly Dictionary<string, Action<StringBuilder>> _headerCallbacks =
            new Dictionary<string, Action<StringBuilder>>();
        private static readonly Dictionary<string, Action<StringBuilder>> _windowCallbacks =
            new Dictionary<string, Action<StringBuilder>>();
        private static readonly Dictionary<string, Action<StringBuilder>> _markCallbacks =
            new Dictionary<string, Action<StringBuilder>>();
        private static readonly Dictionary<string, Action> _raidStartCallbacks =
            new Dictionary<string, Action>();
        private static readonly Dictionary<string, Action> _raidEndCallbacks =
            new Dictionary<string, Action>();

        // Per-bot predicates are a fifth, narrower shape than the four Action-based
        // registrations above - Tau's catch (room, 2026-08-17 ~18:33Z): CountBots() calls
        // BotStandByUpdatePatch.RoleStandByKnown(bot)/.RoleAllowsStandBy(bot) INSIDE a loop
        // over every bot on the roster, not once per window, so it cannot be a single
        // fragment-append call the way every other capstone-coupled read is. Collapsed to
        // ONE delegate (not two) because the two methods are always called together at
        // this site and the nullable bool already carries exactly their combined meaning:
        // null = RoleStandByKnown was false ("cannot tell"), true/false = known and the
        // answer. BotOwner is a shared EFT type both assemblies reference directly (no
        // bridging needed for the parameter itself - only the delegate BODY, which reaches
        // into Framesaver.Patches.BotStandByUpdatePatch, needs to compile where that class
        // is visible, which is the same JIT-safety property every other registration here
        // relies on). Only ONE registrant is meaningful for this - there is exactly one
        // stand-by system - so this is a single slot, not a per-guid dictionary; a second
        // registration REPLACES the first, logged, so a silent double-registration cannot
        // happen unnoticed.
        private static Func<BotOwner, bool?> _botStandByPredicate;
        private static string _botStandByPredicateOwner;

        /// <summary>
        /// Registers a callback invoked once, at Ranger's own header write (plugin load,
        /// before any raid). Same replace-on-re-register and per-guid nesting as the
        /// window/mark callbacks. For static, once-per-session facts (device identity,
        /// build config) - a registrant with nothing header-shaped simply never calls this.
        /// </summary>
        public static void RegisterHeaderCallback(string modGuid, Action<StringBuilder> callback)
        {
            if (string.IsNullOrEmpty(modGuid) || callback == null) return;
            _headerCallbacks[modGuid] = callback;
        }

        /// <summary>
        /// Registers a callback invoked once per telemetry window, its result nested
        /// under `"&lt;modGuid&gt;":{...}` in that window's NDJSON line. Re-registering the
        /// same `modGuid` REPLACES the prior callback rather than adding a second one -
        /// a mod's plugin can only be loaded once per session, so a second Awake
        /// registering again is a reload, not two producers.
        /// </summary>
        public static void RegisterWindowCallback(string modGuid, Action<StringBuilder> callback)
        {
            if (string.IsNullOrEmpty(modGuid) || callback == null) return;
            _windowCallbacks[modGuid] = callback;
        }

        /// <summary>Registers a callback invoked once per mark event (Plugin.MarkKey press). Same replace-on-re-register and per-guid nesting as <see cref="RegisterWindowCallback"/>.</summary>
        public static void RegisterMarkCallback(string modGuid, Action<StringBuilder> callback)
        {
            if (string.IsNullOrEmpty(modGuid) || callback == null) return;
            _markCallbacks[modGuid] = callback;
        }

        /// <summary>Registers a callback invoked once when a raid starts (the same edge Telemetry.cs's own per-raid reset block fires on). No StringBuilder - this is a lifecycle hook, not a line contributor; a registrant wanting a raid-start NDJSON fact should write its own line inside the callback using its own writer, or wait for the next window/mark to carry it.</summary>
        public static void RegisterRaidStartCallback(string modGuid, Action callback)
        {
            if (string.IsNullOrEmpty(modGuid) || callback == null) return;
            _raidStartCallbacks[modGuid] = callback;
        }

        /// <summary>Registers a callback invoked once when a raid ends (mirrors <see cref="RegisterRaidStartCallback"/>).</summary>
        public static void RegisterRaidEndCallback(string modGuid, Action callback)
        {
            if (string.IsNullOrEmpty(modGuid) || callback == null) return;
            _raidEndCallbacks[modGuid] = callback;
        }

        /// <summary>
        /// Registers the single stand-by-role predicate a bot-counting loop calls per bot.
        /// See the field's own comment for why this is one delegate, one slot, not the
        /// per-guid dictionary shape every other registration here uses - there is
        /// exactly one stand-by system to ask. A second registration REPLACES the first
        /// and logs a warning naming both guids, since a silent replace here (unlike the
        /// four Action-based registrations, where replace-on-reload is the expected
        /// common case) most likely means two real producers collided.
        /// </summary>
        public static void RegisterBotStandByPredicate(string modGuid, Func<BotOwner, bool?> predicate)
        {
            if (string.IsNullOrEmpty(modGuid) || predicate == null) return;
            if (_botStandByPredicate != null && _botStandByPredicateOwner != modGuid)
            {
                Plugin.LogSource.LogWarning("Ranger: bot stand-by predicate re-registered by '"
                    + modGuid + "', replacing '" + _botStandByPredicateOwner
                    + "' - if both are real producers, only the latest wins.");
            }
            _botStandByPredicate = predicate;
            _botStandByPredicateOwner = modGuid;
        }

        /// <summary>
        /// Asks the registered stand-by predicate about one bot. Returns false with
        /// `known` false when nothing is registered (Ranger present but no consumer mod
        /// has wired the predicate yet, or none ever will) or when the predicate itself
        /// returns null ("cannot tell", per RoleStandByKnown's own semantics) - the two
        /// "cannot answer" cases collapse to the same caller-visible shape deliberately,
        /// since a caller counting exempt bots must skip both identically.
        /// </summary>
        internal static bool TryAskBotStandBy(BotOwner bot, out bool known, out bool allowed)
        {
            known = false;
            allowed = false;
            if (_botStandByPredicate == null) return false;

            try
            {
                bool? result = _botStandByPredicate(bot);
                if (result == null) return true;
                known = true;
                allowed = result.Value;
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogWarning("Ranger: bot stand-by predicate threw for one bot - " + e);
                return true;
            }
        }

        /// <summary>
        /// Invokes every registered window callback, each appended as its own
        /// `,"&lt;modGuid&gt;":{...}` fragment. Called by the sampler core once per window,
        /// after its own fields are written. A throwing callback is rolled back to where
        /// `sb` stood before it ran (mirrors GpuTelemetry.Guarded) and its guid is skipped
        /// for the rest of THIS window only - it is not de-registered, since a single bad
        /// window must not silence a mod for the whole session.
        /// </summary>
        internal static void InvokeHeaderCallbacks(StringBuilder sb)
        {
            InvokeAll(_headerCallbacks, sb);
        }

        internal static void InvokeWindowCallbacks(StringBuilder sb)
        {
            InvokeAll(_windowCallbacks, sb);
        }

        /// <summary>Invokes every registered mark callback. Same rollback posture as <see cref="InvokeWindowCallbacks"/>.</summary>
        internal static void InvokeMarkCallbacks(StringBuilder sb)
        {
            InvokeAll(_markCallbacks, sb);
        }

        /// <summary>Invokes every registered raid-start callback. A throwing callback is logged and skipped; it does not roll back anything since there is no StringBuilder to roll back.</summary>
        internal static void InvokeRaidStartCallbacks()
        {
            InvokeAll(_raidStartCallbacks);
        }

        /// <summary>Invokes every registered raid-end callback. Same posture as <see cref="InvokeRaidStartCallbacks"/>.</summary>
        internal static void InvokeRaidEndCallbacks()
        {
            InvokeAll(_raidEndCallbacks);
        }

        private static void InvokeAll(Dictionary<string, Action<StringBuilder>> callbacks, StringBuilder sb)
        {
            foreach (KeyValuePair<string, Action<StringBuilder>> entry in callbacks)
            {
                int mark = sb.Length;
                try
                {
                    sb.Append(",\"").Append(Escape(entry.Key)).Append("\":{");
                    int fieldsStart = sb.Length;
                    entry.Value(sb);
                    // A callback that appends nothing still gets a valid (empty) object
                    // rather than a dangling "," from a comma-first field convention -
                    // callers write fields as `,"key":value` (matching every AppendX
                    // method in this codebase), so a leading comma is stripped if the
                    // callback wrote at least one field.
                    if (sb.Length > fieldsStart && sb[fieldsStart] == ',')
                    {
                        sb.Remove(fieldsStart, 1);
                    }
                    sb.Append('}');
                }
                catch (Exception e)
                {
                    sb.Length = mark;
                    Plugin.LogSource.LogWarning("Ranger: telemetry callback for '" + entry.Key
                        + "' threw and was skipped for this window - " + e);
                }
            }
        }

        private static void InvokeAll(Dictionary<string, Action> callbacks)
        {
            foreach (KeyValuePair<string, Action> entry in callbacks)
            {
                try
                {
                    entry.Value();
                }
                catch (Exception e)
                {
                    Plugin.LogSource.LogWarning("Ranger: telemetry lifecycle callback for '" + entry.Key
                        + "' threw - " + e);
                }
            }
        }

        private static string Escape(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
