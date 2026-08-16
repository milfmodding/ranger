using System.Collections.Generic;

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
        private static readonly Dictionary<string, string> _tags = new Dictionary<string, string>();

        /// <summary>Accumulate a delta under `key`. Call once per occurrence; the bus sums.</summary>
        public static void Count(string key, int delta)
        {
            if (!Enabled) return;
            _counts.TryGetValue(key, out int current);
            _counts[key] = current + delta;
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
        public static bool TryGetTag(string key, out string value) => _tags.TryGetValue(key, out value);

        /// <summary>Clears all accumulated facts. Call at window close, before the next window's publishers fire.</summary>
        public static void ResetWindow()
        {
            _counts.Clear();
            _events.Clear();
            _tags.Clear();
        }
    }
}
