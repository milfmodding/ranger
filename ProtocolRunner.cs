using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace Framesaver
{
    /// <summary>
    /// Steps a measurement protocol on an operator keypress, stamping the arm into the telemetry.
    ///
    /// **Why a keypress rather than detecting the config change.** Sophia previously changed knobs through
    /// the F12 ConfigurationManager overlay, which has three separate problems: it is a large IMGUI draw,
    /// so the window it is open in belongs to no arm; the change lands mid-window and `cfg` is stamped at
    /// write time, so the straddling window is labelled with the *new* arm; and - the one nobody had
    /// written down until the 2026-07-28 raid - **it moves the view**, because EFT still processes mouse
    /// look while the overlay is open. That run recorded `swept = 102 degrees` in a knob-change window and
    /// left arm 3 sitting ~510 draw calls above arm 1.
    ///
    /// Flushing on the config change instead does not work either: she closes the overlay *after*
    /// changing the knob, so the new window opens contaminated.
    ///
    /// **The keypress is Gamma's design and the reason is that it stamps the arm into the line.** Arms
    /// become data rather than something inferred from `cfg` labelling or from `drawCalls.max / .avg`. On
    /// the 2026-07-28 raid `look.swept` did the segmentation - 196.8 and 195.1 on the turns against 0-3.1
    /// held - which worked, and worked *by accident*. A label does it by design.
    ///
    /// The protocol is declarative and read at raid start, so changing one is not a build.
    /// </summary>
    public static class ProtocolRunner
    {
        public sealed class Step
        {
            public string Arm;

            /// <summary>
            /// How long this step runs before advancing itself, or 0 for
            /// "wait for the key". Set by `@seconds`.
            /// </summary>
            public float Seconds;

            public readonly List<KeyValuePair<ConfigEntryBase, object>> Assignments =
                new List<KeyValuePair<ConfigEntryBase, object>>();
        }

        private static readonly List<Step> Steps = new List<Step>();

        /// <summary>
        /// When the current step began, on the same clock the telemetry window
        /// uses. 0 before the protocol is started.
        /// </summary>
        private static float _stepStart;

        /// <summary>File-level `@seconds`, inherited by steps that do not
        /// set their own.</summary>
        private static float _defaultSeconds;

        /// <summary>
        /// The running step's box has elapsed, so the caller should flush and
        /// advance exactly as if the key had been pressed.
        ///
        /// **Only ever true after the operator has started the protocol by
        /// hand.** `_stepStart` is set by Advance and cleared per raid, so a
        /// loaded protocol sitting in the menu cannot burn through its steps,
        /// and the first arm still begins on a deliberate act. That split is
        /// the point of the feature: the press at raid start is made calmly,
        /// and the ones that get missed are the ones during a fight.
        /// </summary>
        public static bool Due
        {
            get
            {
                // _stepStart is only ever set by Advance, which has already
                // incremented StepIndex - so a non-zero stamp means a step is
                // running and StepSeconds describes it.
                if (!CanAdvance || _stepStart <= 0f)
                {
                    return false;
                }

                float box = StepSeconds;
                return box > 0f && Time.realtimeSinceStartup - _stepStart >= box;
            }
        }

        /// <summary>The running step's box, for the log header. 0 when
        /// hand-driven.</summary>
        public static float StepSeconds
        {
            get { return StepIndex > 0 && StepIndex <= Steps.Count ? Steps[StepIndex - 1].Seconds : 0f; }
        }

        /// <summary>True only when a protocol parsed cleanly. A partially-valid protocol is refused
        /// whole: applying half the assignments of a step would produce an arm that matches no
        /// definition, which is worse than not stepping at all.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>Why loading failed, or empty. On the header so a run without a protocol is
        /// distinguishable from a run whose protocol was rejected - those need different fixes.</summary>
        public static string Failure { get; private set; }

        public static string Name { get; private set; }

        /// <summary>Steps taken. **0 means loaded but not started**, which is a real state and not a
        /// missing one - the protocol is armed and waiting for the first press. Absent-protocol is
        /// spelled `null` on the line instead, never 0.</summary>
        public static int StepIndex { get; private set; }

        /// <summary>Label of the current step, or null before the first press. Null rather than an empty
        /// string for the same reason: unstarted and unlabelled are different.</summary>
        public static string Arm
        {
            get { return StepIndex > 0 && StepIndex <= Steps.Count ? Steps[StepIndex - 1].Arm : null; }
        }

        public static int StepCount
        {
            get { return Steps.Count; }
        }

        public static string Path
        {
            get
            {
                return System.IO.Path.Combine(
                    BepInEx.Paths.ConfigPath, "framesaver.protocol.ini");
            }
        }

        /// <summary>
        /// Reads and validates the protocol. Called at raid start so an edit takes effect on the next
        /// raid without a relaunch.
        ///
        /// **No file is not a failure** - most runs have no protocol - so Failure stays empty and Loaded
        /// stays false. A file that exists and does not parse *is* a failure and says so.
        /// </summary>
        public static void Load()
        {
            Steps.Clear();
            Loaded = false;
            Failure = "";
            Name = "";
            StepIndex = 0;
            _stepStart = 0f;
            _defaultSeconds = 0f;

            try
            {
                if (!File.Exists(Path))
                {
                    return;
                }

                Dictionary<string, ConfigEntryBase> entries = BuildEntryMap();
                Step current = null;
                int lineNo = 0;

                foreach (string raw in File.ReadAllLines(Path))
                {
                    lineNo++;
                    string line = StripComment(raw).Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (line[0] == '[')
                    {
                        int close = line.IndexOf(']');
                        if (close < 2)
                        {
                            Fail("line " + lineNo + ": step header is not [label]");
                            return;
                        }

                        // Inherits the file-level box, so a protocol with
                        // uniform arms states the duration once.
                        current = new Step
                        {
                            Arm = line.Substring(1, close - 1).Trim(),
                            Seconds = _defaultSeconds,
                        };
                        Steps.Add(current);
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq < 1)
                    {
                        Fail("line " + lineNo + ": expected 'key = value'");
                        return;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    // Runner directives are @-prefixed so they can never
                    // collide with a mod setting. Config keys are readable
                    // words ("Brain update period"), so `@` is free forever -
                    // which matters because a bare `seconds` key would break
                    // every protocol file on disk the day someone adds a
                    // setting by that name. Bare `name` is still accepted; it
                    // predates this and every existing file uses it.
                    if (key.Length > 1 && key[0] == '@')
                    {
                        if (!Directive(key.Substring(1), value, current, lineNo))
                        {
                            return;
                        }

                        continue;
                    }

                    if (current == null)
                    {
                        if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                        {
                            Name = value;
                            continue;
                        }

                        Fail("line " + lineNo + ": '" + key + "' appears before any [step]");
                        return;
                    }

                    // Unknown keys are refused rather than skipped. A typo that silently does nothing
                    // produces an arm whose config never changed, which is indistinguishable in the data
                    // from a knob that had no effect - the exact conclusion an A/B exists to draw.
                    ConfigEntryBase entry;
                    if (!entries.TryGetValue(key, out entry))
                    {
                        Fail("line " + lineNo + ": no config entry named '" + key + "'");
                        return;
                    }

                    object parsed;
                    if (!TryParse(value, entry.SettingType, out parsed))
                    {
                        Fail("line " + lineNo + ": '" + value + "' is not a "
                             + entry.SettingType.Name + " for '" + key + "'");
                        return;
                    }

                    current.Assignments.Add(
                        new KeyValuePair<ConfigEntryBase, object>(entry, parsed));
                }

                if (Steps.Count == 0)
                {
                    Fail("no [step] sections");
                    return;
                }

                Loaded = true;
                WarnOnPartialWindows();
                Plugin.LogSource.LogInfo("Framesaver protocol '" + Name + "' loaded: " + Steps.Count
                                         + " steps. Press the protocol key to start.");
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        /// <summary>
        /// Advances one step and applies its config values. Returns true when the caller should flush the
        /// window - which is every successful advance, because the point is that the change lands on a
        /// window boundary rather than inside one.
        ///
        /// **Refuses loudly.** A key that silently does nothing is worse than no key: the operator
        /// believes the arm changed, the run continues, and nothing in the data disagrees until analysis.
        /// </summary>
        /// <summary>
        /// Whether a press would do anything. Advance() tests this same property rather than
        /// re-deriving its own precondition, so the two cannot drift.
        ///
        /// It exists because the caller needs to flush the window BEFORE advancing: Advance()
        /// applies the step's config, so a flush afterwards labels the closing line with the arm
        /// about to start while its measurements describe the arm just ended. Fixing that needs
        /// the caller to know in advance whether the press will take - and writing
        /// `Loaded && StepIndex < StepCount` at the call site would be a copy of this line that
        /// goes stale the first time the protocol grows a rule. Same reason the unknown-key
        /// refusal exists: a second statement of a rule is a second place for it to be wrong.
        /// </summary>
        public static bool CanAdvance
        {
            get { return Loaded && StepIndex < Steps.Count; }
        }

        public static bool Advance()
        {
            // One precondition, checked once. The two messages below only explain a refusal -
            // they never decide it, which is what stops them drifting from CanAdvance.
            if (!CanAdvance)
            {
                if (!Loaded)
                {
                    Plugin.LogSource.LogError(
                        "Framesaver: protocol key pressed but no protocol is loaded"
                        + (Failure.Length > 0 ? " (" + Failure + ")" : " - expected " + Path)
                        + ". Nothing changed.");
                }
                else
                {
                    Plugin.LogSource.LogWarning("Framesaver: protocol '" + Name + "' is finished at step "
                                                + StepIndex + " of " + Steps.Count + ". Nothing changed.");
                }

                return false;
            }

            Step step = Steps[StepIndex];
            foreach (KeyValuePair<ConfigEntryBase, object> a in step.Assignments)
            {
                a.Key.BoxedValue = a.Value;
            }

            StepIndex++;
            _stepStart = Time.realtimeSinceStartup;
            Plugin.LogSource.LogInfo("Framesaver protocol '" + Name + "' -> step " + StepIndex + "/"
                                     + Steps.Count + " arm '" + step.Arm + "'"
                                     + (step.Seconds > 0f
                                        ? " for " + step.Seconds.ToString("0.#", CultureInfo.InvariantCulture)
                                          + "s"
                                        : " (press to advance)"));
            return true;
        }

        /// <summary>
        /// A runner directive - `@name`, `@seconds`.
        ///
        /// Unknown directives are refused rather than ignored, for exactly the
        /// reason unknown config keys are: a typo that silently does nothing
        /// produces a protocol that ran differently from the one written down,
        /// and nothing in the data disagrees with the write-up.
        /// </summary>
        private static bool Directive(string name, string value, Step current, int lineNo)
        {
            if (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase))
            {
                Name = value;
                return true;
            }

            if (!string.Equals(name, "seconds", StringComparison.OrdinalIgnoreCase))
            {
                Fail("line " + lineNo + ": unknown directive '@" + name + "'");
                return false;
            }

            float seconds;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                || seconds < 0f)
            {
                Fail("line " + lineNo + ": '@seconds = " + value + "' is not a number of seconds");
                return false;
            }

            // Before any [step] it is the file-level default; inside one it
            // overrides for that step only.
            if (current == null)
            {
                _defaultSeconds = seconds;
            }
            else
            {
                current.Seconds = seconds;
            }

            return true;
        }

        /// <summary>
        /// Warns when a box is not a whole number of telemetry windows.
        ///
        /// Not an error - a protocol is still valid and still runs. But a
        /// 90 s box against a 60 s window gives one full window and one half
        /// window per arm, and the half window is a sample of a different size
        /// that averages in as though it were not. Cheaper to say at load than
        /// to find in analysis.
        /// </summary>
        private static void WarnOnPartialWindows()
        {
            float window = Plugin.TelemetryWindow.Value;
            if (window <= 0f)
            {
                return;
            }

            foreach (Step step in Steps)
            {
                if (step.Seconds <= 0f)
                {
                    continue;
                }

                float windows = step.Seconds / window;
                if (Mathf.Abs(windows - Mathf.Round(windows)) > 0.01f)
                {
                    Plugin.LogSource.LogWarning(
                        "Framesaver protocol: arm '" + step.Arm + "' is "
                        + step.Seconds.ToString("0.#", CultureInfo.InvariantCulture)
                        + "s against a " + window.ToString("0.#", CultureInfo.InvariantCulture)
                        + "s window, so it ends mid-window and that arm's last sample is short.");
                }
            }
        }

        /// <summary>Reset between raids so a second raid starts the protocol from the beginning rather
        /// than inheriting the first raid's position - the same cross-raid leak shape as Sleeping.</summary>
        public static void ResetForRaid()
        {
            StepIndex = 0;
            _stepStart = 0f;
            Load();
        }

        private static void Fail(string why)
        {
            Steps.Clear();
            Loaded = false;
            Failure = why;
            Plugin.LogSource.LogError("Framesaver protocol not loaded - " + why + " (" + Path + ")");
        }

        /// <summary>
        /// Every ConfigEntry on Plugin, keyed by its BepInEx key.
        ///
        /// Reflection rather than a hand-written list, for the reason the phase blocklist exists: a list
        /// silently omits whatever nobody thought to name, and here that would mean a protocol could not
        /// vary a setting for no reason the author could see. Reflection covers new settings the day they
        /// are added, and an unknown key still fails loudly above.
        /// </summary>
        private static Dictionary<string, ConfigEntryBase> BuildEntryMap()
        {
            Dictionary<string, ConfigEntryBase> map =
                new Dictionary<string, ConfigEntryBase>(StringComparer.OrdinalIgnoreCase);

            foreach (FieldInfo f in typeof(Plugin).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                ConfigEntryBase entry = f.GetValue(null) as ConfigEntryBase;
                if (entry != null)
                {
                    map[entry.Definition.Key] = entry;
                }
            }

            return map;
        }

        private static string StripComment(string line)
        {
            int hash = line.IndexOf('#');
            int semi = line.IndexOf(';');
            int cut = hash < 0 ? semi : (semi < 0 ? hash : Math.Min(hash, semi));
            return cut < 0 ? line : line.Substring(0, cut);
        }

        private static bool TryParse(string text, Type type, out object value)
        {
            value = null;

            try
            {
                if (type == typeof(bool))
                {
                    bool b;
                    if (!bool.TryParse(text, out b)) return false;
                    value = b;
                    return true;
                }

                if (type == typeof(int))
                {
                    int i;
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                        return false;
                    value = i;
                    return true;
                }

                if (type == typeof(float))
                {
                    float f;
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        return false;
                    value = f;
                    return true;
                }

                if (type == typeof(string))
                {
                    value = text;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }
    }
}
