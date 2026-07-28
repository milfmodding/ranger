using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Framesaver
{
    /// <summary>
    /// Brackets every top-level Unity player-loop phase with timing delegates.
    ///
    /// The measurers the game exposes only cover Update, FixedUpdate and render, which between them account
    /// for about a third of gameUpdate on Streets. The recurring ~350ms hitch lands in the remainder, and
    /// naming suspects one at a time has not worked. This bisects the whole frame instead: whichever phase
    /// carries the hitch is the phase to investigate.
    ///
    /// Injection is additive - a begin marker prepended and an end marker appended to each phase's subsystem
    /// list - so the game's own systems run untouched between them.
    /// </summary>
    public static class PlayerLoopProfiler
    {
        private static readonly object Gate = new object();

        private static string[] _names = new string[0];
        private static long[] _starts = new long[0];
        private static double[] _totals = new double[0];
        private static double[] _snapshot = new double[0];

        // Collections that *completed* inside each phase. A frame-level gen0 delta says a collection happened
        // somewhere in the frame; it cannot say where, and "where" is the whole question for the TimeUpdate
        // family. GC.CollectionCount is a counter read, so this is two extra reads per bracketed phase.
        private static int[] _gcStarts = new int[0];
        private static int[] _gcTotals = new int[0];
        private static int[] _gcSnapshot = new int[0];

        public static bool Installed { get; private set; }

        // ---- Inter-frame gap -----------------------------------------------------------------------
        //
        // The eight top-level phases tile PlayerLoop(), and the residual is everything outside it. Delta
        // localised the 165-402 ms family there: on all twelve non-GC instances every phase reads
        // ordinary, including PostLateUpdate at 3.8-15.3 ms, so the time is not inside any of them.
        //
        // SPT already brackets the interval for us. CustomPlayerLoopSystemsInjector inserts EndOfFrame as
        // the LAST subsystem of PostLateUpdate and StartOfFrame as the FIRST of EarlyUpdate, both as
        // public static Action events on non-obfuscated types. So EndOfFrame -> StartOfFrame spans
        // native-gap + TimeUpdate + Initialization of the following frame, and we already measure those
        // two - subtraction gives the native gap.
        //
        // Deliberately emitted raw rather than pre-subtracted: this reports what was read, and the
        // subtraction is analysis. Two event subscriptions, no Harmony patch, no obfuscated types.
        private static long _endOfFrameAt;
        private static double _endToStartMs;

        // Pairing guard. The bracket assumes EndOfFrame and StartOfFrame fire exactly once each, in that
        // order, every frame. If the injector is re-run, a frame is skipped, or the player loop is
        // rewritten mid-session, the pairing drifts and the span silently covers more than one frame -
        // producing a large reading indistinguishable from the 165-402 ms family this exists to find.
        //
        // An instrument whose failure mode manufactures its own target cannot be allowed to report a
        // number it is unsure of. Counting EndOfFrame calls between consecutive StartOfFrame calls makes
        // the assumption checkable per frame: exactly one is valid, anything else emits null.
        private static int _endCount;
        private static bool _gapValid;

        // Per-window fire counts. The guard makes an unsure frame silent, which is right - but silence
        // and success look identical in the output, so drift stays invisible. These must be equal to
        // within 1 (the window boundary); a divergence means every endToStart in that window is suspect,
        // readable off the data rather than inferred from whether the injector could have re-run.
        private static int _endFires;
        private static int _startFires;

        public static int EndOfFrameFires
        {
            get { return _endFires; }
        }

        public static int StartOfFrameFires
        {
            get { return _startFires; }
        }

        public static void ResetFrameGapCounters()
        {
            _endFires = 0;
            _startFires = 0;
        }

        /// <summary>
        /// Wall time from the last subsystem of PostLateUpdate to the first of EarlyUpdate.
        ///
        /// **The raw value is never the answer.** It CONTAINS TimeUpdate and Initialization, so it reads
        /// 74-128 ms on the TimeUpdate-dominant collection frames as well as on a native block, and large
        /// for anything else that stalls in that interval. Only `endToStart - TimeUpdate - Initialization`
        /// distinguishes them, and both subtrahends are on the same line.
        ///
        /// Telemetry emits null - not this value - when the subscription failed or the frame's pairing
        /// was not 1:1, so a caller reading zero here is reading a genuine zero.
        /// </summary>
        public static double EndToStartMs
        {
            get { return _endToStartMs; }
        }

        /// <summary>False when the last frame's EndOfFrame/StartOfFrame pairing was not exactly 1:1, so
        /// the span cannot be trusted to be one frame boundary. Emit null, never the number.</summary>
        public static bool GapValid
        {
            get { return _gapValid; }
        }

        /// <summary>True when both events were subscribed. Reported so a null reading is distinguishable
        /// from a zero one - the failure this project keeps having to make visible.</summary>
        public static bool FrameGapArmed { get; private set; }

        /// <summary>
        /// Subscribes the inter-frame bracket.
        ///
        /// Separate from Install() and separately guarded: these are event subscriptions rather than
        /// Harmony registrations, so Plugin.TryEnable does not cover them and a resolution failure would
        /// propagate out of Awake and drop everything after it - the same cascade TryEnable exists to
        /// stop. Called once; there is no unsubscribe because the plugin lives for the process.
        /// </summary>
        public static void ArmFrameGap()
        {
            try
            {
                CustomPlayerLoopSystem.EndOfFrame.OnUpdate += OnEndOfFrame;
                CustomPlayerLoopSystem.StartOfFrame.OnUpdate += OnStartOfFrame;
                FrameGapArmed = true;
            }
            catch (Exception e)
            {
                FrameGapArmed = false;
                Plugin.LogSource.LogWarning("Framesaver: inter-frame gap not armed - " + e.Message
                                            + ". endToStart will read null.");
            }
        }

        private static void OnEndOfFrame()
        {
            _endOfFrameAt = Stopwatch.GetTimestamp();
            _endCount++;
            _endFires++;
        }

        private static void OnStartOfFrame()
        {
            // Exactly one EndOfFrame since the previous StartOfFrame is the only case where the span is
            // one frame boundary. Zero means EndOfFrame did not fire; more than one means the span covers
            // several frames and would read as a stall that never happened.
            _startFires++;
            _gapValid = _endCount == 1;
            _endCount = 0;

            if (!_gapValid)
            {
                return;
            }

            _endToStartMs = (Stopwatch.GetTimestamp() - _endOfFrameAt) * 1000d / Stopwatch.Frequency;
        }

        public static string[] PhaseNames
        {
            get { return _names; }
        }

        /// <summary>
        /// The phases actually expanded, resolved at Install().
        ///
        /// On the header because the setting has never been recorded anywhere, and under a blocklist that
        /// becomes unrecoverable: a *blocked* phase and a phase whose children all fall under the 0.5 ms
        /// drop threshold emit byte-identical output. Under the old allowlist the setting could be
        /// inferred from which children appeared; a blocklist deletes that positive trace.
        ///
        /// The resolved list rather than the raw setting, for the `animCulled` reason - report the
        /// effect, not the intent. It also survives a mistyped entry, which the raw string would not.
        /// </summary>
        private static string[] _expandedPhases = new string[0];

        public static string[] ExpandedPhases
        {
            get { return _expandedPhases; }
        }

        /// <summary>Per-phase milliseconds for the frame just completed. Index matches PhaseNames.</summary>
        public static double[] Snapshot
        {
            get { return _snapshot; }
        }

        /// <summary>
        /// Gen-0 collections that completed inside each phase during the frame just completed. Index matches
        /// PhaseNames. This is the instrument that turns "a collection happened on this frame and the frame
        /// was slow" into "the collection ran inside this phase".
        /// </summary>
        public static int[] GcSnapshot
        {
            get { return _gcSnapshot; }
        }

        /// <summary>
        /// Name of the top-level phase a collection completed in this frame, or empty. Children are ignored:
        /// a collection inside an expanded child also counts against its parent, and reporting both would
        /// read as two collections.
        /// </summary>
        public static string GcPhase()
        {
            for (int i = 0; i < _gcSnapshot.Length && i < _names.Length; i++)
            {
                if (_gcSnapshot[i] > 0 && _names[i].IndexOf('/') < 0)
                {
                    return _names[i];
                }
            }

            return "";
        }

        public static void Install()
        {
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null || root.subSystemList.Length == 0)
                {
                    return;
                }

                int count = root.subSystemList.Length;

                System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();
                System.Collections.Generic.List<string> topLevel = new System.Collections.Generic.List<string>();
                System.Collections.Generic.List<string> expandedNames = new System.Collections.Generic.List<string>();

                // First pass: name every slot we intend to time, so indices are stable before we build the
                // delegates that close over them.
                for (int i = 0; i < count; i++)
                {
                    PlayerLoopSystem phase = root.subSystemList[i];
                    string phaseName = phase.type != null ? phase.type.Name : ("phase" + i);
                    names.Add(phaseName);
                    topLevel.Add(phaseName);

                    if (ShouldExpand(phaseName) && phase.subSystemList != null)
                    {
                        expandedNames.Add(phaseName);
                        foreach (PlayerLoopSystem child in phase.subSystemList)
                        {
                            names.Add(phaseName + "/" + (child.type != null ? child.type.Name : "?"));
                        }
                    }
                }

                LogExpansion(expandedNames, topLevel);
                _expandedPhases = expandedNames.ToArray();

                _names = names.ToArray();
                _starts = new long[_names.Length];
                _totals = new double[_names.Length];
                _snapshot = new double[_names.Length];
                _gcStarts = new int[_names.Length];
                _gcTotals = new int[_names.Length];
                _gcSnapshot = new int[_names.Length];

                int slot = 0;
                for (int i = 0; i < count; i++)
                {
                    PlayerLoopSystem phase = root.subSystemList[i];
                    string phaseName = _names[slot];
                    int phaseSlot = slot;
                    slot++;

                    PlayerLoopSystem[] inner = phase.subSystemList ?? new PlayerLoopSystem[0];

                    // Expand the children of the phase under investigation, innermost first, so the parent's
                    // own markers still bracket everything.
                    if (ShouldExpand(phaseName))
                    {
                        PlayerLoopSystem[] expanded = new PlayerLoopSystem[inner.Length * 3];
                        for (int c = 0; c < inner.Length; c++)
                        {
                            int childSlot = slot;
                            slot++;
                            expanded[c * 3] = MakeBegin(childSlot);
                            expanded[c * 3 + 1] = inner[c];
                            expanded[c * 3 + 2] = MakeEnd(childSlot);
                        }

                        inner = expanded;
                    }

                    PlayerLoopSystem[] wrapped = new PlayerLoopSystem[inner.Length + 2];
                    wrapped[0] = MakeBegin(phaseSlot);
                    Array.Copy(inner, 0, wrapped, 1, inner.Length);
                    wrapped[wrapped.Length - 1] = MakeEnd(phaseSlot);

                    phase.subSystemList = wrapped;
                    root.subSystemList[i] = phase;
                }

                PlayerLoop.SetPlayerLoop(root);
                Installed = true;
                Plugin.LogSource.LogInfo("Framesaver: player-loop profiler installed over " + _names.Length +
                                         " slots: " + string.Join(", ", _names));
            }
            catch (Exception e)
            {
                Installed = false;
                Plugin.LogSource.LogError("Framesaver: player-loop profiler install failed - " + e);
            }
        }

        /// <summary>
        /// Copies the accumulated per-phase totals and zeroes them. Called once per frame from Telemetry.
        /// </summary>
        public static void ReadAndReset()
        {
            lock (Gate)
            {
                for (int i = 0; i < _totals.Length; i++)
                {
                    _snapshot[i] = _totals[i];
                    _totals[i] = 0d;
                }

                for (int i = 0; i < _gcTotals.Length; i++)
                {
                    _gcSnapshot[i] = _gcTotals[i];
                    _gcTotals[i] = 0;
                }
            }
        }

        /// <summary>
        /// The game swaps the player loop around during raid load (BaseLocalGame and NetworkGame both replace
        /// EarlyUpdate.UpdateTextureStreamingManager and restore it afterwards), which can drop our markers.
        /// Telemetry re-checks periodically and reinstalls if they have gone.
        /// </summary>
        public static bool MarkersPresent()
        {
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null)
                {
                    return false;
                }

                foreach (PlayerLoopSystem phase in root.subSystemList)
                {
                    PlayerLoopSystem[] inner = phase.subSystemList;
                    if (inner != null && inner.Length > 0 && inner[0].type == typeof(BeginMarker))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A blocklist, not an allowlist. An allowlist silently omits any phase nobody thought to name -
        /// which is how a phase carrying a rare large spike goes unmeasured while the output looks
        /// complete. A blocklist fails toward collecting too much, which is the right direction when
        /// runs are scarce.
        ///
        /// Empty means expand everything. Deliberately no default entries: `Initialization` medians
        /// 0.005 ms over 140 in-raid windows and looks like an obvious block, but FINDINGS records one
        /// in-raid `Initialization` spike at 74.8 ms. Blocking on average cost would reintroduce exactly
        /// the omission this shape exists to prevent - a spike instrument has to be aimed at the tail.
        /// </summary>
        private static bool ShouldExpand(string phaseName)
        {
            string blocked = Plugin.ExpandPhase != null ? Plugin.ExpandPhase.Value : "";
            if (string.IsNullOrEmpty(blocked))
            {
                return true;
            }

            foreach (string entry in blocked.Split(','))
            {
                // Trimmed and case-insensitive, matching what the allowlist accepted. A stricter
                // replacement would silently start expanding a phase someone had been blocking.
                if (string.Equals(entry.Trim(), phaseName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reports what was actually expanded and which blocklist entries matched nothing.
        ///
        /// The failure mode got quieter with the move to a blocklist: a typo in an allowlist expanded
        /// nothing and was obvious in the output, while a typo in a blocklist expands a phase you meant
        /// to block and looks identical to a correct run. This is the line that makes it visible.
        /// </summary>
        private static void LogExpansion(System.Collections.Generic.List<string> expanded,
                                        System.Collections.Generic.List<string> topLevel)
        {
            string blocked = Plugin.ExpandPhase != null ? Plugin.ExpandPhase.Value : "";
            System.Collections.Generic.List<string> unmatched = new System.Collections.Generic.List<string>();

            foreach (string entry in blocked.Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                bool matched = false;
                for (int i = 0; i < topLevel.Count; i++)
                {
                    if (string.Equals(trimmed, topLevel[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    unmatched.Add(trimmed);
                }
            }

            Plugin.LogSource.LogInfo("Framesaver player loop: expanded [" + string.Join(", ", expanded.ToArray())
                                     + "]" + (unmatched.Count > 0
                                         ? " - blocklist entries matching no phase: "
                                           + string.Join(", ", unmatched.ToArray())
                                         : ""));
        }

        private static PlayerLoopSystem MakeBegin(int slot)
        {
            return new PlayerLoopSystem
            {
                type = typeof(BeginMarker),
                updateDelegate = delegate
                {
                    _starts[slot] = Stopwatch.GetTimestamp();
                    _gcStarts[slot] = GC.CollectionCount(0);
                }
            };
        }

        private static PlayerLoopSystem MakeEnd(int slot)
        {
            return new PlayerLoopSystem
            {
                type = typeof(EndMarker),
                updateDelegate = delegate
                {
                    long start = _starts[slot];
                    if (start != 0L)
                    {
                        _totals[slot] += (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                        _gcTotals[slot] += GC.CollectionCount(0) - _gcStarts[slot];
                    }
                }
            };
        }

        private struct BeginMarker
        {
        }

        private struct EndMarker
        {
        }
    }
}
