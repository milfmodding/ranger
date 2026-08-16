using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Comfort.Common;
using Framesaver.Patches;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Framesaver
{
    /// <summary>
    /// GPU-side instruments. Everything else in this mod measures the main thread, which leaves the largest
    /// remaining spike family - Unity's TimeUpdate / WaitForLastPresentationAndUpdateTime - filed as
    /// "GPU-side, not reachable by patching". It is reachable; it just is not reachable through Harmony.
    ///
    /// Three independent sources, each probed at runtime and each disabled permanently the first time it
    /// fails, so an unavailable source costs one exception and never runs again:
    ///
    ///   vram          CameraClass.GetVRamUsage - DXGI QueryVideoMemoryInfo, already wired up by BSG for
    ///                 their own overlay. `localCurrentUsage` above `localBudget` means the driver is
    ///                 evicting to system RAM over PCIe, which produces stutter that no main-thread
    ///                 instrument in this mod can see. This is the one expected to pay off: TextureQuality 3
    ///                 with Mip Streaming off pins globalTextureMipmapLimit at 0, so every mip of every
    ///                 texture stays resident.
    ///   frameTiming   UnityEngine.Rendering.FrameTimingManager - real gpuFrameTime plus the main thread's
    ///                 present wait, if the build was made with Frame Timing Stats enabled. Probably was not;
    ///                 it is a baked player setting. Ten lines to find out, and it is vendor-neutral, so it
    ///                 is worth trying before the NVIDIA-only Reflex path.
    ///   render        ProfilerRecorder counters (draw calls, SetPass, triangles). CPU-side submission cost
    ///                 rather than GPU time, but it is what says whether N full-LocalPlayer bots are also N
    ///                 bots' worth of draw calls. Only emits if this build shipped with ENABLE_PROFILER.
    ///
    /// Instruments that cost more than what they measure are a recurring failure here - the WeaponSoundPlayer
    /// Stopwatch pair ran on 49 instances a frame to measure 0.002ms. So the DXGI query is timed and reports
    /// its own worst case as `queryMsMax`; if that is not negligible, raise the interval or drop the source.
    /// </summary>
    internal static class GpuTelemetry
    {
        /// <summary>
        /// How often the DXGI query runs. VRAM pressure builds over seconds - bundles landing, a map
        /// streaming in - so per-frame sampling would buy nothing but overhead.
        /// </summary>
        private const float VramIntervalSeconds = 0.5f;

        /// <summary>
        /// Frames to keep trying FrameTimingManager before concluding the build lacks the flag. It needs a
        /// few frames of history before it reports anything, so a single probe at startup would false-negative.
        /// </summary>
        private const int FrameTimingProbeFrames = 240;

        private enum SourceState
        {
            Untested,
            Live,
            Unavailable,
        }

        // ---- wall clock ---------------------------------------------------------------------

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceFrequency(out long value);

        private static bool _qpcChecked;
        private static bool _qpcUsable;

        /// <summary>
        /// True QPC, for joining against an external capture such as PresentMon.
        ///
        /// Stopwatch.GetTimestamp() is NOT this under Mono. On .NET it returns the raw QueryPerformanceCounter
        /// value; Mono returns 100ns ticks measured from *process start*. Both report Stopwatch.Frequency as
        /// 10,000,000, so the tick rate matches and durations are correct either way - but the epoch does not,
        /// and the two series simply do not overlap. Measured on the 2026-07-27 Streets run: PresentMon
        /// timestamps sat at 5.19e12 while Stopwatch reported 1.07e9 to 1.00e10, and the join had to be
        /// recovered afterwards by matching a 37-second stall in both files by hand.
        ///
        /// P/Invoking QueryPerformanceCounter gives the value PresentMon actually writes. Durations elsewhere
        /// in this mod stay on Stopwatch, which is correct for deltas and cheaper.
        /// </summary>
        internal static long Qpc()
        {
            if (!_qpcChecked)
            {
                _qpcChecked = true;
                try
                {
                    long probe;
                    _qpcUsable = QueryPerformanceCounter(out probe);
                }
                catch (Exception)
                {
                    _qpcUsable = false;
                }

                if (!_qpcUsable)
                {
                    Plugin.LogSource.LogWarning(
                        "Framesaver GPU: QueryPerformanceCounter unavailable; qpc falls back to Stopwatch, "
                        + "which under Mono is process-relative and will not join against an external capture.");
                }
            }

            if (_qpcUsable)
            {
                long now;
                if (QueryPerformanceCounter(out now))
                {
                    return now;
                }
            }

            return Stopwatch.GetTimestamp();
        }

        /// <summary>Ticks per second for <see cref="Qpc"/>, so the stamps convert to seconds.</summary>
        internal static long QpcFrequency()
        {
            try
            {
                long freq;
                if (QueryPerformanceFrequency(out freq) && freq > 0L)
                {
                    return freq;
                }
            }
            catch (Exception)
            {
                // fall through
            }

            return Stopwatch.Frequency;
        }

        // CONSTRAINT ON EVERY FIELD BELOW: no field in this class may be typed with a game type
        // (`CameraClass`, `GraphicsSettingsClass`, `SharedGameSettingsClass`, any `GClass*`). Those names are
        // obfuscated and move between SPT versions, and a static field of one moves the resolution failure
        // into this class's *type initialiser* - which no guard inside the class can catch, and which then
        // poisons every member, including `Qpc()` that `Telemetry` calls without a guard. Game types belong
        // in method bodies reached through `Guarded`/`Sample`, where the JIT-time failure lands inside a try.
        //
        // `ProfilerRecorder` and `FrameTiming` below are fine: Unity types, not obfuscated, no rename risk.
        // See FINDINGS.md methodology notes, "a try inside a method does not protect against a
        // type-resolution failure in that method".

        // ---- vram ---------------------------------------------------------------------------
        private static SourceState _vramState = SourceState.Untested;
        private static string _vramError;
        private static float _nextVramSample;
        private static readonly Stat _vramUsedMb = new Stat();
        private static readonly Stat _vramBudgetMb = new Stat();
        private static double _vramTotalMb;
        private static int _overBudgetSamples;
        private static double _overBudgetWorstMb;
        private static double _vramQueryMsMax;
        // Latched for spike lines, which fire between timer samples and must not trigger a query of their own.
        private static double _lastVramUsedMb, _lastVramBudgetMb;

        // ---- frameTiming --------------------------------------------------------------------
        private static SourceState _frameTimingState = SourceState.Untested;
        private static string _frameTimingError;
        private static int _frameTimingProbes;
        private static readonly FrameTiming[] _timings = new FrameTiming[1];
        private static ulong _lastPresentStamp;
        private static readonly Stat _gpuFrame = new Stat();
        private static readonly Stat _presentWait = new Stat();
        private static readonly Stat _renderThread = new Stat();
        private static readonly Stat _cpuFrame = new Stat();
        private static double _lastGpuFrameMs, _lastPresentWaitMs;

        // ---- render counters ----------------------------------------------------------------
        private static SourceState _renderState = SourceState.Untested;
        private static string _renderError;
        private static ProfilerRecorder _drawCalls, _setPass, _triangles;
        private static readonly Stat _drawCallStat = new Stat();
        private static readonly Stat _setPassStat = new Stat();
        private static readonly Stat _triangleStat = new Stat();

        /// <summary>
        /// Set if anything in this file throws in a way its own guards could not catch, after which none of it
        /// runs again.
        ///
        /// The per-source try/catch blocks below are not sufficient on their own. A missing or renamed type -
        /// `CameraClass`, `GraphicsSettingsClass`, `SharedGameSettingsClass` - fails when the *method
        /// referencing it is JIT-compiled, before its body executes, so the `try` inside that method never
        /// gets the chance to catch it. The exception surfaces at the call site instead, which is
        /// `Telemetry.Sample`, and it would take out every other instrument in the file along with this one.
        ///
        /// Same failure mode the PMC session's `TryEnable` guards against for Harmony registrations
        /// (COORDINATION.md, 2026-07-27); nothing here is a patch, so it needs its own version.
        /// </summary>
        private static bool _fatal;
        private static void Fatal(Exception e, string where)
        {
            _fatal = true;
            try
            {
                Plugin.LogSource.LogError(
                    "Framesaver GPU: disabled after an unrecoverable error in " + where + " - " + e);
            }
            catch (Exception)
            {
                // Never take the game down over telemetry, including over failing to log about telemetry.
            }
        }

        /// <summary>
        /// Called once per sampled frame from Telemetry.Sample. Each source guards itself; the outer catch is
        /// for what those guards structurally cannot see.
        /// </summary>
        internal static void Sample()
        {
            if (_fatal || !Plugin.GpuTelemetryEnabled.Value)
            {
                return;
            }

            try
            {
                SampleVram();
                SampleFrameTiming();
                SampleRenderCounters();
            }
            catch (Exception e)
            {
                Fatal(e, "Sample");
            }
        }

        private static void SampleVram()
        {
            if (_vramState == SourceState.Unavailable)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextVramSample)
            {
                return;
            }

            _nextVramSample = now + VramIntervalSeconds;

            try
            {
                // Exist, not Instance: the Instance getter constructs a CameraClass when there is not one
                // already, so reading it from the menu would have telemetry bringing a game singleton into
                // existence early. Never observe through an accessor that can create.
                if (!EFT.CameraControl.CameraManager.Exist)
                {
                    // Not a failure - retry next interval.
                    return;
                }

                EFT.CameraControl.CameraManager camera = EFT.CameraControl.CameraManager.Instance;

                long start = Stopwatch.GetTimestamp();
                ulong total, budget, used;
                camera.GetVRamUsage(out total, out budget, out used);
                double queryMs = AiTiming.ToMs(Stopwatch.GetTimestamp() - start);

                if (queryMs > _vramQueryMsMax)
                {
                    _vramQueryMsMax = queryMs;
                }

                // GetVRamUsage is null-safe on its own wrapper and returns zeros when the native plugin never
                // initialised, which is indistinguishable from a failed query. Treat all-zero as not available
                // rather than reporting a 0 MB budget as if it were real.
                if (total == 0UL && budget == 0UL && used == 0UL)
                {
                    return;
                }

                _vramState = SourceState.Live;

                const double ToMb = 1024d * 1024d;
                double usedMb = used / ToMb;
                double budgetMb = budget / ToMb;

                _vramTotalMb = total / ToMb;
                _lastVramUsedMb = usedMb;
                _lastVramBudgetMb = budgetMb;
                _vramUsedMb.Add(usedMb);
                _vramBudgetMb.Add(budgetMb);

                // The budget is what the driver is currently willing to give this process, and it moves as
                // other applications take and release memory. Overshoot is the signal, not absolute usage.
                if (budgetMb > 0d && usedMb > budgetMb)
                {
                    _overBudgetSamples++;
                    double over = usedMb - budgetMb;
                    if (over > _overBudgetWorstMb)
                    {
                        _overBudgetWorstMb = over;
                    }
                }
            }
            catch (Exception e)
            {
                Disable(ref _vramState, ref _vramError, e, "vram");
            }
        }

        private static void SampleFrameTiming()
        {
            if (_frameTimingState == SourceState.Unavailable)
            {
                return;
            }

            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _timings) == 0)
                {
                    ProbeFailed();
                    return;
                }

                FrameTiming t = _timings[0];

                // GPU timings lag the main thread by a frame or two, so consecutive calls routinely return the
                // same frame. Without this the window averages would be weighted by how often a frame happened
                // to be re-read rather than by the frames themselves.
                if (t.cpuTimePresentCalled == _lastPresentStamp)
                {
                    return;
                }

                _lastPresentStamp = t.cpuTimePresentCalled;

                if (t.gpuFrameTime <= 0d)
                {
                    // The manager exists and hands back records, but the GPU column is empty - which is what an
                    // unflagged build looks like. Everything else in the record is CPU-side and still useful,
                    // so this only counts as a probe failure while the whole source is still on trial.
                    ProbeFailed();
                }
                else
                {
                    _frameTimingState = SourceState.Live;
                }

                _lastGpuFrameMs = t.gpuFrameTime;
                _lastPresentWaitMs = t.cpuMainThreadPresentWaitTime;

                _gpuFrame.Add(t.gpuFrameTime);
                _presentWait.Add(t.cpuMainThreadPresentWaitTime);
                _renderThread.Add(t.cpuRenderThreadFrameTime);
                _cpuFrame.Add(t.cpuFrameTime);
            }
            catch (Exception e)
            {
                Disable(ref _frameTimingState, ref _frameTimingError, e, "frameTiming");
            }
        }

        /// <summary>
        /// Frame Timing Stats is a baked player setting, so "no data yet" and "never any data" look identical
        /// for the first few frames. Give it a window before writing the source off.
        /// </summary>
        private static void ProbeFailed()
        {
            if (_frameTimingState == SourceState.Live)
            {
                return;
            }

            if (++_frameTimingProbes >= FrameTimingProbeFrames)
            {
                _frameTimingState = SourceState.Unavailable;
                _frameTimingError = "no gpu timings after " + FrameTimingProbeFrames
                                    + " frames (Frame Timing Stats not enabled in this build)";
                Plugin.LogSource.LogInfo("Framesaver GPU: frameTiming unavailable - " + _frameTimingError);
            }
        }

        private static void SampleRenderCounters()
        {
            if (_renderState == SourceState.Unavailable)
            {
                return;
            }

            try
            {
                if (_renderState == SourceState.Untested)
                {
                    _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                    _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
                    _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");

                    if (!_drawCalls.Valid)
                    {
                        // A release player without ENABLE_PROFILER hands back recorders that never become
                        // valid. Release them rather than leaving three dead handles allocated.
                        Shutdown();
                        _renderState = SourceState.Unavailable;
                        _renderError = "profiler counters not available in this build";
                        Plugin.LogSource.LogInfo("Framesaver GPU: render counters unavailable - " + _renderError);
                        return;
                    }

                    _renderState = SourceState.Live;
                    Plugin.LogSource.LogInfo("Framesaver GPU: render counters live");
                }

                if (_drawCalls.Valid)
                {
                    _drawCallStat.Add(_drawCalls.LastValue);
                }

                if (_setPass.Valid)
                {
                    _setPassStat.Add(_setPass.LastValue);
                }

                if (_triangles.Valid)
                {
                    _triangleStat.Add(_triangles.LastValue);
                }
            }
            catch (Exception e)
            {
                Disable(ref _renderState, ref _renderError, e, "render");
            }
        }

        private static void Disable(ref SourceState state, ref string error, Exception e, string label)
        {
            state = SourceState.Unavailable;
            error = e.GetType().Name + ": " + e.Message;
            Plugin.LogSource.LogWarning("Framesaver GPU: " + label + " disabled - " + error);
        }

        /// <summary>
        /// The per-window block. Sources that never came up are reported by name rather than omitted, so a
        /// missing number is distinguishable from a zero one.
        /// </summary>
        /// <summary>
        /// Rolls the StringBuilder back to where it was if the body throws. A half-written field would make
        /// the whole line invalid JSON and cost the window that contains it, which is a worse outcome than
        /// losing this block.
        /// </summary>
        private static void Guarded(StringBuilder sb, Action<StringBuilder> body, string where)
        {
            if (_fatal)
            {
                return;
            }

            int mark = sb.Length;
            try
            {
                body(sb);
            }
            catch (Exception e)
            {
                sb.Length = mark;
                Fatal(e, where);
            }
        }

        private static readonly Action<StringBuilder> WindowBody = AppendWindowCore;
        private static readonly Action<StringBuilder> GraphicsBody = AppendGraphicsConfigCore;
        private static readonly Action<StringBuilder> HeaderBody = AppendHeaderCore;

        internal static void AppendWindow(StringBuilder sb)
        {
            Guarded(sb, WindowBody, "AppendWindow");
        }

        private static void AppendWindowCore(StringBuilder sb)
        {
            if (!Plugin.GpuTelemetryEnabled.Value)
            {
                return;
            }

            AppendSettingsDumpOnce(sb);

            sb.Append(",\"gpu\":{");

            sb.Append("\"vram\":");
            if (_vramState == SourceState.Live)
            {
                sb.Append("{\"usedMb\":{\"avg\":").Append(Fmt(_vramUsedMb.Average))
                  .Append(",\"min\":").Append(Fmt(_vramUsedMb.Min))
                  .Append(",\"max\":").Append(Fmt(_vramUsedMb.Max)).Append('}')
                  .Append(",\"budgetMb\":").Append(Fmt(_vramBudgetMb.Average))
                  .Append(",\"totalMb\":").Append(Fmt(_vramTotalMb))
                  // The two fields to watch. Any non-zero overBudget is the driver evicting, and eviction
                  // stutter is invisible to every other instrument in this file.
                  .Append(",\"overBudget\":").Append(_overBudgetSamples)
                  .Append(",\"overBudgetMaxMb\":").Append(Fmt(_overBudgetWorstMb))
                  .Append(",\"samples\":").Append(_vramUsedMb.Count)
                  .Append(",\"queryMsMax\":").Append(Fmt(_vramQueryMsMax))
                  .Append('}');
            }
            else
            {
                AppendUnavailable(sb, _vramState, _vramError);
            }

            sb.Append(",\"frameTiming\":");
            if (_frameTimingState == SourceState.Live)
            {
                sb.Append("{\"gpuMs\":{\"avg\":").Append(Fmt(_gpuFrame.Average))
                  .Append(",\"max\":").Append(Fmt(_gpuFrame.Max)).Append('}')
                  .Append(",\"presentWaitMs\":{\"avg\":").Append(Fmt(_presentWait.Average))
                  .Append(",\"max\":").Append(Fmt(_presentWait.Max)).Append('}')
                  .Append(",\"renderThreadMs\":{\"avg\":").Append(Fmt(_renderThread.Average))
                  .Append(",\"max\":").Append(Fmt(_renderThread.Max)).Append('}')
                  .Append(",\"cpuMs\":").Append(Fmt(_cpuFrame.Average))
                  .Append(",\"samples\":").Append(_gpuFrame.Count)
                  .Append('}');
            }
            else
            {
                AppendUnavailable(sb, _frameTimingState, _frameTimingError);
            }

            sb.Append(",\"render\":");
            if (_renderState == SourceState.Live)
            {
                sb.Append("{\"drawCalls\":{\"avg\":").Append(Fmt(_drawCallStat.Average))
                  .Append(",\"max\":").Append(Fmt(_drawCallStat.Max)).Append('}')
                  .Append(",\"setPass\":").Append(Fmt(_setPassStat.Average))
                  .Append(",\"triangles\":").Append(Fmt(_triangleStat.Average))
                  .Append('}');
            }
            else
            {
                AppendUnavailable(sb, _renderState, _renderError);
            }

            sb.Append('}');
        }

        private static void AppendUnavailable(StringBuilder sb, SourceState state, string error)
        {
            if (state == SourceState.Untested)
            {
                sb.Append("\"pending\"");
                return;
            }

            sb.Append('"').Append(Escape(error ?? "unavailable")).Append('"');
        }

        /// <summary>
        /// Compact form for spike lines. Reads only latched values - a spike line must not trigger a DXGI
        /// query, both for cost and because it would sample a different instant from the phases it sits with.
        /// </summary>
        internal static void AppendSpike(StringBuilder sb)
        {
            if (_fatal || !Plugin.GpuTelemetryEnabled.Value)
            {
                return;
            }

            if (_frameTimingState == SourceState.Live)
            {
                sb.Append(",\"gpuMs\":").Append(Fmt(_lastGpuFrameMs))
                  .Append(",\"presentWaitMs\":").Append(Fmt(_lastPresentWaitMs));
            }

            if (_vramState == SourceState.Live)
            {
                sb.Append(",\"vramUsedMb\":").Append(Fmt(_lastVramUsedMb))
                  .Append(",\"vramBudgetMb\":").Append(Fmt(_lastVramBudgetMb));
            }
        }

        /// <summary>
        /// Graphics state on every window line. These are not our config, but they change what every other
        /// number in the file means, and EFT's settings are editable mid-session from the graphics tab - so a
        /// header written at load can lie about them for exactly the same reason the BepInEx block can.
        /// Reflex in particular rewrites targetFrameRate and vSyncCount when it is switched on.
        /// </summary>
        internal static void AppendGraphicsConfig(StringBuilder sb)
        {
            Guarded(sb, GraphicsBody, "AppendGraphicsConfig");
        }

        private static void AppendGraphicsConfigCore(StringBuilder sb)
        {
            sb.Append(",\"gfx\":{");
            sb.Append("\"screen\":\"").Append(Screen.width).Append('x').Append(Screen.height).Append('"');

            // Internal render resolution, which is what the GPU actually shades. With FSR3 Balanced this is
            // 0.588x per axis, so it is roughly a third of the pixels the screen resolution implies - and
            // reading frame times against the wrong one of those two is how a GPU-bound conclusion gets made
            // for a config that is nowhere near GPU-bound.
            try
            {
                Camera cam = EFT.CameraControl.CameraManager.Exist ? EFT.CameraControl.CameraManager.Instance.Camera : null;
                if (cam != null)
                {
                    sb.Append(",\"render\":\"").Append(cam.pixelWidth).Append('x').Append(cam.pixelHeight).Append('"');
                }
            }
            catch (Exception)
            {
                // Camera rig not up yet; the screen resolution above is still worth having.
            }

            sb.Append(",\"vSyncCount\":").Append(QualitySettings.vSyncCount);
            sb.Append(",\"targetFps\":").Append(Application.targetFrameRate);
            sb.Append(",\"mipLimit\":").Append(QualitySettings.globalTextureMipmapLimit);
            sb.Append(",\"lodBias\":").Append(Fmt(QualitySettings.lodBias));

            try
            {
                EFT.Settings.Graphics.GraphicsSettingsGroup g = GraphicsSettings();
                if (g != null)
                {
                    sb.Append(",\"reflex\":\"").Append(g.NVidiaReflex.Value).Append('"');
                    sb.Append(",\"textureQuality\":").Append(g.TextureQuality.Value);
                    sb.Append(",\"mipStreaming\":").Append(g.MipStreaming.Value ? "true" : "false");
                    sb.Append(",\"dlss\":\"").Append(g.DLSSMode.Value).Append('"');
                    sb.Append(",\"fsr2\":\"").Append(g.FSR2Mode.Value).Append('"');
                    sb.Append(",\"fsr3\":\"").Append(g.FSR3Mode.Value).Append('"');
                    sb.Append(",\"aa\":\"").Append(g.AntiAliasing.Value).Append('"');
                }
            }
            catch (Exception)
            {
                // Settings singleton not up yet. The QualitySettings values above are read straight from Unity
                // and are always valid, so a partial block beats no block.
            }

            sb.Append('}');
        }

        /// <summary>
        /// Full graphics dump, written once. Everything here is either immutable for the session or too
        /// verbose to repeat per line; the mutable subset lives in AppendGraphicsConfig.
        /// </summary>
        internal static void AppendHeader(StringBuilder sb)
        {
            Guarded(sb, HeaderBody, "AppendHeader");
        }

        private static void AppendHeaderCore(StringBuilder sb)
        {
            sb.Append(",\"gpuDevice\":{");
            sb.Append("\"name\":\"").Append(Escape(SystemInfo.graphicsDeviceName)).Append('"');
            sb.Append(",\"api\":\"").Append(Escape(SystemInfo.graphicsDeviceType.ToString())).Append('"');
            sb.Append(",\"driver\":\"").Append(Escape(SystemInfo.graphicsDeviceVersion)).Append('"');
            sb.Append(",\"vramMb\":").Append(SystemInfo.graphicsMemorySize);
            sb.Append(",\"multiThreaded\":").Append(SystemInfo.graphicsMultiThreaded ? "true" : "false");
            sb.Append('}');

            // Deliberately not the graphics settings: the settings singleton does not exist yet at plugin load,
            // so a dump written here says only "not instantiated". AppendSettingsDumpOnce puts it on the first
            // window line that can actually resolve it.
        }

        private static bool _settingsDumped;

        /// <summary>
        /// The full graphics dump, emitted once on the first window line where the settings singleton exists.
        /// Everything here is either immutable for the session or too verbose to repeat per line; the mutable
        /// subset that matters lives in AppendGraphicsConfig and goes on every line.
        /// </summary>
        private static void AppendSettingsDumpOnce(StringBuilder sb)
        {
            if (_settingsDumped)
            {
                return;
            }

            EFT.Settings.Graphics.GraphicsSettingsGroup probe = null;
            try
            {
                probe = GraphicsSettings();
            }
            catch (Exception)
            {
                // Retry on the next window.
            }

            if (probe == null)
            {
                return;
            }

            _settingsDumped = true;

            sb.Append(",\"gfxSettings\":{");
            try
            {
                EFT.Settings.Graphics.GraphicsSettingsGroup g = probe;
                {
                    sb.Append("\"textureQuality\":").Append(g.TextureQuality.Value);
                    sb.Append(",\"mipStreaming\":").Append(g.MipStreaming.Value ? "true" : "false");
                    sb.Append(",\"mipStreamingBufferSize\":").Append(g.MipStreamingBufferSize.Value);
                    sb.Append(",\"shadowsQuality\":").Append(g.ShadowsQuality.Value);
                    // ShadowDistance and SuperSamplingFactor are plain derived properties on the settings
                    // object, not GameSetting<T> bindables like the rest.
                    sb.Append(",\"shadowDistance\":").Append(Fmt(g.ShadowDistance));
                    sb.Append(",\"overallVisibility\":").Append(Fmt(g.OverallVisibility.Value));
                    sb.Append(",\"lodBias\":").Append(Fmt(g.LodBias.Value));
                    sb.Append(",\"superSamplingFactor\":").Append(Fmt(g.SuperSamplingFactor));
                    sb.Append(",\"vSync\":").Append(g.VSync.Value ? "true" : "false");
                    sb.Append(",\"gameFramerate\":").Append(g.GameFramerate.Value);
                    sb.Append(",\"reflex\":\"").Append(g.NVidiaReflex.Value).Append('"');
                }
            }
            catch (Exception e)
            {
                sb.Append("\"error\":\"").Append(Escape(e.GetType().Name + ": " + e.Message)).Append('"');
            }

            sb.Append('}');
        }

        // Deliberately not probed: GClass3692.IsReflexAvailable(). It looks like a free capability query and is
        // not one - it latches a static Bool_0 on any error or NvReflex_ERROR status, and GClass3692 short
        // circuits to "unavailable" forever once that is set (only Dispose, on camera teardown, clears it).
        // Probing it early enough to put in the header would mean a failed probe silently preventing Reflex
        // from ever initialising - disabling the feature we want to measure, in the file that measures it.
        // The `reflex` setting value plus whether frame reports ever appear answers the same question safely.

        private static EFT.Settings.Graphics.GraphicsSettingsGroup GraphicsSettings()
        {
            // 4.1: SharedGameSettingsClass survives as EFT.Settings.SettingsManager; Graphics is a
            // SettingsWithController<GraphicsSettingsGroup, ...> whose inherited `.Settings` field is
            // the group this returns. Reached through Singleton - if SettingsManager does not register
            // itself there, Instantiated stays false and this yields null, which the callers already treat
            // as "no settings yet" rather than an error.
            if (!Singleton<EFT.Settings.SettingsManager>.Instantiated)
            {
                return null;
            }

            EFT.Settings.SettingsManager shared = Singleton<EFT.Settings.SettingsManager>.Instance;
            return shared != null && shared.Graphics != null ? shared.Graphics.Settings : null;
        }

        internal static void ResetWindow()
        {
            _vramUsedMb.Reset();
            _vramBudgetMb.Reset();
            _overBudgetSamples = 0;
            _overBudgetWorstMb = 0d;
            _vramQueryMsMax = 0d;

            _gpuFrame.Reset();
            _presentWait.Reset();
            _renderThread.Reset();
            _cpuFrame.Reset();

            _drawCallStat.Reset();
            _setPassStat.Reset();
            _triangleStat.Reset();
        }

        /// <summary>ProfilerRecorder holds an unmanaged handle; it does not clean itself up.</summary>
        internal static void Shutdown()
        {
            DisposeRecorder(ref _drawCalls);
            DisposeRecorder(ref _setPass);
            DisposeRecorder(ref _triangles);
        }

        private static void DisposeRecorder(ref ProfilerRecorder recorder)
        {
            try
            {
                if (recorder.Valid)
                {
                    recorder.Dispose();
                }
            }
            catch (Exception)
            {
                // Shutdown path; a failure here must not stop the remaining handles being released.
            }

            recorder = default(ProfilerRecorder);
        }

        private static string Fmt(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "null";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Local copy of Telemetry's accumulator. Duplicated rather than shared because Telemetry.Stat is
        /// private to that class and widening it for this would expose it to every patch in the mod.
        /// </summary>
        private class Stat
        {
            public int Count;
            private double _sum;
            private double _min = double.MaxValue;
            private double _max = double.MinValue;

            /// <summary>
            /// Zero rather than the sentinel when nothing was recorded. These are sampled on a timer, not per
            /// frame, so a window shortened by a state transition can legitimately contain no sample at all -
            /// and the raw sentinels serialise as 309-digit numbers that break any consumer parsing the line.
            /// Read `samples` to tell "no data" from "genuinely zero".
            /// </summary>
            public double Min
            {
                get { return Count > 0 ? _min : 0d; }
            }

            public double Max
            {
                get { return Count > 0 ? _max : 0d; }
            }

            public double Average
            {
                get { return Count > 0 ? _sum / Count : 0d; }
            }

            public void Add(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    return;
                }

                Count++;
                _sum += value;

                if (value < _min)
                {
                    _min = value;
                }

                if (value > _max)
                {
                    _max = value;
                }
            }

            public void Reset()
            {
                Count = 0;
                _sum = 0d;
                _min = double.MaxValue;
                _max = double.MinValue;
            }
        }
    }
}
