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

        public static string[] PhaseNames
        {
            get { return _names; }
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

                // First pass: name every slot we intend to time, so indices are stable before we build the
                // delegates that close over them.
                for (int i = 0; i < count; i++)
                {
                    PlayerLoopSystem phase = root.subSystemList[i];
                    string phaseName = phase.type != null ? phase.type.Name : ("phase" + i);
                    names.Add(phaseName);

                    if (ShouldExpand(phaseName) && phase.subSystemList != null)
                    {
                        foreach (PlayerLoopSystem child in phase.subSystemList)
                        {
                            names.Add(phaseName + "/" + (child.type != null ? child.type.Name : "?"));
                        }
                    }
                }

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

        private static bool ShouldExpand(string phaseName)
        {
            string wanted = Plugin.ExpandPhase != null ? Plugin.ExpandPhase.Value : "";
            return !string.IsNullOrEmpty(wanted) && string.Equals(phaseName, wanted, StringComparison.OrdinalIgnoreCase);
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
