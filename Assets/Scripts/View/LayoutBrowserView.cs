using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// The Layouts browser on the System page: an Explorer-style walk of the layouts
    /// folder where a LEFT-CLICK loads the kit immediately. A kit you have to open a
    /// dialog to reach is a kit you don't change mid-set.
    ///
    /// Instant is only safe because of the guard: when the grid no longer matches the
    /// last save or load, the click asks first (Save / Don't Save / Cancel) instead of
    /// discarding work. PadLayoutService owns that dirty fact — this view only reads it.
    ///
    /// It owns no file logic. Every load goes through PadLayoutService.LoadFrom, which
    /// is still the only thing that reads a .layout.json, so refcounts and missing-file
    /// reporting behave exactly as they do from the Load button.
    ///
    /// This deliberately does NOT derive from LibraryView, and repeats about eighty
    /// lines of its navigation. LibraryView is audio: a preview voice, a ClipCache
    /// reference, assign-to-pad. Sharing a base class would mean reshaping a working
    /// browser in the same change that adds a new one. When the lightshow browser makes
    /// it three copies, that is the moment to extract a FolderBrowserView base — not
    /// before. (DESIGN.md §13: one class, one file.)
    ///
    /// Buttons are wired in Awake — leave their OnClick lists EMPTY (§9.4).
    /// </summary>
    [DisallowMultipleComponent]
    public class LayoutBrowserView : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Manager manager;
        [SerializeField] private PadLayoutService layouts;
        [SerializeField] private NotificationService notifications;

        [Header("Navigation Bar")]
        [SerializeField] private TextMeshProUGUI pathLabel;
        [SerializeField] private Button upButton;
        [Tooltip("Optional. Save As: asks for a name and saves into the folder this " +
                 "browser is showing. (This slot used to be the Refresh button.)")]
        [UnityEngine.Serialization.FormerlySerializedAs("refreshButton")]
        [SerializeField] private Button saveAsButton;
        
        [Tooltip("Optional. Re-points Manager's Layouts Folder, so every layout dialog " +
                 "follows at once.")]
        [SerializeField] private Button chooseRootButton;

        [Header("List")]
        [Tooltip("ScrollRect content with a VerticalLayoutGroup + ContentSizeFitter.")]
        [SerializeField] private RectTransform content;
        [SerializeField] private LibraryItemView rowPrefab;

        [Tooltip("Optional. Only needed once the list is long enough to scroll.")]
        [SerializeField] private ScrollRect scrollRect;

        [Header("Row Icons (optional)")]
        [SerializeField] private Sprite folderIcon;
        [SerializeField] private Sprite fileIcon;

        [Header("Behaviour")]
        [Tooltip("Left-click loads the layout straight away. Off = it only says what it " +
                 "would load, for anyone who wants a browser that cannot fire.")]
        [SerializeField] private bool loadOnClick = true;

        [Tooltip("When the grid has unsaved changes, ask before loading over it. Turning " +
                 "this off means a single click can discard an unsaved kit.")]
        [SerializeField] private bool confirmWhenUnsaved = true;

        [Tooltip("Mark the row of the layout that is currently loaded.")]
        [SerializeField] private bool markCurrent = true;

        [Tooltip("Shown in front of the current layout's name. Any text will do; a " +
                 "character the row's font cannot draw falls back to \">\" by itself " +
                 "(TextGlyphs), so this can never turn into an empty box.")]
        [SerializeField] private string currentMarker = "►";
        private string _currentDir;
        private readonly List<LibraryItemView> _rows = new List<LibraryItemView>();

        /// <summary>
        /// The folder being browsed. Manager owns it (ResolvedLayoutsFolder), which is
        /// also what PadLayoutService.FolderPath reads — so pointing this browser
        /// somewhere new points Save and Load there too, which is the whole idea.
        /// </summary>
        private string Root => layouts != null ? layouts.FolderPath : null;

        // ------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (manager       == null) manager       = FindAnyObjectByType<Manager>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            // Include inactive: PadLayoutService is a scene root today, but a browser
            // that silently finds nothing is the fault we spent a session on.
            if (layouts == null)
                layouts = FindAnyObjectByType<PadLayoutService>(FindObjectsInactive.Include);

            if (layouts == null)
                Debug.LogError($"{nameof(LayoutBrowserView)} found no {nameof(PadLayoutService)}; " +
                               "there is nothing that can load a layout.", this);

            if (upButton         != null) { upButton.onClick.RemoveAllListeners();         upButton.onClick.AddListener(NavigateUp); }
            if (saveAsButton     != null) { saveAsButton.onClick.RemoveAllListeners();     saveAsButton.onClick.AddListener(SaveAs); }            if (chooseRootButton != null) { chooseRootButton.onClick.RemoveAllListeners(); chooseRootButton.onClick.AddListener(ChooseRoot); }
        }

        private void OnEnable()
        {
            if (layouts != null)
            {
                layouts.LayoutSaved   += OnLayoutSettled;
                layouts.LayoutLoaded  += OnLayoutSettled;
                layouts.LayoutCleared += OnLayoutCleared;
            }

            CancelPending();

            string root = Root;

            if (string.IsNullOrEmpty(root))
            {
                _currentDir = null;
                ShowMessage("No layouts folder. Click \"Folder…\".");
                return;
            }

            // Re-root when the folder moved while this page was hidden, and on first
            // show. Otherwise keep the sub-folder the user left off in.
            if (string.IsNullOrEmpty(_currentDir) || !LibraryScanner.IsWithin(_currentDir, root))
                _currentDir = root;

            Rebuild();
        }

        private void OnDisable()
        {
            if (layouts != null)
            {
                layouts.LayoutSaved   -= OnLayoutSettled;
                layouts.LayoutLoaded  -= OnLayoutSettled;
                layouts.LayoutCleared -= OnLayoutCleared;
            }
        }
        
        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        // ------------------------------------------------------------ navigation

        public void NavigateTo(string directory)
        {
            if (!LibraryScanner.IsWithin(directory, Root)) return;   // never escape the root

            _currentDir = LibraryScanner.NormalizeDir(directory);
            CancelPending();
            Rebuild();
        }

        public void NavigateUp()
        {
            if (string.IsNullOrEmpty(_currentDir)) return;

            string root = LibraryScanner.NormalizeDir(Root ?? "");
            if (_currentDir.Equals(root, System.StringComparison.OrdinalIgnoreCase)) return;

            var parent = Directory.GetParent(_currentDir);
            if (parent != null) NavigateTo(parent.FullName);
        }

        public void Refresh()
        {
            CancelPending();
            if (!string.IsNullOrEmpty(_currentDir)) Rebuild();
        }
        
        /// <summary>
        /// Save As, into the folder this browser is SHOWING — sub-folder included. The
        /// user has already navigated; a second navigation dialog would only ask them to
        /// do it again. Replaced the Refresh button: the list rebuilds itself on every
        /// save, load, clear and folder change, so a manual refresh had nothing left to do.
        /// </summary>
        public void SaveAs()
        {
            if (layouts == null) return;

            CancelPending();

            string folder = !string.IsNullOrEmpty(_currentDir) ? _currentDir : Root;

            if (LayoutSavePrompt.Ask(layouts, folder)) return;

            // No ConfirmDialogView, or one with no name field: fall back to the file
            // dialog, opened in the same folder, rather than doing nothing.
            string suggested = string.IsNullOrWhiteSpace(layouts.CurrentLayoutName)
                ? "My Kit"
                : layouts.CurrentLayoutName;

            if (!FileBrowserView.SaveFile("Save layout as", folder, suggested,
                    layouts.Extension, path => layouts.SaveTo(path)))
                Notify("No ConfirmDialogView name field and no FileBrowserView, so there is " +
                       "nowhere to type a name.", NoticeLevel.Error);
        }

        // ------------------------------------------------------------ list build

        private void Rebuild()
        {
            ClearRows();

            // The folder can change WITHOUT this view being told: SessionService.Start
            // restores Manager.layoutsFolder after our OnEnable has already rooted the
            // list in the scene default. Re-root on every rebuild, so the list can never
            // show one folder while Save writes to another.
            string liveRoot = Root;
            if (!string.IsNullOrEmpty(liveRoot) &&
                (string.IsNullOrEmpty(_currentDir) || !LibraryScanner.IsWithin(_currentDir, liveRoot)))
                _currentDir = liveRoot;
            
            // The filter is the SERVICE's extension, not a constant here: change
            // PadLayoutService.Extension and this browser follows. Suffix-matched, so
            // the compound ".layout.json" works (LibraryScanner.HasExtension).
            string[] filter = { layouts != null ? layouts.Extension : ".layout.json" };

            if (!LibraryScanner.TryRead(_currentDir, filter, out var listing))
            {
                ShowMessage("Folder unavailable. Choose another with \"Folder…\".");
                Notify($"Could not read \"{_currentDir}\".", NoticeLevel.Warning);
                return;
            }

            if (pathLabel != null) pathLabel.text = TextGlyphs.Fit(pathLabel, RelativeToRoot(listing.Path));

            string root = LibraryScanner.NormalizeDir(Root ?? "");
            if (upButton != null)
                upButton.interactable =
                    !listing.Path.Equals(root, System.StringComparison.OrdinalIgnoreCase);

            foreach (var folder in listing.Folders) AddRow(folder);
            foreach (var file   in listing.Files)   AddRow(file);

            if (listing.Folders.Count == 0 && listing.Files.Count == 0)
                ShowEmptyHint();

            Canvas.ForceUpdateCanvases();
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
        }

        private void AddRow(LibraryEntry entry)
        {
            var row = Instantiate(rowPrefab, content);
            row.gameObject.SetActive(true);

            // Files are shown by LAYOUT NAME, not file name: "Techno", not
            // "Techno.layout.json". The compound suffix is noise on every single row.
            string caption = entry.IsDirectory || layouts == null
                ? entry.Name
                : layouts.NameFromPath(entry.Path);

            // Compared by PATH: two "Techno" layouts in different subfolders must not
            // both wear the marker.
            if (markCurrent && !entry.IsDirectory && layouts != null &&
                !string.IsNullOrWhiteSpace(layouts.CurrentLayoutPath) &&
                string.Equals(entry.Path.Replace('\\', '/'), layouts.CurrentLayoutPath,
                    System.StringComparison.OrdinalIgnoreCase))
                caption = string.IsNullOrEmpty(currentMarker)
                    ? caption
                    : currentMarker + " " + caption;

            row.Bind(new LibraryEntry(entry.Path, caption, entry.IsDirectory),
                     entry.IsDirectory ? folderIcon : fileIcon,
                     OnRowActivated);

            _rows.Add(row);
        }

        private void ClearRows()
        {
            foreach (var row in _rows)
            {
                if (row == null) continue;
                row.transform.SetParent(null, false);   // leaves the layout group immediately
                Destroy(row.gameObject);
            }
            _rows.Clear();
        }

        private void ShowMessage(string message)
        {
            ClearRows();
            if (pathLabel != null) pathLabel.text = TextGlyphs.Fit(pathLabel, message);
            if (upButton  != null) upButton.interactable = false;
        }

        /// <summary>
        /// An empty folder and an unreadable one look identical otherwise, and the first
        /// thing a new user sees here IS an empty folder.
        /// </summary>
        private void ShowEmptyHint() =>
            Notify("No layouts here yet — press Save As to make one.", NoticeLevel.Info);
        
        private string RelativeToRoot(string dir)
        {
            string root = LibraryScanner.NormalizeDir(Root ?? "");
            if (string.IsNullOrEmpty(root)) return dir;
            if (dir.Equals(root, System.StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(root) + "/";
            if (dir.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(root) + dir.Substring(root.Length);
            return dir;
        }

        // ---------------------------------------------------------------- clicks

        private void OnRowActivated(LibraryItemView row, PointerEventData.InputButton button)
        {
            if (row.Entry.IsDirectory)
            {
                if (button == PointerEventData.InputButton.Left) NavigateTo(row.Entry.Path);
                return;
            }

            if (button != PointerEventData.InputButton.Left) return;   // right-click reserved

            if (!loadOnClick)
            {
                Notify($"\"{row.Entry.Name}\" — click-to-load is off.", NoticeLevel.Info);
                return;
            }

            RequestLoad(row.Entry.Path, row.Entry.Name);
        }

        /// <summary>
        /// The guard now lives in LayoutLoadGuard, shared with the Load button, so the
        /// list and the button can never disagree about asking first.
        /// </summary>
        private void RequestLoad(string path, string displayName) =>
            LayoutLoadGuard.Request(layouts, path, displayName, confirmWhenUnsaved);
        
        private void CancelPending() => LayoutLoadGuard.CancelPending();

        // ----------------------------------------------------------------- chrome

        // A save or load changes which row is current, and a save may have created the
        // file we are looking at.
        private void OnLayoutSettled(string name) { if (isActiveAndEnabled) Rebuild(); }

        private void OnLayoutCleared() { if (isActiveAndEnabled) Rebuild(); }

        // ------------------------------------------------------------------ root

        /// <summary>
        /// Re-points Manager's Layouts Folder. Manager owns it, PadLayoutService reads
        /// it, so Save, Load and this browser all move together (DESIGN.md §3).
        /// </summary>
        public void ChooseRoot()
        {
            string start = Root ?? FileBrowserView.LastFolder;

            // Layout files are listed (dimmed) so you can recognise the right folder.
            string[] show = { layouts != null ? layouts.Extension : ".layout.json" };

            if (FileBrowserView.PickFolder("Choose layouts folder", start, ApplyRoot,
                    null, show)) return;
            
#if UNITY_EDITOR
            string picked = UnityEditor.EditorUtility.OpenFolderPanel(
                "Choose layouts folder", start ?? Application.dataPath, "");

            if (string.IsNullOrEmpty(picked)) return;

            ApplyRoot(picked);
#else
            Notify("No FileBrowserView in the scene, so there is no way to pick a folder. " +
                   "Add one to an always-active object.", NoticeLevel.Error);
#endif
        }

        private void ApplyRoot(string picked)
        {
            if (manager == null)
            {
                Notify("No Manager, so the layouts folder cannot be changed.",
                       NoticeLevel.Error);
                return;
            }

            manager.layoutsFolder = ToPortable(LibraryScanner.NormalizeDir(picked));

            string root = Root;                     // re-read through Manager
            if (string.IsNullOrEmpty(root))
            {
                ShowMessage("That folder could not be read.");
                Notify($"Could not read \"{picked}\".", NoticeLevel.Error);
                return;
            }

            _currentDir = root;
            Rebuild();

            Notify($"Layouts folder: {root}", NoticeLevel.Success);
        }
        
        /// <summary>One rule for every picked folder; see Manager.ToPortablePath.</summary>
        private static string ToPortable(string dir) => Manager.ToPortablePath(dir);

        [ContextMenu("Log Layout Browser Wiring")]
        private void LogWiring() =>
            Debug.Log($"[{nameof(LayoutBrowserView)}] service=" +
                      $"{(layouts != null ? "yes" : "NONE")} root={Root ?? "-"} " +
                      $"dir={_currentDir ?? "-"} rows={_rows.Count} " +
                      $"dirty={(layouts != null && layouts.IsDirty)} " +
                      $"confirm={(ConfirmDialogView.Instance != null ? "yes" : "NONE")}",
                      this);
    }
}
