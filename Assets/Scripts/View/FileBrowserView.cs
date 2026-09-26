using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// What the caller wants. A class rather than a pile of arguments because half the
    /// fields are optional and a call with six bare strings in it is unreadable.
    /// </summary>
    public class FileBrowserRequest
    {
        public string Title = "Choose a file";

        /// <summary>Where to start. Falls back to Root, then LastFolder, then the project folder.</summary>
        public string StartFolder;

        /// <summary>Navigation cannot escape this. Null = the whole filesystem.</summary>
        public string RootFolder;

        /// <summary>Null or empty = every file.</summary>
        public string[] Extensions;

        /// <summary>Save mode shows the name field and allows a name that does not exist yet.</summary>
        public bool SaveMode;

        /// <summary>
        /// Folder mode accepts the folder you are STANDING IN, which is how every OS
        /// folder picker behaves. Files are listed dimmed and cannot be chosen;
        /// Extensions filters which ones appear. SaveMode is ignored.
        /// </summary>
        public bool FolderMode;

        public string DefaultName = "";

        /// <summary>Appended in save mode when the typed name has no extension. ".json" etc.</summary>
        public string DefaultExtension;

        public string AcceptLabel;

        /// <summary>Absolute path. Only called on a real choice.</summary>
        public Action<string> OnAccept;

        public Action OnCancel;
    }

    /// <summary>
    /// The one file dialog. LibraryView remains the DOCKED audio browser because it is
    /// part of the pad workflow; this is the MODAL one, for everything that is a file
    /// operation rather than a browsing session.
    ///
    /// This component must live on an always-active object, with Window pointing at the
    /// panel it shows — the same rule as NotificationLogView, and for the same reason:
    /// a component on a hidden object cannot be asked to un-hide itself.
    ///
    /// Callers never construct UI. They call the three statics, each of which returns
    /// FALSE when there is no browser in the scene, so an editor-only fallback stays
    /// possible instead of the button silently doing nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public class FileBrowserView : MonoBehaviour
    {
        public static FileBrowserView Instance { get; private set; }

        /// <summary>
        /// Folder of the last accepted choice, shared by every dialog. Reopening where
        /// you left off is the single biggest difference between a usable picker and a
        /// chore, and it costs one static string.
        /// </summary>
        public static string LastFolder { get; set; }

        [Header("Window")]
        [Tooltip("The panel that is shown and hidden. NOT this object.")]
        [SerializeField] private GameObject window;

        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text pathLabel;

        [Header("List")]
        [Tooltip("ScrollRect content with a VerticalLayoutGroup + ContentSizeFitter.")]
        [SerializeField] private RectTransform content;

        [Tooltip("The same LibraryRow prefab the library uses.")]
        [SerializeField] private LibraryItemView rowPrefab;

        [Tooltip("Optional. Needed once the list is long enough to scroll.")]
        [SerializeField] private ScrollRect scrollRect;

        [Header("Buttons (optional — wired automatically, leave OnClick EMPTY)")]
        [SerializeField] private Button upButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button acceptButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button newFolderButton;

        [Tooltip("Optional. The caption on the accept button — \"Open\" or \"Save\".")]
        [SerializeField] private TMP_Text acceptLabel;

        [Header("Name (save mode)")]
        [Tooltip("Optional in open mode, REQUIRED for saving. Hidden when not saving.")]
        [SerializeField] private TMP_InputField nameField;

        [Tooltip("Optional. The row holding the name field, hidden in open mode.")]
        [SerializeField] private GameObject nameRow;

        [Header("Row Icons (optional)")]
        [SerializeField] private Sprite folderIcon;
        [SerializeField] private Sprite fileIcon;

        [Header("Behaviour")]
        [Tooltip("Clicking a file in OPEN mode accepts it immediately. Off = it only " +
                 "fills the name box and you press Open.")]
        [SerializeField] private bool clickToOpen = true;

        [Tooltip("Navigating above a drive root lists the drives instead of refusing to " +
                 "move. Only applies when the request has no root folder.")]
        [SerializeField] private bool allowDriveList = true;

        [Tooltip("Escape cancels, Return accepts.")]
        [SerializeField] private bool keyboardShortcuts = true;

        [Tooltip("Saving over an existing file needs a SECOND press of the button, which " +
                 "changes its caption first. Cheaper than a second modal dialog.")]
        [SerializeField] private bool confirmOverwrite = true;

        [SerializeField] private NotificationService notifications;

        private readonly List<LibraryItemView> _rows = new List<LibraryItemView>();

        private FileBrowserRequest _request;
        private string _currentDir;          // null = the drive list
        private string _selected;
        private string _pendingOverwrite;    // path awaiting a confirming second press

        public bool IsOpen => window != null && window.activeSelf;

        /// <summary>The folder currently being shown. Null while the drive list is up.</summary>
        public string CurrentFolder => _currentDir;

        // ------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this)
                Debug.LogWarning("Second FileBrowserView; the first keeps the static " +
                                 "handle and this one will never be asked to open.", this);

            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            if (window == null)
                Debug.LogError($"{nameof(FileBrowserView)} on \"{name}\" has no Window " +
                               "object, so there is nothing to show.", this);
            else if (window == gameObject)
                Debug.LogError($"{nameof(FileBrowserView)} on \"{name}\" has Window set to its " +
                               "OWN object. Hiding it would make the browser impossible to " +
                               "reopen. Put the component on an always-active parent.", this);

            if (upButton        != null) { upButton.onClick.RemoveAllListeners();        upButton.onClick.AddListener(NavigateUp); }
            if (refreshButton   != null) { refreshButton.onClick.RemoveAllListeners();   refreshButton.onClick.AddListener(Refresh); }
            if (acceptButton    != null) { acceptButton.onClick.RemoveAllListeners();    acceptButton.onClick.AddListener(Accept); }
            if (cancelButton    != null) { cancelButton.onClick.RemoveAllListeners();    cancelButton.onClick.AddListener(Cancel); }
            if (newFolderButton != null) { newFolderButton.onClick.RemoveAllListeners(); newFolderButton.onClick.AddListener(NewFolder); }

            if (nameField != null)
            {
                nameField.onValueChanged.RemoveAllListeners();
                nameField.onValueChanged.AddListener(_ => CancelOverwriteConfirm());
            }

            if (window != null) window.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!keyboardShortcuts || !IsOpen) return;

