using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// The dismissible "Feature Notes" — virtual post-it hints that point new users at
    /// each control on the UI.
    ///
    /// Manager.ShowFeatureNotes is the single source of truth: the Settings toggle and
    /// the session file both drive it, and this component only reflects it. Clicking the
    /// FeatureNotes button turns the notes OFF (through the Manager, so the Settings
    /// toggle and the session save follow); turning them back ON is done from Settings.
    ///
    /// This component lives on the FeatureNotes BUTTON, which stays active. It toggles a
    /// separate notesRoot — the container the individual notes are parented under — so
    /// hiding the notes never disables this component and it can always be switched back
    /// on. If notesRoot is left empty it falls back to toggling this object's own
    /// children, which matches "add the notes to this object".
    ///
    /// The button is wired in Awake — leave its OnClick list EMPTY.
    /// </summary>
    [DisallowMultipleComponent]
    public class FeatureNotes : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Manager manager;

        [Tooltip("Optional. The button that dismisses the notes. Defaults to a Button on " +
                 "this object.")]
        [SerializeField] private Button dismissButton;

        [Tooltip("Optional. Container the note post-its are parented under, shown and " +
                 "hidden as a group. Leave empty to toggle this object's own children " +
                 "instead — keep this component on an ALWAYS-ACTIVE object in that case.")]
        [SerializeField] private GameObject notesRoot;

        private void Awake()
        {
            if (manager == null) manager = FindAnyObjectByType<Manager>();
            if (dismissButton == null) dismissButton = GetComponent<Button>();

            if (dismissButton != null)
            {
                // Wired here, not in the Inspector: a listener left in the OnClick list
                // fires a second time, per the project-wide rule.
                dismissButton.onClick.RemoveAllListeners();
                dismissButton.onClick.AddListener(Dismiss);
            }

            if (notesRoot == gameObject)
                Debug.LogError($"{nameof(FeatureNotes)} on \"{name}\" has Notes Root set to " +
                               "its OWN object. Hiding the notes would disable this " +
                               "component, so they could never be switched back on. Leave " +
                               "it empty (to toggle the children) or point it at a child " +
                               "container.", this);
        }

        private void OnEnable()
        {
            if (manager != null)
            {
                manager.ShowFeatureNotesChanged += Apply;
                Apply(manager.ShowFeatureNotes);
            }
        }

        private void OnDisable()
        {
            if (manager != null) manager.ShowFeatureNotesChanged -= Apply;
        }

        /// <summary>Clicking the button hides the notes, through the Manager.</summary>
        public void Dismiss()
        {
            if (manager != null) manager.ShowFeatureNotes = false;
            else Apply(false);
        }

        /// <summary>UnityEvent-friendly: show the notes again from anywhere.</summary>
        public void Reveal()
        {
            if (manager != null) manager.ShowFeatureNotes = true;
            else Apply(true);
        }

        private void Apply(bool show)
        {
            if (notesRoot != null && notesRoot != gameObject)
            {
                if (notesRoot.activeSelf != show) notesRoot.SetActive(show);
                return;
            }

            // No dedicated container: toggle the note children in place. The button's
            // own graphic stays, so there is still something to interact with.
            foreach (Transform child in transform)
                if (child.gameObject.activeSelf != show) child.gameObject.SetActive(show);
        }
    }
}
