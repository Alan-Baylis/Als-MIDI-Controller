using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// The runtime colour picker, and the only one that exists in a BUILD —
    /// UnityEditor.ColorPicker does not ship. HSV sliders rather than RGB because
    /// "make it a bit more orange" is one slider in HSV and three in RGB.
    ///
    /// Live: every drag calls onChanged, so the pad grid and the hardware LEDs preview
    /// the colour as you move the slider. Cancel restores the colour the picker opened
    /// with, which is the only reason a live preview is safe to hand a user.
    ///
    /// Put this on an ALWAYS-ACTIVE object and point Window at the panel it shows —
    /// a component that hides itself cannot be asked to show itself again (§9.1).
    ///
    /// Buttons and sliders are wired in Awake. Leave their listener lists EMPTY (§9.4).
    /// </summary>
    [DisallowMultipleComponent]
    public class ColourPickerView : MonoBehaviour
    {
        public static ColourPickerView Instance { get; private set; }

        [Header("Window")]
        [Tooltip("The panel that is shown and hidden. NOT this object.")]
        [SerializeField] private GameObject window;

        [SerializeField] private TMP_Text titleText;

        [Header("Preview")]
        [Tooltip("Filled with the colour being edited.")]
        [SerializeField] private Graphic preview;

        [Tooltip("Optional. Filled with the colour the picker opened with, for comparison.")]
        [SerializeField] private Graphic original;

        [Header("Sliders")]
        [SerializeField] private Slider hueSlider;
        [SerializeField] private Slider saturationSlider;
        [SerializeField] private Slider valueSlider;

        [Tooltip("Optional. Hidden unless the caller asks for alpha.")]
        [SerializeField] private Slider alphaSlider;

        [Tooltip("Optional. The alpha slider's row, hidden with it.")]
        [SerializeField] private GameObject alphaRow;

        [Header("Hex")]
        [Tooltip("Optional. Accepts RRGGBB or RRGGBBAA, with or without the #.")]
        [SerializeField] private TMP_InputField hexField;

        [Header("Buttons (wired in Awake — leave OnClick EMPTY)")]
        [SerializeField] private Button acceptButton;
        [SerializeField] private Button cancelButton;

        [Tooltip("Optional. Puts the colour back to what it was without closing.")]
        [SerializeField] private Button revertButton;

        [Header("Behaviour")]
        [Tooltip("Escape cancels, Return accepts.")]
        [SerializeField] private bool keyboardShortcuts = true;

        private Action<Color32> _onChanged;
        private Action<Color32> _onAccept;
        private Action          _onCancel;

        private Color32 _opening;
        private bool    _withAlpha;
        private bool    _suppress;      // model → widget, so widgets do not echo back

        public bool IsOpen => window != null && window.activeSelf;

        // ------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this)
                Debug.LogWarning("Second ColourPickerView; the first keeps the static " +
                                 "handle and this one will never be asked to open.", this);

            if (window == null)
                Debug.LogError($"{nameof(ColourPickerView)} on \"{name}\" has no Window " +
                               "object, so there is nothing to show.", this);
            else if (window == gameObject)
                Debug.LogError($"{nameof(ColourPickerView)} on \"{name}\" has Window set to " +
                               "its OWN object. Hiding it would make the picker impossible " +
                               "to reopen. Put the component on an always-active parent.",
                               this);

            WireSlider(hueSlider);
            WireSlider(saturationSlider);
            WireSlider(valueSlider);
            WireSlider(alphaSlider);

            if (hexField != null)
            {
                hexField.onEndEdit.RemoveAllListeners();
                hexField.onEndEdit.AddListener(OnHexEntered);
            }

            if (acceptButton != null) { acceptButton.onClick.RemoveAllListeners(); acceptButton.onClick.AddListener(Accept); }
            if (cancelButton != null) { cancelButton.onClick.RemoveAllListeners(); cancelButton.onClick.AddListener(Cancel); }
            if (revertButton != null) { revertButton.onClick.RemoveAllListeners(); revertButton.onClick.AddListener(Revert); }

            if (window != null) window.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void WireSlider(Slider slider)
        {
            if (slider == null) return;

            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.onValueChanged.RemoveAllListeners();
            slider.onValueChanged.AddListener(_ => OnSliderMoved());
        }

        private void Update()
        {
            if (!keyboardShortcuts || !IsOpen) return;

#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.escapeKey.wasPressedThisFrame) { Cancel(); return; }

            // Not while the hex field has focus: Return there means "commit the text".
            if (hexField != null && hexField.isFocused) return;

            if (keyboard.enterKey.wasPressedThisFrame ||
                keyboard.numpadEnterKey.wasPressedThisFrame) Accept();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape)) { Cancel(); return; }

            if (hexField != null && hexField.isFocused) return;

            if (Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter)) Accept();
