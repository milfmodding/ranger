using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using Framesaver.Patches;
using UnityEngine;
using UnityEngine.Scripting;

namespace Framesaver
{
    /// <summary>
    /// Samples once per frame and appends a newline-delimited JSON summary per window, plus one line per
    /// slow frame.
    ///
    /// Deliberately narrow. Fields that answered their question and were then refuted have been removed
    /// rather than left to accumulate - render/update/fixedUpdate (duplicated by the player-loop phases),
    /// the AI brain split, VisualPass, GameWorld.LateUpdate, camera count, fixedSteps, the
    /// drain budget's defer/truncate counters, ForceExecuteContinuations, the shell sweep, weapon audio,
    /// and the bundle dependency graph. FINDINGS.md records what each of them showed.
    ///
    /// What is kept is either still open, guards a confirmed fix against regression, or is a headline
    /// number:
    ///
    ///   frame / gameUpdate  GClass1357, the same counters `fps 3` draws. gameUpdate excludes the
    ///                       frame-limiter sleep and is the metric to trust.
    ///   phases              player-loop breakdown; the workhorse, and it subsumes the dropped measurers
    ///   asyncUpdateDrain    the bot/generate completion - 81% of in-raid spike time
    ///   profileBuild        where that completion's time goes, section by section
    ///   playerLate/Tick     evidence the sleeping-bot skips are still working
    ///   bots                awake/asleep split; animCulled guards the animator cull
    ///   jobQueue / jobSchedulerLate   continuation backlog, still open
    /// </summary>
    public class Telemetry : MonoBehaviour
    {
        private const string TimeFormat = "yyyyMMdd-HHmmss";

        private readonly Stat _frame = new Stat();
        private readonly Stat _gameUpdate = new Stat();
        private readonly Stat _jobQueue = new Stat();
        private readonly Stat _aiTotal = new Stat();

        private readonly Stat _heapMb = new Stat();
        private readonly Stat _jobSched = new Stat();
        private readonly Stat _ambientLight = new Stat();
        private readonly Stat _asyncUpdate = new Stat();
        private readonly Stat _asyncFixed = new Stat();
        private readonly Stat _asyncDrained = new Stat();
        private readonly Stat _playerLate = new Stat();
        private readonly Stat _playerTick = new Stat();
        private Stat[] _phases;
        private float _nextLoopCheck;
        private float _vanillaMaxDeltaTime;

        // Raw samples so the window can report percentiles. A fixed spike threshold turned out to be a poor
        // stutter metric: 16ms is 1.7x the mean on Customs but only 1.3x on Streets, so the same number means
        // very different things per map. p99/p50 is scale-free.
        private readonly List<double> _gameUpdateSamples = new List<double>(8192);
        private readonly List<double> _frameSamples = new List<double>(8192);

        // Brains ticked, summed over the window rather than read once at flush.
        //
        // `LastBrainsTicked` is overwritten every frame and its slicing value is `perFrame`, which divides
        // by `Time.deltaTime` - so a slow frame ticks MORE brains. Sampling it once per window would
        // correlate the measurement with frame time, which is the quantity the A/B is testing. Summing
        // removes the correlation and keeps the counts re-derivable.
        //
        // `_liveSum` exists so the ratio has a matching denominator. Dividing a window sum by the
        // last frame's `live` would mix two populations, and the roster changes across a window.
        // Both are divided by `n`, which is the frame count these were accumulated under.
        private long _tickedSum;
        private long _liveSum;

        // Frame times for the mark lookback, in a ring that outlives ResetWindow.
        //
        // A mark is pressed BECAUSE something just happened, so the frames worth dumping are the ones
        // immediately before the press. Reading them from `_frameSamples` would lose exactly those
        // whenever the press lands early in a window, because ResetWindow clears it - and that loss
        // correlates with the mark being worth making rather than falling randomly. Same shape as
        // sampling LastBrainsTicked at flush, and the reason this is a ring rather than a caveat is
        // that a caveat has to be remembered by every future reader while a ring does not.
        //
        // No timestamps: frame times ARE durations, so summing backwards until the total reaches the
        // lookback gives the span directly. 1024 frames is ~17 s at 60 fps and ~7 s at 150.
        private readonly double[] _markRing = new double[1024];
        private int _markNext;
        private int _markCount;

        // Not a config entry, because it cannot be set independently of the ring above: a lookback
        // longer than 1024 frames can span would truncate silently, which is the failure the ring
        // exists to remove.
        private const double MarkLookbackMs = 5000d;

        /// <summary>Per-raid mark counter. This is the join key to Sophia's written notes - "Factory
        /// mark 2, mid-fight" needs the ordinal to mean anything - so it resets with the raid, not
        /// with the window or the session.</summary>
        private int _markOrdinal;

        /// <summary>Which regime a window's numbers came from. Menu is only entered once sampling has begun.</summary>
        private enum SessionState
        {
            Menu,
            Loading,
            Raid,
        }

        private SessionState CurrentState()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                return SessionState.Menu;
            }

            if (Singleton<AbstractGame>.Instantiated)
            {
                // GameStatus.Started is the same gate the drain budget uses, so "raid" means the same
                // thing in both places.
                GameStatus status = Singleton<AbstractGame>.Instance.Status;
                if (status == GameStatus.Started)
                {
                    return SessionState.Raid;
                }

                if (status == GameStatus.Stopped || status == GameStatus.Stopping
                    || status == GameStatus.SoftStopping)
                {
                    return SessionState.Menu;
                }

                return SessionState.Loading;
            }