#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.escapeKey.wasPressedThisFrame) { Cancel(); return; }

            if (keyboard.enterKey.wasPressedThisFrame ||
                keyboard.numpadEnterKey.wasPressedThisFrame) Accept();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape)) { Cancel(); return; }

            if (Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter)) Accept();
#endif
        }

        // ------------------------------------------------------------ static API

        /// <summary>
        /// Returns false when there is no browser in the scene, so a caller can fall back
        /// to the editor panel rather than doing nothing at all.
        /// </summary>
        public static bool OpenFile(string title, string folder, string[] extensions,
                                    Action<string> onAccept, string root = null)
        {
            if (Instance == null) return false;

            Instance.Open(new FileBrowserRequest
            {
                Title       = title,
                StartFolder = folder,
                RootFolder  = root,
                Extensions  = extensions,
                AcceptLabel = "Open",
                OnAccept    = onAccept
            });

            return true;
        }

        public static bool SaveFile(string title, string folder, string defaultName,
                                    string extension, Action<string> onAccept,
                                    string root = null)
        {
            if (Instance == null) return false;

            Instance.Open(new FileBrowserRequest
            {
                Title            = title,
                StartFolder      = folder,
                RootFolder       = root,
                Extensions       = string.IsNullOrEmpty(extension) ? null : new[] { extension },
                SaveMode         = true,
                DefaultName      = defaultName,
                DefaultExtension = extension,
                AcceptLabel      = "Save",
                OnAccept         = onAccept
            });

            return true;
        }

        /// <summary>
        /// Accepts the folder you are standing in. Files matching showFiles are listed
        /// dimmed so the folder is recognisable; null lists every file.
        /// </summary>
        public static bool PickFolder(string title, string folder,
            Action<string> onAccept, string root = null,
            string[] showFiles = null)
        {
            if (Instance == null) return false;

            Instance.Open(new FileBrowserRequest
            {
                Title       = title,
                StartFolder = folder,
                RootFolder  = root,
                Extensions  = showFiles,
                FolderMode  = true,
                AcceptLabel = "Use",
                OnAccept    = onAccept
            });

            return true;
        }

        // ---------------------------------------------------------------- open

        public void Open(FileBrowserRequest request)
        {
            if (request == null || window == null) return;

            if (IsOpen)
            {
                // Two dialogs at once is a lost callback and a stuck panel. The one that
                // is already up wins; the newcomer is told, not silently dropped.
                Notify("A file dialog is already open.", NoticeLevel.Warning);
                return;
            }

            _request  = request;
            _selected = null;
            _pendingOverwrite = null;

            string start = FirstExisting(request.StartFolder, LastFolder, request.RootFolder,
                                         Directory.GetParent(Application.dataPath)?.FullName);

            _currentDir = start != null ? LibraryScanner.NormalizeDir(start) : null;

            if (titleText   != null) titleText.text   = request.Title ?? "Choose a file";

            SetAcceptCaption(request.AcceptLabel ??
                             (request.FolderMode ? "Use" :
                              request.SaveMode   ? "Save" : "Open"));

            bool naming = request.SaveMode && !request.FolderMode;

            if (nameRow   != null) nameRow.SetActive(naming);
            if (nameField != null)
            {
                nameField.gameObject.SetActive(naming);
                nameField.SetTextWithoutNotify(request.DefaultName ?? string.Empty);
            }

            window.SetActive(true);
            Rebuild();
        }

        public void Cancel()
        {
            var request = _request;
            CloseInternal();
            request?.OnCancel?.Invoke();
        }

        private void CloseInternal()
        {
            ClearRows();
            _request  = null;
            _selected = null;
            _pendingOverwrite = null;

            if (window != null) window.SetActive(false);
        }

        // -------------------------------------------------------------- accept

        public void Accept()
        {
            if (_request == null) return;

            // Folder mode accepts where you ARE, not what you clicked — clicking a
            // folder walks into it, exactly like every OS picker.
            if (_request.FolderMode)
            {
                if (string.IsNullOrEmpty(_currentDir))
                {
                    Notify("Open a drive first.", NoticeLevel.Warning);
                    return;
                }

                var folderCallback = _request.OnAccept;
                string chosen = _currentDir;

                LastFolder = chosen;
                CloseInternal();
                folderCallback?.Invoke(chosen);
                return;
            }

            string path = ResolveChosenPath();

            if (string.IsNullOrEmpty(path))
            {
                Notify(_request.SaveMode ? "Type a file name first."
                                         : "Choose a file first.", NoticeLevel.Warning);
                return;
            }

            if (!_request.SaveMode && !File.Exists(path))
            {
                Notify($"\"{Path.GetFileName(path)}\" no longer exists. Refresh the list.",
                       NoticeLevel.Warning);
                Refresh();
                return;
            }

            if (_request.SaveMode && confirmOverwrite && File.Exists(path) &&
                !string.Equals(path, _pendingOverwrite, StringComparison.OrdinalIgnoreCase))
            {
                _pendingOverwrite = path;
                SetAcceptCaption("Overwrite?");

                Notify($"\"{Path.GetFileName(path)}\" already exists. Press again to " +
                       "replace it.", NoticeLevel.Warning);
                return;
            }

            var callback = _request.OnAccept;

            LastFolder = Path.GetDirectoryName(path)?.Replace('\\', '/');

            CloseInternal();
            callback?.Invoke(path);
        }

        /// <summary>
        /// Save mode builds from the name box; open mode uses whatever was clicked. The
        /// default extension is appended rather than replaced, so "kit.v2" saves as
        /// "kit.v2.json" and not as "kit.json" with your version number thrown away.
        /// </summary>
        private string ResolveChosenPath()
        {
            if (!_request.SaveMode) return _selected;

            string typed = nameField != null ? nameField.text?.Trim() : null;
            if (string.IsNullOrWhiteSpace(typed) || string.IsNullOrEmpty(_currentDir)) return null;

            foreach (char c in Path.GetInvalidFileNameChars())
                if (typed.IndexOf(c) >= 0)
                {
                    Notify($"\"{typed}\" contains a character Windows will not allow in a " +
                           "file name.", NoticeLevel.Error);
                    return null;
                }

            string ext = _request.DefaultExtension;

            if (!string.IsNullOrEmpty(ext))
            {
                if (ext[0] != '.') ext = "." + ext;
                if (!typed.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) typed += ext;
            }

            return Path.Combine(_currentDir, typed).Replace('\\', '/');
        }

        private void SetAcceptCaption(string caption)
        {
            if (acceptLabel != null) acceptLabel.text = caption;
        }

        /// <summary>Any navigation or retype makes a pending overwrite stale.</summary>
        private void CancelOverwriteConfirm()
        {
            if (_pendingOverwrite == null) return;

            _pendingOverwrite = null;

            SetAcceptCaption(_request?.AcceptLabel ??
                             (_request != null && _request.SaveMode ? "Save" : "Open"));
        }

        // ---------------------------------------------------------- navigation

        public void NavigateTo(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;

            if (!string.IsNullOrEmpty(_request?.RootFolder) &&
                !LibraryScanner.IsWithin(directory, _request.RootFolder)) return;

            _currentDir = LibraryScanner.NormalizeDir(directory);
            _selected   = null;

            CancelOverwriteConfirm();
            Rebuild();
        }

        public void NavigateUp()
        {
            if (string.IsNullOrEmpty(_currentDir)) return;      // already at the drive list

            if (!string.IsNullOrEmpty(_request?.RootFolder))
            {
                string root = LibraryScanner.NormalizeDir(_request.RootFolder);
                if (_currentDir.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
            }

            var parent = Directory.GetParent(_currentDir);

            if (parent != null) { NavigateTo(parent.FullName); return; }

            // At a drive root with no parent. Refusing to move is the wrong answer on a
            // machine with more than one drive.
            if (!allowDriveList || !string.IsNullOrEmpty(_request?.RootFolder)) return;

            _currentDir = null;
            _selected   = null;

            CancelOverwriteConfirm();
            Rebuild();
        }

        public void Refresh() => Rebuild();

        /// <summary>
        /// Uses the name box as the folder name in save mode — one field, two jobs, and
        /// no second dialog for something this small. In folder/open mode it makes a
        /// "New Folder" you can then rename in Explorer.
        /// </summary>
        public void NewFolder()
        {
            if (string.IsNullOrEmpty(_currentDir))
            {
                Notify("Open a drive or folder first.", NoticeLevel.Info);
                return;
            }

            string typed = nameField != null && nameField.gameObject.activeSelf
                ? nameField.text?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(typed)) typed = "New Folder";

            try
            {
                string leaf = Path.GetFileNameWithoutExtension(typed);

                foreach (char c in Path.GetInvalidFileNameChars())
                    if (leaf.IndexOf(c) >= 0)
                    {
                        Notify($"\"{leaf}\" contains a character Windows will not allow in " +
                               "a folder name.", NoticeLevel.Error);
                        return;
                    }

                string path = Path.Combine(_currentDir, leaf);

                if (Directory.Exists(path))
                {
                    Notify($"\"{leaf}\" already exists here.", NoticeLevel.Warning);
                    return;
                }

                Directory.CreateDirectory(path);
                Notify($"Created \"{leaf}\".", NoticeLevel.Success);
                Rebuild();
            }
            catch (Exception e)
            {
                Notify($"Could not create the folder: {e.Message}", NoticeLevel.Error);
            }
        }

        // --------------------------------------------------------- list build

        private void Rebuild()
        {
            ClearRows();

            if (string.IsNullOrEmpty(_currentDir)) { BuildDriveList(); return; }

            // Both modes honour the request's filter. In folder mode it decides which
            // files are SHOWN (dimmed, for recognition); null shows every file.
            string[] filter = _request?.Extensions;

            if (!LibraryScanner.TryRead(_currentDir, filter, out var listing))
            {
                if (pathLabel != null)
                    pathLabel.text = TextGlyphs.Fit(pathLabel, "Folder unavailable — Refresh, or go Up.");
                
                if (upButton != null) upButton.interactable = true;
                return;
            }

            if (pathLabel != null) pathLabel.text = TextGlyphs.Fit(pathLabel, listing.Path);

            if (upButton != null)
            {
                bool atRoot = !string.IsNullOrEmpty(_request?.RootFolder) &&
                              listing.Path.Equals(LibraryScanner.NormalizeDir(_request.RootFolder),
                                                  StringComparison.OrdinalIgnoreCase);

                upButton.interactable = !atRoot &&
                    (Directory.GetParent(listing.Path) != null ||
                     (allowDriveList && string.IsNullOrEmpty(_request?.RootFolder)));
            }

            foreach (var folder in listing.Folders) AddRow(folder);

            // Folder mode shows files too, dimmed and unclickable: you are choosing the
            // folder, but seeing "Techno, House, Live Set" is how you know it is the
            // right one.
            bool folderMode = _request != null && _request.FolderMode;
            foreach (var file in listing.Files) AddRow(file, inert: folderMode);

            EndList();
        }

        /// <summary>
        /// "Computer". Drives are listed as folders, so the existing row prefab, click
        /// handler and icon all apply unchanged.
        /// </summary>
        private void BuildDriveList()
        {
            if (pathLabel != null) pathLabel.text = "This PC";
            if (upButton  != null) upButton.interactable = false;

            try
            {
                foreach (var drive in Directory.GetLogicalDrives())
                {
                    string normalised = drive.Replace('\\', '/').TrimEnd('/');
                    if (normalised.Length == 0) continue;

                    AddRow(new LibraryEntry(normalised, normalised + "/", true));
                }
            }
            catch (Exception e)
            {
                Notify($"Could not list the drives: {e.Message}", NoticeLevel.Warning);
            }

            EndList();
        }

        private void EndList()
        {
            Canvas.ForceUpdateCanvases();
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
        }

        private void AddRow(LibraryEntry entry, bool inert = false)
        {
            if (rowPrefab == null || content == null) return;

            var row = Instantiate(rowPrefab, content);
            row.gameObject.SetActive(true);
            row.Bind(entry, entry.IsDirectory ? folderIcon : fileIcon, OnRowActivated);
            if (inert) row.SetInert(true);
            _rows.Add(row);
        }

        private void OnRowActivated(LibraryItemView row, PointerEventData.InputButton button)
        {
            if (row.Entry.IsDirectory)
            {
                NavigateTo(row.Entry.Path);
                return;
            }
            
            // Folder mode lists files so you can recognise the folder, not so you can
            // pick one. The row is already inert; this is the backstop for a row prefab
            // with no Button to disable.
            if (_request != null && _request.FolderMode) return;            

            _selected = row.Entry.Path;
            CancelOverwriteConfirm();

            // Filling the name box on a click is what makes "save over that one" a click
            // and a button rather than careful retyping.
            if (nameField != null && _request != null && _request.SaveMode)
                nameField.SetTextWithoutNotify(Path.GetFileName(row.Entry.Path));

            if (pathLabel != null)
                pathLabel.text = TextGlyphs.Fit(pathLabel,
                    $"{_currentDir}  →  {Path.GetFileName(row.Entry.Path)}");
            
            if (clickToOpen && _request != null && !_request.SaveMode &&
                button == PointerEventData.InputButton.Left)
                Accept();
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

        // ---------------------------------------------------------------- utils

        private static string FirstExisting(params string[] candidates)
        {
            foreach (var c in candidates)
                if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return c;

            return null;
        }

        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        [ContextMenu("Log Browser Wiring")]
        private void LogWiring() =>
            Debug.Log($"[FileBrowserView] window={(window != null ? window.name : "NONE")} " +
                      $"content={(content != null ? content.name : "NONE")} " +
                      $"rowPrefab={(rowPrefab != null ? rowPrefab.name : "NONE")} " +
                      $"nameField={(nameField != null ? nameField.name : "NONE")} " +
                      $"open={IsOpen} dir={_currentDir ?? "(drives)"} rows={_rows.Count}", this);
    }
}