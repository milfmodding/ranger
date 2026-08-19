using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Comfort.Common;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Ranger
{
    /// <summary>
    /// GPU-TELEMETRY SPLIT (ruled by Sophia 2026-08-17 07:13Z; register reg-dec-2026-08-17T071420):
    /// the three GPU instruments this file used to carry - the vram DXGI query, the FrameTiming
    /// source (which never fired: "Frame Timing Stats not enabled" in every log of this build),
    /// and the ProfilerRecorder render counters - are ARCHIVED. They found nothing, the build is
    /// CPU-bound (the delta-render-cpu-or-gpu analysis), and they cost complexity in the shipping
    /// surface. History keeps them; see the pre-split GpuTelemetry.cs in git.
    ///
    /// Two things SURVIVED the split, and both are load-bearing rather than nice-to-have:
    ///
    ///   Qpc()/QpcFrequency()   the wall clock on every telemetry line. NOT GPU telemetry: it is
    ///                           the join key for the analysis harness (read-marks.py joins marks
    ///                           to spikes on qpc; alpha-ledger-reconcile.py assigns events to
    ///                           windows by qpc containment) and for any external capture series.
    ///                           Stopwatch under Mono is process-relative and does not overlap a
    ///                           real QPC series - see the war story on Qpc().
    ///   gfx / gpuDevice /      graphics CONFIG CONTEXT, not measurement. Render resolution vs
    ///   gfxSettings            screen is how a GPU-bound conclusion gets made for a config that
    ///                           is nowhere near GPU-bound; Reflex rewrites targetFrameRate and
    ///                           vSyncCount mid-session, so a header written at load can lie.
    ///
    /// AppendWindow survives as a shell carrying only the once-per-session gfxSettings dump, so
    /// the capstone Telemetry compiles against the same surface either way (the gpu window block
    /// and the spike gpuMs/vram fields are gone with the instruments).
    /// </summary>
    internal static class GpuTelemetry
    {
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
                        "Ranger: QueryPerformanceCounter unavailable; qpc falls back to Stopwatch, "
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

        // ---- graphics config context (survived the split) -----------------------------------

        /// <summary>
        /// JObject conversion (2026-08-19, sub-module pass): wraps a build body so a failure in
        /// graphics-config reading cannot corrupt the line it contributes to - the original
        /// StringBuilder version had to protect against an unterminated quote swallowing every
        /// field after it; a JObject cannot half-write in the same way, but a thrown exception mid-
        /// build would still lose the whole fragment, so the same try/catch-and-report shape is
        /// kept for continuity of behaviour (gfxErr appears on the returned object, not spliced into
        /// a shared buffer).
        /// </summary>
        private static JObject Guarded(Func<JObject> body, string where)
        {
            try
            {
                return body();
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogWarning("Ranger: " + where + " failed - " + e.Message);
                JObject err = new JObject();
                err["gfxErr"] = e.GetType().Name;
                return err;
            }
        }

        private static readonly Func<JObject> WindowBody = AppendWindowCore;
        private static readonly Func<JObject> GraphicsBody = AppendGraphicsConfigCore;
        private static readonly Func<JObject> HeaderBody = AppendHeaderCore;

        /// <summary>
        /// Per-window append point. Since the split this carries ONLY the once-per-session gfxSettings
        /// dump (first window line where the settings singleton exists); the gpu instrument block that
        /// used to live here is archived with the instruments. Returns a JObject whose OWN FIELDS are
        /// meant to be merged into the caller's window object (matching every other Append* method's
        /// comma-first-fragment convention, now expressed as "the returned object's top-level keys"
        /// instead of raw text) - an empty JObject when nothing was dumped this call, same as the
        /// StringBuilder version appending nothing.
        /// </summary>
        internal static JObject AppendWindow()
        {
            return Guarded(WindowBody, "AppendWindow");
        }

        private static JObject AppendWindowCore()
        {
            JObject obj = new JObject();
            AppendSettingsDumpOnce(obj);
            return obj;
        }

        private static bool _settingsDumped;

        /// <summary>
        /// The full graphics dump, emitted once on the first window line where the settings singleton exists.
        /// Everything here is either immutable for the session or too verbose to repeat per line; the mutable
        /// subset that matters lives in AppendGraphicsConfig and goes on every line. JObject counterpart:
        /// merges "gfxSettings" into the caller's object exactly once (guarded by the same _settingsDumped
        /// latch as before), and leaves it absent entirely on every call before that - never a null or
        /// empty placeholder key, since "not dumped yet" and "dumped as empty" are different facts.
        /// </summary>
        private static void AppendSettingsDumpOnce(JObject obj)
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

            JObject gfxSettings = new JObject();
            try
            {
                EFT.Settings.Graphics.GraphicsSettingsGroup g = probe;
                {
                    gfxSettings["textureQuality"] = g.TextureQuality.Value;
                    gfxSettings["mipStreaming"] = g.MipStreaming.Value;
                    gfxSettings["mipStreamingBufferSize"] = g.MipStreamingBufferSize.Value;
                    gfxSettings["shadowsQuality"] = g.ShadowsQuality.Value;
                    // ShadowDistance and SuperSamplingFactor are plain derived properties on the settings
                    // object, not GameSetting<T> bindables like the rest.
                    gfxSettings["shadowDistance"] = FmtToken(g.ShadowDistance);
                    gfxSettings["overallVisibility"] = FmtToken(g.OverallVisibility.Value);
                    gfxSettings["lodBias"] = FmtToken(g.LodBias.Value);
                    gfxSettings["superSamplingFactor"] = FmtToken(g.SuperSamplingFactor);
                    gfxSettings["vSync"] = g.VSync.Value;
                    gfxSettings["gameFramerate"] = g.GameFramerate.Value;
                    gfxSettings["reflex"] = g.NVidiaReflex.Value.ToString();
                }
            }
            catch (Exception e)
            {
                gfxSettings = new JObject();
                gfxSettings["error"] = e.GetType().Name + ": " + e.Message;
            }

            obj["gfxSettings"] = gfxSettings;
        }

        // Deliberately not probed: GClass3692.IsReflexAvailable(). It looks like a free capability query and is
        // not one - it latches a static Bool_0 on any error or NvReflex_ERROR status, and GClass3692 short
        // circuits to "unavailable" forever once that is set (only Dispose, on camera teardown, clears it).
        // Probing it early enough to put in the header would mean a failed probe silently preventing Reflex
        // from ever initialising. The `reflex` setting value answers the same question safely.

        /// <summary>
        /// Graphics state on every window line. These are not our config, but they change what every other
        /// number in the file means, and EFT's settings are editable mid-session from the graphics tab - so a
        /// header written at load can lie about them. Reflex in particular rewrites targetFrameRate and
        /// vSyncCount when it is switched on. Returns a JObject whose "gfx" field is meant to be merged into
        /// the caller's window object, same convention as AppendWindow.
        /// </summary>
        internal static JObject AppendGraphicsConfig()
        {
            return Guarded(GraphicsBody, "AppendGraphicsConfig");
        }

        private static JObject AppendGraphicsConfigCore()
        {
            JObject obj = new JObject();
            JObject gfx = new JObject();
            gfx["screen"] = Screen.width + "x" + Screen.height;

            // Internal render resolution, which is what the GPU actually shades. With FSR3 Balanced this is
            // 0.588x per axis, so it is roughly a third of the pixels the screen resolution implies - and
            // reading frame times against the wrong one of those two is how a GPU-bound conclusion gets made
            // for a config that is nowhere near GPU-bound.
            try
            {
                Camera cam = EFT.CameraControl.CameraManager.Exist ? EFT.CameraControl.CameraManager.Instance.Camera : null;
                if (cam != null)
                {
                    gfx["render"] = cam.pixelWidth + "x" + cam.pixelHeight;
                }
            }
            catch (Exception)
            {
                // Camera rig not up yet; the screen resolution above is still worth having.
            }

            gfx["vSyncCount"] = QualitySettings.vSyncCount;
            gfx["targetFps"] = Application.targetFrameRate;
            gfx["mipLimit"] = QualitySettings.globalTextureMipmapLimit;
            gfx["lodBias"] = FmtToken(QualitySettings.lodBias);

            try
            {
                EFT.Settings.Graphics.GraphicsSettingsGroup g = GraphicsSettings();
                if (g != null)
                {
                    gfx["reflex"] = g.NVidiaReflex.Value.ToString();
                    gfx["textureQuality"] = g.TextureQuality.Value;
                    gfx["mipStreaming"] = g.MipStreaming.Value;
                    gfx["dlss"] = g.DLSSMode.Value.ToString();
                    gfx["fsr2"] = g.FSR2Mode.Value.ToString();
                    gfx["fsr3"] = g.FSR3Mode.Value.ToString();
                    gfx["aa"] = g.AntiAliasing.Value.ToString();
                }
            }
            catch (Exception)
            {
                // Settings singleton not up yet. The QualitySettings values above are read straight from Unity
                // and are always valid, so a partial block beats no block.
            }

            obj["gfx"] = gfx;
            return obj;
        }

        /// <summary>
        /// Device identity for the header. Immutable for the session, so once per file is enough.
        /// Returns a JObject whose "gpuDevice" field is meant to be merged into the caller's header
        /// object, same convention as AppendWindow/AppendGraphicsConfig.
        /// </summary>
        internal static JObject AppendHeader()
        {
            return Guarded(HeaderBody, "AppendHeader");
        }

        private static JObject AppendHeaderCore()
        {
            JObject obj = new JObject();
            JObject gpuDevice = new JObject();
            gpuDevice["name"] = SystemInfo.graphicsDeviceName ?? "";
            gpuDevice["api"] = SystemInfo.graphicsDeviceType.ToString();
            gpuDevice["driver"] = SystemInfo.graphicsDeviceVersion ?? "";
            gpuDevice["vramMb"] = SystemInfo.graphicsMemorySize;
            gpuDevice["multiThreaded"] = SystemInfo.graphicsMultiThreaded;
            obj["gpuDevice"] = gpuDevice;

            // Deliberately not the graphics settings: the settings singleton does not exist yet at plugin load,
            // so a dump written here says only "not instantiated". AppendSettingsDumpOnce puts it on the first
            // window line that can actually resolve it.
            return obj;
        }

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

        /// <summary>
        /// JObject counterpart to the retired StringBuilder-facing Fmt(double) - same shape as
        /// Telemetry.FmtToken, duplicated here rather than shared for the same reason every other
        /// JObject-building file in this codebase duplicates it: no shared numeric-formatting base
        /// class exists, and one is not worth introducing for a single static method. Returns a
        /// real JValue rather than a string, so NaN/Infinity become the bare JSON token null instead
        /// of the quoted string "null".
        /// </summary>
        private static JToken FmtToken(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return JValue.CreateNull();
            }

            return new JValue(value);
        }
    }
}
