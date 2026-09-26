using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The device dropdown: "Auto-detect" plus every profile in MidiDeviceCatalog, and
    /// a status line saying what is actually connected.
    ///
    /// It owns nothing. The choice lives on Manager (MidiDeviceId), the session saves
    /// it, and MidiPadInput reacts to the change — this view writes the one field and
    /// repaints from events, so a session restore moves the dropdown too.
    ///
    /// The dropdown is wired in Awake — leave its OnValueChanged list EMPTY (§9.4).
    /// </summary>
    [DisallowMultipleComponent]
    public class MidiDeviceView : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional. Found automatically.")]
        [SerializeField] private Manager manager;

        [Tooltip("Optional. Found automatically.")]
        [SerializeField] private MidiPadInput midi;

        [Header("Widgets")]
        [SerializeField] private TMP_Dropdown deviceDropdown;

        [Tooltip("Optional. \"Connected: Launchpad X — full RGB\" / \"Looking for …\".")]
        [SerializeField] private TMP_Text statusLabel;

        [SerializeField] private string autoCaption = "Auto-detect";

        private void Awake()
        {
            if (manager == null) manager = FindAnyObjectByType<Manager>(FindObjectsInactive.Include);
            if (midi    == null) midi    = FindAnyObjectByType<MidiPadInput>(FindObjectsInactive.Include);

            if (deviceDropdown == null)
            {
                Debug.LogWarning($"{nameof(MidiDeviceView)} on \"{name}\" has no dropdown.", this);
                return;
            }

            var options = new List<TMP_Dropdown.OptionData> { new TMP_Dropdown.OptionData(autoCaption) };
            foreach (var profile in MidiDeviceCatalog.All)
                options.Add(new TMP_Dropdown.OptionData(profile.DisplayName));

            deviceDropdown.ClearOptions();
            deviceDropdown.AddOptions(options);

            deviceDropdown.onValueChanged.RemoveAllListeners();
            deviceDropdown.onValueChanged.AddListener(OnDropdownChanged);
        }

        private void OnEnable()
        {
            if (manager != null) manager.MidiDeviceIdChanged += OnChoiceChanged;

            if (midi != null)
            {
                midi.ProfileChanged    += OnProfileChanged;
                midi.ConnectionChanged += OnConnectionChanged;
            }

            Refresh();
        }

        private void OnDisable()
        {
            if (manager != null) manager.MidiDeviceIdChanged -= OnChoiceChanged;

            if (midi != null)
            {
                midi.ProfileChanged    -= OnProfileChanged;
                midi.ConnectionChanged -= OnConnectionChanged;
            }
        }

        private void OnChoiceChanged(string id)             => Refresh();
        private void OnProfileChanged(PadDeviceProfile p)   => Refresh();
        private void OnConnectionChanged(bool connected)    => Refresh();

        /// <summary>Widget → model. Index 0 is auto; index n is catalog entry n-1.</summary>
        private void OnDropdownChanged(int index)
        {
            if (manager == null) return;

            manager.MidiDeviceId = index <= 0 || index > MidiDeviceCatalog.All.Count
                ? MidiDeviceCatalog.AutoId
                : MidiDeviceCatalog.All[index - 1].Id;
        }

        /// <summary>Model → widget, without notify (§9.5).</summary>
        private void Refresh()
        {
            string id = manager != null ? manager.MidiDeviceId : MidiDeviceCatalog.AutoId;

            if (deviceDropdown != null)
            {
                deviceDropdown.SetValueWithoutNotify(MidiDeviceCatalog.IndexOf(id) + 1);
                deviceDropdown.RefreshShownValue();
            }

            if (statusLabel == null) return;

            var chosen = MidiDeviceCatalog.Find(id);

            string status;

            if (midi != null && midi.Connected && midi.Profile != null)
                status = $"Connected: {midi.Profile.DisplayName} — {midi.Profile.ColourDepth}";
            else
                status = chosen != null
                    ? $"Looking for {chosen.DisplayName}…"
                    : "Not connected — plug in any supported Launchpad.";

            statusLabel.text = TextGlyphs.Fit(statusLabel, status);
        }
    }
}