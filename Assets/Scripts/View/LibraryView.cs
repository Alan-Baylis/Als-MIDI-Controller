using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// Lower half of the Pad page: a folder browser mirroring Explorer, rooted at Root.
    ///
    /// Left-click auditions. Right-click acts — loads into the selected pad, falling
    /// back to a preview when no pad is selected. Rows become drag-sources next.
    ///
    /// The pad being targeted is shown by PadInspectorView directly above, so this
    /// class no longer reports it.
    /// </summary>
    [DisallowMultipleComponent]
    public class LibraryView : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Manager manager;
        [SerializeField] private SelectionService selection;
        [SerializeField] private PadController controller;
        [SerializeField] private NotificationService notifications;
        [SerializeField] private ClipCache clipCache;

        [Header("Navigation Bar")]
        [SerializeField] private TextMeshProUGUI pathLabel;
        [SerializeField] private Button upButton;
        [SerializeField] private Button refreshButton;
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
        [Tooltip("Left-click auditions a file. Turn off for a silent browser.")]
        [SerializeField] private bool previewOnClick = true;

        [Tooltip("Play the pad once immediately after assigning. Off by default: the " +
                 "user has usually just previewed the clip, so replaying it is noise.")]
        [SerializeField] private bool auditionOnAssign = false;

        [SerializeField, Range(0f, 1f)] private float previewVolume = 0.8f;

        /// <summary>
        /// Is the audition voice sounding? The screensaver asks, because a preview is
        /// the app being used just as much as a pad is.
        /// </summary>
        public bool IsPreviewing => _previewSource != null && _previewSource.isPlaying;
        
        private string _currentDir;
        private readonly List<LibraryItemView> _rows = new List<LibraryItemView>();

        /// <summary>
        /// Lives on its own scene-root object, NOT on this one: a Unity AudioSource is
        /// silenced the moment its GameObject deactivates, so a preview hosted here
        /// would be cut off every time the page is hidden.
        /// </summary>
        private GameObject _previewHost;
        private AudioSource _previewSource;

        /// <summary>Cache key the preview voice holds. Non-null EXACTLY when we hold a reference.</summary>
        private string _previewKey;

        /// <summary>Bumped on every preview and every stop, so a slow decode that lands
        /// after the user moved on releases its own reference instead of stealing the current one.</summary>
        private int _previewToken;

        private string Root
        {
            get => manager != null ? manager.ResolvedLibraryRoot : null;
            set { if (manager != null) manager.libraryRoot = value; }
        }

        private void Awake()
        {
            if (manager       == null) manager       = FindAnyObjectByType<Manager>();
            if (selection     == null) selection     = FindAnyObjectByType<SelectionService>();
            if (controller    == null) controller    = FindAnyObjectByType<PadController>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();
            if (clipCache     == null) clipCache     = FindAnyObjectByType<ClipCache>();

            _previewHost = new GameObject("Library Preview");
            _previewSource = _previewHost.AddComponent<AudioSource>();
            _previewSource.playOnAwake  = false;
            _previewSource.spatialBlend = 0f;

            if (upButton         != null) upButton.onClick.AddListener(NavigateUp);
            if (refreshButton    != null) refreshButton.onClick.AddListener(Refresh);
            if (chooseRootButton != null) chooseRootButton.onClick.AddListener(ChooseRoot);
        }

        private void OnEnable()
        {
            string root = Root;

            if (string.IsNullOrEmpty(root))
            {
                _currentDir = null;

                // Two different faults, two different messages: nothing chosen, or a
                // folder chosen that is not there (not copied into the build, drive
                // unplugged, folder renamed). The old text said "not set" for both.
                string configured = manager != null ? manager.libraryRoot : null;

                ShowMessage(string.IsNullOrWhiteSpace(configured)
                    ? "No library folder set. Click \"Folder…\"."
                    : $"Library folder \"{configured}\" not found. Click \"Folder…\".");
                return;
            }

            if (string.IsNullOrEmpty(_currentDir) || !LibraryScanner.IsWithin(_currentDir, root))
                _currentDir = root;

            Rebuild();
        }

        private void OnDestroy()
        {
            ReleasePreview();
            if (_previewHost != null) Destroy(_previewHost);
        }

        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        // ------------------------------------------------------------ actions

        public void NavigateTo(string directory)
        {
            if (!LibraryScanner.IsWithin(directory, Root)) return;   // never escape the root

            _currentDir = LibraryScanner.NormalizeDir(directory);
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
            if (!string.IsNullOrEmpty(_currentDir)) Rebuild();
        }

        // --------------------------------------------------------- list build

        private void Rebuild()
        {
            ClearRows();

            if (!LibraryScanner.TryRead(_currentDir, out var listing))
            {
                ShowMessage("Folder unavailable. Try Refresh or choose another.");
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

            Canvas.ForceUpdateCanvases();
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
        }

        private void AddRow(LibraryEntry entry)
        {
            var row = Instantiate(rowPrefab, content);
            row.gameObject.SetActive(true);
            row.Bind(entry, entry.IsDirectory ? folderIcon : fileIcon, OnRowActivated);
            _rows.Add(row);
        }

        /// <summary>
        /// Left = audition. Right = act: load into the selected pad, falling back to a
        /// preview when there is no pad to load into.
        /// </summary>
        private void OnRowActivated(LibraryItemView row, PointerEventData.InputButton button)
        {
            if (row.Entry.IsDirectory)
            {
                // Right-clicking a folder is reserved for a context menu
                // (set as root, reveal in Explorer).
                if (button == PointerEventData.InputButton.Left) NavigateTo(row.Entry.Path);
                return;
            }

            if (button == PointerEventData.InputButton.Left)
            {
                if (previewOnClick) Preview(row.Entry.Path);
                return;
            }

            if (selection != null && selection.Selected.HasValue && controller != null)
                AssignTo(selection.Selected.Value, row.Entry.Path);
            else if (previewOnClick)
                Preview(row.Entry.Path);
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

        // ------------------------------------------------------------- assign

        private void AssignTo(int note, string absolutePath)
        {
            StopPreview();

            string fileName = Path.GetFileName(absolutePath);
            Notify($"Loading \"{fileName}\" → Pad {note}…");

            // All refcounting and load-state handling lives in the controller.
            controller.AssignClipAsync(note, absolutePath, success =>
            {
                if (!success) return;      // the controller already reported the failure

                if (auditionOnAssign) controller.TriggerPadFromMouse(note);

                var clip = controller.GetClip(note);

                // Safe to read the trim here: AssignClipAsync → SetClip → model.Set
                // raises PadChanged synchronously, so ApplyToVoice has already run and
                // measured this clip before the callback is reached.
                //
                // Said on the ASSIGN, not on the measurement: the measurement also
                // happens for all 64 pads on a session load, and nobody wants to read
                // that. Here it answers the question the user is actually asking —
                // "why does this start where it does".
                float trimMs = controller.TrimSeconds(note) * 1000f;
                string trim  = trimMs > 0f ? $", skipping {trimMs:0} ms of silence" : "";

                Notify(clip != null
                        ? $"Pad {note} ← \"{clip.name}\" ({clip.length:F2}s){trim}"
                        : $"Pad {note} ← \"{fileName}\"{trim}",
                    NoticeLevel.Success);
            });
        }

        // ------------------------------------------------------------ preview

        private void Preview(string absolutePath)
        {
            StopPreview();

            if (clipCache == null)
            {
                Notify("No ClipCache in the scene; cannot preview.", NoticeLevel.Error);
                return;
            }

            int token = ++_previewToken;

            clipCache.Acquire(absolutePath, (clip, error) =>
            {
                if (clip == null)
                {
                    // Failure takes no reference, so there is nothing to release here.
                    Notify($"Could not preview \"{Path.GetFileName(absolutePath)}\" — " +
                           $"{error}. See the console for details.", NoticeLevel.Error);
                    return;
                }

                // Superseded while decoding: hand the reference straight back.
                if (_previewToken != token)
                {
                    clipCache.Release(absolutePath);
                    return;
                }

                // _previewKey is set ONLY here, so it always means "we hold this".
                _previewKey = ClipCache.Normalize(absolutePath);

                _previewSource.clip   = clip;
                _previewSource.volume = previewVolume;
                _previewSource.Play();

                Notify($"Preview: \"{clip.name}\" ({clip.length:F2}s)");
            });
        }

        /// <summary>
        /// Public so a future "stop all" control pad can call it. Note that switching
        /// side-panel pages deliberately does NOT stop a preview — it plays out.
        /// </summary>
        public void StopPreview()
        {
            _previewToken++;            // cancels any decode still in flight

            if (_previewSource != null)
            {
                if (_previewSource.isPlaying) _previewSource.Stop();
                _previewSource.clip = null;
            }

            ReleasePreview();
        }

        private void ReleasePreview()
        {
            if (_previewKey == null) return;

            clipCache?.Release(_previewKey);
            _previewKey = null;
        }
        
        /// <summary>
        /// The in-app browser first — it is the only picker that exists in a BUILD. The
        /// editor panel remains as a fallback so the button is never dead.
        /// </summary>
        public void ChooseRoot()
        {
            string start = !string.IsNullOrEmpty(Root) ? Root : FileBrowserView.LastFolder;

            // Audio files are listed (dimmed) so you can see you are in the right folder.
            if (FileBrowserView.PickFolder("Choose library folder", start, ApplyRoot,
                    null, LibraryScanner.Extensions)) return;
            
#if UNITY_EDITOR
            string picked = UnityEditor.EditorUtility.OpenFolderPanel(
                "Choose library folder", start ?? Application.dataPath, "");

            if (string.IsNullOrEmpty(picked)) return;

            ApplyRoot(picked);
#else
            Notify("No FileBrowserView in the scene, so there is no way to pick a folder. " +
                   "Add one to an always-active object.", NoticeLevel.Error);
#endif
        }

        private void ApplyRoot(string picked)
        {
            // Stored portable ("Samples"), not as this machine's absolute path — the same
            // rule the layouts folder uses, and the reason a build finds its library.
            Root = Manager.ToPortablePath(LibraryScanner.NormalizeDir(picked));

            string root = Root;                     // re-read: null if unreadable
            if (string.IsNullOrEmpty(root))
            {
                ShowMessage("That folder could not be read.");
                Notify($"Could not read \"{picked}\".", NoticeLevel.Error);
                return;
            }

            _currentDir = root;
            Rebuild();

            Notify($"Library root: {root}", NoticeLevel.Success);
        }
    }
}