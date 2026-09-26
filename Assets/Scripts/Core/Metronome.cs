using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The transport clock. Converts Manager.BPM into beat events and nothing else —
    /// it owns no visuals, so the LEDs, a future playhead, and MIDI LED output can all
    /// hang off the same Beat event.
    ///
    /// Driven by AudioSettings.dspTime, not Time.time: the audio clock does not stutter
    /// when a frame is long, so the metronome stays locked to playing clips instead of
    /// drifting away from them.
    /// </summary>
    [DisallowMultipleComponent]
    public class Metronome : MonoBehaviour
    {
        /// <summary>Beat index within the bar, 0-based. 0 is the downbeat.</summary>
        public event System.Action<int> Beat;

        /// <summary>Raised when the metronome is switched off, so views can go dark.</summary>
        public event System.Action Stopped;
        
        [Header("References")]
        [SerializeField] private Manager manager;

        [Header("Timing")]
        [Tooltip("Beats per bar. Should match the number of metronome LEDs.")]
        [SerializeField, Min(1)] private int beatsPerBar = 4;

        [Tooltip("Frames longer than this (editor hitches, alt-tab) are not replayed as a burst of beats.")]
        [SerializeField, Min(0.05f)] private float maxCatchUpSeconds = 0.25f;

        [Header("Audible Click (optional)")]
        [SerializeField] private AudioClip accentClip;   // beat 0
        [SerializeField] private AudioClip clickClip;    // beats 1..n
        [SerializeField, Range(0f, 1f)] private float clickVolume = 0.5f;

        private AudioSource _clickSource;

        private double _lastDsp;
        private double _phase;      // 0-1 through the current beat
        private int    _beat;
        private bool   _running;

        public int  BeatsPerBar => beatsPerBar;
        public int  CurrentBeat => _beat;
        public bool Running     => _running;

        /// <summary>0-1 progress through the current beat. For a future playhead/pulse animation.</summary>
        public float BeatProgress => (float)_phase;

        private void Awake()
        {
            if (manager == null) manager = FindAnyObjectByType<Manager>();

            _clickSource = gameObject.AddComponent<AudioSource>();
            _clickSource.playOnAwake  = false;
            _clickSource.spatialBlend = 0f;
            
            EnsureClickSource();
        }

        private void OnEnable()
        {
            if (manager != null)
                manager.MetronomeEnabledChanged += OnMetronomeEnabledChanged;

            if (manager != null && manager.MetronomeEnabled) StartClock();
            else _running = false;
        }

        private void OnDisable()
        {
            if (manager != null)
                manager.MetronomeEnabledChanged -= OnMetronomeEnabledChanged;

            StopClock();
        }

        /// <summary>
        /// The click needs a 2D AudioSource. Creating it here rather than requiring an
        /// Inspector drag means moving this component to a new GameObject can't silently
        /// mute the metronome.
        /// </summary>
        private void EnsureClickSource()
        {
            if (_clickSource != null) return;

            _clickSource = GetComponent<AudioSource>();
            if (_clickSource == null) _clickSource = gameObject.AddComponent<AudioSource>();

            _clickSource.playOnAwake  = false;
            _clickSource.loop         = false;
            _clickSource.spatialBlend = 0f;   // 2D: a UI click must not be positioned
        }
        
        private void OnMetronomeEnabledChanged(bool enabled)
        {
            if (enabled) StartClock();
            else StopClock();
        }

        /// <summary>Restarts on the downbeat and fires beat 0 immediately.</summary>
        public void StartClock()
        {
            _lastDsp = AudioSettings.dspTime;
            _phase   = 0.0;
            _beat    = 0;
            _running = true;
            Fire(0);
        }

        public void StopClock()
        {
            if (!_running) return;
            _running = false;
            Stopped?.Invoke();
        }

        /// <summary>Re-anchor to a downbeat without changing the on/off state.</summary>
        public void ResetToDownbeat()
        {
            if (_running) StartClock();
        }

        private void Update()
        {
            if (!_running || manager == null) return;

            double now   = AudioSettings.dspTime;
            double delta = now - _lastDsp;

            // dspTime only advances on audio buffer boundaries; several frames can
            // share a value. Nothing to do until it moves.
            if (delta <= 0.0) return;
            _lastDsp = now;

            if (delta > maxCatchUpSeconds) delta = maxCatchUpSeconds;

            // Accumulating PHASE rather than scheduling an absolute next-beat time is
            // what makes BPM changes apply on the very next frame: the divisor is
            // re-read every tick, and no already-elapsed time is lost or repeated.
            _phase += delta / manager.SecondsPerBeat;

            while (_phase >= 1.0)
            {
                _phase -= 1.0;
                _beat = (_beat + 1) % Mathf.Max(1, beatsPerBar);
                Fire(_beat);
            }
        }

        private void Fire(int beat)
        {
            Beat?.Invoke(beat);

            // Manager owns the on/off state so the UI toggle and the startup default are
            // the same field. Set it on the Manager object to choose the default.
            if (manager == null || !manager.MetronomeClickEnabled || _clickSource == null) return;

            var clip = (beat == 0 && accentClip != null) ? accentClip : clickClip;
            if (clip != null) _clickSource.PlayOneShot(clip, clickVolume);
        }
    }
}