using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// Top half of the Pad page: edits whichever pad is selected. The file browser
    /// below it shares that selection, so clicking a clip fills the pad shown here.
    ///
    /// Buttons are wired in Awake — leave their OnClick lists EMPTY in the Inspector
    /// or they will fire twice.
    /// </summary>
    public class PadInspectorView : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private PadController controller;
        [SerializeField] private SelectionService selection;

        [Tooltip("Optional. Only used to fill the start-beat dropdown with the right " +
                 "number of beats.")]
        [SerializeField] private Metronome metronome;

        [Tooltip("Optional. Only used to start the Load dialog in the library folder.")]
        [SerializeField] private Manager manager;

        [Header("Widgets")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI clipText;
        [SerializeField] private TMP_InputField labelField;
        [SerializeField] private Slider volumeSlider;
        [SerializeField] private Toggle loopToggle;

        [Header("Timing (optional)")]
        [Tooltip("Optional. One control for the whole quantise story: Freestyle (play on " +
                 "the press), Next Beat (the next beat, whichever it is), or a fixed beat " +
                 "of the bar. Filled from the Metronome, so it can never offer a beat the " +
                 "bar does not have.")]
        [SerializeField] private TMP_Dropdown beatDropdown;

        [Header("Buttons (optional — wired automatically)")]
        [SerializeField] private Button loadButton;
        [SerializeField] private Button previewButton;
        [SerializeField] private Button clearButton;

        private int _note = -1;
        private bool _suppressCallbacks;

        public int Note => _note;

        [Tooltip("Optional. The block of per-clip controls. Hidden while the pad has no " +
                 "clip, because volume/loop/label/Clear are edits to a clip that isn't " +
                 "there. Keep the Load button OUTSIDE this object or it vanishes too.")]
        [SerializeField] private GameObject clipSettings;

        private void Awake()
        {
            if (controller == null) controller = FindAnyObjectByType<PadController>();
            if (selection  == null) selection  = FindAnyObjectByType<SelectionService>();
            if (metronome  == null) metronome  = FindAnyObjectByType<Metronome>();
            if (manager    == null) manager    = FindAnyObjectByType<Manager>();

            if (labelField   != null) labelField.onEndEdit.AddListener(OnLabelEdited);
            if (volumeSlider != null) volumeSlider.onValueChanged.AddListener(OnVolumeChanged);
            if (loopToggle   != null) loopToggle.onValueChanged.AddListener(OnLoopChanged);

            BuildBeatOptions();
            if (beatDropdown != null)
                beatDropdown.onValueChanged.AddListener(OnTimingChanged);

            if (loadButton    != null) loadButton.onClick.AddListener(LoadClip);
            if (previewButton != null) previewButton.onClick.AddListener(PreviewClip);
            if (clearButton   != null) clearButton.onClick.AddListener(ClearClip);
        }

        /// <summary>
        /// "  (−18 ms silence)". Shown because a clip that starts late and a clip that has
        /// been trimmed sound identical — the difference is only visible if we say it.
        /// </summary>
        private string TrimSuffix()
        {
            float seconds = controller != null ? controller.TrimSeconds(_note) : 0f;
            return seconds > 0.001f ? $"   (\u2212{seconds * 1000f:0} ms silence)" : string.Empty;
        }
        
        /// <summary>
        /// The dropdown IS the quantise control now. Index 0 is Freestyle (quantize off);
        /// index 1 is "Next Beat" (quantize on, startBeat 0); index N+1 is beat N. Filled
        /// from the METRONOME, so beatsPerBar can never leave a pad pointing at a beat
        /// that no longer exists in the bar.
        /// </summary>
        private void BuildBeatOptions()
        {
            if (beatDropdown == null) return;

            int perBar = metronome != null ? Mathf.Max(1, metronome.BeatsPerBar) : 4;

            var options = new System.Collections.Generic.List<TMP_Dropdown.OptionData>(perBar + 2)
            {
                new TMP_Dropdown.OptionData("Freestyle"),
                new TMP_Dropdown.OptionData("Next Beat")
            };

            for (int beat = 1; beat <= perBar; beat++)
                options.Add(new TMP_Dropdown.OptionData(
                    beat == 1 ? "Beat 1 (downbeat)" : $"Beat {beat}"));

            beatDropdown.ClearOptions();
            beatDropdown.AddOptions(options);
        }

        private void OnEnable()
        {
            if (controller?.Model != null) controller.Model.PadChanged += OnPadChanged;
            if (selection != null) selection.SelectionChanged += OnSelectionChanged;

            Bind(selection != null && selection.Selected.HasValue
                ? selection.Selected.Value
                : -1);
        }

        private void OnDisable()
        {
            if (controller?.Model != null) controller.Model.PadChanged -= OnPadChanged;
            if (selection != null) selection.SelectionChanged -= OnSelectionChanged;
        }

        /// <summary>-1 (or any non-grid note) puts the page into its "nothing selected" state.</summary>
        public void Bind(int note)
        {
            _note = note;
            Refresh();
        }

        private void OnSelectionChanged(int? previous, int? current) => Bind(current ?? -1);

        private void OnPadChanged(int note)
        {
            if (note == _note) Refresh();
        }

        /// TMP's Ellipsis truncates the END, which on a filename throws away the
        /// extension — the one part that is always worth seeing. Cut the middle instead.
        private static string Elide(string s, int max = 28)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            int keepRight = Mathf.Min(12, max / 2);          // ".wav" plus a little context
            int keepLeft  = max - keepRight - 1;
            return s.Substring(0, keepLeft) + "\u2026" + s.Substring(s.Length - keepRight);
        }
        
        private void Refresh()
        {
            bool valid = NoteGrid.IsGridNote(_note);
            var  pad   = valid ? controller?.Model?.Get(_note) : null;

            if (titleText != null)
                titleText.text = valid ? $"Pad {_note}" : "No pad selected";

            if (loadButton    != null) loadButton.interactable    = valid;
            if (previewButton != null) previewButton.interactable = valid && pad != null && pad.clip != null;
            if (clearButton   != null) clearButton.interactable   = valid && pad != null && pad.HasClip;

            if (clipSettings != null)
                clipSettings.SetActive(pad != null && pad.HasClip);

            if (pad == null)
            {
                if (clipText   != null) clipText.text   = string.Empty;
                if (labelField != null) labelField.text = string.Empty;

                // With nothing bound the control must not keep whatever index the
                // scene happened to serialise — a dropdown reading "Beat 1" over an
                // unselected pad is a setting nobody chose.
                if (beatDropdown != null)
                {
                    beatDropdown.SetValueWithoutNotify(0);   // Freestyle
                    beatDropdown.RefreshShownValue();
                }

                return;
            }
            
            _suppressCallbacks = true;

            if (clipText != null)
            {
                switch (pad.loadState)
                {
                    case PadLoadState.Loading: clipText.text = TextGlyphs.Fit(clipText, "loading…"); break;         break;
                    case PadLoadState.Failed:  clipText.text = "could not decode"; break;
                    case PadLoadState.Missing: clipText.text = "file missing";     break;
                    default:
                        clipText.text = TextGlyphs.Fit(clipText, Elide(pad.DisplayLabel) + TrimSuffix());
                        break;
                }
            }

            if (labelField   != null) labelField.text    = pad.label ?? string.Empty;
            if (volumeSlider != null) volumeSlider.value = pad.volume;
            if (loopToggle   != null) loopToggle.isOn    = pad.loop;

            if (beatDropdown != null)
            {
                beatDropdown.SetValueWithoutNotify(TimingToIndex(pad));
                beatDropdown.RefreshShownValue();
                beatDropdown.interactable = true;   // always usable — it is the timing control
            }

            _suppressCallbacks = false;
        }

        // ------------------------------------------------------------ editing

        private void OnLabelEdited(string value)
        {
            if (_suppressCallbacks || !NoteGrid.IsGridNote(_note)) return;
            controller.Model.Set(_note, pad => pad.label = value);
        }

        private void OnVolumeChanged(float value)
        {
            if (_suppressCallbacks || !NoteGrid.IsGridNote(_note)) return;
            controller.Model.Set(_note, pad => pad.volume = Mathf.Clamp01(value));
        }

        private void OnLoopChanged(bool value)
        {
            if (_suppressCallbacks || !NoteGrid.IsGridNote(_note)) return;
            controller.Model.Set(_note, pad => pad.loop = value);
        }

        /// <summary>
        /// One handler for the whole timing story. Index 0 = Freestyle (quantize off);
        /// index 1 = Next Beat (quantize on, startBeat 0); index i>=2 = Beat (i-1).
        /// </summary>
        private void OnTimingChanged(int index)
        {
            if (_suppressCallbacks || !NoteGrid.IsGridNote(_note)) return;

            controller.Model.Set(_note, pad =>
            {
                if (index <= 0)
                {
                    pad.quantizeToBeat = false;      // Freestyle
                }
                else
                {
                    pad.quantizeToBeat = true;
                    pad.startBeat      = index - 1;  // 1 → 0 (Next Beat), 2 → beat 1, …
                }
            });
        }

        /// <summary>Model → dropdown index. Mirror of OnTimingChanged.</summary>
        private int TimingToIndex(PadState pad)
        {
            if (pad == null || !pad.quantizeToBeat) return 0;               // Freestyle
            if (pad.startBeat <= 0) return 1;                              // Next Beat

            int max = beatDropdown != null ? beatDropdown.options.Count - 1 : pad.startBeat + 1;
            return Mathf.Clamp(pad.startBeat + 1, 0, max);
        }

        // ------------------------------------------------------------ actions

        public void ClearClip()
        {
            if (!NoteGrid.IsGridNote(_note)) return;
            controller.ClearPad(_note);
        }

        public void PreviewClip()
        {
            if (!NoteGrid.IsGridNote(_note)) return;

            if (controller.GetClip(_note) != null) controller.TriggerPadFromMouse(_note);
            else NotificationService.Say($"Pad {_note} has no sample.");
        }

        /// <summary>
        /// The in-app browser first, because it is the only one that exists in a BUILD.
        /// The editor panel stays as a fallback for a scene with no FileBrowserView in
        /// it, so this button is never dead while the UI is being assembled.
        /// </summary>
        public void LoadClip()
        {
            if (!NoteGrid.IsGridNote(_note)) return;

            int note  = _note;                       // captured: the selection may move
            string start = FileBrowserView.LastFolder;

            if (string.IsNullOrEmpty(start) && manager != null)
                start = manager.ResolvedLibraryRoot;

            if (FileBrowserView.OpenFile($"Load a sample onto Pad {note}", start,
                                         LibraryScanner.Extensions,
                                         path => controller.AssignClipAsync(
                                             note, path, ok => AnnounceAssigned(note, ok))))
                return;

#if UNITY_EDITOR
            var picked = UnityEditor.EditorUtility.OpenFilePanel(
                "Select a sample", start ?? "", "wav,ogg,aif,aiff,mp3");
            if (string.IsNullOrEmpty(picked)) return;

            FileBrowserView.LastFolder = Path.GetDirectoryName(picked)?.Replace('\\', '/');
            controller.AssignClipAsync(note, picked, ok => AnnounceAssigned(note, ok));
#else
            NotificationService.Say("No FileBrowserView in the scene, so there is no way " +
                                    "to pick a file. Add one to an always-active object.",
                                    NoticeLevel.Error);
#endif
        }
        
        /// <summary>
        /// What landed on the pad, including the silence offset when there is one.
        /// Reports the pad the user PICKED FOR, not the current selection — the
        /// selection may well have moved while the file decoded, which is why note is
        /// captured by the caller.
        ///
        /// Reading the trim here is safe: AssignClipAsync raises PadChanged
        /// synchronously through model.Set before it invokes this, so ApplyToVoice has
        /// already measured the clip.
        /// </summary>
        private void AnnounceAssigned(int note, bool success)
        {
            if (!success) return;      // the controller already reported the failure

            var clip = controller.GetClip(note);
            if (clip == null) return;

            float trimMs = controller.TrimSeconds(note) * 1000f;
            string trim  = trimMs > 0f ? $", skipping {trimMs:0} ms of silence" : "";

            NotificationService.Say(
                $"Pad {note} ← \"{clip.name}\" ({clip.length:F2}s){trim}",
                NoticeLevel.Success);
        }
    }
}