#endif
        }

        // ------------------------------------------------------------ static API

        /// <summary>
        /// Returns FALSE when there is no picker in the scene, so the caller can fall
        /// back (PadPaletteView drops to the editor picker) instead of doing nothing.
        ///
        /// onChanged fires continuously while the user drags — that is the live preview.
        /// onAccept fires once on OK. onCancel fires on Cancel or Escape, and the caller
        /// is responsible for putting the colour back, because only the caller knows
        /// where it lives.
        /// </summary>
        public static bool Show(string title, Color32 initial, bool withAlpha,
                                Action<Color32> onChanged,
                                Action<Color32> onAccept,
                                Action onCancel = null)
        {
            if (Instance == null) return false;

            Instance.Open(title, initial, withAlpha, onChanged, onAccept, onCancel);
            return true;
        }

        // --------------------------------------------------------------- opening

        public void Open(string title, Color32 initial, bool withAlpha,
                         Action<Color32> onChanged,
                         Action<Color32> onAccept,
                         Action onCancel = null)
        {
            if (window == null) return;

            // A second request over the top of a first would lose the first one's
            // callbacks silently. Cancelling it is the honest resolution.
            if (IsOpen) Cancel();

            _opening   = initial;
            _withAlpha = withAlpha;
            _onChanged = onChanged;
            _onAccept  = onAccept;
            _onCancel  = onCancel;

            if (titleText != null) titleText.text = title;
            if (original  != null) original.color = new Color32(initial.r, initial.g, initial.b, 255);

            if (alphaRow    != null) alphaRow.SetActive(withAlpha);
            if (alphaSlider != null && alphaRow == null)
                alphaSlider.gameObject.SetActive(withAlpha);

            PushToWidgets(initial);

            window.SetActive(true);
        }

        /// <summary>
        /// Model → widget, so SetValueWithoutNotify throughout (§9.5). Otherwise each
        /// slider echoes the write back as a user edit and the three of them ping-pong.
        /// </summary>
        private void PushToWidgets(Color32 colour)
        {
            _suppress = true;

            Color.RGBToHSV(new Color(colour.r / 255f, colour.g / 255f, colour.b / 255f),
                           out float h, out float s, out float v);

            // A pure grey has no meaningful hue, and RGBToHSV reports 0 for it. Keeping
            // the slider where the user left it stops the handle jumping to red the
            // moment saturation reaches zero.
            if (s > 0.0001f && hueSlider != null) hueSlider.SetValueWithoutNotify(h);

            if (saturationSlider != null) saturationSlider.SetValueWithoutNotify(s);
            if (valueSlider      != null) valueSlider.SetValueWithoutNotify(v);
            if (alphaSlider      != null) alphaSlider.SetValueWithoutNotify(colour.a / 255f);

            _suppress = false;

            Repaint(colour);
        }

        private void Repaint(Color32 colour)
        {
            if (preview != null)
                preview.color = _withAlpha
                    ? (Color32)colour
                    : new Color32(colour.r, colour.g, colour.b, 255);

            if (hexField == null) return;

            string hex = _withAlpha
                ? ColorUtility.ToHtmlStringRGBA(colour)
                : ColorUtility.ToHtmlStringRGB(colour);

            hexField.SetTextWithoutNotify("#" + hex);
        }

        // ---------------------------------------------------------------- edits

        private Color32 FromWidgets()
        {
            float h = hueSlider        != null ? hueSlider.value        : 0f;
            float s = saturationSlider != null ? saturationSlider.value : 0f;
            float v = valueSlider      != null ? valueSlider.value      : 1f;

            Color rgb = Color.HSVToRGB(h, s, v);

            byte a = 255;
            if (_withAlpha && alphaSlider != null)
                a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alphaSlider.value) * 255f);

            return new Color32((byte)Mathf.RoundToInt(rgb.r * 255f),
                               (byte)Mathf.RoundToInt(rgb.g * 255f),
                               (byte)Mathf.RoundToInt(rgb.b * 255f),
                               a);
        }

        private void OnSliderMoved()
        {
            if (_suppress) return;

            var colour = FromWidgets();
            Repaint(colour);
            _onChanged?.Invoke(colour);      // live: grid and LEDs follow the drag
        }

        private void OnHexEntered(string text)
        {
            if (_suppress) return;

            if (!TryParseHex(text, out var colour))
            {
                // Put the field back rather than leaving bad text sitting there looking
                // as though it were applied.
                Repaint(FromWidgets());
                return;
            }

            if (!_withAlpha) colour.a = 255;

            PushToWidgets(colour);
            _onChanged?.Invoke(colour);
        }

        /// <summary>
        /// Accepts RRGGBB and RRGGBBAA, with or without the leading #. Deliberately
        /// stricter than ColorUtility, which also takes 3- and 4-digit forms — the
        /// palette writes 8-digit hex and a file that round-trips should read back
        /// exactly what it wrote.
        /// </summary>
        public static bool TryParseHex(string text, out Color32 colour)
        {
            colour = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string hex = text.Trim().TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8) return false;

            if (!uint.TryParse(hex, NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out uint packed))
                return false;

            if (hex.Length == 6)
            {
                colour = new Color32((byte)((packed >> 16) & 0xFF),
                                     (byte)((packed >> 8)  & 0xFF),
                                     (byte)(packed         & 0xFF),
                                     255);
                return true;
            }

            colour = new Color32((byte)((packed >> 24) & 0xFF),
                                 (byte)((packed >> 16) & 0xFF),
                                 (byte)((packed >> 8)  & 0xFF),
                                 (byte)(packed         & 0xFF));
            return true;
        }

        // -------------------------------------------------------------- closing

        /// <summary>Back to the opening colour without closing — an undo inside the picker.</summary>
        public void Revert()
        {
            PushToWidgets(_opening);
            _onChanged?.Invoke(_opening);
        }

        public void Accept()
        {
            var colour = FromWidgets();

            // Callbacks cleared and the window hidden BEFORE the callback runs: it may
            // well open another dialog, and a picker still nominally open would cancel it.
            var onAccept = _onAccept;
            Clear();

            onAccept?.Invoke(colour);
        }

        public void Cancel()
        {
            var onChanged = _onChanged;
            var onCancel  = _onCancel;
            var opening   = _opening;

            Clear();

            // Put the live preview back FIRST, then tell the caller. Otherwise the
            // colour stays wherever the drag left it, which is the one thing a Cancel
            // must never do.
            onChanged?.Invoke(opening);
            onCancel?.Invoke();
        }

        private void Clear()
        {
            _onChanged = null;
            _onAccept  = null;
            _onCancel  = null;

            if (window != null) window.SetActive(false);
        }

        [ContextMenu("Log Picker Wiring")]
        private void LogWiring() =>
            Debug.Log($"[{nameof(ColourPickerView)}] window=" +
                      $"{(window != null ? window.name : "NONE")} open={IsOpen} " +
                      $"h={(hueSlider != null)} s={(saturationSlider != null)} " +
                      $"v={(valueSlider != null)} a={(alphaSlider != null)} " +
                      $"hex={(hexField != null)}", this);
    }
}
