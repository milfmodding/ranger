using System.Diagnostics;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
{
    /// <summary>
    /// Splits the cost of constructing a bot Profile into its expensive parts.
    ///
    /// `new Profile(descriptor)` runs ~41ms per bot and is the whole of the bot/generate stall, which is in
    /// turn 81% of in-raid spike time. Before hollowing anything out we need to know where inside it the time
    /// goes: if `Inventory.ToInventory()` - the item graph, which bots genuinely need - is most of it, then
    /// stripping the wishlists and trader tables buys nothing.
    ///
    /// Sections nest inside the total rather than partitioning it, so `other` is the subtraction:
    ///   other = total - inventory - traders - skills
    ///
    /// `traders` is the one that should not exist at all: the constructor loops every trader in
    /// TradersSettings and builds a Profile.TraderInfo per trader, per bot, so a scav gets a full
    /// trader-standing table it can never use.
    /// </summary>
    public static class ProfileBuild
    {
        public static double TotalMs;
        public static double InventoryMs;
        public static int Profiles;

        /// <summary>Deepest section timings are only valid while a Profile ctor is on the stack.</summary>
        internal static int Depth;

        public static double OtherMs
        {
            get
            {
                double other = TotalMs - InventoryMs;
                return other > 0d ? other : 0d;
            }
        }

        public static void ResetWindow()
        {
            TotalMs = 0d;
            InventoryMs = 0d;
            Profiles = 0;
        }
    }

    internal class ProfileCtorPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Constructor(typeof(Profile), new[] { typeof(EFT.ProfileDescriptor) });
        }

        [PatchPrefix]
        private static void Prefix()
        {
            if (ProfileBuild.Depth++ == 0)
            {
                _start = Stopwatch.GetTimestamp();
            }
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (--ProfileBuild.Depth > 0)
            {
                return;
            }

            ProfileBuild.Depth = 0;
            ProfileBuild.TotalMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            ProfileBuild.Profiles++;
        }
    }

    /// <summary>
    /// The item graph. Gated on being inside a Profile constructor so inventory work elsewhere - the player's
    /// own stash, trader assortments - does not land in these numbers.
    /// </summary>
    internal class ProfileInventoryPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EFT.InventoryDescriptor), nameof(EFT.InventoryDescriptor.ToInventory));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (ProfileBuild.Depth > 0)
            {
                ProfileBuild.InventoryMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
            }
        }
    }
}
