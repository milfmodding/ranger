using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        /// </summary>
        private void AppendProc(StringBuilder sb)
        {
            try
            {
                System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess();
                p.Refresh();

                long ws = p.WorkingSet64;
                long priv = p.PrivateMemorySize64;

                sb.Append(",\"proc\":{\"wsMb\":").Append(ws / 1048576L);
                sb.Append(",\"privMb\":").Append(priv / 1048576L);
                sb.Append(",\"notResidentMb\":").Append((priv - ws) / 1048576L);
                sb.Append(",\"wsDeltaMb\":").Append(_lastWs == 0L ? 0L : (ws - _lastWs) / 1048576L);
                sb.Append(",\"privDeltaMb\":").Append(_lastPriv == 0L ? 0L : (priv - _lastPriv) / 1048576L);
                sb.Append('}');

                _lastWs = ws;
                _lastPriv = priv;
            }
            catch (Exception)
            {
                // Explicit failure rather than omission - an absent block would be indistinguishable
                // from a run where nobody asked for it.
                sb.Append(",\"proc\":null");
            }
        }

        private long _lastWs;
        private long _lastPriv;

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
                PlayerLoopProfiler.ReadAndReset();
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
            }

            _periodSamples++;

            // Wall time covered by the phase accumulators just read, measured directly rather than taken from
            // GameFrameMeasurer. The game's counter reports the *previous* frame, so pairing it with this
            // frame's phases produced residuals that were off by a frame - including negative ones.
            long now = Stopwatch.GetTimestamp();
            double periodMs = _lastSampleTicks == 0L ? 0d : AiTiming.ToMs(now - _lastSampleTicks);
            _lastSampleTicks = now;

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
        /// Update, PreLateUpdate and PostLateUpdate are the previous frame's. The sum is still exactly one
        /// frame's wall time, which is what makes the residual valid.
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
            CountBots(ref awake, ref asleep);

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
              .Append(",\"animCulled\":").Append(Framesaver.Patches.SleepingBotAnimatorPatch.CulledLastFrame).Append('}');

            sb.Append(",\"agents\":{\"live\":").Append(AICoreControllerUpdatePatch.LiveAgents)
              .Append(",\"pendingRemoval\":").Append(AICoreControllerUpdatePatch.PendingRemoval)
              .Append(",\"removedTotal\":").Append(AICoreControllerUpdatePatch.RemovedTotal).Append('}');

            float elapsed = Mathf.Max(0.001f, Time.realtimeSinceStartup - _windowStart);
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
            sb.Append(",\"frameGapArmed\":").Append(Bool(PlayerLoopProfiler.FrameGapArmed));
            sb.Append(",\"endOfFrameFires\":").Append(PlayerLoopProfiler.EndOfFrameFires);
            sb.Append(",\"startOfFrameFires\":").Append(PlayerLoopProfiler.StartOfFrameFires);

            sb.Append(",\"cfg\":{\"standBy\":").Append(Bool(Plugin.StandByEnabled.Value))
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

        private static void CountBots(ref int awake, ref int asleep)
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
            }
        }

        private void WriteHeader()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"type\":\"header\"");
            sb.Append(",\"version\":\"0.1.0\"");
            sb.Append(",\"started\":\"").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"tag\":\"").Append(Escape(Plugin.RunTag.Value)).Append('"');
            sb.Append(",\"windowSeconds\":").Append(Fmt(Plugin.TelemetryWindow.Value));
            // Ticks per second for the `qpc` field on every line below. Needed to convert those stamps into
            // the seconds an external capture reports.
            sb.Append(",\"qpcFrequency\":").Append(GpuTelemetry.QpcFrequency());
            Num(sb, "spikeEventMs", Plugin.SpikeEventMs.Value);

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
            _periodSamples = 0;

            // Position accumulators. _lastPos and _hasPos deliberately survive: distance must not gain a
            // spurious jump at every window boundary from re-seeding against an unset origin.
            _distance = 0d;
            _posSamples = 0;
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
