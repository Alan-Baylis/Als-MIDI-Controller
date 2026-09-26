using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Mirrors the metronome onto the Launchpad's own LEDs — our play mode inside
    /// Programmer mode. A view like FlashingBulbs: it owns no timing and no port,
    /// it listens to Metronome.Beat and asks MidiPadInput to send.
    ///
    /// Default targets are the top function row, 91-98 in programmer-mode addressing,
    /// which sits above the 8x8 grid and so never fights with pad colours.
    /// </summary>
    [DisallowMultipleComponent]
    public class MetronomeLedView : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Metronome metronome;
        [SerializeField] private MidiPadInput midi;

        [Header("Targets — in BEAT order, downbeat first")]
        [Tooltip("Programmer-mode LED indices. Top row is 91-98, bottom row 101-108, " +
                 "grid 11-88. Check the Programmer's Reference if a lamp stays dark.")]
        [SerializeField] private int[] beatLeds = { 91, 92, 93, 94 };

        [Header("Colour")]
        [Tooltip("The downbeat. Red, to match the panel artwork.")]
        [SerializeField] private Color32 accentColour = new Color32(255, 30, 20, 255);

        [SerializeField] private Color32 beatColour = new Color32(0, 255, 60, 255);

        [Tooltip("Colour for the beats that are NOT currently sounding. Black = off.")]
        [SerializeField] private Color32 restColour = new Color32(0, 0, 0, 255);

        [Tooltip("Leave the previous beats lit so the bar fills up, cinema-marquee style.")]
        [SerializeField] private bool cumulative = false;

        [Header("Timing")]
        [Tooltip("Hold the lamp for the whole beat. Off = match the on-screen flash.")]
        [SerializeField] private bool holdUntilNextBeat = true;

        [SerializeField, Min(0.01f)] private float onDuration = 0.1f;

        private readonly List<LedWrite> _writes = new List<LedWrite>(8);

        private int   _current = -1;
        private float _offTimer;

        private void Awake()
        {
            if (metronome == null) metronome = FindAnyObjectByType<Metronome>();
            if (midi      == null) midi      = FindAnyObjectByType<MidiPadInput>();

            if (midi == null)
                Debug.LogWarning($"{nameof(MetronomeLedView)} found no {nameof(MidiPadInput)}; " +
                                 "hardware LEDs will stay dark.", this);
        }

        private void OnEnable()
        {
            if (metronome != null)
            {
                metronome.Beat    += OnBeat;
                metronome.Stopped += AllOff;
            }
            if (midi != null) midi.ConnectionChanged += OnMidiConnectionChanged;
        }

        private void OnDisable()
        {
            if (metronome != null)
            {
                metronome.Beat    -= OnBeat;
                metronome.Stopped -= AllOff;
            }

            // Component teardown order is undefined, so MidiPadInput may already have
            // closed the port. Its LedOutputReady guard makes this a safe no-op.
            AllOff();
            
            if (midi != null) midi.ConnectionChanged -= OnMidiConnectionChanged;
        }
        
        /// <summary>A returning device knows nothing about the bar, so repaint the row we last had.</summary>
        private void OnMidiConnectionChanged(bool connected)
        {
            if (connected && _current >= 0) Paint(_current);
        }
        
        private void OnBeat(int beat)
        {
            if (beatLeds == null || beatLeds.Length == 0) return;

            _current  = beat % beatLeds.Length;
            _offTimer = onDuration;

            Paint(_current);
        }

        private void Update()
        {
            if (holdUntilNextBeat || _current < 0) return;

            _offTimer -= Time.unscaledDeltaTime;
            if (_offTimer > 0f) return;

            AllOff();
        }

        private void Paint(int activeIndex)
        {
            if (midi == null || !midi.LedOutputReady) return;

            _writes.Clear();

            for (int i = 0; i < beatLeds.Length; i++)
            {
                bool lit = cumulative ? i <= activeIndex : i == activeIndex;

                Color32 colour = !lit    ? restColour
                               : i == 0  ? accentColour
                                         : beatColour;

                _writes.Add(new LedWrite(beatLeds[i], colour));
            }

            // One SysEx for the whole row: the lamps change on the same device tick,
            // so there's no visible sweep between turning one off and the next on.
            midi.SetLeds(_writes, beatLeds);
        }

        private void AllOff()
        {
            _current = -1;
            Paint(-1);
        }
    }
}