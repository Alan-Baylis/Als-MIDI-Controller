using System;
using System.Text;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// What a second press does to a ONE-SHOT that is still sounding. This was a
    /// bool, which could only say "stop" or "don't" — and neither is what a drum
    /// pad does. A one-shot is a one-shot: hitting it again fires it again.
    /// </summary>
    public enum OneShotRepress
    {
        Restart,   // fire it again from the top, quantised exactly as the first press was
        Stop,      // the old pressStopsAnySounding = true behaviour
        Ignore     // let it ring — the press still flashes the pad
    }
    
    /// <summary>
    /// Orchestration. Applies PadState to Rack voices, triggers playback from
    /// mouse or MIDI, and is the only class that writes clips into the model.
    ///
    /// It is also the sole owner of pad-side ClipCache references: every pad that
    /// holds a cached clip has its key in _held, and every path out of that state
    /// (assign, clear, move, copy, swap, teardown) balances the refcount.
    ///
    /// TIMING. Three things are deferred to the metronome rather than done on the press:
    ///   • a pad with quantizeToBeat waits for its startBeat before it sounds
    ///     (startBeat 0 = the NEXT beat, whichever that turns out to be);
    ///   • pressing a SOUNDING looped pad asks it to stop, which happens on the next
    ///     downbeat so the bar finishes rather than being chopped;
    ///   • a quantised LOOP is re-started on the grid instead of wrapping the instant
    ///     the clip ends, so a sample that is not exactly a bar long cannot drift.
    /// All are cancellable by pressing the pad again, and all fall back to
    /// "immediately" when the metronome is not running — a queue with no clock would
    /// simply never fire. Every one of those fallbacks now SAYS SO on the ticker,
    /// because a pad that quietly ignores its own timing setting is indistinguishable
    /// from a bug.
    ///
    /// LEADING SILENCE. A clip whose first 40 ms are silence sounds 40 ms late however
    /// tightly it is quantised, so the play head is moved past it. That is an OFFSET,
    /// not an edit: the clip in ClipCache is shared and is never modified.
    /// </summary>
    [DisallowMultipleComponent]
    public class PadController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Rack rack;
        [SerializeField] private PadModel model;
        [SerializeField] private SelectionService selection;
        [SerializeField] private ClipCache clipCache;
        [SerializeField] private NotificationService notifications;
        [SerializeField] private Metronome metronome;

        [Header("Playback")]
        [SerializeField, Range(0f, 1f)] private float mouseVelocity = 0.8f;
        [SerializeField] private bool retriggerFromStart = true;

        [Header("Timing")]
        [Tooltip("Master switch for quantised starts. Off = every pad plays the instant " +
                 "it is hit, whatever its own setting says.")]
        [SerializeField] private bool quantizeEnabled = true;

        [Tooltip("What a second press does to a ONE-SHOT that is still sounding. " +
                 "Restart = fire it again from the top, obeying the pad's own quantise " +
                 "setting; Stop = silence it; Ignore = let it ring. A LOOP always stops " +
                 "on a second press whatever this says, because that is the only thing " +
                 "a second press on a loop can sensibly mean.")]
        [SerializeField] private OneShotRepress oneShotRepress = OneShotRepress.Restart;

        [Tooltip("What a press does to a pad that is already WAITING to stop. On = the " +
                 "impatient second press is obeyed and it stops NOW; off = the stop is " +
                 "called off and the pad keeps going.")]
        [SerializeField] private bool secondPressStopsImmediately = true;

        [Tooltip("Pressing a SOUNDING looped pad stops it. On = it finishes the bar first; " +
                 "off = it stops under your finger.")]
        [SerializeField] private bool stopLoopsAtBarEnd = true;

        [Tooltip("With the above on, wait for the DOWNBEAT (beat 1). Off = the next beat, " +
                 "which is tighter but cuts across the bar.")]
        [SerializeField] private bool stopOnDownbeatOnly = true;

        [Tooltip("Pressing a pad that is already queued cancels the queue instead of " +
                 "re-queueing it.")]
        [SerializeField] private bool pressAgainCancels = true;

        [Tooltip("A pad that LOOPS and QUANTIZES is re-started on its beat each time the " +
                 "clip ends, instead of AudioSource.loop wrapping at clip length — which " +
                 "is free-running against the bar and drifts. Off = the old behaviour.")]
        [SerializeField] private bool resyncLoopsToGrid = true;

        [Tooltip("Say so when a pad asks to wait for a beat and there is no clock to wait " +
                 "for, or when a grid-synced loop has to fall back to wrapping. Throttled, " +
                 "because it would otherwise fire on every press.")]
        [SerializeField] private bool warnWhenClockMissing = true;

        [Header("Clips")]
        [Tooltip("Loading a clip replaces the pad's caption with the new file's name. " +
                 "Off = a custom caption survives a re-load and has to be cleared by hand.")]
        [SerializeField] private bool overwriteLabelOnAssign = true;

        [Tooltip("Start past any silence at the head of a clip, so a pad fires when you " +
                 "hit it rather than when the file gets around to making a sound. The clip " +
                 "is never modified — only the play head moves.")]
        [SerializeField] private bool trimLeadingSilence = true;

        [Tooltip("Anything quieter than this counts as silence. -48 dBFS is far below a " +
                 "room noise floor and below the start of any deliberate fade-in.")]
        [SerializeField, Range(-90f, -20f)] private float silenceThresholdDb = -48f;

        [Tooltip("Only the first N seconds are examined, so this is ALSO the most that can " +
                 "ever be skipped. A clip that is silent for longer is silent on purpose.")]
        [SerializeField, Range(0.1f, 5f)] private float maxTrimSeconds = 1f;

        [Tooltip("Start this far BEFORE the first audible sample, so an attack transient " +
                 "is never clipped. 2 ms is inaudible.")]
        [SerializeField, Range(0f, 0.05f)] private float trimBackoffSeconds = 0.002f;

        [Tooltip("Log every trim measurement to the console. The fastest way to find out " +
                 "why a particular file still sounds late.")]
        [SerializeField] private bool logTrim = false;

        public PadModel Model => model;
        public SelectionService Selection => selection;
        public ClipCache Cache => clipCache;

        [SerializeField] private Manager manager;   // assign in Inspector

        private bool _testTonesActive;

        /// <summary>Cache key each pad currently holds a reference to. Null = none.</summary>
        private readonly string[] _held = new string[NoteGrid.ArraySize];

        /// <summary>Bumped per assign so a slow decode can't overwrite a newer one.</summary>
        private readonly int[] _assignToken = new int[NoteGrid.ArraySize];

        /// <summary>Last trigger velocity per pad, so volume edits during playback keep the dynamic.</summary>
        private readonly float[] _velocity = new float[NoteGrid.ArraySize];

        /// <summary>Sample the pad's clip actually starts at. 0 = the top of the file.</summary>
        private readonly int[] _trimStart = new int[NoteGrid.ArraySize];

        // ---- queued transport, driven by Metronome.Beat ----
        private readonly bool[]  _startQueued = new bool[NoteGrid.ArraySize];
        /// <summary>
        /// A queued RESTART, as opposed to a queued fresh start. The difference matters
        /// to pressAgainCancels: cancelling a fresh start silences the voice, but
        /// cancelling a restart must NOT, because that voice is the pass the PREVIOUS
        /// press started and nobody asked for it to stop.
        /// </summary>
        private readonly bool[] _restartQueued = new bool[NoteGrid.ArraySize];
        private readonly int[]   _startBeat   = new int[NoteGrid.ArraySize];   // 0 = next beat
        private readonly float[] _startVel    = new float[NoteGrid.ArraySize];
        private readonly bool[]  _stopQueued  = new bool[NoteGrid.ArraySize];

        /// <summary>Pad is a grid-synced loop: when its clip ends, re-queue rather than stop.</summary>
        private readonly bool[] _loopArmed = new bool[NoteGrid.ArraySize];

        private float _nextClockWarning;
        private float _nextLoopWarning;

        public event Action<int, float> PadTriggered;   // note, velocity 0-1
        public event Action<int>        PadReleased;

        /// <summary>
        /// Raised when a voice STARTS a clip: a trigger, a retrigger, or the wrap of a
        /// loop. Input events cannot tell you about the third, which is the one that
        /// matters for "which pads are sounding".
        /// </summary>
        public event Action<int> PadPulsed;

        /// <summary>(note, waitingToStart, waitingToStop). For a "queued" pad colour later.</summary>
        public event Action<int, bool, bool> PadQueueChanged;

        [Tooltip("Watch the voices so a pad can pulse on every pass of a loop, not just " +
                 "on the press that started it.")]
        [SerializeField] private bool trackPlayback = true;

        private readonly bool[] _wasPlaying  = new bool[NoteGrid.ArraySize];
        private readonly int[]  _lastSamples = new int[NoteGrid.ArraySize];

        [Tooltip("Swapping the clip on a SOUNDING pad keeps it sounding. Off = the old " +
                 "behaviour, where a drop or a Load stops the voice under your finger.")]
        [SerializeField] private bool keepPlayingOnClipChange = true;

        [Tooltip("With the above on, resume at the same sample position. Off = the new " +
                 "clip starts from the top. Position is wrapped when the replacement is " +
                 "shorter, so a 2-bar loop swapped for a 1-bar one lands somewhere real.")]
        [SerializeField] private bool resumeFromSamePosition = true;

        private void Start()
        {
            if (manager != null && manager.DebugEnabled) SetTestTonesEnabled(true);

            // These two switches silently change what EVERY quantised pad does, and an
            // Inspector bool that is off for no reason is the hardest kind of fault to
            // see. Said once, at startup, on the console only.
            if (!quantizeEnabled)
                Debug.LogWarning($"[{nameof(PadController)}] Quantize Enabled is OFF, so every " +
                                 "pad plays the instant it is hit and no loop is grid-synced.",
                                 this);
            else if (!resyncLoopsToGrid)
                Debug.LogWarning($"[{nameof(PadController)}] Resync Loops To Grid is OFF, so a " +
                                 "quantised loop still wraps at the end of its clip and drifts " +
                                 "against the bar.", this);
        }

        private void Awake()
        {
            if (rack          == null) rack          = FindAnyObjectByType<Rack>();
            if (model         == null) model         = FindAnyObjectByType<PadModel>();
            if (selection     == null) selection     = FindAnyObjectByType<SelectionService>();
            if (manager       == null) manager       = FindAnyObjectByType<Manager>();
            if (clipCache     == null) clipCache     = FindAnyObjectByType<ClipCache>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            // Include inactive: a Metronome parented under a side-panel page is hidden
            // half the time, and a null reference here would be a silent loss of every
            // quantised start rather than the loud one the clock deserves.
            if (metronome == null)
                metronome = FindAnyObjectByType<Metronome>(FindObjectsInactive.Include);

            if (rack == null || model == null)
            {
                Debug.LogError($"{nameof(PadController)} needs both a {nameof(Rack)} " +
                               $"and a {nameof(PadModel)}.", this);
                enabled = false;
                return;
            }

            if (clipCache == null)
                Debug.LogWarning($"{nameof(PadController)} found no {nameof(ClipCache)}. " +
                                 "Clips will still load, but every load leaks an AudioClip.", this);

            rack.InitializeVoices();            // idempotent

            for (int i = 0; i < _velocity.Length; i++) _velocity[i] = 1f;
        }

        private void OnEnable()
        {
            if (manager != null) manager.DebugChanged += SetTestTonesEnabled;

            if (model != null)
            {
                model.PadChanged    += ApplyToVoice;
                model.ModelReloaded += ApplyAllToVoices;
            }

            if (metronome != null)
            {
                metronome.Beat    += OnBeat;
                metronome.Stopped += OnMetronomeStopped;
            }
        }

        private void OnDisable()
        {
            if (manager != null) manager.DebugChanged -= SetTestTonesEnabled;

            if (model != null)
            {
                model.PadChanged    -= ApplyToVoice;
                model.ModelReloaded -= ApplyAllToVoices;
            }

            if (metronome != null)
            {
                metronome.Beat    -= OnBeat;
                metronome.Stopped -= OnMetronomeStopped;
            }
        }

        private void OnDestroy() => ReleaseAllHeld();

        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        /// <summary>Is this pad's voice sounding right now?</summary>
        public bool IsPlaying(int note)
        {
            var voice = rack != null ? rack.GetVoice(note) : null;
            return voice != null && voice.isPlaying;
        }

        /// <summary>
        /// Is ANY voice sounding? Cheap enough to poll: 64 bools and an early exit.
        /// The screensaver asks this, because "nothing has been clicked for two minutes"
        /// and "nothing is happening" are not the same statement while a loop is running.
        /// </summary>
        public bool AnyVoicePlaying
        {
            get
            {
                if (rack == null) return false;

                foreach (var voice in rack.Voices)
                    if (voice != null && voice.isPlaying) return true;

                return false;
            }
        }

        /// <summary>Voices currently sounding. For a debug overlay and the transport strip.</summary>
        public int PlayingCount
        {
            get
            {
                if (rack == null) return 0;

                int n = 0;
                foreach (var voice in rack.Voices)
                    if (voice != null && voice.isPlaying) n++;

                return n;
            }
        }

        /// <summary>Anything waiting on the metronome counts as "busy" too.</summary>
        public bool AnyQueued
        {
            get
            {
                foreach (int note in NoteGrid.All)
                    if (_startQueued[note] || _stopQueued[note]) return true;

                return false;
            }
        }

        public bool IsStartQueued(int note) =>
            NoteGrid.IsGridNote(note) && _startQueued[note];

        public bool IsStopQueued(int note) =>
            NoteGrid.IsGridNote(note) && _stopQueued[note];

        /// <summary>Is the transport clock actually ticking? For views and for diagnostics.</summary>
        public bool ClockRunning => metronome != null && metronome.Running;

        // ------------------------------------------------------------ silence trim

        /// <summary>Sample this pad starts playing at. 0 = the top of the file.</summary>
        public int TrimStartSample(int note) =>
            NoteGrid.IsGridNote(note) ? _trimStart[note] : 0;

        /// <summary>How much leading silence is being skipped, in seconds. 0 = none.</summary>
        public float TrimSeconds(int note)
        {
            if (!NoteGrid.IsGridNote(note) || _trimStart[note] <= 0) return 0f;

            var clip = GetClip(note);
            if (clip == null || clip.frequency <= 0) return 0f;

            return _trimStart[note] / (float)clip.frequency;
        }

        /// <summary>
        /// Measured once per clip change, not per press: the scan reads up to a second of
        /// samples, which is nothing on a load and would be absurd on every hit.
        /// </summary>
        private int MeasureTrim(int note, PadState pad)
        {
            // pad.HasClip is "a FILE is assigned", which excludes the generated debug
            // test tones — those open with a deliberate 5 ms fade, and trimming it would
            // put back the click that fade exists to remove.
            if (!trimLeadingSilence || pad == null || pad.clip == null || !pad.HasClip) return 0;

            var result = ClipAnalysis.FindStart(pad.clip, silenceThresholdDb,
                                                maxTrimSeconds, trimBackoffSeconds);

            if (logTrim)
                Debug.Log($"[{nameof(PadController)}] Pad {note} \"{pad.clip.name}\": " +
                          $"{result.Reason}.", this);

            return result.StartSample;
        }

        private void Update()
        {
            if (rack == null) return;

            // The scan does two jobs now, so it runs if EITHER is wanted.
            if (!trackPlayback && !resyncLoopsToGrid) return;

            foreach (int note in NoteGrid.All)
            {
                var voice = rack.GetVoice(note);
                if (voice == null) continue;

                if (!voice.isPlaying)
                {
                    bool justEnded = _wasPlaying[note];

                    _wasPlaying[note]  = false;
                    _lastSamples[note] = 0;

                    // The clip ran out rather than being stopped: every deliberate stop
                    // disarms first, so reaching here armed means "the loop wants to go
                    // round again, on the grid".
                    if (justEnded && _loopArmed[note]) RequeueLoop(note);
                    continue;
                }

                int samples = voice.timeSamples;

                // Either it wasn't playing last frame, or the playhead jumped BACKWARDS —
                // which is a loop wrapping, or a retrigger that started and stopped
                // inside one frame. Both are "the clip just started" as far as the eye
                // is concerned.
                if (trackPlayback && (!_wasPlaying[note] || samples < _lastSamples[note]))
                    PadPulsed?.Invoke(note);

                _wasPlaying[note]  = true;
                _lastSamples[note] = samples;
            }
        }

        /// <summary>
        /// Fills every pad that has no real clip with a generated sine, so the grid can be
        /// auditioned with nothing loaded. Pads with a clipPath are never touched, and the
        /// tones are runtime-only (clip is [NonSerialized]), so sessions are unaffected.
        /// </summary>
        public void SetTestTonesEnabled(bool enabled)
        {
            if (_testTonesActive == enabled) return;
            _testTonesActive = enabled;

            foreach (int note in NoteGrid.All)
            {
                var pad = model.Get(note);
                if (pad == null || pad.HasClip) continue;   // real content wins, always

                model.Set(note, p => p.clip = enabled ? rack.CreateTestTone(note) : null);
            }
        }

        // ------------------------------------------------------- state → voice

        private void ApplyAllToVoices()
        {
            foreach (int note in NoteGrid.All) ApplyToVoice(note);
        }

        /// <summary>
        /// Pushes a PadState onto its AudioSource. The only place voices are configured.
        ///
        /// A CLIP change is the only property that has to interrupt playback — and even
        /// then only as far as Unity forces it to: assigning a clip stops the source, so
        /// a sounding pad is captured, swapped, and re-started at the same position.
        /// Everything else (volume, loop, pitch) is applied live, because nudging a
        /// slider must not kill the sound you are adjusting it against.
        /// </summary>
        private void ApplyToVoice(int note)
        {
            var voice = rack.GetVoice(note);
            var pad   = model.Get(note);
            if (voice == null || pad == null) return;

            bool resume   = false;
            int  resumeAt = 0;

            if (voice.clip != pad.clip)
            {
                bool wasPlaying = voice.isPlaying;
                int  position   = wasPlaying ? voice.timeSamples : 0;

                // Unity stops the source the moment a new clip is assigned, so this is
                // not optional — it is just made explicit.
                if (wasPlaying) voice.Stop();

                var previous = voice.clip;
                voice.clip = pad.clip;
                rack.DisposeIfGenerated(previous);   // no-op for cached user clips

                // Measured HERE because this is the one place a clip reaches a voice,
                // so no assignment path can skip it.
                _trimStart[note] = MeasureTrim(note, pad);

                resume = keepPlayingOnClipChange && wasPlaying && pad.clip != null;

                if (resume && resumeFromSamePosition && pad.clip.samples > 0)
                {
                    // Wrapped, not clamped: a shorter replacement has no such sample,
                    // and parking on the last frame of it is a click, not a continuation.
                    resumeAt = position % pad.clip.samples;
                }

                // A stopped-and-not-resumed pad is not going round again.
                if (!resume) _loopArmed[note] = false;
            }

            // A grid-synced loop must NOT use AudioSource.loop: that wraps at clip
            // length, which is exactly the drift the re-sync exists to remove.
            voice.loop  = pad.loop && !UsesLoopResync(pad);
            voice.pitch = pad.pitch;

            // Armed from the CURRENT state, not from the press that started the sound.
            // Ticking Loop — or choosing a beat — on a pad that is already sounding has
            // to take effect on THIS pass; the old code only ever disarmed, so such a
            // pad lost AudioSource.loop and gained nothing in its place.
            _loopArmed[note] = voice.isPlaying && UsesLoopResync(pad);

            // Velocity is preserved so a live slider drag scales the hit you played,
            // rather than jumping to full volume.
            voice.volume = pad.volume * _velocity[note];

            if (!resume) return;

            voice.timeSamples = Mathf.Clamp(resumeAt, 0, Mathf.Max(0, pad.clip.samples - 1));
            voice.Play();

            // Re-armed here because the stop above disarmed it, and a grid-synced loop
            // that survives a clip swap must still ask for its next pass.
            _loopArmed[note] = UsesLoopResync(pad);
        }

        /// <summary>
        /// Silences every voice. Does not touch the model, so pad state, loop flags and
        /// selection all survive — this is a panic button, not a reset.
        /// </summary>
        public void StopAllVoices()
        {
            ClearAllQueues();

            Array.Clear(_loopArmed, 0, _loopArmed.Length);

            if (rack == null) return;

            foreach (var voice in rack.Voices)
                if (voice != null && voice.isPlaying) voice.Stop();
        }

        // ------------------------------------------------------------ playback

        public void TriggerPad(int note, float velocity)
        {
            var voice = rack  != null ? rack.GetVoice(note) : null;
            var pad   = model != null ? model.Get(note)     : null;

            if (!NoteGrid.IsGridNote(note) || voice == null || pad == null)
            {
                PadTriggered?.Invoke(note, velocity);   // fires even with no pad, for UI/LED
                return;
            }

            _velocity[note] = Mathf.Clamp01(velocity);

            // 1. Already waiting to STOP. The press is an answer to a question this pad
            //    has already asked, so it must not be read as a fresh trigger.
            if (_stopQueued[note])
            {
                if (secondPressStopsImmediately)
                {
                    StopPad(note);                      // clears both queues and raises
                }
                else
                {
                    _stopQueued[note] = false;
                    _loopArmed[note]  = UsesLoopResync(pad);   // NOT false: a grid-synced
                    RaiseQueue(note);                          // loop must stay armed
                }

                PadTriggered?.Invoke(note, velocity);
                return;
            }

            // 2. Waiting to START. The press is a cancel, not a second booking.
            if (pressAgainCancels && _startQueued[note])
            {
                bool wasRestart = _restartQueued[note];

                _startQueued[note]   = false;
                _restartQueued[note] = false;
                _loopArmed[note]     = false;
                RaiseQueue(note);

                // Defensive: a queue over a still-ringing voice should not leave the
                // voice behind. Cancelling must mean silence, not "half cancelled".
                //
                // EXCEPT for a restart. That voice is not a leftover — it is the pass
                // the PREVIOUS press started, and it was never asked to stop. Silencing
                // it would turn "no, don't retrigger" into "stop", which is a different
                // instruction and the one the user did not give.
                if (!wasRestart && voice.isPlaying) voice.Stop();

                PadTriggered?.Invoke(note, velocity);
                return;
            }

            // 3. SOUNDING. A LOOP is asked to stop — the only thing a second press on a
            //    loop can sensibly mean, and stopLoopsAtBarEnd decides when. A ONE-SHOT
            //    is a one-shot: pressing it again fires it again. Stopping it made a
            //    one-shot pad alternate play/silence like a latch, which is not how a
            //    drum pad behaves anywhere else.
            bool sounding = voice.clip != null && voice.isPlaying;

            if (sounding && pad.loop)
            {
                RequestStop(note);
                PadTriggered?.Invoke(note, velocity);
                return;
            }

            if (sounding && oneShotRepress == OneShotRepress.Stop)
            {
                RequestStop(note);
                PadTriggered?.Invoke(note, velocity);
                return;
            }

            if (sounding && oneShotRepress == OneShotRepress.Ignore)
            {
                // The press still raises PadTriggered, so the pad flashes. Silence AND
                // no flash is indistinguishable from a pad that is broken.
                PadTriggered?.Invoke(note, velocity);
                return;
            }

            // 4. Start: a first press, or the restart of a sounding one-shot. Both take
            //    the SAME path, so a restart obeys quantizeToBeat/startBeat exactly as
            //    the first press did — the point of the feature is that a re-hit lands
            //    in time, not that it is instant.
            if (ShouldQuantize(pad, voice))
            {
                QueueStart(note, velocity, pad.startBeat, sounding);
            }
            else
            {
                // Deliberately NOT gated on quantizeEnabled: the master switch being off
                // is one of the reasons the pad is about to ignore its own setting, and
                // it is the reason nothing else would ever mention.
                if (pad.quantizeToBeat && voice.clip != null) WarnNoClock(note);

                PlayNow(note, velocity, sounding);
            }

            PadTriggered?.Invoke(note, velocity);
        }

        public void TriggerPadFromMouse(int note) => TriggerPad(note, mouseVelocity);

        public void ReleasePad(int note) => PadReleased?.Invoke(note);

        /// <summary>Stop one pad regardless of loop, queue or bar position.</summary>
        public void StopPad(int note)
        {
            if (!NoteGrid.IsGridNote(note)) return;

            _startQueued[note] = false;
            _restartQueued[note] = false;
            _stopQueued[note]  = false;
            _loopArmed[note]   = false;
            RaiseQueue(note);

            var voice = rack != null ? rack.GetVoice(note) : null;
            if (voice != null && voice.isPlaying) voice.Stop();
        }

        private bool ShouldQuantize(PadState pad, AudioSource voice) =>
            quantizeEnabled && pad.quantizeToBeat && voice.clip != null &&
            metronome != null && metronome.Running;

        /// <summary>
        /// Does this pad loop by being re-triggered on the grid rather than by
        /// AudioSource.loop? Deliberately does NOT ask whether the clock is running:
        /// the voice's loop flag is set once, and a clock that stops later is handled
        /// by RequeueLoop falling back to an immediate restart.
        /// </summary>
        private bool UsesLoopResync(PadState pad) =>
            resyncLoopsToGrid && quantizeEnabled && pad != null &&
            pad.loop && pad.quantizeToBeat;

        /// <summary>
        /// fromStart = this is a deliberate RESTART of a pad that is still sounding, so
        /// it must rewind even when retriggerFromStart is off. Play() on a source that
        /// is already playing keeps whatever position the play head had, so the only
        /// dependable restart is Stop() first — which is also what lets the trim offset
        /// below apply, so a restart is trimmed exactly like a first press.
        /// </summary>
        private void PlayNow(int note, float velocity, bool fromStart = false)
        {
            var voice = rack  != null ? rack.GetVoice(note) : null;
            var pad   = model != null ? model.Get(note)     : null;
            if (voice == null || pad == null || voice.clip == null) return;

            voice.volume = pad.volume * Mathf.Clamp01(velocity);

            if ((retriggerFromStart || fromStart) && voice.isPlaying) voice.Stop();
            _restartQueued[note] = false;   // whatever this press was, it has now fired
            
            voice.loop = pad.loop && !UsesLoopResync(pad);

            // The head is set BEFORE Play: a clip with 40 ms of silence at the front
            // would otherwise sound 40 ms late however tightly it was quantised. Only
            // when we are genuinely starting from the top — with retriggerFromStart off
            // and the voice still running, the head belongs to the sound already playing.
            //
            // NOTE: a FREE-RUNNING loop (AudioSource.loop) wraps to sample 0, so the
            // silence returns on every pass. A grid-synced loop re-triggers through here
            // and stays trimmed — one more reason to leave Resync Loops To Grid on.
            if (!voice.isPlaying)
            {
                int start = _trimStart[note];
                voice.timeSamples = start > 0 && start < voice.clip.samples ? start : 0;
            }

            voice.Play();

            _loopArmed[note] = UsesLoopResync(pad);

            // The pad asked to loop on the grid and cannot. Say which switch stopped it,
            // rather than leaving a loop that audibly drifts with no explanation.
            if (pad.loop && pad.quantizeToBeat && !UsesLoopResync(pad))
                WarnLoopFreeRunning(note,
                    !quantizeEnabled
                        ? "quantise is switched off on the PadController (Quantize Enabled)"
                        : "Resync Loops To Grid is switched off on the PadController");
        }

        /// <summary>A grid-synced loop reached the end of its clip. Book the next pass.</summary>
        private void RequeueLoop(int note)
        {
            var pad = model != null ? model.Get(note) : null;

            if (pad == null || !UsesLoopResync(pad))
            {
                _loopArmed[note] = false;
                return;
            }

            // No clock: a queue would never fire, so this degrades to a plain loop
            // rather than to silence — and says so, because "it loops at the end of the
            // clip instead of on the bar" is otherwise indistinguishable from a bug.
            if (metronome == null || !metronome.Running)
            {
                WarnLoopFreeRunning(note,
                    metronome == null
                        ? "there is no Metronome in the scene"
                    : !metronome.isActiveAndEnabled
                        ? $"the Metronome on \"{metronome.name}\" is switched off along with " +
                          "its GameObject — move it to an always-active object"
                        : "the metronome is not running (Manager page → Metronome)");

                PlayNow(note, _velocity[note]);
                return;
            }

            _loopArmed[note] = false;                     // re-armed by PlayNow when it fires
            QueueStart(note, _velocity[note], pad.startBeat);
        }

        /// <summary>
        /// restart = this queue is a re-hit of a pad that is STILL SOUNDING, so the
        /// voice it is about to replace must survive a cancel. See TriggerPad step 2.
        /// </summary>
        private void QueueStart(int note, float velocity, int beat, bool restart = false)
        {
            int perBar = metronome != null ? Mathf.Max(1, metronome.BeatsPerBar) : 4;

            _startQueued[note] = true;
            _restartQueued[note] = restart;
            _startVel[note]    = Mathf.Clamp01(velocity);

            // 0 is "the next beat, whichever it is" — for freestyling, where waiting up
            // to a whole bar for beat 1 is the wrong kind of help.
            _startBeat[note]   = Mathf.Clamp(beat, 0, perBar);

            RaiseQueue(note);
        }

        private void RequestStop(int note)
        {
            _loopArmed[note] = false;   // whatever happens, it is not going round again

            var pad = model != null ? model.Get(note) : null;

            // Only a LOOP finishes the bar. A one-shot you have asked to stop should
            // stop, or the press is indistinguishable from no press at all.
            bool canWait = stopLoopsAtBarEnd && pad != null && pad.loop &&
                           metronome != null && metronome.Running;

            if (!canWait)
            {
                StopPad(note);
                return;
            }

            _stopQueued[note] = true;
            RaiseQueue(note);
        }

        /// <summary>
        /// Throttled, and it names the actual cause. "It played immediately" with no
        /// reason is the message that cost an evening.
        /// </summary>
        private void WarnNoClock(int note)
        {
            if (!warnWhenClockMissing) return;
            if (Time.unscaledTime < _nextClockWarning) return;

            _nextClockWarning = Time.unscaledTime + 5f;

            string why =
                !quantizeEnabled
                    ? "quantise is switched off on the PadController (Quantize Enabled)"
                : metronome == null
                    ? "there is no Metronome in the scene"
                : !metronome.isActiveAndEnabled
                    ? $"the Metronome on \"{metronome.name}\" is switched off along with its " +
                      "GameObject — move it to an always-active object"
                : !metronome.Running
                    ? "the metronome is not running (Manager page → Metronome)"
                    : "the clock is running but the pad was not queued";

            Notify($"Pad {note} is set to wait for the beat, but {why}. It played immediately.",
                   NoticeLevel.Warning);
        }

        /// <summary>
        /// The other half of the same story, and the one that was missing: a pad can be
        /// quantised on the way IN and still wrap free-running at the end of its clip.
        /// Separate throttle, because the two happen at different moments.
        /// </summary>
        private void WarnLoopFreeRunning(int note, string why)
        {
            if (!warnWhenClockMissing) return;
            if (Time.unscaledTime < _nextLoopWarning) return;

            _nextLoopWarning = Time.unscaledTime + 5f;

            Notify($"Pad {note} is set to loop on the beat, but {why}. It is wrapping at the " +
                   "end of the clip instead, which will drift against the bar.",
                   NoticeLevel.Warning);
        }

        // --------------------------------------------------------- the clock

        /// <summary>beat is 0-based from the metronome; pads speak in 1-based bar positions.</summary>
        private void OnBeat(int beat)
        {
            int human = beat + 1;

            foreach (int note in NoteGrid.All)
            {
                bool changed = false;

                if (_startQueued[note] && (_startBeat[note] == 0 || _startBeat[note] == human))
                {
                    bool wasRestart = _restartQueued[note];

                    _startQueued[note]   = false;
                    _restartQueued[note] = false;

                    // Pass the restart through: the voice is still sounding the previous
                    // pass, and only PlayNow(fromStart: true) will rewind it.
                    PlayNow(note, _startVel[note], wasRestart);
                    changed = true;
                }

                if (_stopQueued[note] && (!stopOnDownbeatOnly || beat == 0))
                {
                    _stopQueued[note] = false;
                    _loopArmed[note]  = false;

                    var voice = rack != null ? rack.GetVoice(note) : null;
                    if (voice != null && voice.isPlaying) voice.Stop();

                    changed = true;
                }

                if (changed) RaiseQueue(note);
            }
        }

        /// <summary>
        /// The clock stopping must not strand a queue: a pad waiting for beat 3 on a
        /// metronome that will never reach it is a pad that is silently broken.
        /// </summary>
        private void OnMetronomeStopped()
        {
            foreach (int note in NoteGrid.All)
            {
                if (_startQueued[note])
                {
                    bool wasRestart = _restartQueued[note];

                    _startQueued[note]   = false;
                    _restartQueued[note] = false;
                    PlayNow(note, _startVel[note], wasRestart);
                    RaiseQueue(note);
                }

                if (_stopQueued[note])
                {
                    _stopQueued[note] = false;
                    _loopArmed[note]  = false;

                    var voice = rack != null ? rack.GetVoice(note) : null;
                    if (voice != null && voice.isPlaying) voice.Stop();

                    RaiseQueue(note);
                }
            }

            // Without a clock there is nothing to sync to, so anything still sounding
            // goes back to wrapping on its own.
            ApplyAllToVoices();
        }

        private void ClearAllQueues()
        {
            foreach (int note in NoteGrid.All)
            {
                if (!_startQueued[note] && !_stopQueued[note] && !_restartQueued[note])
                    continue;

                _startQueued[note]   = false;
                _restartQueued[note] = false;
                _stopQueued[note]    = false;
                RaiseQueue(note);
            }
        }

        private void RaiseQueue(int note) =>
            PadQueueChanged?.Invoke(note, _startQueued[note], _stopQueued[note]);

        // ----------------------------------------------------- clip management

        /// <summary>
        /// The sanctioned way to put a file on a pad. Handles Loading → Ready/Failed,
        /// refcounting, and out-of-order decodes. Callers do not touch ClipCache.
        /// </summary>
        public void AssignClipAsync(int note, string absolutePath, Action<bool> onComplete = null)
        {
            if (!NoteGrid.IsGridNote(note))
            {
                Debug.LogWarning($"AssignClipAsync called with non-grid note {note}.", this);
                onComplete?.Invoke(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                ClearPad(note);
                onComplete?.Invoke(true);
                return;
            }

            if (clipCache == null)
            {
                Debug.LogError("No ClipCache; cannot assign.", this);
                onComplete?.Invoke(false);
                return;
            }

            int token = ++_assignToken[note];
            SetLoadState(note, PadLoadState.Loading);

            clipCache.Acquire(absolutePath, (clip, error) =>
            {
                // Superseded: the user assigned something else while this decoded.
                if (_assignToken[note] != token)
                {
                    if (clip != null) clipCache.Release(absolutePath);
                    onComplete?.Invoke(false);
                    return;
                }

                if (clip == null)
                {
                    SetLoadState(note, PadLoadState.Failed);
                    Notify($"Pad {note}: {error}. See the console for the format details.",
                           NoticeLevel.Error);
                    onComplete?.Invoke(false);
                    return;
                }

                // Acquire the new reference before releasing the old one: if they are the
                // same file, releasing first could evict the clip we just asked for.
                string previous = _held[note];
                _held[note] = ClipCache.Normalize(absolutePath);

                SetClip(note, clip, absolutePath);

                if (previous != null) clipCache.Release(previous);

                onComplete?.Invoke(true);
            });
        }

        /// <summary>
        /// Assigns an already-decoded clip. Synchronous primitive underneath
        /// AssignClipAsync — it does NOT touch refcounts, so anything calling it with a
        /// cached clip must manage the reference itself.
        /// </summary>
        public void SetClip(int note, AudioClip clip, string sourcePath = null)
        {
            if (model.Get(note) == null)
            {
                Debug.LogWarning($"No pad for note {note}.", this);
                return;
            }

            model.Set(note, pad =>
            {
                // A DIFFERENT file is a different pad as far as the caption goes. Keeping
                // the old one meant the pad was named after a clip it no longer holds,
                // and the only cure was "clear the box to get the filename back" — a rule
                // nobody should have to be taught.
                bool newSource = clip != null && !SamePath(pad.clipPath, sourcePath);

                pad.clip      = clip;
                pad.clipPath  = sourcePath;
                pad.loadState = clip != null ? PadLoadState.Ready : PadLoadState.Empty;

                if (clip == null) return;

                if ((overwriteLabelOnAssign && newSource) || string.IsNullOrWhiteSpace(pad.label))
                    pad.label = clip.name;

                // The category is NOT re-guessed over a deliberate choice; only an
                // untouched pad gets a guess.
                if (pad.category == PadCategory.None)
                    pad.category = PadCategories.Guess(clip.name);
            });
        }

        private static bool SamePath(string a, string b)
        {
            string left  = ClipCache.Normalize(a);
            string right = ClipCache.Normalize(b);

            if (left == null || right == null) return left == right;

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public void SetLoadState(int note, PadLoadState state) =>
            model.Set(note, pad => pad.loadState = state);

        public void ClearPad(int note)
        {
            if (!NoteGrid.IsGridNote(note)) return;

            _assignToken[note]++;        // cancel any decode in flight for this pad

            _startQueued[note] = false;
            _stopQueued[note]  = false;
            _loopArmed[note]   = false;
            _trimStart[note]   = 0;
            RaiseQueue(note);

            ReleaseHeld(note);
            model.Clear(note);
        }

        public AudioClip GetClip(int note) => model.Get(note)?.clip;

        public bool HasClip(int note) => GetClip(note) != null;

        // ---------------------------------------------------------- transfers

        public void MovePad(int from, int to)
        {
            if (from == to) return;
            if (!NoteGrid.IsGridNote(from) || !NoteGrid.IsGridNote(to)) return;

            ReleaseHeld(to);                 // destination's old content goes first

            _held[to]   = _held[from];       // the reference moves, net count unchanged
            _held[from] = null;

            model.Move(from, to);
        }

        public void CopyPad(int from, int to)
        {
            if (from == to) return;
            if (!NoteGrid.IsGridNote(from) || !NoteGrid.IsGridNote(to)) return;

            ReleaseHeld(to);

            string key = _held[from];

            // A second pad on the same clip needs a second reference, or clearing the
            // first would evict a clip the second is still playing.
            if (key != null && clipCache != null && clipCache.Retain(key))
                _held[to] = key;

            model.Copy(from, to);
        }

        public void SwapPads(int a, int b)
        {
            if (a == b) return;
            if (!NoteGrid.IsGridNote(a) || !NoteGrid.IsGridNote(b)) return;

            var temp = _held[a];
            _held[a] = _held[b];
            _held[b] = temp;

            model.Swap(a, b);
        }

        // ------------------------------------------------------- refcounting

        private void ReleaseHeld(int note)
        {
            string key = _held[note];
            if (key == null) return;

            _held[note] = null;
            clipCache?.Release(key);
        }

        private void ReleaseAllHeld()
        {
            if (clipCache == null) return;

            for (int note = 0; note < _held.Length; note++)
            {
                if (_held[note] == null) continue;
                clipCache.Release(_held[note]);
                _held[note] = null;
            }
        }

        /// <summary>Diagnostic: how many pads hold a cache reference.</summary>
        public int HeldReferenceCount
        {
            get
            {
                int n = 0;
                foreach (int note in NoteGrid.All) if (_held[note] != null) n++;
                return n;
            }
        }

        /// <summary>
        /// The one place to look when a pad ignores its timing. Prints the master
        /// switches (any of which can silently override every pad) and then one line per
        /// timing-relevant pad, INCLUDING the AudioSource's own loop flag — because
        /// "pad.loop is true but AudioSource.loop is also true" is the whole fault.
        /// </summary>
        [ContextMenu("Log Transport State")]
        private void LogTransport()
        {
            string clock = metronome == null ? "NO METRONOME"
                         : !metronome.isActiveAndEnabled ? $"\"{metronome.name}\" is INACTIVE"
                         : metronome.Running ? $"running, beat {metronome.CurrentBeat + 1}/{metronome.BeatsPerBar}"
                         : "stopped";

            int armed = 0, trimmed = 0;
            foreach (int note in NoteGrid.All)
            {
                if (_loopArmed[note]) armed++;
                if (_trimStart[note] > 0) trimmed++;
            }

            var text = new StringBuilder();

            text.Append($"[{nameof(PadController)}] clock: {clock}\n")
                .Append($"  switches: quantizeEnabled={quantizeEnabled}, " +
                        $"resyncLoopsToGrid={resyncLoopsToGrid}, " +
                        $"stopLoopsAtBarEnd={stopLoopsAtBarEnd}, " +
                        $"oneShotRepress={oneShotRepress}, " +
                        $"trimLeadingSilence={trimLeadingSilence}\n")
                .Append($"  {PlayingCount} voice(s) sounding, {armed} grid-synced loop(s) " +
                        $"armed, {trimmed} pad(s) trimmed, queued={AnyQueued}");

            foreach (int note in NoteGrid.All)
            {
                var pad = model != null ? model.Get(note) : null;
                if (pad == null || (!pad.quantizeToBeat && !pad.loop)) continue;

                var voice = rack != null ? rack.GetVoice(note) : null;

                text.Append($"\n  Pad {note}: quantize={pad.quantizeToBeat} " +
                            $"startBeat={pad.startBeat} loop={pad.loop} → " +
                            $"AudioSource.loop={(voice != null ? voice.loop.ToString() : "-")} " +
                            $"gridSynced={UsesLoopResync(pad)} armed={_loopArmed[note]} " +
                            $"trim={_trimStart[note]} samples");
            }

            Debug.Log(text.ToString(), this);
        }
    }
}