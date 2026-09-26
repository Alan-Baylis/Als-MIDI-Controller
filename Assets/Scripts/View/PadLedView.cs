using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Mirrors the 64 pads onto the Launchpad's own grid LEDs — the hardware twin of
    /// PadView, sharing its colour rules through PadPalette.
    /// </summary>
    [DisallowMultipleComponent]
    public class PadLedView : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Manager manager;
        [SerializeField] private PadModel model;
        [SerializeField] private PadController controller;
        [SerializeField] private SelectionService selection;
        [SerializeField] private MidiPadInput midi;

        [Header("Behaviour")]
        [Tooltip("Flash buttonColorFlash on trigger, matching the on-screen pads.")]
        [SerializeField] private bool mirrorFlash = true;

        [Tooltip("Scales every LED. The hardware is far brighter than the screen.")]
        [SerializeField, Range(0.05f, 1f)] private float brightness = 1f;

        [Tooltip("Turn the grid off when this component is disabled or the app quits.")]
        [SerializeField] private bool clearOnDisable = true;

        private readonly HashSet<int> _dirty = new HashSet<int>();
        private readonly Dictionary<int, float> _flashUntil = new Dictionary<int, float>();
        private readonly List<LedWrite> _writes = new List<LedWrite>(64);
        private readonly List<int> _touched = new List<int>(64);
        private readonly List<int> _expired = new List<int>(8);

        private bool _wasReady;

        [Tooltip("Mirror the on-screen play pulse: the pad's own colour, brighter, " +
                 "each time its clip starts.")]
        [SerializeField] private bool mirrorPulse = true;

        [SerializeField, Range(0f, 1f)] private float pulseBrightness = 0.35f;
        [SerializeField, Min(0.01f)] private float pulseSeconds = 0.07f;

        private readonly Dictionary<int, float> _pulseUntil = new Dictionary<int, float>();
        
        [Tooltip("Optional. Animation layer (screensaver, lightshow). When it is active " +
                 "it owns every LED.")]
        [SerializeField] private PadOverlay overlay;
        
        private void Awake()
        {
            if (manager    == null) manager    = FindAnyObjectByType<Manager>();
            if (model      == null) model      = FindAnyObjectByType<PadModel>();
            if (controller == null) controller = FindAnyObjectByType<PadController>();
            if (selection  == null) selection  = FindAnyObjectByType<SelectionService>();
            if (midi       == null) midi       = FindAnyObjectByType<MidiPadInput>();
            if (overlay    == null) overlay    = FindAnyObjectByType<PadOverlay>();
            
            if (midi == null)
                Debug.LogWarning($"{nameof(PadLedView)} found no {nameof(MidiPadInput)}; " +
                                 "the hardware grid will stay dark.", this);

            if (model == null)
                Debug.LogWarning($"{nameof(PadLedView)} found no {nameof(PadModel)}; " +
                                 "there is nothing to mirror.", this);
        }

        private void OnEnable()
        {
            if (model != null)
            {
                model.PadChanged    += MarkDirty;
                model.ModelReloaded += MarkAllDirty;
            }

            if (selection  != null) selection.SelectionChanged += OnSelectionChanged;
            if (controller != null) controller.PadTriggered    += OnPadTriggered;

            if (controller != null)
            {
                controller.PadTriggered += OnPadTriggered;
                controller.PadPulsed    += OnPadPulsed;
            }
            
            if (overlay != null)
            {
                overlay.NoteChanged += MarkDirty;
                overlay.Changed     += MarkAllDirty;
            }
            
            if (manager != null) manager.PaletteChanged += MarkAllDirty;
            
            MarkAllDirty();
        }
        
        private void OnDisable()
        {
            if (model != null)
            {
                model.PadChanged    -= MarkDirty;
                model.ModelReloaded -= MarkAllDirty;
            }

            if (selection  != null) selection.SelectionChanged -= OnSelectionChanged;
            if (controller != null) controller.PadTriggered    -= OnPadTriggered;

            if (controller != null)
            {
                controller.PadTriggered -= OnPadTriggered;
                controller.PadPulsed    -= OnPadPulsed;
            }

            if (overlay != null)
            {
                overlay.NoteChanged -= MarkDirty;      // OnDisable
                overlay.Changed     -= MarkAllDirty;
            }
            
            _dirty.Clear();
            _flashUntil.Clear();
            _pulseUntil.Clear();
            _wasReady = false;
            
            // Teardown order between components is undefined, so the port may already
            // be closed. MidiPadInput's LedOutputReady guard makes this a safe no-op.
            if (clearOnDisable && midi != null) midi.ClearLeds(NoteGrid.All);
            
            if (manager != null) manager.PaletteChanged -= MarkAllDirty;
        }

        // ------------------------------------------------------------- events

        private void MarkDirty(int note)
        {
            if (NoteGrid.IsGridNote(note)) _dirty.Add(note);
        }

        private void MarkAllDirty()
        {
            foreach (int note in NoteGrid.All) _dirty.Add(note);
        }

        private void OnSelectionChanged(int? previous, int? current)
        {
            if (previous.HasValue) MarkDirty(previous.Value);
            if (current.HasValue)  MarkDirty(current.Value);
        }

        private void OnPadTriggered(int note, float velocity)
        {
            if (!mirrorFlash || !NoteGrid.IsGridNote(note)) return;

            float seconds = manager != null ? manager.flashSeconds : 0.08f;
            _flashUntil[note] = Time.unscaledTime + seconds;

            MarkDirty(note);
        }

        // ------------------------------------------------------------ painting

        private void OnPadPulsed(int note)
        {
            if (!mirrorPulse || !NoteGrid.IsGridNote(note)) return;

            _pulseUntil[note] = Time.unscaledTime + pulseSeconds;
            MarkDirty(note);
        }
        
        private void Update()
        {
            // Programmer mode arrives a few frames after the port opens, so the first
            // paint has to wait for it. Repaint everything on the false→true edge —
            // this also covers re-plugging the device mid-session.
            bool ready = midi != null && midi.LedOutputReady;

            if (ready && !_wasReady) MarkAllDirty();
            _wasReady = ready;

            Expire(_flashUntil);
            Expire(_pulseUntil);
        }
        
        /// <summary>Drops timers that have run out and marks their pads for a repaint.</summary>
        private void Expire(Dictionary<int, float> timers)
        {
            if (timers.Count == 0) return;

            _expired.Clear();

            foreach (var pair in timers)
                if (Time.unscaledTime >= pair.Value) _expired.Add(pair.Key);

            for (int i = 0; i < _expired.Count; i++)
            {
                timers.Remove(_expired[i]);
                MarkDirty(_expired[i]);
            }
        }
        
        /// <summary>LateUpdate so every event this frame lands in one message.</summary>
        private void LateUpdate()
        {
            if (_dirty.Count == 0) return;
            if (midi == null || !midi.LedOutputReady || model == null) return;

            _writes.Clear();
            _touched.Clear();

            foreach (int note in _dirty)
            {
                _writes.Add(new LedWrite(note, ColourFor(note)));
                _touched.Add(note);
            }

            _dirty.Clear();

            midi.SetLeds(_writes, _touched);
        }

        private Color32 ColourFor(int note)
        {
            // An overlay outranks everything, including the trigger flash: an animation
            // that flickers back to pad colours under your mouse is not an animation.
            if (overlay != null && overlay.TryGet(note, out var overlayColour))
                return PadPalette.Flatten(overlayColour, brightness);
 
            // The trigger flash is a full override; the play pulse is a modifier on
            // whatever the pad already is.
            if (mirrorFlash && _flashUntil.ContainsKey(note))
                return PadPalette.Flatten(
                    manager != null ? manager.FlashColour : new Color32(255, 255, 255, 255),
                    brightness);

            var  pad      = model.Get(note);
            bool selected = selection != null && selection.IsSelected(note);

            var colour = PadPalette.ColourFor(pad, manager, selected);

            if (mirrorPulse && _pulseUntil.ContainsKey(note))
                colour = PadPalette.Brighten(colour, pulseBrightness);

            return PadPalette.Flatten(colour, brightness);
        }

        /// <summary>Force a full resend. Handy after a manual mode switch.</summary>
        [UnityEngine.ContextMenu("Repaint All LEDs")]
        public void RepaintAll() => MarkAllDirty();
    }
}