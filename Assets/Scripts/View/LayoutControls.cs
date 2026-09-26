using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// The always-visible Layouts strip, beside TransportControls — a kit you have to
    /// navigate to is a kit you don't change mid-set.
    ///
    /// Save now writes directly or asks for a name.
    /// Buttons are wired in Awake — leave their OnClick lists EMPTY.
    /// </summary>
    [DisallowMultipleComponent]
    public class LayoutControls : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private PadLayoutService layouts;
        [SerializeField] private PadModel model;
        [SerializeField] private NotificationService notifications;

        [Header("Buttons (optional — wired automatically, leave OnClick EMPTY)")]
        [SerializeField] private Button saveButton;
        [SerializeField] private Button loadButton;
        [SerializeField] private Button newButton;

        [Header("Chrome (optional)")]
        [Tooltip("Optional. Shows the current layout name, with a * while it differs " +
                 "from what is on disk.")]
        [SerializeField] private TMP_Text nameLabel;

        [Tooltip("Caption shown before anything has been saved or loaded.")]
        [SerializeField] private string emptyCaption = "Untitled";

        [Tooltip("Optional. The New button's caption, so it can ask for confirmation.")]
        [SerializeField] private TMP_Text newLabel;

        [Header("Behaviour")]
        [Tooltip("Clearing the grid needs a SECOND press, which changes the caption " +
                 "first. Cheaper than a modal, and Clear-by-accident is expensive.")]
        [SerializeField] private bool confirmNew = true;

        [SerializeField, Min(1f)] private float confirmSeconds = 4f;

        [Tooltip("Mark the name with a * when the grid no longer matches the saved file. " +
                 "The fact itself lives on PadLayoutService — this only decides whether " +
                 "to show it.")]
        [SerializeField] private bool markUnsaved = true;

        [Tooltip("When the grid has unsaved changes, ask before the Load button loads " +
                 "over it — the same rule as clicking a row in the Layouts browser.")]
        [SerializeField] private bool confirmWhenUnsaved = true;
        
        private float _confirmUntil;

        // ------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (layouts       == null) layouts       = FindAnyObjectByType<PadLayoutService>();
            if (model         == null) model         = FindAnyObjectByType<PadModel>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            if (layouts == null)
                Debug.LogWarning($"{nameof(LayoutControls)} found no {nameof(PadLayoutService)}; " +
                                 "the buttons will have nothing to call.", this);

            if (saveButton != null) { saveButton.onClick.RemoveAllListeners(); saveButton.onClick.AddListener(SaveLayout); }
            if (loadButton != null) { loadButton.onClick.RemoveAllListeners(); loadButton.onClick.AddListener(LoadLayout); }
            if (newButton  != null) { newButton.onClick.RemoveAllListeners();  newButton.onClick.AddListener(NewLayout); }

            if (newLabel == null && newButton != null)
                newLabel = newButton.GetComponentInChildren<TMP_Text>(true);
        }

        private void OnEnable()
        {
            if (layouts != null)
            {
                layouts.LayoutSaved   += OnLayoutSettled;
                layouts.LayoutLoaded  += OnLayoutSettled;
                layouts.LayoutCleared += OnLayoutCleared;
                layouts.DirtyChanged  += OnDirtyChanged;
            }

            RefreshCaption();
        }

        private void OnDisable()
        {
            if (layouts != null)
            {
                layouts.LayoutSaved   -= OnLayoutSettled;
                layouts.LayoutLoaded  -= OnLayoutSettled;
                layouts.LayoutCleared -= OnLayoutCleared;
                layouts.DirtyChanged  -= OnDirtyChanged;
            }
        }

        private void Update()
        {
            if (confirmNew && _confirmUntil > 0f && Time.unscaledTime >= _confirmUntil)
                CancelConfirm();
        }

        // ---------------------------------------------------------------- actions

        /// <summary>
        /// Save over the layout the grid came from — one press, no questions. A grid
        /// that has never been saved has no file yet, so it asks for a name instead.
        /// Saving under a NEW name is the Layouts browser's Save As button.
        /// </summary>
        public void SaveLayout()
        {
            if (layouts == null) return;

            CancelConfirm();

            string current = layouts.CurrentLayoutPath;

            // The folder must still exist: a kit loaded from a USB stick that has since
            // been pulled has a path but nowhere to write it. Asking for a name then
            // saves into the layouts folder instead of failing.
            if (!string.IsNullOrWhiteSpace(current) &&
                System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(current)))
            {
                layouts.SaveTo(current);
                return;
            }

            if (LayoutSavePrompt.Ask(layouts, layouts.FolderPath)) return;

            // No name prompt in the scene: the file dialog is the fallback.
            string suggested = string.IsNullOrWhiteSpace(layouts.CurrentLayoutName)
                ? "My Kit"
                : layouts.CurrentLayoutName;

            if (FileBrowserView.SaveFile("Save pad layout", layouts.FolderPath, suggested,
                    layouts.Extension,
                    path => layouts.SaveTo(path)))
                return;

