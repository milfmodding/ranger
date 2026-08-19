using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Bots;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Reflection.Patching;

namespace Ranger
{
    /// <summary>
    /// Records, once per raid, the gates that decide whether a forced boss
    /// garrison actually arrives - and answers the one question no other check
    /// we have can.
    ///
    /// A garrison forced server-side can pass every test we designed - chance
    /// 100, ShallSpawn true, ForceSpawn past both the zone-occupancy and
    /// not-enough-points gates, valid fixture, matching hash - and still
    /// produce nothing, because ExcludedBosses blocks the role outright via
    /// BotSpawner.SetBlockedRoles. Every instrument says the run is good and
    /// it is not.
    ///
    /// **`forcedButExcluded` is that failure announcing itself.** Non-empty
    /// means the run is void, known at raid start rather than from a
    /// confusing analysis weeks later.
    ///
    /// THE TWO OPERANDS ARE NOT AVAILABLE IN THE SAME PLACE, which is the
    /// whole reason this is two patches:
    ///
    /// - `LocalGame.smethod_8` is called from the static factory
    ///   `LocalGame.smethod_6`, whose parameters do not include
    ///   BotControllerSettings at all. ExcludedBosses is not merely
    ///   unpopulated there - it is not in scope.
    /// - `LocalGame.vmethod_1(BotControllerSettings, ISpawnSystem)` takes it
    ///   as an argument, and runs on the instance the factory built, so the
    ///   ordering is guaranteed by object lifetime rather than by luck.
    ///
    /// Computing the intersection in the first would have intersected against
    /// nothing and read EMPTY - a false all-clear, in the check written to
    /// close the failure it was reporting on. Alpha called that hazard before
    /// either of us knew which way the ordering went.
    /// </summary>
    public static class BossSpawnGate
    {
        /// <summary>
        /// False until smethod_8's postfix has run. Everything derived below
        /// is meaningless without it, and **must read `null` rather than
        /// empty** in that case - an empty intersection is the all-clear, and
        /// "we could not compute it" must not be able to impersonate one.
        /// </summary>
        private static bool _sawWaves;

        private static bool _sawSettings;
        private static int _entries;
        private static bool _pveOffline;
        private static string _botAmountWaves = "";
        private static string _botAmountRaid = "";

        // Parsed, not raw, because both sides of the comparison are parsed by
        // the game: BossName through Enum.Parse in ParseMainTypesTypes, and
        // ExcludedBosses through Enum.TryParse in SetBlockedRoles. Comparing
        // strings would claim a match the game would not make.
        private static readonly List<WildSpawnType> Forced = new List<WildSpawnType>(8);
        private static readonly List<WildSpawnType> Excluded = new List<WildSpawnType>(8);

        /// <summary>
        /// Raw ExcludedBosses text. Kept alongside the parsed list because
        /// SetBlockedRoles silently drops anything Enum.TryParse rejects, so
        /// a typo there excludes nothing - and a raw entry with no parsed
        /// counterpart is the only way to see that from the log.
        /// </summary>
        private static readonly List<string> ExcludedRaw = new List<string>(8);

        /// <summary>
        /// Clears the settings half too, and that is the whole raid-reset
        /// mechanism - there is deliberately no ResetForRaid.
        ///
        /// Telemetry's raid-start hook fires when sampling begins, which is
        /// AFTER LocalGame construction, so a reset called from there would
        /// wipe what smethod_8 had already recorded. Rather than depend on
        /// getting that ordering right, nothing external resets this: both
        /// records are overwritten wholesale at the start of every raid by
        /// the patches themselves, and this method runs first because it is
        /// driven from the static factory that BUILDS the object vmethod_1
        /// is later called on. Object lifetime, not luck.
        ///
        /// Clearing the settings half here is what stops a raid whose
        /// vmethod_1 never fired from reporting the PREVIOUS raid's excluded
        /// list under `sawSettings: true`.
        /// </summary>
        internal static void RecordWaves(BossLocationSpawn[] waves, bool pveOffline, EBotAmount amount)
        {
            _sawSettings = false;
            _botAmountRaid = "";
            Excluded.Clear();
            ExcludedRaw.Clear();

            Forced.Clear();
            _entries = waves == null ? 0 : waves.Length;
            _pveOffline = pveOffline;
            _botAmountWaves = amount.ToString();

            for (int i = 0; i < _entries; i++)
            {
                BossLocationSpawn wave = waves[i];
                if (wave != null && wave.ForceSpawn && wave.BossChance >= 100f)
                {
                    Forced.Add(wave.BossType);
                }
            }

            _sawWaves = true;
        }

