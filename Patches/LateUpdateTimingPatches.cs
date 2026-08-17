using System.Diagnostics;
using System.Reflection;
using Diz.Jobs;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Framesaver.Patches
{
    /// <summary>
    /// Decomposes the largest single block of frame time.
    ///
    /// On Streets, gameUpdate averages ~12ms of which Update is ~3.5ms and FixedUpdate ~0.2ms. That leaves
    /// ~8.3ms - the biggest CPU block in the frame - somewhere between StartOfFrame and PostLateUpdate but in
    /// neither measured phase: Unity's animation pass, every MonoBehaviour LateUpdate, and culling.
    ///
    /// These three timers carve off the parts that are plausibly bot-scaled. Whatever the gap has left over
    /// after subtracting them is Unity-internal (animation evaluation, culling) and not reachable by patching.
    ///
    /// Values accumulate across all callers within a frame and are read-then-zeroed from Telemetry.Update,
    /// which runs in the Update phase and therefore sees the previous frame's completed totals.
    /// </summary>
    public static class LateTiming
    {
        /// <summary>JobScheduler.LateUpdate - the continuation pump.</summary>
        public static double JobSchedulerMs;

        /// <summary>
        /// AmbientLight.LateUpdate. Per frame it rebuilds the stencil-shadow command buffer for every
        /// registered camera - clear, then walk a SortedSet of every StencilShadow in the scene,
        /// frustum-testing each - and every RenderDelay seconds (0.05 by default) renders a cubemap face
        /// for the sky ambient.
        ///
        /// Measured exactly 0 on Streets, but only because the component is inactive on that map - not
        /// because it is cheap. A 2023 community report put it at 5ms/frame. This must be present before
        /// testing any other map or a regression there would be invisible.
        /// </summary>
        public static double AmbientLightMs;

        /// <summary>
        /// Summed Player.LateUpdate across every Player this frame. In offline/SPT every bot is a full
        /// LocalPlayer MonoBehaviour; online, remote characters are the far lighter ObservedPlayerView
        /// instead. This is therefore a cost that only exists locally.
        /// </summary>
        public static double PlayerLateMs;

        /// <summary>GameWorld.PlayerTick - the manual per-Player world tick, same local-only reasoning.</summary>
        public static double PlayerTickMs;

        public static void Reset()
        {
            PlayerLateMs = 0d;
            PlayerTickMs = 0d;
            JobSchedulerMs = 0d;
            AmbientLightMs = 0d;
        }
    }

    internal class JobSchedulerLateUpdatePatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(JobScheduler), nameof(JobScheduler.LateUpdate));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            LateTiming.JobSchedulerMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    internal class AmbientLightLateUpdatePatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AmbientLight), nameof(AmbientLight.LateUpdate));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            LateTiming.AmbientLightMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    internal class PlayerLateUpdateTimingPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.LateUpdate));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            LateTiming.PlayerLateMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }

    internal class GameWorldPlayerTickPatch : ModulePatch
    {
        private static long _start;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.PlayerTick));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            _start = Stopwatch.GetTimestamp();
        }

        [PatchPostfix]
        private static void Postfix()
        {
            LateTiming.PlayerTickMs += AiTiming.ToMs(Stopwatch.GetTimestamp() - _start);
        }
    }
}