#if UNITY_EDITOR
            string picked = UnityEditor.EditorUtility.SaveFilePanel(
                "Save pad layout", layouts.FolderPath, suggested, "json");

            if (!string.IsNullOrEmpty(picked)) layouts.SaveTo(picked);
#else
            Notify("No ConfirmDialogView or FileBrowserView in the scene, so there is " +
                   "nowhere to type a name.", NoticeLevel.Error);
#endif
        }

        public void LoadLayout()
        {
            if (layouts == null) return;

            CancelConfirm();

            string folder = layouts.FolderPath;

            if (FileBrowserView.OpenFile("Load pad layout", folder,
                                         new[] { layouts.Extension },
                                         path => LayoutLoadGuard.Request(
                                             layouts, path, null, confirmWhenUnsaved)))
                return;

#if UNITY_EDITOR
            string picked = UnityEditor.EditorUtility.OpenFilePanel(
                "Load pad layout", folder, "json");

            if (!string.IsNullOrEmpty(picked))
                LayoutLoadGuard.Request(layouts, picked, null, confirmWhenUnsaved);
#else
            Notify("No FileBrowserView in the scene, so there is no way to choose a file. " +
                   "Add one to an always-active object.", NoticeLevel.Error);
#endif
        }

        /// <summary>Empties the grid. Asks twice when there is unsaved work.</summary>
        public void NewLayout()
        {
            if (layouts == null) return;

            bool needsConfirm = confirmNew && (layouts.IsDirty || layouts.HasContent());

            if (needsConfirm && _confirmUntil <= 0f)
            {
                _confirmUntil = Time.unscaledTime + confirmSeconds;
                if (newLabel != null) newLabel.text = "Sure?";

                Notify("Press New again to clear the grid. Unsaved changes will be lost.",
                       NoticeLevel.Warning);
                return;
            }

            CancelConfirm();
            layouts.NewLayout();
        }

        // ---------------------------------------------------------------- chrome

        private void OnLayoutSettled(string name) => RefreshCaption();

        private void OnLayoutCleared() => RefreshCaption();
        
        // The service owns the dirty fact now, so "settled" only needs a repaint.
        private void OnDirtyChanged(bool dirty) => RefreshCaption();
        
        private void RefreshCaption()
        {
            if (nameLabel == null) return;

            string name = layouts != null && !string.IsNullOrWhiteSpace(layouts.CurrentLayoutName)
                ? layouts.CurrentLayoutName
                : emptyCaption;

            nameLabel.text = markUnsaved && layouts != null && layouts.IsDirty
                ? name + " *"
                : name;
        }

        private void CancelConfirm()
        {
            _confirmUntil = 0f;
            if (newLabel != null) newLabel.text = "New";
        }

        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        [ContextMenu("Log Layout Wiring")]
        private void LogWiring() =>
            Debug.Log($"[LayoutControls] service={(layouts != null ? "yes" : "NONE")} " +
                      $"folder={(layouts != null ? layouts.FolderPath : "-")} " +
                      $"current=\"{(layouts != null ? layouts.CurrentLayoutName : "")}\" " +
                      $"dirty={(layouts != null && layouts.IsDirty)}", this);
    }
}