        internal static void RecordSettings(BotControllerSettings settings)
        {
            Excluded.Clear();
            ExcludedRaw.Clear();
            // BotControllerSettings is a STRUCT, so there is no null to guard
            // against here - the compiler refuses the comparison outright.
            _botAmountRaid = settings.BotAmount.ToString();

            string[] names = settings.ExcludedBosses;
            if (names != null)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    ExcludedRaw.Add(names[i] ?? "");

                    WildSpawnType parsed;
                    if (Enum.TryParse(names[i], out parsed))
                    {
                        Excluded.Add(parsed);
                    }
                }
            }

            _sawSettings = true;
        }

        public static bool Any
        {
            get { return _sawWaves || _sawSettings; }
        }

        /// <summary>
        /// JObject conversion (2026-08-19, sub-module follow-on pass): was a StringBuilder
        /// fragment builder, same class of risk the "bots" block bug came from - a
        /// silently-missing .Append(...) call reads identically to correct output around
        /// it. Now builds a real JObject directly; Telemetry.cs's Flush() assigns it to
        /// obj["spawnGate"] rather than wrapping it in JRaw.
        /// </summary>
        public static JObject Append()
        {
            JObject obj = new JObject();
            obj["sawWaves"] = _sawWaves;
            obj["sawSettings"] = _sawSettings;
            obj["entries"] = _entries;
            obj["pveOffline"] = _pveOffline;
            obj["botAmountWaves"] = _botAmountWaves;
            obj["botAmountRaid"] = _botAmountRaid;
            obj["forced"] = Roles(Forced);
            obj["excluded"] = Roles(Excluded);
            obj["excludedRaw"] = Strings(ExcludedRaw);

            // null, not [], unless BOTH halves were observed. See _sawWaves.
            if (!_sawWaves || !_sawSettings)
            {
                obj["forcedButExcluded"] = JValue.CreateNull();
            }
            else
            {
                var hit = new List<WildSpawnType>(2);
                for (int i = 0; i < Forced.Count; i++)
                {
                    if (Excluded.Contains(Forced[i]) && !hit.Contains(Forced[i]))
                    {
                        hit.Add(Forced[i]);
                    }
                }

                obj["forcedButExcluded"] = Roles(hit);
            }

            return obj;
        }

        private static JArray Roles(List<WildSpawnType> roles)
        {
            JArray arr = new JArray();
            for (int i = 0; i < roles.Count; i++)
            {
                arr.Add(roles[i].ToString());
            }

            return arr;
        }

        private static JArray Strings(List<string> values)
        {
            // JValue/JArray handle their own quoting and escaping - the manual
            // Replace("\\",...).Replace("\"",...) escaping the StringBuilder version did
            // by hand is exactly the kind of hand-matched-quote logic this conversion
            // removes a class of bugs from.
            JArray arr = new JArray();
            for (int i = 0; i < values.Count; i++)
            {
                arr.Add(values[i]);
            }

            return arr;
        }

    }

    /// <summary>
    /// Reads __result, not the input array: smethod_8 early-returns
    /// Array.Empty when IsBosses is off and the raid is not PvE-offline,
    /// dropping EVERY boss wave. Counting the result means that gate shows
    /// up as `entries: 0` rather than being invisible.
    /// </summary>
    internal class BossWaveSettingsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LocalGame), "smethod_8");
        }

        [PatchPostfix]
        private static void Postfix(bool isPVEOffline, WavesSettings wavesSettings, BossLocationSpawn[] __result)
        {
            BossSpawnGate.RecordWaves(__result, isPVEOffline, wavesSettings.BotAmount);
        }
    }

    /// <summary>
    /// Prefix, not postfix: vmethod_1 is async, so a postfix would run when
    /// the state machine yields at its first await rather than at completion.
    /// Nothing here needs to happen after the awaits - controllerSettings is
    /// an argument, readable before the machine starts.
    /// </summary>
    internal class BotControllerSettingsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LocalGame), "vmethod_1");
        }

        [PatchPrefix]
        private static void Prefix(BotControllerSettings controllerSettings)
        {
            BossSpawnGate.RecordSettings(controllerSettings);
        }
    }
}
