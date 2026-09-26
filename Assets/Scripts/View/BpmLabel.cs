using TMPro;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Displays Manager.BPM on any TextMeshProUGUI. Add one per label; they all read
    /// the same value, so there is still exactly one BPM in the project.
    /// Re-reads on enable, which covers labels that were switched off by SidePanel
    /// while the value changed.
    /// </summary>
    [DisallowMultipleComponent]
    public class BpmLabel : MonoBehaviour
    {
        [SerializeField] private Manager manager;
        [SerializeField] private TextMeshProUGUI label;
        [SerializeField] private string format = "{0:F0} BPM";

        private void Awake()
        {
            if (manager == null) manager = FindAnyObjectByType<Manager>();
            if (label   == null) label   = GetComponent<TextMeshProUGUI>();

            if (label == null)
                Debug.LogWarning($"{nameof(BpmLabel)} on \"{name}\" has no TextMeshProUGUI.", this);
        }

        private void OnEnable()
        {
            if (manager != null)
            {
                manager.BpmChanged += Refresh;
                Refresh(manager.BPM);
            }
        }

        private void OnDisable()
        {
            if (manager != null) manager.BpmChanged -= Refresh;
        }

        private void Refresh(float bpm)
        {
            if (label != null) label.text = string.Format(format, bpm);
        }
    }
}
