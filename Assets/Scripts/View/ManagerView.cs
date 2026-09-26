using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// Side-panel page for global settings. Every widget is optional: an unassigned
    /// slot simply means that control isn't on the panel yet.
    ///
    /// Model → widget always uses SetValueWithoutNotify / SetIsOnWithoutNotify.
    /// Without that, Manager raises a change, the widget echoes it back as a user
    /// edit, and the two ping-pong.
    /// </summary>
    /// <remarks>
    /// ManagerView owns widgets on MORE THAN ONE side-panel page (BPM and the
    /// metronome toggles live on SystemView; V-Sync, pad captions, feature notes
    /// and debug live on SettingsView). SidePanel.Show() makes those pages
    /// mutually exclusive, so this component MUST NOT live on any of them: the
    /// page that hides also unsubscribes it, and every control it owns quietly
    /// stops matching the model until that page is shown again.
    ///
    /// That is exactly how the Feature Notes toggle went stale — pressing
    /// FeatureNotesButton raised ShowFeatureNotesChanged while this object was
    /// off and nothing was listening. Host it on SidePanel (a scene root that
    /// never deactivates) and leave the widget slots pointing into the pages;
    /// references to inactive objects are valid and SetIsOnWithoutNotify
    /// repaints a hidden toggle correctly.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ManagerView : MonoBehaviour
    {
        [SerializeField] private Manager manager;

        [Header("Display")]
        [Tooltip("Optional. The Off item in vSyncDropdown now functions as the toggle.")]
        [SerializeField] private Toggle vSyncToggle;

        [Tooltip("Optional. Off / Every frame / Every 2nd … — one control for both the " +
                 "V-Sync switch and its interval. Filled in Awake; don't hand-type the " +
                 "options or they can disagree with what the code sends.")]
        [SerializeField] private TMP_Dropdown vSyncDropdown;

        [Tooltip("Optional. \"144 Hz display, V-Sync on (every 2nd frame) → 72 fps\".")]
        [SerializeField] private TMP_Text displayLabel;

        [Header("Transport")]
        [SerializeField] private Slider bpmSlider;
        [SerializeField] private Toggle metronomeToggle;

        [Tooltip("Audible click. Separate from the metronome LEDs.")]
        [SerializeField] private Toggle metronomeClickToggle;

        [Header("Mix")]
        [SerializeField] private Slider masterVolumeSlider;

        [Header("Pads")]
        [Tooltip("Optional. Show the clip name on every loaded pad.")]
        [SerializeField] private Toggle padLabelsToggle;

        [Tooltip("Optional. Show the dismissible Feature Notes over the UI.")]
        [SerializeField] private Toggle featureNotesToggle;
        
        [Header("Debug")]
        [SerializeField] private Toggle debugToggle;

        // NOTE: the BPM readout is not this class's job — put a BpmLabel on the
        // "BPM Text" object instead, so every page shares one code path.

        // One error, not one per page switch — a console flood gets scrolled past.
        private bool _warnedDisabled;

        // OnDisable also runs on quit and scene unload. Those are not faults.
        private bool _quitting;
        
        private void Awake()
        {
            if (manager == null) manager = FindAnyObjectByType<Manager>();

            if (bpmSlider != null)
            {
                bpmSlider.minValue = Manager.MinBpm;
                bpmSlider.maxValue = Manager.MaxBpm;
                bpmSlider.onValueChanged.AddListener(OnBpmSliderChanged);
            }

            if (masterVolumeSlider != null)
            {
                masterVolumeSlider.minValue = 0f;
                masterVolumeSlider.maxValue = 1f;
                masterVolumeSlider.onValueChanged.AddListener(OnMasterVolumeChanged);
            }

            if (debugToggle != null)
                debugToggle.onValueChanged.AddListener(OnDebugChanged);

            if (metronomeToggle != null)
                metronomeToggle.onValueChanged.AddListener(OnMetronomeChanged);

            if (metronomeClickToggle != null)
                metronomeClickToggle.onValueChanged.AddListener(OnMetronomeClickChanged);
            
            if (vSyncToggle != null)
                vSyncToggle.onValueChanged.AddListener(OnVSyncChanged);

            if (padLabelsToggle != null)
                padLabelsToggle.onValueChanged.AddListener(OnPadLabelsChanged);

            if (featureNotesToggle != null)
                featureNotesToggle.onValueChanged.AddListener(OnFeatureNotesChanged);
            
            BuildVSyncOptions();

            if (vSyncDropdown != null)
                vSyncDropdown.onValueChanged.AddListener(OnVSyncDropdownChanged);
        }

        /// <summary>
        /// Index 0 is off; index N is "present every Nth frame", so the index IS the
        /// vSyncCount Unity wants. No lookup table to get out of step.
        /// </summary>
        private void BuildVSyncOptions()
        {
            if (vSyncDropdown == null) return;

            var options = new List<TMP_Dropdown.OptionData>
            {
                new TMP_Dropdown.OptionData("Off"),
                new TMP_Dropdown.OptionData("Every frame"),
                new TMP_Dropdown.OptionData("Every 2nd frame"),
                new TMP_Dropdown.OptionData("Every 3rd frame"),
                new TMP_Dropdown.OptionData("Every 4th frame")
            };

            vSyncDropdown.ClearOptions();
            vSyncDropdown.AddOptions(options);
        }

        private void OnEnable()
        {
            if (manager != null)
            {
                manager.BpmChanged                   += OnBpmChangedExternally;
                manager.MetronomeEnabledChanged      += OnMetronomeChangedExternally;
                manager.MetronomeClickEnabledChanged += OnMetronomeClickChangedExternally;
                manager.MasterVolumeChanged          += OnMasterVolumeChangedExternally;
                manager.DebugChanged                 += OnDebugChangedExternally;
                manager.VSyncChanged                 += OnVSyncChangedExternally;
                manager.VSyncIntervalChanged         += OnVSyncIntervalChangedExternally;
                manager.DisplayRefreshRateChanged    += OnRefreshRateChanged;
                manager.ShowPadLabelsChanged         += OnPadLabelsChangedExternally;
                manager.ShowFeatureNotesChanged      += OnFeatureNotesChangedExternally;
            }

            Refresh();
        }

        private void OnDisable()
        {
            WarnIfSwitchedOffMidSession();

            if (manager == null) return;

            manager.BpmChanged                   -= OnBpmChangedExternally;
            manager.MetronomeEnabledChanged      -= OnMetronomeChangedExternally;
            manager.MetronomeClickEnabledChanged -= OnMetronomeClickChangedExternally;
            manager.MasterVolumeChanged          -= OnMasterVolumeChangedExternally;
            manager.DebugChanged                 -= OnDebugChangedExternally;
            manager.VSyncChanged                 -= OnVSyncChangedExternally;
            manager.VSyncIntervalChanged         -= OnVSyncIntervalChangedExternally;
            manager.DisplayRefreshRateChanged    -= OnRefreshRateChanged;
            manager.ShowPadLabelsChanged         -= OnPadLabelsChangedExternally;
            manager.ShowFeatureNotesChanged      -= OnFeatureNotesChangedExternally;
        }
        
        // Unity sets this before OnDisable/OnDestroy on quit, which is how we tell a
        // real fault from an ordinary teardown.
        private void OnApplicationQuit()
        {
            _quitting = true;
        }

        /// A ManagerView that is switched off receives no Manager events, so every
        /// control it owns drifts from the model. Silent drift is the bug we just
        /// spent a session on; say so the first time it happens.
        private void WarnIfSwitchedOffMidSession()
        {
            if (_warnedDisabled || _quitting || !gameObject.scene.isLoaded) return;

            _warnedDisabled = true;

            Debug.LogError(
                $"{nameof(ManagerView)} on \"{name}\" was switched off during play. " +
                "While it is off it receives no Manager events, so every control it " +
                "owns — the Feature Notes toggle included — stops matching the model " +
                "and only catches up when this object is switched back on. Move this " +
                "component to an always-active object (the SidePanel root) and leave " +
                "its widget slots pointing into the pages.", this);
        }

        private void Refresh()
        {
            if (manager == null) return;

            if (bpmSlider != null)
                bpmSlider.SetValueWithoutNotify(manager.BPM);

            if (masterVolumeSlider != null)
                masterVolumeSlider.SetValueWithoutNotify(manager.MasterVolume);

            if (debugToggle != null)
                debugToggle.SetIsOnWithoutNotify(manager.DebugEnabled);

            if (metronomeToggle != null)
                metronomeToggle.SetIsOnWithoutNotify(manager.MetronomeEnabled);

            if (metronomeClickToggle != null)
                metronomeClickToggle.SetIsOnWithoutNotify(manager.MetronomeClickEnabled);
            
            if (vSyncToggle != null) vSyncToggle.SetIsOnWithoutNotify(manager.VSync);

            if (padLabelsToggle != null)
                padLabelsToggle.SetIsOnWithoutNotify(manager.ShowPadLabels);

            if (featureNotesToggle != null)
                featureNotesToggle.SetIsOnWithoutNotify(manager.ShowFeatureNotes);
            
            RefreshVSyncDropdown();
            RefreshDisplayLabel();
        }
        
        // ------------------------------------------------------- widget → model

        private void OnBpmSliderChanged(float value)
        {
            if (manager != null) manager.BPM = value;      // setter clamps and raises
        }

        private void OnMasterVolumeChanged(float value)
        {
            if (manager != null) manager.MasterVolume = value;
        }

        private void OnDebugChanged(bool value)
        {
            if (manager != null) manager.DebugEnabled = value;
        }

        private void OnMetronomeChanged(bool value)
        {
            if (manager != null) manager.MetronomeEnabled = value;
        }

        private void OnMetronomeClickChanged(bool value)
        {
            if (manager != null) manager.MetronomeClickEnabled = value;
        }

        private void OnPadLabelsChanged(bool value)
        {
            if (manager != null) manager.ShowPadLabels = value;
        }

        private void OnFeatureNotesChanged(bool value)
        {
            if (manager != null) manager.ShowFeatureNotes = value;
        }
        
        private void OnFeatureNotesChangedExternally(bool value)
        {
            if (featureNotesToggle != null) featureNotesToggle.SetIsOnWithoutNotify(value);
        }
        
        private void OnVSyncChanged(bool value)
        {
            if (manager != null) manager.VSync = value;
        }

        private void OnVSyncDropdownChanged(int index)
        {
            if (manager == null) return;

            if (index <= 0) { manager.VSync = false; return; }

            manager.VSyncInterval = index;   // index N == present every Nth frame
            manager.VSync = true;
        }

        // ------------------------------------------------------- model → widget

        private void OnBpmChangedExternally(float value)
        {
            if (bpmSlider != null) bpmSlider.SetValueWithoutNotify(value);
        }

        private void OnMasterVolumeChangedExternally(float value)
        {
            if (masterVolumeSlider != null) masterVolumeSlider.SetValueWithoutNotify(value);
        }

        private void OnMetronomeChangedExternally(bool value)
        {
            if (metronomeToggle != null) metronomeToggle.SetIsOnWithoutNotify(value);
        }

        private void OnMetronomeClickChangedExternally(bool value)
        {
            if (metronomeClickToggle != null) metronomeClickToggle.SetIsOnWithoutNotify(value);
        }

        private void OnDebugChangedExternally(bool value)
        {
            if (debugToggle != null) debugToggle.SetIsOnWithoutNotify(value);
        }

        private void OnPadLabelsChangedExternally(bool value)
        {
            if (padLabelsToggle != null) padLabelsToggle.SetIsOnWithoutNotify(value);
        }

        private void OnVSyncChangedExternally(bool value)
        {
            if (vSyncToggle != null) vSyncToggle.SetIsOnWithoutNotify(value);
            RefreshVSyncDropdown();
            RefreshDisplayLabel();
        }

        private void OnVSyncIntervalChangedExternally(int value)
        {
            RefreshVSyncDropdown();
            RefreshDisplayLabel();
        }

        private void OnRefreshRateChanged(int hz) => RefreshDisplayLabel();

        private void RefreshVSyncDropdown()
        {
            if (vSyncDropdown == null || manager == null) return;

            int index = manager.VSync ? Mathf.Clamp(manager.VSyncInterval, 1, 4) : 0;

            vSyncDropdown.SetValueWithoutNotify(index);
            vSyncDropdown.RefreshShownValue();
        }

        private void RefreshDisplayLabel()
        {
            if (displayLabel != null && manager != null)
                displayLabel.text = manager.DescribeDisplay();
        }
    }
}