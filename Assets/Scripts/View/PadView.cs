using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// One of the 64 grid buttons. Owns its own appearance and input; reads the
    /// model, never writes AudioSources.
    ///
    /// Two kinds of feedback, deliberately different: the FLASH is white and means
    /// "you hit this"; the PULSE is the pad's own colour, brighter, and means "this is
    /// sounding" — including every pass of a loop.
    ///
    /// The caption is painted in the pad's INVERSE colour, so it follows a re-theme
    /// without anyone maintaining a second palette. A pad with no clip has no caption:
    /// an empty pad with a name on it is a pad that lies about its contents.
    ///
    /// A PadOverlay, when active, outranks all of it: an animation owns the grid
    /// outright while it runs, and hands it straight back when it stops.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    public class PadView : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private int note;                       // auto-derived from name if 0
        [Tooltip("Optional — resolved from this object in Awake if empty.")]
        [SerializeField] private Image background;
        [Tooltip("Optional — resolved from this object in Awake if empty.")]
        [SerializeField] private TextMeshProUGUI labelText;      // optional; auto-found

        [Header("Feedback")]
        [Tooltip("White flash on trigger — \"you pressed it\".")]
        [SerializeField] private bool flashOnTrigger = true;

        [Tooltip("Brighten the pad's own colour whenever its clip starts, loops included.")]
        [SerializeField] private bool pulseOnPlay = true;

        [Tooltip("0 = no change, 1 = white. Keep it low: the pad must still read as its " +
                 "own colour, not as a second flash.")]
        [SerializeField, Range(0f, 1f)] private float pulseBrightness = 0.35f;

        [SerializeField, Min(0.01f)] private float pulseSeconds = 0.07f;

        [Header("Label")]
        [Tooltip("Show the clip (or custom) name on this pad. Manager.ShowPadLabels is " +
                 "the master switch; this only opts ONE pad out of it.")]
        [SerializeField] private bool showLabel = true;

        [Tooltip("Paint the caption in the pad's inverse colour. Off = plain black or " +
                 "white, whichever the pad is not.")]
        [SerializeField] private bool invertLabelColour = true;

        [Tooltip("Force the caption to no-wrap, centred, with an ellipsis. Off = leave " +
                 "the TMP object exactly as it is in the Inspector.")]
        [SerializeField] private bool styleLabel = true;

        [Tooltip("Blank the label while an overlay (screensaver, lightshow) owns the grid.")]
        [SerializeField] private bool hideLabelUnderOverlay = true;

        private PadGrid _grid;
        private PadModel _model;
        private PadController _controller;
        private SelectionService _selection;
        private SidePanel _sidePanel;
        private Manager _manager;
        private PadOverlay _overlay;

        private Coroutine _flash;
        private Coroutine _pulse;
        private bool _pulsing;

        public int Note => note;

        private void Awake()
        {
            if (note == 0) note = NoteGrid.ParseTrailingInt(name);
            if (background == null) background = GetComponent<Image>();

            // Auto-found so enabling the caption is one toggle on Manager, not 64
            // Inspector drags.
            if (labelText == null) labelText = GetComponentInChildren<TextMeshProUGUI>(true);

            if (!NoteGrid.IsGridNote(note))
            {
                Debug.LogWarning($"PadView \"{name}\" has an invalid note: {note}", this);
                enabled = false;
                return;
            }

            _grid       = GetComponentInParent<PadGrid>();
            _model      = FindAnyObjectByType<PadModel>();
            _controller = FindAnyObjectByType<PadController>();
            _selection  = FindAnyObjectByType<SelectionService>();
            _sidePanel  = FindAnyObjectByType<SidePanel>();
            _manager    = FindAnyObjectByType<Manager>();
            _overlay    = FindAnyObjectByType<PadOverlay>();

            if (labelText != null && styleLabel)
            {
                labelText.enableWordWrapping = false;
                labelText.overflowMode       = TextOverflowModes.Ellipsis;
                labelText.alignment          = TextAlignmentOptions.Center;
                labelText.raycastTarget      = false;   // never swallow a pad click
            }

            if (_grid == null)
                Debug.LogWarning($"PadView \"{name}\" has no PadGrid ancestor.", this);
        }

        private void OnEnable()
        {
            _grid?.Register(this);

            if (_model != null) _model.PadChanged += OnPadChanged;
            if (_selection != null) _selection.SelectionChanged += OnSelectionChanged;

            if (_controller != null)
            {
                _controller.PadTriggered += OnPadTriggered;
                _controller.PadPulsed    += OnPadPulsed;
            }

            if (_overlay != null)
            {
                _overlay.NoteChanged += OnOverlayNote;
                _overlay.Changed     += Repaint;
            }

            if (_manager != null) _manager.ShowPadLabelsChanged += OnShowLabelsChanged;

            Repaint();
        }

        private void OnDisable()
        {
            _grid?.Unregister(this);

            if (_model != null) _model.PadChanged -= OnPadChanged;
            if (_selection != null) _selection.SelectionChanged -= OnSelectionChanged;

            if (_controller != null)
            {
                _controller.PadTriggered -= OnPadTriggered;
                _controller.PadPulsed    -= OnPadPulsed;
            }

            if (_overlay != null)
            {
                _overlay.NoteChanged -= OnOverlayNote;
                _overlay.Changed     -= Repaint;
            }

            if (_manager != null) _manager.ShowPadLabelsChanged -= OnShowLabelsChanged;

            // Coroutines die with the object; the flag they set does not.
            _pulsing = false;
            _flash   = null;
            _pulse   = null;
        }

        // -------------------------------------------------------------- input

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left)
            {
                _controller?.TriggerPadFromMouse(note);
            }
            else if (e.button == PointerEventData.InputButton.Right)
            {
                if (_selection != null && _selection.IsSelected(note))
                    _selection.ClearSelection();      // stay on the Pad page, nothing selected
                else
                    _sidePanel?.ShowPadInspector(note);
            }
        }

        // ------------------------------------------------------------- events

        private void OnPadChanged(int changedNote)
        {
            if (changedNote == note) Repaint();
        }

        private void OnSelectionChanged(int? previous, int? current)
        {
            if (previous == note || current == note) Repaint();
        }

        private void OnOverlayNote(int overlayNote)
        {
            if (overlayNote == note) Repaint();
        }

        private void OnShowLabelsChanged(bool _) => Repaint();

        private void OnPadTriggered(int triggeredNote, float velocity)
        {
            if (!flashOnTrigger || triggeredNote != note || _manager == null) return;

            if (_flash != null) StopCoroutine(_flash);
            _flash = StartCoroutine(FlashRoutine());
        }

        private void OnPadPulsed(int pulsedNote)
        {
            if (!pulseOnPlay || pulsedNote != note) return;

            if (_pulse != null) StopCoroutine(_pulse);
            _pulse = StartCoroutine(PulseRoutine());
        }

        private IEnumerator FlashRoutine()
        {
            if (background != null)
            {
                background.color = _manager.FlashColour;
                PaintLabel(_manager.FlashColour, false);
            }

            yield return new WaitForSeconds(_manager.flashSeconds);
            _flash = null;
            Repaint();
        }

        private IEnumerator PulseRoutine()
        {
            _pulsing = true;
            Repaint();

            yield return new WaitForSecondsRealtime(pulseSeconds);

            _pulsing = false;
            _pulse   = null;
            Repaint();
        }

        // ------------------------------------------------------------ visuals

        public void Repaint()
        {
            if (background == null || _manager == null) return;

            var pad = _model != null ? _model.Get(note) : null;

            // An overlay owns the pad outright — no state colour, no flash, no pulse.
            if (_overlay != null && _overlay.TryGet(note, out var overlayColour))
            {
                background.color = overlayColour;
                PaintLabel(overlayColour, true);
                return;
            }

            bool selected = _selection != null && _selection.IsSelected(note);

            var colour = PadPalette.ColourFor(pad, _manager, selected);

            // Applied on top of whatever the palette chose, so a pulsing pad is still
            // recognisably loaded / selected / half-strength.
            if (_pulsing) colour = PadPalette.Brighten(colour, pulseBrightness);

            background.color = colour;
            PaintLabel(colour, false);
        }

        /// <summary>
        /// The caption follows the pad, not the palette: it is computed from whatever
        /// colour was just written, so a custom pad colour, a pulse and a flash all get
        /// a legible label without any of them knowing the label exists.
        /// </summary>
        private void PaintLabel(Color32 padColour, bool overlayActive)
        {
            if (labelText == null) return;

            bool allowed = showLabel &&
                           (_manager == null || _manager.ShowPadLabels) &&
                           !(overlayActive && hideLabelUnderOverlay);

            var pad = _model != null ? _model.Get(note) : null;

            // HasClip is "a file is assigned", so a missing or failed pad still shows the
            // name it is looking for — which is the whole point of not discarding it.
            string text = allowed && pad != null && pad.HasClip
                ? TextGlyphs.Fit(labelText, pad.DisplayLabel)
                : string.Empty;
            
            if (labelText.text != text) labelText.text = text;

            if (text.Length > 0)
                labelText.color = PadPalette.LabelColourFor(padColour, invertLabelColour);
        }
    }
}