            // A resident GameWorld with no AbstractGame has two meanings, and the singletons alone
            // cannot tell them apart:
            //
            //   loading - the world is built and passed to LocalGame.smethod_6 before
            //             Singleton<AbstractGame>.Create, and that window contains the 37 s
            //             /client/match/local/start stall, so it must keep sampling
            //   menu    - the raid ended and AbstractGame was released while the world stayed resident
            //
            // Gating on AbstractGame alone would fix the menu artifact by blinding the largest loading
            // stall we measure. What separates them is *which* world: each raid builds a new one, so a
            // world we have already sampled a raid in can only be the post-raid menu. Stored as an id
            // rather than a reference - holding a GameWorld here would be the leak shape we just fixed.
            //
            // Known residual: a raid that aborts before reaching GameStatus.Started never latches an id,
            // so the menu after it still reports `loading`. Rare and self-limiting, and much smaller than
            // the artifact this removes - but it makes the fix "most menu idle" rather than "menu idle",
            // and the next reader should not have to rediscover that.
            return Singleton<GameWorld>.Instance.GetInstanceID() == _raidedWorldId
                ? SessionState.Menu
                : SessionState.Loading;
        }

        /// <summary>
        /// Raid clock, matching the readout the O key shows. Returns false outside a running raid.
        /// </summary>
        private static bool TryGetRaidClock(out double elapsed, out double remaining)
        {
            elapsed = 0d;
            remaining = 0d;

            if (!Singleton<AbstractGame>.Instantiated)
            {
                return false;
            }

            GameTimerClass timer = Singleton<AbstractGame>.Instance.GameTimer;
            if (timer == null || timer.StartDateTime == null)
            {
                return false;
            }

            elapsed = timer.PastTime.TotalSeconds;
            remaining = timer.SessionTime != null ? timer.SessionTime.Value.TotalSeconds - elapsed : 0d;
            return true;
        }

        /// <summary>
        /// Emits the raid clock as both seconds and the HH:MM:SS remaining figure the O key shows, so a
        /// reported "it stuttered around 22 minutes left" maps straight onto a line without arithmetic.
        /// </summary>
        /// <summary>
        /// Stamps every line with which raid and which map it came from. Instance method rather than static
        /// because both values are per-session state; kept next to the clock writer since they are always
        /// emitted together.
        /// </summary>
        private void AppendRaidIdentity(StringBuilder sb)
        {
            sb.Append(",\"raid\":").Append(_raid);
            sb.Append(",\"map\":\"").Append(Escape(_map)).Append('"');
        }

        private static void AppendRaidClock(StringBuilder sb)
        {
            double elapsed, remaining;
            if (!TryGetRaidClock(out elapsed, out remaining))
            {
                return;
            }

            sb.Append(",\"raidElapsed\":").Append(Fmt(elapsed))
              .Append(",\"raidLeft\":").Append(Fmt(remaining))
              .Append(",\"raidClock\":\"").Append(Clock(remaining)).Append('"');
        }

        private static string Clock(double seconds)
        {
            bool negative = seconds < 0d;
            if (negative)
            {
                seconds = -seconds;
            }

            int total = (int)seconds;
            return (negative ? "-" : "") + (total / 3600).ToString("00", CultureInfo.InvariantCulture)
                   + ":" + (total / 60 % 60).ToString("00", CultureInfo.InvariantCulture)
                   + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private string _path;
        private SessionState _state = SessionState.Menu;

        /// <summary>
        /// GetInstanceID of the GameWorld a raid has actually been sampled in. See CurrentState: it is
        /// what separates "world resident, game not built yet" from "world resident, game released".
        /// An id rather than a reference, so nothing here roots a GameWorld.
        /// </summary>
        private int _raidedWorldId;

        // ---- Position ------------------------------------------------------------------------------
        //
        // Location dominates frame time on large maps, and until now every cross-window comparison could
        // only *warn* about it. Six caveats in FINDINGS say "hold position"; nothing checked whether it
        // was held. The knob A/B on 2026-07-28 was rendered uninterpretable by exactly this - p50 drifted
        // 13.9 -> 22.1 -> 14.3 while awake bots sat flat, so the reversal did not reverse.
        //
        // `dist` is the field that matters, not the coordinates. A point sample at flush time answers
        // "where was she when the window ended", which for 60 seconds is nearly useless; the question is
        // "did she move during this window", and that is a distance, not a position. It turns a caveat a
        // reader has to remember into a filter the data enforces.
        private bool _hasPos;
        private Vector3 _lastPos;
        private double _distance;
        private Vector3 _posMin;
        private Vector3 _posMax;
        private int _posSamples;

        // Look accumulators. Separate sample counter from _posSamples deliberately: rotation is read
        // through a different property and can fail on its own, and a shared counter would make a
        // failed look block indistinguishable from a held view.
        private int _lookSamples;
        private float _lastYaw;
        private float _lastPitch;
        private double _yawCum;
        private double _pitchCum;
        private double _yawMin;
        private double _yawMax;
        private double _pitchMin;
        private double _pitchMax;
        private double _yawSwept;
        private double _pitchSwept;

        /// <summary>
        /// GameWorld.LocationId for the raid currently being sampled, and a 1-based counter of raids since
        /// the game launched. One log file spans every raid of a session, so without these two there is no
        /// way to segment it - `window` restarts from 0 each raid and `t` restarts with it.
        ///
        /// Cached rather than read per line because spike lines can be frequent. Resolved lazily: GameWorld
        /// exists before LocationId is populated, so the first loading windows would otherwise be stamped
        /// with an empty string.
        /// </summary>
        private string _map = "";
        private int _raid;

        /// <summary>Gen-0 collections attributable to the frame a spike line describes.</summary>
        private int _gen0PrevFrame;
        private int _gcThisFrame;
        private bool _sampling;
        private float _sampleStart;
        private int _periodSamples;
        private readonly Queue<string> _pending = new Queue<string>();
        private readonly AutoResetEvent _pendingSignal = new AutoResetEvent(false);
        private Thread _writer;
        private volatile bool _writerStop;

        private float _nextWrite;
        private float _windowStart;
        private bool _flushedByProtocol;
        private int _window;
        private int _asyncFixedSkips;

        // GC tracking. Tarkov defers collection aggressively, so a pause can land in any phase - which makes
        // "did a collection happen on this exact frame" the only way to tell a GC pause from slow code.
        private long _lastSampleTicks;
        private double _lastAsyncFixed, _lastAsyncUpdate;
        private int _lastDrained;

        private int _gen0;
        private int _gen0Base;
        private long _lastHeap;
        private double _lastHeapDeltaMb;
        private double _allocatedBytes;

        private void Awake()
        {
            string dir = Path.Combine(PluginDirectory(), "Framesaver-logs");
            Directory.CreateDirectory(dir);

            string tag = Sanitise(Plugin.RunTag.Value);
            string stamp = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
            _path = Path.Combine(dir, "framesaver-" + stamp + (tag.Length > 0 ? "-" + tag : "") + ".ndjson");

            _vanillaMaxDeltaTime = Time.maximumDeltaTime;
            StartWriter();
            WriteHeader();
            Plugin.LogSource.LogInfo("Framesaver telemetry -> " + _path);
        }

        private void Update()
        {
            // Sampling used to start only once GameWorld and the frame measurers both existed, which meant the
            // multi-second continuation stalls during load were timed but never attributed - the diagnostics
            // that name the callback were gated off until the raid was Started. Sampling now runs from plugin
            // load, with `state` on every line saying which regime the numbers came from.
            SessionState state = CurrentState();

            // Before the menu early-return, deliberately. A dead10 deadline that expires as the raid ends
            // would otherwise never fire and its line would vanish - and "the subject was destroyed" is
            // itself the finding, so losing it silently is the one outcome the error line exists to
            // prevent. Both calls are a timestamp compare and an empty-queue check when idle.
            Framesaver.Patches.Census.Tick();
            DrainCensus();

            // Both before the menu early-return, and for the same reason as the two calls above.
            //
            // She marked a major hitch on the intermission screen and it was lost twice over: the key
            // was never read there, and nothing filled the ring either - so the hitch was not merely
            // unmarked, it was unmeasured. Loading and its transitions are goal 2's secondary target
            // and the stalls either side of that intermission ran 1.8 s and 19.9 s, so a mark carrying
            // no frames would be the one place we most need frames.
            //
            // The ring takes Unity's own frame delta rather than BSG's measurer, because that is the
            // only source that exists in every state. It follows that a mark's `frameMs` will not
            // exactly equal `frame` or `framePct`, which are BSG's - the mark answers "what did the
            // last five seconds feel like", not "what did the measurer record".
            _markRing[_markNext] = Time.unscaledDeltaTime * 1000d;
            _markNext = (_markNext + 1) % _markRing.Length;
            if (_markCount < _markRing.Length)
            {
                _markCount++;
            }

            if (Pressed(Plugin.MarkKey.Value))
            {
                WriteMark(state);
            }

            if (state == SessionState.Menu)
            {
                // Back to the menu: close the session out rather than logging idle hideout time forever. A
                // later match creates a new GameWorld and starts a fresh set of windows.
                if (_sampling)
                {
                    Flush(true);
                    _sampling = false;
                    _state = SessionState.Menu;
                }

                return;
            }

            if (!_sampling)
            {
                _sampling = true;
                _sampleStart = Time.realtimeSinceStartup;
                _nextWrite = _sampleStart + Plugin.TelemetryWindow.Value;
                _raid++;
                _map = "";

                // Per-raid state owned elsewhere. SleepingBotAnimatorPatch keys a static dictionary by
                // Player and only removes on a stand-by transition, which never fires at teardown - so
                // without this every raid inherits the previous raid's sleeping bots and animCulled
                // reports them forever. See ResetForRaid.
                Framesaver.Patches.SleepingBotAnimatorPatch.ResetForRaid();
                Framesaver.Patches.Census.ResetForRaid();
                _markOrdinal = 0;
                // Re-reads the file too, so editing a protocol takes effect on the next raid rather
                // than the next launch.
                ProtocolRunner.ResetForRaid();

                ResetWindow();
            }

            // LocationId lands some time after GameWorld does, so keep trying until it takes.
            if (_map.Length == 0)
            {
                GameWorld world = Singleton<GameWorld>.Instance;
                if (world != null && !string.IsNullOrEmpty(world.LocationId))
                {
                    _map = world.LocationId;
                }
            }

            // A window must not straddle two regimes or its averages describe nothing in particular.
            if (state != _state)
            {
                if (_frame.Count > 0 || _periodSamples > 0)
                {
                    Flush(false);
                    _nextWrite = Time.realtimeSinceStartup + Plugin.TelemetryWindow.Value;
                }

                // Latch the world this raid ran in, so the post-raid menu - same world, AbstractGame
                // released - stops being read as a fresh load.
                if (state == SessionState.Raid && Singleton<GameWorld>.Instantiated)
                {
                    _raidedWorldId = Singleton<GameWorld>.Instance.GetInstanceID();
                }

                _state = state;
            }

            // Live-applied so it can be toggled mid-raid like the other experimental flags.
            float wanted = Plugin.MaxDeltaTime.Value > 0f ? Plugin.MaxDeltaTime.Value : _vanillaMaxDeltaTime;
            if (!Mathf.Approximately(Time.maximumDeltaTime, wanted))
            {
                Time.maximumDeltaTime = wanted;
            }

            ApplyJobSchedulerOverrides();
            GcControl.ApplyConfig();
            GcControl.Drive();

            Sample();

            // The game rewrites the player loop during raid load; re-arm if our markers were dropped.
            if (Time.realtimeSinceStartup >= _nextLoopCheck)
            {
                _nextLoopCheck = Time.realtimeSinceStartup + 5f;
                if (Plugin.ProfilePlayerLoop.Value && !PlayerLoopProfiler.MarkersPresent())
                {
                    PlayerLoopProfiler.Install();
                }
            }

            // Before the ordinary roll below, so a press and a timed boundary in the same frame produce
            // one flush rather than two - the second would be an empty window.
            //
            // FLUSH BEFORE ADVANCE, and the order is the whole fix. `Advance()` applies the step's
            // config and increments the step, and `Flush` reads config live - so advancing first
            // stamped the incoming arm's `cfg`, `agents.slicing` and `protocol.arm` onto the outgoing
            // arm's sums. `slicing` is exactly the field a reader trusts to answer "was the lever
            // pulled", and on that one line it answered for the wrong arm.
            //
            // Gated on `CanAdvance` rather than on `Advance()`'s return so the flush does not happen
            // when nothing will change. `Advance()` tests the same property, so the two cannot drift,
            // and it is still called unconditionally below to keep its loud refusal.
            if (Pressed(Plugin.ProtocolKey.Value))
            {
                if (ProtocolRunner.CanAdvance)
                {
                    _flushedByProtocol = true;
                    Flush(false);
                    _nextWrite = Time.realtimeSinceStartup + Plugin.TelemetryWindow.Value;
                }

                ProtocolRunner.Advance();
            }

            if (Time.realtimeSinceStartup >= _nextWrite)
            {
                Flush(false);
                _nextWrite = Time.realtimeSinceStartup + Plugin.TelemetryWindow.Value;
            }
        }

        /// <summary>
        /// Applied live rather than at load, since JobScheduler is recreated per session and the game
        /// rewrites FrameTicks whenever graphics settings change.
        /// </summary>
        private static void ApplyJobSchedulerOverrides()
        {
            if (!Singleton<JobScheduler>.Instantiated)
            {
                return;
            }

            JobScheduler js = Singleton<JobScheduler>.Instance;

            float budget = Plugin.JobSchedulerBudgetMs.Value;
            if (budget > 0f)
            {
                long ticks = (long)(budget * TimeSpan.TicksPerMillisecond);
                if (js.FrameTicks != ticks)
                {
                    js.FrameTicks = ticks;
                }
            }

            int slow = Plugin.JobSchedulerSlowFrames.Value;
            if (slow >= 0 && js.SlowFrames != (byte)slow)
            {
                js.SlowFrames = (byte)slow;
            }
        }

        /// <summary>
        /// Accumulates the main player's movement for the window. Two property reads per frame.
        ///
        /// Distance is summed per frame rather than taken as start-to-end displacement, so a loop that
        /// returns to its origin still reports as movement - which is the case a displacement measure
        /// would call stationary and a frame-time comparison would then trust.
        /// </summary>
        private void SamplePosition()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                return;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            Player player = world != null ? world.MainPlayer : null;
            if (player == null)
            {
                return;
            }

            SampleLook(player);

            Vector3 p = player.Position;

            if (_posSamples == 0)
            {
                _posMin = p;
                _posMax = p;
            }
            else
            {
                _posMin = Vector3.Min(_posMin, p);
                _posMax = Vector3.Max(_posMax, p);
                if (_hasPos)
                {
                    _distance += Vector3.Distance(_lastPos, p);
                }
            }

            _lastPos = p;
            _hasPos = true;
            _posSamples++;
        }

        /// <summary>
        /// Accumulates where the player looked. One property read per frame - Player.Rotation is
        /// (yaw, pitch) in one Vector2, so this costs the same as either axis alone.
        ///
        /// Exists because Protocol B holds the view fixed and varies draw calls, and the log could not
        /// show the view was actually held. The stand-in was `drawCalls.max / .avg` - which is circular
        /// for that experiment specifically, since draw calls are the variable being manipulated:
        /// using their stability to certify the view was held uses the stability of the thing being
        /// varied to certify you held the thing that varies it.
        ///
        /// **Yaw wraps and raw min/max is a trap.** A view held near the wrap point produces samples at
        /// 359.9 and 0.1, whose min/max spans the entire circle - a held view reported as a full sweep,
        /// which is precisely the reading this exists to make trustworthy. So both axes accumulate an
        /// *unwrapped* angle relative to the first sample: each frame's delta is folded into
        /// (-180, 180] before being added. Range and sweep are then both wrap-safe, and `range` means
        /// angular extent rather than "largest value seen minus smallest".
        ///
        /// `swept` is the angular analogue of `dist` and answers the question directly: it sums absolute
        /// per-frame change, so a look away and back still reads as movement where a range would call it
        /// stationary.
        /// </summary>
        private void SampleLook(Player player)
        {
            Vector2 r;
            try
            {
                r = player.Rotation;
            }
            catch (Exception)
            {
                // Leave _lookSamples at 0 so the block emits null rather than a held-view-looking zero.
                return;
            }

            if (_lookSamples == 0)
            {
                _yawCum = 0d;
                _pitchCum = 0d;
                _yawMin = 0d;
                _yawMax = 0d;
                _pitchMin = 0d;
                _pitchMax = 0d;
                _yawSwept = 0d;
                _pitchSwept = 0d;
            }
            else
            {
                double dYaw = Unwrap(r.x - _lastYaw);
                double dPitch = Unwrap(r.y - _lastPitch);

                _yawCum += dYaw;
                _pitchCum += dPitch;
                _yawSwept += dYaw < 0d ? -dYaw : dYaw;
                _pitchSwept += dPitch < 0d ? -dPitch : dPitch;

                if (_yawCum < _yawMin) { _yawMin = _yawCum; }
                if (_yawCum > _yawMax) { _yawMax = _yawCum; }
                if (_pitchCum < _pitchMin) { _pitchMin = _pitchCum; }
                if (_pitchCum > _pitchMax) { _pitchMax = _pitchCum; }
            }

            _lastYaw = r.x;
            _lastPitch = r.y;
            _lookSamples++;
        }

        /// <summary>Folds a raw angular difference into (-180, 180], so 359.9 -> 0.1 reads as +0.2
        /// rather than -359.8. The loops handle any number of wraps without assuming one.</summary>
        private static double Unwrap(double degrees)
        {
            while (degrees > 180d)
            {
                degrees -= 360d;
            }

            while (degrees <= -180d)
            {
                degrees += 360d;
            }

            return degrees;
        }

        /// <summary>
        /// Emits the window's movement, or explicit nulls when no player was ever sampled - a teardown
        /// window must be visibly a teardown window rather than a gap.
        /// </summary>
        private void AppendPosition(StringBuilder sb)
        {
            if (_posSamples == 0)
            {
                sb.Append(",\"pos\":{\"dist\":null,\"samples\":0}");
                return;
            }

            sb.Append(",\"pos\":{\"dist\":").Append(Fmt(_distance));
            sb.Append(",\"samples\":").Append(_posSamples);
            sb.Append(",\"x\":[").Append(Fmt(_posMin.x)).Append(',').Append(Fmt(_posMax.x)).Append(']');
            sb.Append(",\"y\":[").Append(Fmt(_posMin.y)).Append(',').Append(Fmt(_posMax.y)).Append(']');
            sb.Append(",\"z\":[").Append(Fmt(_posMin.z)).Append(',').Append(Fmt(_posMax.z)).Append(']');
            sb.Append(",\"end\":[").Append(Fmt(_lastPos.x)).Append(',').Append(Fmt(_lastPos.y))
              .Append(',').Append(Fmt(_lastPos.z)).Append(']');
            AppendLook(sb);
            sb.Append('}');
        }

        /// <summary>
        /// The look block, nested in `pos`.
        ///
        /// **Null, never zero, when nothing was sampled.** A held view and a field that failed to read
        /// both produce zero variance, and the whole point of this block is to certify a held view - so
        /// the two must not be spelled the same. That is the `proc` precedent, which published zeros for
        /// a day that meant "could not read".
        ///
        /// `range` is [min, max] of the unwrapped angle relative to the first sample of the window, so
        /// max - min is angular extent in degrees. `swept` is total absolute change.
        /// </summary>
        private void AppendLook(StringBuilder sb)
        {
            if (_lookSamples == 0)
            {
                sb.Append(",\"look\":null");
                return;
            }

            sb.Append(",\"look\":{\"samples\":").Append(_lookSamples);
            sb.Append(",\"yaw\":{\"range\":[").Append(Fmt(_yawMin)).Append(',').Append(Fmt(_yawMax))
              .Append("],\"swept\":").Append(Fmt(_yawSwept)).Append('}');
            sb.Append(",\"pitch\":{\"range\":[").Append(Fmt(_pitchMin)).Append(',').Append(Fmt(_pitchMax))
              .Append("],\"swept\":").Append(Fmt(_pitchSwept)).Append('}');
            sb.Append('}');
        }

        /// <summary>
        /// Process memory, once per window.
        ///
        /// Alpha measured 31.2 GB private commit against a 22.3 GB working set on a machine with 10.6 GB
        /// free - so ~9 GB is committed and not resident. That is not a GC story: the managed heap is
        /// ~2.5 GB, 8% of commit. It is here because **B is time outside PlayerLoop() entirely**, and a
        /// hard page fault blocks the thread wherever it occurs - native or managed - landing in exactly
        /// that interval while being invisible to every phase marker.
        ///
        /// A candidate, not a finding. PageFaultCount counts soft faults too, so it is a proxy rather
        /// than a measurement, and hard-fault rate needs an external poller. Deltas per window are what
        /// matter; the absolutes exist only to give the trajectory a baseline, since the only two samples
        /// anyone has taken were accidental and at different points in a session.
        ///
        /// Read through psapi rather than System.Diagnostics.Process. WorkingSet64 and
        /// PrivateMemorySize64 return 0 under this Mono, in every window of every run to date, and
        /// **from inside the log a dead field and a real zero are the same bytes**. That was caught
        /// only because the same quantity had been measured externally an hour earlier - so the zero
        /// guard below is the instrument, not padding around it.
        /// </summary>
        private void AppendProc(StringBuilder sb)
        {
            ProcessMemoryCountersEx c = default(ProcessMemoryCountersEx);
            bool ok;

            try
            {
                ok = GetProcessMemoryInfo(GetCurrentProcess(), out c,
                                          (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)));
            }
            catch (Exception)
            {
                // DllNotFound / EntryPointNotFound - the call never happened at all.
                sb.Append(",\"proc\":{\"err\":\"pinvoke\"}");
                return;
            }

            long ws = ok ? (long)c.WorkingSetSize.ToUInt64() : 0L;
            long priv = ok ? (long)c.PrivateUsage.ToUInt64() : 0L;

            if (!ok || ws == 0L || priv == 0L)
            {
                // A running process cannot have a zero working set. Emitting the zeros is exactly
                // what made the previous implementation read as a measurement for a whole day.
                sb.Append(",\"proc\":{\"err\":\"").Append(ok ? "zero" : "call").Append("\"}");
                return;
            }

            // Signed before subtracting: WorkingSetSize counts shared pages that PrivateUsage does
            // not, so working set legitimately exceeds commit and an unsigned difference wraps.
            long faults = c.PageFaultCount;

            sb.Append(",\"proc\":{\"wsMb\":").Append(ws / 1048576L);
            sb.Append(",\"privMb\":").Append(priv / 1048576L);
            sb.Append(",\"notResidentMb\":").Append((priv - ws) / 1048576L);
            sb.Append(",\"faults\":").Append(faults);

            if (_hasProcBaseline)
            {
                sb.Append(",\"wsDeltaMb\":").Append((ws - _lastWs) / 1048576L);
                sb.Append(",\"privDeltaMb\":").Append((priv - _lastPriv) / 1048576L);
                sb.Append(",\"faultsDelta\":").Append(faults - _lastFaults);
            }
            else
            {
                // null, not 0, on the first window. "No baseline yet" and "nothing moved" are
                // different readings; the previous implementation reported both as 0.
                sb.Append(",\"wsDeltaMb\":null,\"privDeltaMb\":null,\"faultsDelta\":null");
            }

            sb.Append('}');

            _lastWs = ws;
            _lastPriv = priv;
            _lastFaults = faults;
            _hasProcBaseline = true;
        }

        /// <summary>
        /// PROCESS_MEMORY_COUNTERS_EX. PrivateUsage is the process commit charge - Task Manager's
        /// "Commit size", and the field carrying the 31.2 GB figure. It exists only in the EX form,
        /// which is why the size is passed explicitly rather than taken from the base struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters,
                                                        uint size);

        /// <summary>Returns the pseudo-handle -1; nothing to close.</summary>
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private long _lastWs;
        private long _lastPriv;
        private long _lastFaults;
        private bool _hasProcBaseline;

        private int _negResidualFrames;
        private int _frameOverPeriodFrames;

        // Magnitude alongside count, because the counts have no threshold and cannot be read as a
        // rate on their own. Worst gives the tail; Sum over the count gives the mean, which is what
        // separates sub-millisecond jitter from the split-Update mechanism at tens to hundreds of ms.
        // Neither pre-commits to a cut, so the cut can be chosen from the data rather than guessed
        // now - the mistake that produced the 23.9% figure this comment used to carry.
        private double _negResidualWorstMs;
        private double _negResidualSumMs;
        private double _frameOverPeriodWorstMs;
        private double _frameOverPeriodSumMs;

        // Eligible-frame denominator and the boundary-miss count. Both exist so a zero is readable:
        // negResidualFrames is zero by construction once the latch lands, and without these a broken
        // instrument produces the same zero as a working one.
        private int _clockResidualFrames;
        private int _boundaryMissedFrames;

        /// <summary>
        /// Counts the two clock-disagreement signatures on EVERY frame, not just spike lines.
        ///
        /// `unaccounted` must never be negative, and it is: **~8-10% of in-raid spike lines at a 1 ms
        /// cut, stable across builds** (10.5% control, 8.3% on 2026-07-28). It predates the first
        /// build of that day, so nothing shipped since caused it. The mechanism is known - see the
        /// note on EmitSpikeEvent - and the fix is to move the snapshot boundary out of `Update`.
        ///
        /// **The raw counts are not defect rates and must not be quoted as ones.** They carry no
        /// magnitude cut. `frameOverPeriodFrames` in particular reads near 50% of all frames, which
        /// is exactly what two clocks measuring the same span with symmetric sub-millisecond noise
        /// produce: at face value it is evidence the clocks agree, not that they disagree. An earlier
        /// version of this comment cited 23.9% and 29.1% from uncut counts and read as establishing a
        /// defect rate that does not exist. The Worst and Sum fields exist to prevent the repeat.
        ///
        /// Counting only on spike lines sees the tail. The mechanism moves time from line N to line N+1,
        /// so it makes N large and N+1 ordinary - which means **any filter selecting lines by magnitude
        /// is structurally blind to the follow-up line**, and the population defined by the instrument's
        /// own trigger is the population that hides it. Hence every frame.
        ///
        /// Two comparisons and a sum over eight top-level phases per frame.
        /// </summary>
        private void CountClockDisagreement(double periodMs, double frameMs)
        {
            // The first sampled frame of a process has no previous timestamp, so `period` is 0 against a
            // span that was never measured. Both comparisons below then fire once, and the residual one
            // fires against the whole phase total accumulated since install - a single frame carrying an
            // arbitrarily large deficit into the worst-magnitude field, which is the field least able to
            // absorb it.
            if (periodMs <= 0d)
            {
                return;
            }

            if (frameMs > periodMs)
            {
                double over = frameMs - periodMs;
                _frameOverPeriodFrames++;
                _frameOverPeriodSumMs += over;
                if (over > _frameOverPeriodWorstMs)
                {
                    _frameOverPeriodWorstMs = over;
                }
            }

            if (!PlayerLoopProfiler.Installed)
            {
                return;
            }

            // Frames on which the residual test actually ran. Gamma's, and it is not merely a denominator:
            // once the boundary latch makes negResidualFrames zero by construction, a zero from the
            // assertion holding and a zero from this method returning early are identical in the output.
            // The two guards above gate different counters - periodMs <= 0 skips both, !Installed skips
            // only the residual half - so `frames` on the line is the denominator of neither, which is
            // why this counter exists. Read the rate as negResidualFrames / clockResidualFrames.
            _clockResidualFrames++;

            string[] names = PlayerLoopProfiler.PhaseNames;
            double[] phase = PlayerLoopProfiler.Snapshot;
            double accounted = 0d;

            for (int i = 0; i < phase.Length && i < names.Length; i++)
            {
                // Top-level only; children would double-count their parent. Same rule as the spike line.
                if (names[i].IndexOf('/') < 0)
                {
                    accounted += phase[i];
                }
            }

            // Deficit rather than residual, so the magnitude is positive and the two Worst fields read
            // the same direction: both are "how far the wrong way did this frame go".
            double deficit = accounted - periodMs;
            if (deficit > 0d)
            {
                _negResidualFrames++;
                _negResidualSumMs += deficit;
                if (deficit > _negResidualWorstMs)
                {
                    _negResidualWorstMs = deficit;
                }
            }
        }

        private void Sample()
        {
            // Collections since the previous sampled frame. Per-window gen0 cannot resolve a single 330ms
            // frame, and the recurring early-raid spikes that land entirely in `unaccounted` have exactly
            // the shape of a stop-the-world pause - no measured phase accounts for them and they are
            // suspiciously uniform in size. A non-zero value on those lines settles it.
            int gen0Now = GC.CollectionCount(0);
            _gcThisFrame = gen0Now - _gen0PrevFrame;
            _gen0PrevFrame = gen0Now;

            // The frame measurers only exist once a match is being set up, so everything that reads them is
            // optional now that sampling starts at plugin load. The phase timers, GC counters and drain
            // diagnostics work regardless, and they are what the pre-raid data is for.
            SamplePosition();

            GClass1357 m = Singleton<GClass1357>.Instantiated ? Singleton<GClass1357>.Instance : null;
            double gameUpdate = 0d;

            if (m != null)
            {
                // LastValue is the most recently completed span for each measurer. Sampling from Update means
                // the frame/render figures are one frame behind, which is irrelevant over a whole window.
                gameUpdate = m.GameUpdateMeasurer.MeasureStatistics.LastValue;

                _frame.Add(m.GameFrameMeasurer.MeasureStatistics.LastValue);
                _gameUpdate.Add(gameUpdate);
            }

            int gen0 = GC.CollectionCount(0);
            _gen0 = gen0;

            long heap = GC.GetTotalMemory(false);
            // Signed, unlike _allocatedBytes below: a spike line wants the drop specifically, because a heap
            // that ends a frame smaller than it started is a collection having run inside it.
            _lastHeapDeltaMb = _lastHeap > 0 ? (heap - _lastHeap) / (1024d * 1024d) : 0d;
            if (_lastHeap > 0 && heap > _lastHeap)
            {
                // Only positive deltas: a drop means a collection ran, not that memory was un-allocated.
                _allocatedBytes += heap - _lastHeap;
            }

            _lastHeap = heap;
            _heapMb.Add(heap / (1024d * 1024d));

            _jobQueue.Add(JobScheduler.QueueLength);

            _aiTotal.Add(AiTiming.TotalMs);

            // These accumulate during LateUpdate, which runs after this method, so what we read here is the
            // previous frame's completed total. Zero them so the next frame starts clean.
            _jobSched.Add(LateTiming.JobSchedulerMs);
            _ambientLight.Add(LateTiming.AmbientLightMs);
            // Latched before the resets below, because the spike line at the end of this method reports the
            // frame these belong to.
            _lastAsyncUpdate = AsyncWorkerTiming.UpdateDrainMs;
            _lastAsyncFixed = AsyncWorkerTiming.FixedDrainMs;
            _lastDrained = AsyncDrain.Drained;

            _asyncUpdate.Add(_lastAsyncUpdate);
            _asyncFixed.Add(_lastAsyncFixed);
            _asyncFixedSkips += AsyncWorkerTiming.FixedSkips;
            AsyncWorkerTiming.Reset();
            _asyncDrained.Add(_lastDrained);
            AsyncDrain.ResetFrame();
            // How many physics steps ran this frame - separates "many steps" from "one expensive step".
            if (m != null)
            {
            }
            _playerLate.Add(LateTiming.PlayerLateMs);
            _playerTick.Add(LateTiming.PlayerTickMs);
            // AmbientLight rebuilds a full stencil-shadow command buffer per registered camera per frame, so
            // camera count is a direct multiplier on that cost. Scope optics add one; some mods add more.

            if (PlayerLoopProfiler.Installed)
            {
                // ReadAndReset moved to the frame-boundary marker, which latches the snapshot and the
                // period timestamp together. Calling it here would take a second snapshot mid-Update and
                // reintroduce the split this change exists to remove.
                double[] phase = PlayerLoopProfiler.Snapshot;
                if (_phases == null || _phases.Length != phase.Length)
                {
                    _phases = new Stat[phase.Length];
                    for (int i = 0; i < _phases.Length; i++)
                    {
                        _phases[i] = new Stat();
                    }
                }

                for (int i = 0; i < phase.Length; i++)
                {
                    _phases[i].Add(phase[i]);
                }
            }
            Framesaver.Patches.SleepingBotAnimatorPatch.ReadAndReset();
            LateTiming.Reset();

            GpuTelemetry.Sample();
            GcControl.Track();

            double frameMs = m != null ? m.GameFrameMeasurer.MeasureStatistics.LastValue : 0d;
            if (m != null)
            {
                _gameUpdateSamples.Add(gameUpdate);
                _frameSamples.Add(frameMs);
                _tickedSum += AICoreControllerUpdatePatch.LastBrainsTicked;
                _liveSum += AICoreControllerUpdatePatch.LiveAgents;
            }

            _periodSamples++;

            // Wall time covered by the phase accumulators just read, latched at the frame boundary by the
            // same delegate that took the snapshot. Taking it here instead - inside the Update phase -
            // is what split a stall across two lines and drove `unaccounted` negative.
            double periodMs;
            if (PlayerLoopProfiler.Installed)
            {
                if (!PlayerLoopProfiler.ConsumeFrameBoundary(out periodMs))
                {
                    // Markers dropped since the previous sample. Emitting the stale latch would repeat one
                    // frame's period indefinitely and look exactly like a healthy run, so this frame
                    // contributes to nothing - not the counters, not a spike line, not the denominator.
                    _boundaryMissedFrames++;
                    return;
                }
            }
            else
            {
                // No profiler, so there are no phases and no residual to compute - but `period` still
                // drives the spike lines, which are a core instrument and must not be lost with it.
                // Measuring it here reintroduces the mid-Update split, which costs nothing when there
                // are no phase totals to be split against.
                long now = Stopwatch.GetTimestamp();
                periodMs = _lastSampleTicks == 0L ? 0d : AiTiming.ToMs(now - _lastSampleTicks);
                _lastSampleTicks = now;
            }

            CountClockDisagreement(periodMs, frameMs);

            if (periodMs >= Plugin.SpikeEventMs.Value && Plugin.SpikeEventMs.Value > 0f)
            {
                EmitSpikeEvent(periodMs, frameMs);
            }
        }

        /// <summary>
        /// One line per bad frame, carrying that frame's own phase breakdown.
        ///
        /// Window aggregates cannot answer "where did this frame go" - the max of `frame` and the max of each
        /// phase need not be the same frame, so a spike whose phases all look normal is unresolvable from the
        /// summary alone. It is also what gives the exact cadence of a recurring spike, which is how a
        /// timer-driven cost gets traced back to the timer.
        ///
        /// `period` is the authoritative duration here: it is the wall time between the two ReadAndReset calls
        /// that bracket these phase values, so `period - sum(phases)` is a true residual. `frame` is the game's
        /// own counter and lags by one frame - kept only for continuity with the window summaries.
        ///
        /// Caveat on individual phases: this runs inside ScriptRunBehaviourUpdate, so phases that execute before
        /// Update (TimeUpdate, Initialization, EarlyUpdate, FixedUpdate, PreUpdate) are this frame's, while
        /// Update, PreLateUpdate and PostLateUpdate are the previous frame's.
        ///
        /// **This comment used to end "the sum is still exactly one frame's wall time, which is what makes the
        /// residual valid". That is wrong and `unaccounted` does go negative.** The sum is one duration per
        /// phase, but the sample point sits *inside* Update, so the eight durations do not tile the interval
        /// `period` measures. A stall in Update before this method runs lands in `period` on one line and in
        /// the Update phase total on the next: positive residual, then negative. No clock is wrong - three
        /// correct measurements covering slightly different intervals. Moving the snapshot to the frame
        /// boundary is the fix, and negResidualWorstMs is what will show whether it worked.
        /// </summary>
        private void EmitSpikeEvent(double periodMs, double frameMs)
        {
            StringBuilder sb = new StringBuilder(384);
            // Collections that happened during this frame specifically. Per-window gen0 cannot resolve a
            // single 330ms frame, and the recurring early-raid spikes that land entirely in `unaccounted`
            // have exactly the shape of a stop-the-world pause: no measured phase accounts for them, and
            // they are suspiciously uniform in size. If gen0 is non-zero on those lines, that is the answer.
            sb.Append("{\"type\":\"spike\",\"window\":").Append(_window);
            // True QueryPerformanceCounter via GpuTelemetry.Qpc(), NOT Stopwatch.GetTimestamp().
            // Under Mono the Stopwatch epoch is process-relative while reporting a 10 MHz
            // frequency, so durations are correct but timestamps will not join against an
            // external capture. Stamped at frame END; PresentMon's CPUStartQPC is frame
            // START, so subtract period before matching - and match by containment in
            // [CPUStartQPC, CPUStartQPC + FrameTime), not by nearest start. Nearest-start
            // lands on the neighbouring ordinary frame on exactly the stall frames that
            // matter, which reads as "the GPU was fine through the stall".
            sb.Append(",\"qpc\":").Append(GpuTelemetry.Qpc());
            sb.Append(",\"gcGen0\":").Append(_gcThisFrame);
            Num(sb, "t", Time.realtimeSinceStartup - _sampleStart);
            sb.Append(",\"state\":\"").Append(_state.ToString().ToLowerInvariant()).Append('"');
            AppendRaidIdentity(sb);
            AppendRaidClock(sb);
            Num(sb, "period", periodMs);
            Num(sb, "frame", frameMs);

            // Where this frame happened. A spike that only occurs in one part of a map is a different
            // finding from one that happens anywhere, and nothing has been able to tell them apart.
            if (_hasPos)
            {
                sb.Append(",\"at\":[").Append(Fmt(_lastPos.x)).Append(',').Append(Fmt(_lastPos.y))
                  .Append(',').Append(Fmt(_lastPos.z)).Append(']');
            }
            else
            {
                sb.Append(",\"at\":null");
            }

            // Wall time from PostLateUpdate's last subsystem to EarlyUpdate's first - i.e. outside
            // PlayerLoop() entirely. Contains TimeUpdate and Initialization, both measured separately in
            // `phases`, so the native inter-frame gap is this minus those two. Raw rather than
            // pre-subtracted, so the line reports what was read.
            //
            // null, not 0, when the subscription failed: a gap that genuinely measured zero and an
            // instrument that never armed must not look alike.
            // null covers two distinct failures and both must not look like a measurement: the
            // subscription never armed, or the EndOfFrame/StartOfFrame pairing was not 1:1 this frame.
            // The second matters most - a drifted pairing spans several frames and reads as exactly the
            // large gap this field exists to detect, so an unsure instrument must stay silent.
            if (PlayerLoopProfiler.FrameGapArmed && PlayerLoopProfiler.GapValid)
            {
                Num(sb, "endToStart", PlayerLoopProfiler.EndToStartMs);
            }
            else
            {
                sb.Append(",\"endToStart\":null");
            }

            // endToLatch closes the same gap at the frame boundary, so it pairs with `period` and
            // `unaccounted` on THIS line instead of the previous one. See PlayerLoopProfiler.
            //
            // **Both are emitted deliberately, for one run only.** endToStart is superseded and its
            // three-term identity is a registered prediction - replacing it outright would make that
            // prediction unevaluable and remove the only way to show the fix worked. This is the same
            // argument as shipping the boundary latch paired with the counters that prove it: a silent
            // regression and a fix look identical without the thing being replaced still present.
            // Drop endToStart once endToLatch is validated against it.
            if (PlayerLoopProfiler.FrameGapArmed && PlayerLoopProfiler.LatchGapValid)
            {
                Num(sb, "endToLatch", PlayerLoopProfiler.EndToLatchMs);
            }
            else
            {
                sb.Append(",\"endToLatch\":null");
            }

            double accounted = 0d;
            if (PlayerLoopProfiler.Installed)
            {
                string[] names = PlayerLoopProfiler.PhaseNames;
                double[] phase = PlayerLoopProfiler.Snapshot;
                sb.Append(",\"phases\":{");
                bool first = true;
                for (int i = 0; i < phase.Length && i < names.Length; i++)
                {
                    // Only top-level phases count toward the total; children would double-count their parent.
                    bool child = names[i].IndexOf('/') >= 0;
                    if (!child)
                    {
                        accounted += phase[i];
                    }

                    if (phase[i] < 0.5d)
                    {
                        continue;
                    }

                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append('"').Append(Escape(names[i])).Append("\":").Append(Fmt(phase[i]));
                }

                sb.Append('}');
            }

            // The residual is the whole point: time inside no player-loop phase at all is a different class of
            // problem from time inside one, and only a per-frame line can show it.
            Num(sb, "unaccounted", periodMs - accounted);
            Num(sb, "asyncFixed", _lastAsyncFixed);
            Num(sb, "asyncUpdate", _lastAsyncUpdate);
            sb.Append(",\"drained\":").Append(_lastDrained);

            // gcPhase and heapDeltaMb are both gated on a collection having completed this
            // frame, so heapDeltaMb reports how much was reclaimed and nothing more. It
            // cannot establish that the collection *was* the pause: the field only exists
            // on frames already selected for carrying one, so a frame stalled by something
            // else never gets a reading to contradict it.
            // The discriminator for "did the collection occupy this frame" is frame vs
            // period, both already on the line. A collection at the frame boundary leaves
            // frame ordinary (12-27 ms) while period runs 104-246 ms; a stall inside the
            // frame has frame ~= period. Measured 13/13 and 0/12 on the control run.
            if (_gcThisFrame > 0 && PlayerLoopProfiler.Installed)
            {
                string gcPhase = PlayerLoopProfiler.GcPhase();
                if (gcPhase.Length > 0)
                {
                    sb.Append(",\"gcPhase\":\"").Append(Escape(gcPhase)).Append('"');
                }

                Num(sb, "heapDeltaMb", _lastHeapDeltaMb);
                GcControl.AppendSpike(sb);
            }
            // A spike that is all TimeUpdate and no drain is currently unattributable. gpuMs and presentWaitMs
            // separate "the GPU was busy" from "the present call blocked for some other reason", and vram
            // says whether the driver was evicting at the time.
            GpuTelemetry.AppendSpike(sb);
            sb.Append('}');
            Append(sb.ToString());
        }

        private static void AppendPercentiles(StringBuilder sb, string name, List<double> samples)
        {
            if (samples.Count == 0)
            {
                return;
            }

            samples.Sort();
            sb.Append(",\"").Append(name).Append("\":{\"p50\":").Append(Fmt(Percentile(samples, 0.50)))
              .Append(",\"p95\":").Append(Fmt(Percentile(samples, 0.95)))
              .Append(",\"p99\":").Append(Fmt(Percentile(samples, 0.99)))
              .Append(",\"p999\":").Append(Fmt(Percentile(samples, 0.999))).Append('}');
        }

        private static double Percentile(List<double> sorted, double fraction)
        {
            int index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= sorted.Count)
            {
                index = sorted.Count - 1;
            }

            return sorted[index];
        }

        private void Flush(bool final)
        {
            // Frame-measurer count, not sample count: outside a match those measurers do not exist, so a
            // pre-raid window legitimately has n == 0 while still carrying phase and drain data worth keeping.
            if (_periodSamples == 0)
            {
                return;
            }

            int awake = 0;
            int asleep = 0;
            int exempt = 0;
            int roleUnknown = 0;
            CountBots(ref awake, ref asleep, ref exempt, ref roleUnknown);

            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"type\":\"sample\"");
            sb.Append(",\"window\":").Append(_window);
            sb.Append(",\"qpc\":").Append(GpuTelemetry.Qpc());
            Num(sb, "t", Time.realtimeSinceStartup - _sampleStart);
            sb.Append(",\"state\":\"").Append(_state.ToString().ToLowerInvariant()).Append('"');
            AppendRaidIdentity(sb);
            AppendRaidClock(sb);
            sb.Append(",\"final\":").Append(final ? "true" : "false");
            sb.Append(",\"frames\":").Append(_periodSamples);
            sb.Append(",\"n\":").Append(_frame.Count);

            AppendPosition(sb);
            AppendProc(sb);

            Block(sb, "frame", _frame);
            Block(sb, "gameUpdate", _gameUpdate);
            Block(sb, "jobQueue", _jobQueue);
            Block(sb, "aiTotal", _aiTotal);

            // Beside aiTotal, not inside it: this is BotOwner.UpdateManual, which
            // aiTotal (BotsController.method_0) does not contain. Sums over the
            // window, not a distribution - the quantity wanted is a per-call mean
            // per bucket, so the divisor has to travel with the total.
            sb.Append(",\"updateManual\":");
            Framesaver.Patches.UpdateManualTiming.Append(sb);

            Block(sb, "jobSchedulerLate", _jobSched);
            Block(sb, "ambientLight", _ambientLight);
            Block(sb, "asyncUpdateDrain", _asyncUpdate);
            Block(sb, "asyncFixedDrain", _asyncFixed);
            Block(sb, "asyncDrained", _asyncDrained);
            sb.Append(",\"asyncFixedSkips\":").Append(_asyncFixedSkips);

            // Where the bot/generate stall actually goes. Sections nest inside the total, so `other` is a
            // subtraction rather than a measured span.
            sb.Append(",\"profileBuild\":{\"profiles\":").Append(ProfileBuild.Profiles)
              .Append(",\"totalMs\":").Append(Fmt(ProfileBuild.TotalMs))
              .Append(",\"inventoryMs\":").Append(Fmt(ProfileBuild.InventoryMs))
              .Append('}');

            // Raid initialisation - BotsController.Init and the spawn scenarios, which resume inline inside
            // whichever bot/generate callback completes the last preset batch. Emitted only in the window
            // that contains it, since this is a once-per-raid event and an empty block every window would be
            // noise. Compare `totalMs` here against `raidInitMs` on the worstCallbacks entry: they should be
            // the same number seen from two directions.
            if (RaidInit.Any)
            {
                sb.Append(",\"raidInit\":");
                RaidInit.Append(sb);
            }

            // Raid-scoped, so it repeats every window rather than appearing once:
            // the question "was this window's data collected under a forced
            // garrison" is asked of every window, and a value present only in
            // the window that observed it is a join waiting to be got wrong.
            if (Framesaver.Patches.BossSpawnGate.Any)
            {
                sb.Append(",\"spawnGate\":");
                Framesaver.Patches.BossSpawnGate.Append(sb);
            }


            // Backup-profile system. `bailed` is the one to watch: a flush refused by the in-flight guard
            // leaves the pending list uncleared, which is the suspected source of the 75-bot requests.
            sb.Append(",\"botBackup\":{\"fired\":").Append(BotBackup.Fired)
              .Append(",\"bailed\":").Append(BotBackup.Bailed)
              .Append('}');
            sb.Append(",\"bundleLoad\":{\"calls\":").Append(BundleLoad.Calls)
              .Append(",\"keys\":").Append(BundleLoad.Keys)
              .Append(",\"keysMax\":").Append(BundleLoad.KeysMax)
              .Append(",\"syncMsMax\":").Append(Fmt(BundleLoad.SyncMsMax))
              .Append(",\"syncMsTotal\":").Append(Fmt(BundleLoad.SyncMsTotal))
              .Append(",\"inFlightMax\":").Append(BundleLoad.InFlightMax)
              .Append('}');

            sb.Append(",\"gcSuspended\":").Append(AsyncDrain.GcSuspended);

            // Spawn attempts vs bots that actually resulted. `creates` should track botPool.calls; the
            // gap between `creates` and `botOwners` is how much of the profile and bundle work is wasted.
            sb.Append(",\"spawn\":{\"creates\":").Append(SpawnAttempts.Creates)
              .Append(",\"byWave\":").Append(SpawnAttempts.ByWave)
              .Append(",\"withoutWave\":").Append(SpawnAttempts.WithoutWave)
              .Append(",\"byTypeForce\":").Append(SpawnAttempts.ByTypeForce)
              .Append(",\"zoneAttempts\":").Append(SpawnAttempts.ZoneAttempts)
              .Append(",\"botOwners\":").Append(SpawnAttempts.BotOwners)
              .Append(",\"createMsTotal\":").Append(Fmt(SpawnAttempts.CreateMsTotal))
              .Append(",\"createMsMax\":").Append(Fmt(SpawnAttempts.CreateMsMax))
              .Append(",\"perFrameMax\":").Append(SpawnAttempts.PerFrameMax)
              .Append(",\"buildMsTotal\":").Append(Fmt(SpawnAttempts.BuildMsTotal))
              .Append(",\"buildMsMax\":").Append(Fmt(SpawnAttempts.BuildMsMax))
              .Append(",\"buildPerFrameMax\":").Append(SpawnAttempts.BuildPerFrameMax)
              .Append('}');

            // The single slowest completion callback in the window, resolved back to whoever queued it. This is
            // the field that names the culprit rather than just locating it.
            sb.Append(",\"worstCallbacks\":");
            AsyncDrain.AppendTop(sb);
            Block(sb, "playerLate", _playerLate);
            Block(sb, "playerTick", _playerTick);

            if (_phases != null)
            {
                string[] names = PlayerLoopProfiler.PhaseNames;
                sb.Append(",\"phases\":{");
                for (int i = 0; i < _phases.Length && i < names.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append('"').Append(names[i]).Append("\":{\"avg\":").Append(Fmt(_phases[i].Average))
                      .Append(",\"max\":").Append(Fmt(_phases[i].Max)).Append('}');
                }

                sb.Append('}');
            }

            AppendPercentiles(sb, "framePct", _frameSamples);

            GpuTelemetry.AppendWindow(sb);
            GpuTelemetry.AppendGraphicsConfig(sb);
            GcControl.AppendWindow(sb);

            sb.Append(",\"snipersAwake\":").Append(LongRangeExemption.Count);
            sb.Append(",\"bots\":{\"awake\":").Append(awake)
              .Append(",\"asleep\":").Append(asleep)
              .Append(",\"total\":").Append(awake + asleep)
              .Append(",\"animCulled\":").Append(Framesaver.Patches.SleepingBotAnimatorPatch.CulledLastFrame)
              .Append(",\"exempt\":").Append(exempt)
              .Append(",\"roleUnknown\":").Append(roleUnknown).Append('}');

            // `slicing` is the EFFECTIVE state, not the requested one, and it is the same expression the
            // patch branches on at AICoreControllerUpdatePatch.cs:64 rather than a re-derivation of it.
            // `cfg.brainPeriod` already reports what was asked for, and the failure this closes is the two
            // disagreeing silently: BigBrain arrives as a SAIN dependency, ModCompat suppresses slicing, the
            // arm reads as applied, the behaviour is vanilla, and the null reads as "the lever does nothing".
            //
            // `tickedSum` / `n` is brains per frame; `tickedSum` / `liveSum` is the fraction of the roster
            // ticked per frame, which is the quantity that predicts frame time. Sums rather than a ratio
            // because a ratio cannot be re-derived and cannot be pooled across windows.
            bool slicing = Plugin.BrainUpdatePeriod.Value > 0f && !ModCompat.SuppressSlicing;
            sb.Append(",\"agents\":{\"live\":").Append(AICoreControllerUpdatePatch.LiveAgents)
              .Append(",\"pendingRemoval\":").Append(AICoreControllerUpdatePatch.PendingRemoval)
              .Append(",\"removedTotal\":").Append(AICoreControllerUpdatePatch.RemovedTotal)
              .Append(",\"slicing\":").Append(slicing ? "true" : "false")
              .Append(",\"suppressSlicing\":").Append(ModCompat.SuppressSlicing ? "true" : "false")
              .Append(",\"tickedSum\":").Append(_tickedSum)
              .Append(",\"liveSum\":").Append(_liveSum);

            // Which AI/co-op mods are present. Here rather than the header because the
            // header runs in Awake, where reading ModCompat latches detection against a
            // plugin list BepInEx has not finished filling. This block already calls
            // SuppressSlicing two lines up, so detection is forced from this exact site
            // regardless - the names are free, and no other site could say that.
            sb.Append(",\"mods\":");
            ModCompat.AppendDetected(sb);
            sb.Append('}');

            float elapsed = Mathf.Max(0.001f, Time.realtimeSinceStartup - _windowStart);

            // The window's OWN duration, measured, not the configured one.
            //
            // Every per-window rate on this line divides by this quantity, and until now it was implicit:
            // a reader assumed 60 because that is the default of `Window seconds`. That setting is
            // live-editable and was not in `cfg`, so a mid-session edit silently changed the denominator
            // of every rate after it with nothing in the data to say so - and a window closed early by a
            // flush would do the same, which is why this is a hard prerequisite for the protocol keybind
            // rather than a tidy-up alongside it.
            //
            // Measured rather than configured because a flushed partial window has the config's duration
            // and not its own. Emitting the setting instead would be exactly the mistake this fixes.
            Num(sb, "windowSec", elapsed);

            sb.Append(",\"gc\":{\"gen0\":").Append(_gen0 - _gen0Base)
              .Append(",\"allocMbPerSec\":").Append(Fmt(_allocatedBytes / (1024d * 1024d) / elapsed))
              .Append(",\"heapMb\":{\"avg\":").Append(Fmt(_heapMb.Average))
              .Append(",\"min\":").Append(Fmt(_heapMb.Min))
              .Append(",\"max\":").Append(Fmt(_heapMb.Max)).Append("}}");

            // Repeated per line, not just in the header: BepInEx config is live-editable, so a header written
            // at plugin load can be stale by the time the raid starts.
            // frameGapArmed distinguishes the two meanings of a null endToStart: the subscription never
            // armed (a whole-run condition) or the pairing was not 1:1 on that frame (per-frame). Without
            // it the only record of arming is a BepInEx warning, which is the "identifiable from the log
            // rather than from the data" failure the failedPatches item exists for - in a field built
            // specifically so a null and a zero could not be confused.
            // Clock-disagreement counts over every frame in the window, not just the spike lines.
            // Count, worst magnitude and summed magnitude together, because the counts alone are
            // uncut and near-50% counts are the aligned-clocks signature rather than a defect rate.
            // Read Sum/Frames as the mean: well under 1 ms is jitter, tens of ms is the mechanism.
            // See CountClockDisagreement.
            sb.Append(",\"negResidualFrames\":").Append(_negResidualFrames);
            sb.Append(",\"negResidualWorstMs\":").Append(Fmt(_negResidualWorstMs));
            sb.Append(",\"negResidualSumMs\":").Append(Fmt(_negResidualSumMs));
            sb.Append(",\"frameOverPeriodFrames\":").Append(_frameOverPeriodFrames);
            sb.Append(",\"frameOverPeriodWorstMs\":").Append(Fmt(_frameOverPeriodWorstMs));
            sb.Append(",\"frameOverPeriodSumMs\":").Append(Fmt(_frameOverPeriodSumMs));
            sb.Append(",\"clockResidualFrames\":").Append(_clockResidualFrames);
            sb.Append(",\"boundaryMissedFrames\":").Append(_boundaryMissedFrames);
            sb.Append(",\"boundaryFires\":").Append(PlayerLoopProfiler.BoundaryFires);

            sb.Append(",\"frameGapArmed\":").Append(Bool(PlayerLoopProfiler.FrameGapArmed));
            sb.Append(",\"endOfFrameFires\":").Append(PlayerLoopProfiler.EndOfFrameFires);
            sb.Append(",\"startOfFrameFires\":").Append(PlayerLoopProfiler.StartOfFrameFires);

            // Null when no protocol is loaded, never an empty object - and emitted on every line rather
            // than only when present, so "no protocol" and "this build has no protocol support" are not
            // spelled the same across the era boundary this introduces.
            //
            // step 0 with a protocol loaded is a real state: armed, waiting for the first press. That is
            // why absent-protocol is null rather than 0.
            //
            // flushedByProtocol marks the window the keypress cut short. It is a partial window - shorter
            // than `windowSec` would suggest for a full one - and nobody should average it in with whole
            // windows. Marked rather than suppressed, because the contaminated interval is exactly the one
            // an analyst may want to look at deliberately.
            //
            // On that line the labels and the measurements describe DIFFERENT ARMS. `Advance()` applies the
            // step's config and increments the step before the flush at the call site, and this method reads
            // config live - so `protocol.arm`, `protocol.step`, `cfg` and `agents.slicing` all name the arm
            // ABOUT TO START while every accumulated number describes the arm that just ENDED. Being short
            // is the weaker reason to exclude it; this is the stronger one, and `slicing` is exactly the
            // field a reader would trust as ground truth. Beta caught it.
            //
            // FIXED, and this paragraph is kept in the past tense rather than deleted because the
            // exclusion it justifies is still correct. `ProtocolRunner.CanAdvance` landed in e01cb0f and
            // the call site flushes before advancing (ada1824), so a flushed line now labels the arm it
            // measured. `flushedByProtocol` is still excluded from arm comparisons - the window is short,
            // which is the weaker reason and the one that survives.
            //
            // Beta found this still written as a live defect hours after both halves shipped. A stale
            // comment reads exactly as authoritative as a fresh one, and this one sits on the field a
            // reader consults to decide whether the labels can be trusted: it would have had someone
            // discard good windows, or distrust every `agents.slicing` value in the log.
            //
            // ALSO NOTE, because it surprised three of us: this block emits whenever `Loaded` is true,
            // and `ResetForRaid()` calls `Load()`. From the moment the ini is on disk EVERY window of
            // EVERY raid carries `protocol`, including legs that never press the key. `arm` is null until
            // a step is applied, so `arm` is the field that distinguishes an applied arm from an installed
            // file. Readers keying on the object's presence mark the whole run as protocol legs.
            if (ProtocolRunner.Loaded)
            {
                sb.Append(",\"protocol\":{\"name\":\"").Append(Escape(ProtocolRunner.Name))
                  .Append("\",\"step\":").Append(ProtocolRunner.StepIndex)
                  .Append(",\"steps\":").Append(ProtocolRunner.StepCount)
                  .Append(",\"arm\":");
                string arm = ProtocolRunner.Arm;
                if (arm == null)
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append('"').Append(Escape(arm)).Append('"');
                }

                sb.Append('}');
            }
            else
            {
                sb.Append(",\"protocol\":null");
            }

            sb.Append(",\"flushedByProtocol\":").Append(Bool(_flushedByProtocol));

            // windowSeconds is the SETTING; windowSec above is what this window actually lasted. Both,
            // because a short measured window has two causes that need telling apart: a flush closed it
            // early (intentional, and the line is still valid), or someone edited the setting mid-session
            // (every rate before and after is on a different denominator, and comparability is broken).
            // Same shape as null-versus-zero: one symptom, two meanings, so emit what discriminates them.
            sb.Append(",\"cfg\":{\"windowSeconds\":").Append(Fmt(Plugin.TelemetryWindow.Value))
              .Append(",\"standBy\":").Append(Bool(Plugin.StandByEnabled.Value))
              .Append(",\"leakFix\":").Append(Bool(Plugin.FixAgentLeak.Value))
              .Append(",\"brainPeriod\":").Append(Fmt(Plugin.BrainUpdatePeriod.Value))
              .Append(",\"fastAnim\":").Append(Bool(Plugin.ForceFastBodyAnimator.Value))
              .Append(",\"cullSleeping\":").Append(Bool(Plugin.CullSleepingBotAnimators.Value))
              .Append(",\"maxDelta\":").Append(Fmt(Time.maximumDeltaTime))
              .Append(",\"skipLate\":").Append(Bool(Plugin.SkipSleepingLateUpdate.Value))
              .Append(",\"skipTick\":").Append(Bool(Plugin.SkipSleepingWorldTick.Value))
              .Append(",\"jobBudgetMs\":").Append(Fmt(Plugin.JobSchedulerBudgetMs.Value))
              .Append(",\"jobSlowFrames\":").Append(Plugin.JobSchedulerSlowFrames.Value)
              .Append(",\"asyncBudgetMs\":").Append(Fmt(Plugin.AsyncDrainBudgetMs.Value))
              // Every option that changes behaviour belongs here, or a run cannot be told apart from the
              // one before it. This was added late and cost a raid: the suspend-GC flag defaulted off, the
              // log had no way to say so, and the result read as "the fix did nothing".
              .Append(",\"suspendGc\":").Append(Bool(Plugin.SuspendGcDuringCallbacks.Value))
              .Append(",\"reclaimStandBy\":").Append(Bool(Plugin.ReclaimStandBy.Value))
              .Append(",\"deactivateSleeping\":").Append(Bool(Plugin.DeactivateSleepingBotState.Value))
              .Append(",\"keepFighting\":").Append(Bool(Plugin.KeepFightingBotsAwake.Value))
              // Both added 2026-07-27 for the same reason the comment above gives, and both were live
              // omissions rather than theoretical ones.
              //
              // drainInUpdateOnly decides which player-loop phase the completion drain runs in, and it is
              // toggleable mid-raid. The GPU session's cross-check invariant - raid-init collections must be
              // a subset of the Update-phase gen0 count on that frame - is only valid while it is true. With
              // it unrecorded, a run where it had been flipped would look like an instrument disagreeing
              // with another instrument rather than a config difference.
              //
              // drainDiagnostics gates worstCallbacks entirely, so with it off there is no raidInitMs and no
              // per-callback attribution at all - a run that measured nothing would be indistinguishable
              // from a run that measured zero.
              .Append(",\"drainInUpdateOnly\":").Append(Bool(Plugin.DrainInUpdateOnly.Value))
              .Append(",\"drainDiagnostics\":").Append(Bool(Plugin.AsyncDrainDiagnostics.Value));
            GcControl.AppendCfg(sb);
            sb.Append('}');

            sb.Append('}');

            Append(sb.ToString());

            _window++;
            ResetWindow();
        }

        /// <summary>
        /// Awake/asleep, plus the split of WHY a bot is awake.
        ///
        /// `awake` conflates two populations - awake because a human is near, and awake because the
        /// role cannot stand by at all - and until now nothing could tell them apart. That difference
        /// was the whole story of Lighthouse, which floors at 14 of 29 awake where other maps floor at
        /// 0-2, with only one of the 14 a sniper exemption.
        ///
        /// `roleUnknown` is emitted rather than folded into `exempt` because `RoleAllowsStandBy`
        /// answers `false` both for "this role may not stand by" and for "the role could not be read".
        /// Counting those together would put unknowns inside a number named for something else. If it
        /// is always 0 it costs one field and proves `exempt` is clean.
        /// </summary>
        private static void CountBots(ref int awake, ref int asleep, ref int exempt, ref int roleUnknown)
        {
            if (!Singleton<IBotGame>.Instantiated)
            {
                return;
            }

            BotsController controller = Singleton<IBotGame>.Instance.BotsController;
            if (controller == null || controller.Bots == null)
            {
                return;
            }

            IEnumerable<BotOwner> bots = controller.Bots.BotOwners;
            if (bots == null)
            {
                return;
            }

            foreach (BotOwner bot in bots)
            {
                // A null StandBy drops the bot from both counts, so awake+asleep can
                // silently undercount rather than misclassify. Nothing here bounds how
                // many are dropped - that is why agents.live is reported alongside, and
                // the two are cross-checked rather than assumed equal. See FINDINGS.
                if (bot == null || bot.StandBy == null)
                {
                    continue;
                }

                if (bot.StandBy.StandByType_1 == BotStandByType.paused)
                {
                    asleep++;
                }
                else
                {
                    awake++;
                }

                // Inside the same skip as the counts above, deliberately: these describe the same
                // population, so `exempt + roleUnknown` can be read against `awake + asleep` without
                // asking whether the two passes saw the same bots.
                if (!BotStandByUpdatePatch.RoleStandByKnown(bot))
                {
                    roleUnknown++;
                }
                else if (!BotStandByUpdatePatch.RoleAllowsStandBy(bot))
                {
                    exempt++;
                }
            }
        }

        /// <summary>
        /// Main key went down this frame and every configured modifier is held.
        ///
        /// **Not `KeyboardShortcut.IsDown()`, which additionally requires that NOTHING ELSE is held.**
        /// BepInEx's type summary is explicit - *"will trigger only if user presses and holds only
        /// LeftCtrl... if any other keys are pressed, the shortcut will not trigger"* - and
        /// `ModifierKeyTest` walks every keycode to enforce it. Its `IsDown` method summary says only
        /// "main key pressed, modifiers held" and omits the exclusion, which is how both keys shipped
        /// on it.
        ///
        /// The cost was measured, not theorised: of four mark presses in the first marathon, the only
        /// ones that fired were the ones made standing still. Every press while W was held vanished -
        /// so marks registered only when stationary, **systematically excluding hitches during
        /// movement and combat**, which are the ones we are hunting and the ones where her attention
        /// is least able to spare a keypress. The protocol key has the same defect, and there a
        /// swallowed press means the arm silently does not advance while every label says it did.
        ///
        /// Modifiers are still required, so a shortcut configured with them does not fire bare.
        /// `GetKeyDown` is tested first, so the enumerator only allocates on a frame the key moved.
        /// </summary>
        private static bool Pressed(BepInEx.Configuration.KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
            {
                return false;
            }

            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// One line per operator keypress: what the frames looked like just before she reacted.
        ///
        /// `spanMs` is the wall time the dump actually covers, and it is emitted rather than assumed
        /// because it is short in two honest cases - the first seconds of a session, and a stretch so
        /// slow that 1024 frames span less than the lookback. A short dump is not a quiet one, and
        /// without the span there is no way to tell those apart.
        /// </summary>
        private void WriteMark(SessionState state)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"mark\"");
            sb.Append(",\"mark\":").Append(++_markOrdinal);
            sb.Append(",\"window\":").Append(_window);
            sb.Append(",\"qpc\":").Append(GpuTelemetry.Qpc());
            Num(sb, "t", Time.realtimeSinceStartup - _sampleStart);

            // The state passed in, not `_state`: at the menu the latched field holds whatever the last
            // sampled regime was, so a mark on the intermission screen would claim to be in a raid.
            sb.Append(",\"state\":\"").Append(state.ToString().ToLowerInvariant()).Append('"');
            AppendRaidIdentity(sb);
            AppendRaidClock(sb);
            AppendPosition(sb);

            // Walk backwards from the newest frame, newest first, until the durations sum past the
            // lookback or the ring runs out.
            double span = 0d;
            int taken = 0;
            StringBuilder frames = new StringBuilder(3072);
            for (int i = 0; i < _markCount && span < MarkLookbackMs; i++)
            {
                double ms = _markRing[(_markNext - 1 - i + _markRing.Length) % _markRing.Length];
                if (taken > 0)
                {
                    frames.Append(',');
                }

                frames.Append(Fmt(ms));
                span += ms;
                taken++;
            }

            sb.Append(",\"frames\":").Append(taken);
            Num(sb, "spanMs", span);
            sb.Append(",\"frameMs\":[").Append(frames).Append(']');
            sb.Append('}');
            Append(sb.ToString());
            Plugin.LogSource.LogInfo("Framesaver mark: " + taken + " frames, " + Fmt(span) + " ms");
        }

        private void WriteHeader()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"type\":\"header\"");
            // Both derived from the assembly, never written down here. `commit` is separate from
            // `version` rather than the SDK's "0.1.0+<sha>" blob so a reader never has to split it,
            // and so an unstamped build reads as commit:"" instead of a version that looks whole.
            sb.Append(",\"version\":\"").Append(Escape(Plugin.BuildVersion)).Append('"');
            sb.Append(",\"commit\":\"").Append(Escape(Plugin.BuildCommit)).Append('"');
            sb.Append(",\"started\":\"").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('"');
            AppendPlatform(sb);
            AppendDisplay(sb);
            AppendSystem(sb);
            sb.Append(",\"tag\":\"").Append(Escape(Plugin.RunTag.Value)).Append('"');
            sb.Append(",\"windowSeconds\":").Append(Fmt(Plugin.TelemetryWindow.Value));
            // Ticks per second for the `qpc` field on every line below. Needed to convert those stamps into
            // the seconds an external capture reports.
            sb.Append(",\"qpcFrequency\":").Append(GpuTelemetry.QpcFrequency());
            Num(sb, "spikeEventMs", Plugin.SpikeEventMs.Value);

            // What was ASKED for. The state it produces is `agents.suppressSlicing` on every window,
            // because that one cannot be read here: `ModCompat.SuppressSlicing` calls EnsureDetected,
            // which latches `_detected` BEFORE probing, and this runs in Awake - so reading it here
            // would freeze the detection against a plugin list BepInEx may not have finished filling,
            // turn the guard off for the session, and leave no trace but different AI behaviour.
            //
            // Named `deferToAiMods` rather than `defer` because the drain budget already emits a
            // `defer` counter, and a name that two fields answer to makes every future probe of it
            // useless.
            sb.Append(",\"deferToAiMods\":").Append(Plugin.DeferToOtherAiMods.Value ? "true" : "false");

            // Which phases were actually expanded, resolved rather than as configured.
            //
            // `Do not expand phases` is a blocklist, and a blocklist leaves no positive trace: a blocked
            // phase and a phase whose children all fall under the 0.5 ms drop threshold produce identical
            // output. Under the old allowlist the setting could be recovered from a log by seeing which
            // children appeared - that inference is gone, so the resolved set has to be stated.
            sb.Append(",\"expandedPhases\":[");
            string[] expanded = PlayerLoopProfiler.ExpandedPhases;
            for (int i = 0; i < expanded.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append('"').Append(Escape(expanded[i])).Append('"');
            }

            sb.Append(']');

            // Whether Unity's incremental collector is available decides the shape of any GC fix. The PMC
            // bot-generation callback ran 21 stop-the-world collections in 16.4s; if the collector is
            // incremental we can tune the time slice so those become many small ones, which is the actual
            // requirement (loading may take as long as it likes, it may not freeze). If it is not
            // incremental - a build-time player setting we cannot flip at runtime - the only lever left is
            // driving CollectIncremental() by hand, which needs isIncremental true anyway. Read once here.
            sb.Append(",\"gcRuntime\":{");
            try
            {
                sb.Append("\"isIncremental\":").Append(Bool(GarbageCollector.isIncremental))
                  .Append(",\"mode\":\"").Append(GarbageCollector.GCMode.ToString()).Append('"')
                  .Append(",\"timeSliceNs\":").Append(GarbageCollector.incrementalTimeSliceNanoseconds);
            }
            catch (Exception e)
            {
                sb.Append("\"error\":\"").Append(Escape(e.GetType().Name)).Append('"');
            }

            sb.Append('}');

            sb.Append(",\"config\":{");
            sb.Append("\"standByEnabled\":").Append(Bool(Plugin.StandByEnabled.Value));
            Num(sb, "sleepDistance", Plugin.SleepDistance.Value);
            Num(sb, "wakeDistance", Plugin.WakeDistance.Value);
            Num(sb, "checkInterval", Plugin.CheckInterval.Value);
            sb.Append(",\"keepFightingBotsAwake\":").Append(Bool(Plugin.KeepFightingBotsAwake.Value));
            sb.Append(",\"sleepImmediately\":").Append(Bool(Plugin.SleepImmediately.Value));
            sb.Append(",\"forceAllRoles\":").Append(Bool(Plugin.ForceStandByForAllRoles.Value));
            sb.Append(",\"fixAgentLeak\":").Append(Bool(Plugin.FixAgentLeak.Value));
            Num(sb, "brainUpdatePeriod", Plugin.BrainUpdatePeriod.Value);
            sb.Append(",\"minBrainsPerFrame\":").Append(Plugin.MinBrainsPerFrame.Value);
            sb.Append('}');

            GpuTelemetry.AppendHeader(sb);

            sb.Append('}');
            Append(sb.ToString());
        }

        /// <summary>
        /// Hands the line to the writer thread. Writing inline with File.AppendAllText cost 85-99ms once per
        /// window - a whole open/write/close per line, rescanned by the OS each time - which put a spike into
        /// the Update phase larger than most of what this file exists to measure. It showed up as exactly one
        /// ~90ms frame in every window and survived every config change, which is what gave it away.
        /// </summary>
        /// <summary>
        /// Wraps each census body in the context fields every line carries and hands it to the writer.
        /// The census owns what it found; this owns when and where it was found, which is the same split
        /// as every other line kind here.
        /// </summary>
        private void DrainCensus()
        {
            string body;
            while (Framesaver.Patches.Census.TryTakeLine(out body))
            {
                StringBuilder sb = new StringBuilder(body.Length + 256);
                sb.Append("{\"type\":\"census\"");
                AppendRaidIdentity(sb);
                sb.Append(",\"state\":\"").Append(_state.ToString().ToLowerInvariant()).Append('"');
                sb.Append(",\"t\":").Append(Fmt(Time.realtimeSinceStartup - _sampleStart));
                sb.Append(",\"qpc\":").Append(GpuTelemetry.Qpc());
                sb.Append(',').Append(body);
                sb.Append('}');
                Append(sb.ToString());
            }
        }

        private void Append(string line)
        {
            lock (_pending)
            {
                _pending.Enqueue(line);
            }

            _pendingSignal.Set();
        }

        private void StartWriter()
        {
            _writer = new Thread(WriterLoop);
            _writer.Name = "Framesaver telemetry";
            _writer.IsBackground = true;
            _writer.Start();
        }

        private void WriterLoop()
        {
            try
            {
                // No BOM: it is not valid mid-stream for NDJSON and trips strict JSON parsers on line 1.
                using (FileStream fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (StreamWriter writer = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    for (;;)
                    {
                        string line = null;
                        lock (_pending)
                        {
                            if (_pending.Count > 0)
                            {
                                line = _pending.Dequeue();
                            }
                        }

                        if (line == null)
                        {
                            if (_writerStop)
                            {
                                return;
                            }

                            _pendingSignal.WaitOne(250);
                            continue;
                        }

                        writer.Write(line);
                        writer.Write('\n');
                        // Flushed per line so a crash still leaves everything up to that point on disk. This is
                        // the only reason the old implementation reopened the file each time.
                        writer.Flush();
                    }
                }
            }
            catch (Exception e)
            {
                try
                {
                    Plugin.LogSource.LogError("Framesaver telemetry write failed: " + e.Message);
                }
                catch
                {
                    // Logging from a background thread is best-effort; never take the game down over telemetry.
                }
            }
        }

        private void OnDestroy()
        {
            GpuTelemetry.Shutdown();

            _writerStop = true;
            _pendingSignal.Set();

            Thread writer = _writer;
            if (writer != null)
            {
                writer.Join(2000);
            }
        }

        private void ResetWindow()
        {
            _flushedByProtocol = false;

            _periodSamples = 0;

            // Position accumulators. _lastPos and _hasPos deliberately survive: distance must not gain a
            // spurious jump at every window boundary from re-seeding against an unset origin.
            _distance = 0d;
            _posSamples = 0;
            // _lastYaw/_lastPitch deliberately survive, like _lastPos: re-seeding would add a spurious
            // delta at every window boundary. _lookSamples = 0 re-bases the cumulative angle to the
            // first sample of the new window, which is what makes `range` per-window.
            _lookSamples = 0;
            _negResidualFrames = 0;
            _negResidualWorstMs = 0d;
            _negResidualSumMs = 0d;
            _frameOverPeriodFrames = 0;
            _frameOverPeriodWorstMs = 0d;
            _frameOverPeriodSumMs = 0d;
            _clockResidualFrames = 0;
            _boundaryMissedFrames = 0;
            PlayerLoopProfiler.ResetBoundaryCounters();
            PlayerLoopProfiler.ResetFrameGapCounters();

            _frame.Reset();
            _gameUpdate.Reset();
            _jobQueue.Reset();
            _aiTotal.Reset();
            _jobSched.Reset();
            _ambientLight.Reset();
            _asyncUpdate.Reset();
            _asyncFixed.Reset();
            _asyncDrained.Reset();
            _asyncFixedSkips = 0;
            ProfileBuild.ResetWindow();
            BotBackup.ResetWindow();
            BundleLoad.ResetWindow();
            SpawnAttempts.ResetWindow();
            AsyncDrain.ResetWindow();
            RaidInit.ResetWindow();
            Framesaver.Patches.UpdateManualTiming.ResetWindow();
            _playerLate.Reset();
            _playerTick.Reset();
            if (_phases != null)
            {
                for (int i = 0; i < _phases.Length; i++)
                {
                    _phases[i].Reset();
                }
            }
            GpuTelemetry.ResetWindow();
            GcControl.ResetWindow();
            _heapMb.Reset();
            _gameUpdateSamples.Clear();
            _frameSamples.Clear();
            _tickedSum = 0L;
            _liveSum = 0L;
            _allocatedBytes = 0d;
            _gen0Base = _gen0;
            _windowStart = Time.realtimeSinceStartup;
        }

        private static void Block(StringBuilder sb, string name, Stat s)
        {
            sb.Append(",\"").Append(name).Append("\":{\"avg\":").Append(Fmt(s.Average))
              .Append(",\"min\":").Append(Fmt(s.Min))
              .Append(",\"max\":").Append(Fmt(s.Max)).Append('}');
        }

        private static void Num(StringBuilder sb, string name, double value)
        {
            sb.Append(",\"").Append(name).Append("\":").Append(Fmt(value));
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        /// <summary>Invariant culture matters: a comma decimal separator would emit invalid JSON.</summary>
        private static string Fmt(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "null";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// What the numbers were measured AGAINST. Every patch, every timing and every
        /// spawn table belongs to a specific SPT and EFT build, so pooling two versions
        /// compares two different programs - and until now nothing in the file said
        /// which. The undocumented `Base` log set is excluded on exactly this basis, and
        /// **no field in those 211 windows can tell you**: they read era A on the cfg key
        /// count, identical to documented era-A logs. The criterion lived outside the
        /// data, in an install directory name.
        ///
        /// **The key is `sptAssembly`, not `spt`, and the name is the documentation.**
        /// It is spt-reflection's assembly version. That is how the ecosystem reports SPT
        /// in practice - spt-prepatch carries the same 4.0.13.0 that BepInEx logs - but
        /// the two are not the same fact, and **a point release that does not bump the
        /// assembly would make a field named `spt` authoritative-looking and wrong.**
        /// Naming it for what it reads travels with the data; a caveat in a README does
        /// not. Same reasoning as citing a predicate rather than a line number, and the
        /// same defect Shutter renamed `generateMs` to avoid.
        ///
        /// Read from the loaded assembly rather than Chainloader.PluginInfos, and that is
        /// deliberate: this runs in Awake, where reading the plugin list would latch
        /// ModCompat against a list BepInEx may not have finished filling - a LOGGING
        /// change that would switch SuppressSlicing off for the session and leave no
        /// trace but different AI behaviour. We already reference the assembly, so it is
        /// loaded by the time anything here runs.
        ///
        /// Two client fields because EFT does not put its build where you would expect
        /// it. Assembly-CSharp reports 0.0.0.0, and EFT stamps its own build string into
        /// the Unity version slot - BepInEx logs "Running under Unity v0.16.9.4008" from
        /// it. Emitting both costs two strings once per file and removes the guess about
        /// which one is populated.
        /// </summary>
        private static void AppendPlatform(StringBuilder sb)
        {
            sb.Append(",\"platform\":{\"sptAssembly\":\"").Append(Escape(SptVersion()))
              .Append("\",\"game\":\"").Append(Escape(Application.version ?? ""))
              .Append("\",\"unity\":\"").Append(Escape(Application.unityVersion ?? ""))
              .Append("\"}");
        }

        /// <summary>
        /// **A frame cap can make goal 1 pass for reasons that have nothing to do with
        /// this mod, and that is why this block leads the machine one.**
        ///
        /// A tester on 60 Hz vsync reports a p50 pinned near 16.67 ms. That clears our
        /// `p50 >= 60 fps` criterion while being insensitive to everything Framesaver
        /// does - so their report that it works is their monitor. It is not an ambiguous
        /// null, it is **a false pass on the primary success criterion**, arriving in the
        /// number we are least likely to interrogate because it agrees with us. A missing
        /// CPU makes a comparison meaningless, which is visible; a cap makes it wrong in
        /// our favour, which is not.
        ///
        /// **This is a label, not a check.** These are read once, and a mid-session vsync
        /// toggle would not be caught. The check is in the data: a cap at refresh R
        /// forbids any window below 1000/R, so a floor test on p50 EXCLUDES caps
        /// (`analysis/alpha-vsync-floor.py`). Note the asymmetry - a floor can only rule
        /// a cap out, never confirm one, because nothing below the budget separates an
        /// uncapped machine from a slow one. Label and check, computed independently.
        /// </summary>
        private static void AppendDisplay(StringBuilder sb)
        {
            Resolution res = Screen.currentResolution;
            sb.Append(",\"display\":{\"vSyncCount\":").Append(QualitySettings.vSyncCount)
              .Append(",\"targetFrameRate\":").Append(Application.targetFrameRate)
              // refreshRateRatio, not the obsolete refreshRate int: 165 Hz panels report
              // 164 when truncated, and the floor test above compares against 1000/R.
              .Append(",\"refreshHz\":").Append(Fmt(res.refreshRateRatio.value))
              .Append(",\"width\":").Append(res.width)
              .Append(",\"height\":").Append(res.height)
              .Append(",\"fullScreenMode\":\"").Append(Escape(Screen.fullScreenMode.ToString()))
              .Append("\"}");
        }

        /// <summary>
        /// For an outside tester's log, p50 is a property of their machine before it is a
        /// property of anything we did - and a bot-heavy raid is mostly main thread, so
        /// the CPU is the term that matters. `gpuDevice` was already here; nothing
        /// described the processor.
        /// </summary>
        private static void AppendSystem(StringBuilder sb)
        {
            sb.Append(",\"system\":{\"cpu\":\"").Append(Escape(SystemInfo.processorType ?? ""))
              .Append("\",\"cores\":").Append(SystemInfo.processorCount)
              .Append(",\"cpuMhz\":").Append(SystemInfo.processorFrequency)
              .Append(",\"ramMb\":").Append(SystemInfo.systemMemorySize)
              .Append(",\"os\":\"").Append(Escape(SystemInfo.operatingSystem ?? ""))
              .Append("\"}");
        }

        /// <summary>
        /// Split out from AppendPlatform so it can be tested: the two Unity reads beside
        /// it are engine ECalls and throw outside the runtime, which would make the whole
        /// block untestable for the sake of the two fields that need it least.
        /// </summary>
        private static string SptVersion()
        {
            try
            {
                return typeof(SPT.Reflection.Patching.ModulePatch).Assembly.GetName().Version.ToString();
            }
            catch (Exception)
            {
                // A version we cannot read must read as absent, never as a default that
                // looks like a real build.
                return "";
            }
        }

        private static string Escape(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Resolves to BepInEx/plugins alongside the built DLL. Deliberately avoids BepInEx.Paths so the log
        /// location does not depend on a BepInEx API that has moved between versions.
        /// </summary>
        private static string PluginDirectory()
        {
            try
            {
                string location = typeof(Telemetry).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    return Path.GetDirectoryName(location);
                }
            }
            catch (Exception)
            {
                // fall through
            }

            return Path.Combine(Application.dataPath, "..");
        }

        private static string Sanitise(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            }

            return sb.ToString();
        }

        private class Stat
        {
            public int Count;
            private double _sum;
            public double Min = double.MaxValue;
            public double Max = double.MinValue;

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

                if (value < Min)
                {
                    Min = value;
                }

                if (value > Max)
                {
                    Max = value;
                }
            }

            public void Reset()
            {
                Count = 0;
                _sum = 0d;
                Min = double.MaxValue;
                Max = double.MinValue;
            }
        }
    }
}
