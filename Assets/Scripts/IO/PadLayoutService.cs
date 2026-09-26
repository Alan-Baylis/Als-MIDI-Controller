using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>How an incoming layout meets the grid that is already there.</summary>
    public enum LayoutApplyMode
    {
        /// <summary>Clear all 64 pads first. The layout is the whole grid.</summary>
        Replace,

        /// <summary>Only write the pads the file mentions. Everything else stands.</summary>
        Merge
    }

    /// <summary>
    /// Named pad layouts: save the grid, load it back, list what's on disk.
    ///
    /// Every clip goes on through PadController.AssignClipAsync and every clip comes
    /// off through PadController.ClearPad, so ClipCache refcounting is handled in the
    /// one place that already handles it. This class never touches ClipCache and never
    /// writes pad.clip.
    ///
    /// Two save paths, one implementation: Save(name) is the old folder-relative call,
    /// SaveTo(path) is what the file dialog hands back. Everything else routes through
    /// SaveTo so there is exactly one writer.
    ///
    /// Settings are deliberately out of scope — see PadLayoutFile. Nothing here reads
    /// or writes Manager settings; it only asks it where the Layouts folder is.
    /// </summary>
    [DisallowMultipleComponent]
    public class PadLayoutService : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Manager manager;
        [SerializeField] private PadModel model;
        [SerializeField] private PadController controller;
        [SerializeField] private NotificationService notifications;

        [Header("Folder")]
        [Tooltip("Fallback sub-folder, used only when Manager has no Layouts Folder set. " +
                 "Manager.layoutsFolder is the one users can change.")]
        [SerializeField] private string folderName = "Layouts";

        [Tooltip("In the Editor, write next to the project instead of into " +
                 "persistentDataPath, so the files are easy to find and diff.")]
        [SerializeField] private bool useProjectFolderInEditor = true;

        [SerializeField] private string extension = ".layout.json";

        [Header("Behaviour")]
        [Tooltip("How a loaded layout meets the existing grid.")]
        [SerializeField] private LayoutApplyMode applyMode = LayoutApplyMode.Replace;

        [Tooltip("Store paths inside the library root as relative. Off = always absolute, " +
                 "which is machine-local but immune to the root moving.")]
        [SerializeField] private bool storeRelativePaths = true;

        [Tooltip("Stop every voice before a layout lands. A pad whose clip is swapped " +
                 "mid-note is stopped by the controller anyway; this also clears queues.")]
        [SerializeField] private bool stopPlaybackOnLoad = true;

        [Tooltip("Announce saves and loads on the ticker. Off = console only.")]
        [SerializeField] private bool announce = true;

        [Tooltip("Fingerprint the grid twice a second and raise DirtyChanged when it " +
                 "stops matching the last save or load. Off = the * marker never " +
                 "appears and the layout browser will load over unsaved work without " +
                 "asking.")]
        [SerializeField] private bool trackUnsavedChanges = true;

        [Tooltip("How often the grid is fingerprinted. Twice a second is invisible to " +
                 "the user and free next to a frame.")]
        [SerializeField, Min(0.1f)] private float dirtyCheckSeconds = 0.5f;
        
        /// <summary>Raised after a layout is applied, with its display name.</summary>
        public event Action<string> LayoutLoaded;

        /// <summary>Raised after a layout is written, with its display name.</summary>
        public event Action<string> LayoutSaved;

        /// <summary>Raised when the grid is emptied for a new layout.</summary>
        public event Action LayoutCleared;

        /// <summary>Last layout loaded or saved, for a title bar. Empty = none this run.</summary>
        public string CurrentLayoutName { get; private set; } = "";
        
        /// <summary>
        /// The file the grid was last loaded from or saved to. Empty = none. "Save" goes
        /// HERE, not to PathFor(name): the name alone cannot tell a layout in a
        /// subfolder from one in the root, and that mix-up overwrote the wrong file.
        /// </summary>
        public string CurrentLayoutPath { get; private set; } = "";

        /// <summary>The file suffix, dot included. Never empty.</summary>
        public string Extension =>
            string.IsNullOrWhiteSpace(extension) ? ".layout.json" : extension.Trim();

        private void Awake()
        {
            if (manager       == null) manager       = FindAnyObjectByType<Manager>();
            if (model         == null) model         = FindAnyObjectByType<PadModel>();
            if (controller    == null) controller    = FindAnyObjectByType<PadController>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            if (controller == null)
                Debug.LogError($"{nameof(PadLayoutService)} needs a {nameof(PadController)}; " +
                               "without it there is no sanctioned way to put a clip on a pad.",
                               this);
        }

        /// <summary>
        /// Baseline off the MODEL, not off our own Start. SessionService raises
        /// ModelReloaded once, after it has written every pad back — whereas Start
        /// ordering between two components is not guaranteed, so a baseline taken in our
        /// Start can easily be a baseline of an empty grid, and then the restored
        /// session wears an unsaved "*" nobody earned. This is a latent fault the
        /// session-name work exposed, not a new one.
        /// </summary>
        private void OnEnable()
        {
            if (model != null) model.ModelReloaded += OnModelReloaded;
        }

        private void OnDisable()
        {
            if (model != null) model.ModelReloaded -= OnModelReloaded;
        }

        private void OnModelReloaded() => MarkClean();
        // ----------------------------------------------------------------- paths

        /// <summary>
        /// Manager owns the folder, so the Manager page can move it and every layout
        /// dialog follows. The local fallback only applies when that is unset or
        /// unusable — a Save that fails because nobody set a path is not acceptable.
        /// </summary>
        public string FolderPath
        {
            get
            {
                string fromManager = manager != null ? manager.ResolvedLayoutsFolder : null;
                if (!string.IsNullOrEmpty(fromManager)) return fromManager;

                string folder = Application.persistentDataPath;

#if UNITY_EDITOR
                if (useProjectFolderInEditor)
                {
                    var parent = Directory.GetParent(Application.dataPath);
                    if (parent != null) folder = parent.FullName;
                }
#endif
                return Path.Combine(folder, string.IsNullOrWhiteSpace(folderName)
                    ? "Layouts" : folderName).Replace('\\', '/');
            }
        }

        public string PathFor(string layoutName) =>
            Path.Combine(FolderPath, SafeFileName(layoutName) + Extension).Replace('\\', '/');

        /// <summary>
        /// The file a layout called <paramref name="layoutName"/> would be IN a given
        /// folder — Save As into the folder the browser is showing. Same name rules as
        /// PathFor, so "Kit: Live" becomes "Kit- Live" either way.
        /// </summary>
        public string PathIn(string folder, string layoutName) =>
            Path.Combine(string.IsNullOrWhiteSpace(folder) ? FolderPath : folder,
                SafeFileName(layoutName) + Extension).Replace('\\', '/');
        
        /// <summary>
        /// Layout names are user text and end up as file names, so everything Windows
        /// refuses is replaced rather than rejected — being told "invalid character"
        /// after typing a name is worse than having the colon quietly become a dash.
        /// </summary>
        private static string SafeFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Untitled";

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(raw.Trim().Length);

            foreach (char c in raw.Trim())
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);

            string name = builder.ToString().TrimEnd('.', ' ');
            return string.IsNullOrEmpty(name) ? "Untitled" : name;
        }

        /// <summary>"D:/Kits/Techno.layout.json" → "Techno". Handles the compound suffix.</summary>
        public string NameFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "Untitled";

            string leaf = Path.GetFileName(path);

            if (leaf.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                leaf = leaf.Substring(0, leaf.Length - Extension.Length);
            else
                leaf = Path.GetFileNameWithoutExtension(leaf);

            return string.IsNullOrWhiteSpace(leaf) ? "Untitled" : leaf;
        }

        /// <summary>Display names of every layout in the default folder, alphabetical. Never null.</summary>
        public List<string> List()
        {
            var names = new List<string>();

            try
            {
                if (!Directory.Exists(FolderPath)) return names;

                foreach (var file in Directory.EnumerateFiles(FolderPath, "*" + Extension))
                    names.Add(NameFromPath(file));
            }
            catch (Exception e)
            {
                Notify($"Could not list layouts: {e.Message}", NoticeLevel.Warning);
            }

            names.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            return names;
        }

        public bool Exists(string layoutName) => File.Exists(PathFor(layoutName));

        // ---------------------------------------------------------------- saving

        /// <summary>Saves into the default folder under a display name.</summary>
        public bool Save(string layoutName) => SaveTo(PathFor(layoutName), layoutName);

        /// <summary>
        /// Saves to an exact path — what the file dialog hands back. The display name
        /// is derived from the file name unless one is given, so "Save As" renames the
        /// layout as well as the file, which is what everyone expects it to do.
        /// </summary>
        public bool SaveTo(string absolutePath, string displayName = null)
        {
            if (model == null)
            {
                Notify("No PadModel; there is nothing to save.", NoticeLevel.Error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                Notify("No file name given.", NoticeLevel.Warning);
                return false;
            }

            try
            {
                string name = string.IsNullOrWhiteSpace(displayName)
                    ? NameFromPath(absolutePath)
                    : displayName.Trim();

                var file = Capture(name);

                if (file.pads.Count == 0)
                {
                    Notify("Nothing on the grid to save.", NoticeLevel.Warning);
                    return false;
                }

                // Atomic, and it leaves the previous layout as "<name>.layout.json.bak".
                // Overwriting a kit you meant to keep is one wrong click; getting it back
                // should not need a backup you remembered to make.
                AtomicFile.WriteText(absolutePath, JsonUtility.ToJson(file, true));

                CurrentLayoutName = file.name;
                CurrentLayoutPath = absolutePath.Replace('\\', '/');
                LayoutSaved?.Invoke(file.name);
                MarkClean();
                Notify($"Layout \"{file.name}\" saved — {file.pads.Count} pad(s).",
                       NoticeLevel.Success);
                return true;
            }
            catch (Exception e)
            {
                Notify($"Could not save to \"{absolutePath}\": {e.Message}", NoticeLevel.Error);
                return false;
            }
        }

        private PadLayoutFile Capture(string layoutName)
        {
            string root = manager != null ? manager.ResolvedLibraryRoot : null;

            var file = new PadLayoutFile
            {
                version     = PadLayoutFile.CurrentVersion,
                name        = string.IsNullOrWhiteSpace(layoutName) ? "Untitled" : layoutName.Trim(),
                savedAt     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                libraryRoot = root ?? ""
            };

            foreach (int note in NoteGrid.All)
            {
                var pad = model.Get(note);
                if (pad == null) continue;

                // Same "interesting" test as the session: an empty grid should not be a
                // 64-entry file of defaults.
                if (!PadSerialization.IsInteresting(pad)) continue;

                bool   relative = false;
                string stored   = pad.clipPath;

                if (storeRelativePaths && !string.IsNullOrWhiteSpace(stored) &&
                    !string.IsNullOrEmpty(root) && LibraryScanner.IsWithin(stored, root))
                {
                    string normalised = stored.Replace('\\', '/');
                    string normRoot   = LibraryScanner.NormalizeDir(root);

                    stored   = normalised.Substring(normRoot.Length).TrimStart('/');
                    relative = true;
                }

                file.pads.Add(PadSerialization.ToLayoutRecord(note, pad, stored, relative));
            }

            return file;
        }

        // --------------------------------------------------------------- loading

        public bool Load(string layoutName) => LoadFrom(PathFor(layoutName));

        public bool LoadFrom(string path)
        {
            string requested = path;
            path = AtomicFile.ResolveForRead(path, IsReadableLayout, out string recovery);
            if (recovery != null) Notify(recovery, NoticeLevel.Warning);

            if (!File.Exists(path))
            {
                Notify($"No layout at {path}.", NoticeLevel.Warning);
                return false;
            }

            PadLayoutFile file;

            try
            {
                file = JsonUtility.FromJson<PadLayoutFile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                string backup = path + AtomicFile.BackupSuffix;

                Notify($"Layout file is unreadable ({e.Message})." +
                       (File.Exists(backup)
                           ? $" The previous save is beside it as \"{Path.GetFileName(backup)}\" " +
                             "— rename it to open it."
                           : ""),
                    NoticeLevel.Error);
                return false;
            }

            if (file == null || file.pads == null)
            {
                Notify("Layout file is empty.", NoticeLevel.Warning);
                return false;
            }

            if (file.version > PadLayoutFile.CurrentVersion)
                Notify($"Layout is version {file.version}, newer than this build " +
                       $"({PadLayoutFile.CurrentVersion}). Anything unrecognised is dropped " +
                       "if you re-save it.", NoticeLevel.Warning);

            // A file renamed on disk loads under the name it has NOW: the file IS the
            // layout. This used to apply only when the stored name was empty, so a
            // renamed kit kept its old name, and "Save" in the unsaved-changes dialog
            // then wrote to the OLD name's file.
            //
            // "requested", not "path": after a backup recovery, path can point at the
            // .bak, and neither the name nor the next Save should target that.
            file.name = NameFromPath(requested);

            // Set BEFORE Apply, which raises LayoutLoaded. The browser's current-row
            // marker reads this path.
            CurrentLayoutPath = requested.Replace('\\', '/');

            Apply(file);
            return true;
        }

        public void Apply(PadLayoutFile file)
        {
            if (controller == null || model == null) return;

            if (stopPlaybackOnLoad) controller.StopAllVoices();

            // Replace clears through the CONTROLLER, not the model: ClearPad is what
            // releases the cache reference and cancels a decode in flight. Clearing the
            // model directly would strand both.
            if (applyMode == LayoutApplyMode.Replace)
                foreach (int note in NoteGrid.All) controller.ClearPad(note);

            string root = manager != null ? manager.ResolvedLibraryRoot : null;

            int queued = 0, missing = 0, unresolved = 0;

            foreach (var saved in file.pads)
            {
                if (saved == null || !NoteGrid.IsGridNote(saved.note)) continue;

                int note = saved.note;

                // Resolve BEFORE the model write: the clip path has to be on the pad
                // before AssignClipAsync lands, or SetClip sees a null path, decides
                // this is a different file, and overwrites the saved caption with the
                // filename (overwriteLabelOnAssign). That is what made a loaded kit
                // lose every label it was saved with — and it also re-dirtied the
                // layout half a second after loading it, because Signature reads label.
                string absolute = string.IsNullOrWhiteSpace(saved.clipPath)
                    ? null
                    : Resolve(saved, root);

                // Metadata first, clip second — exactly the order SessionService uses, so
                // a label is on screen while the decode is still running.
                //
                // ApplyMetadata deliberately does NOT write the clip path: a layout
                // stores it relative and it has to be resolved against the CURRENT
                // library root, which is the one thing a layout genuinely does
                // differently. Everything else is shared, which is the whole point.
                model.Set(note, pad =>
                {
                    PadSerialization.ApplyMetadata(saved, pad);

                    // Absolute where we could resolve it; the stored string otherwise, so
                    // a path is never discarded — the drive may come back.
                    pad.clipPath = absolute ?? saved.clipPath;
                });

                if (string.IsNullOrWhiteSpace(saved.clipPath)) continue;

                if (absolute == null)
                {
                    // A relative path with no library root is not "missing", it is
                    // "unresolvable" — different fault, different fix, so say so.
                    controller.SetLoadState(note, PadLoadState.Missing);
                    unresolved++;
                    continue;
                }

                if (!File.Exists(absolute))
                {
                    controller.SetLoadState(note, PadLoadState.Missing);
                    missing++;
                    continue;
                }

                queued++;
                controller.AssignClipAsync(note, absolute);
            }

            model.RaiseReloaded();

            CurrentLayoutName = file.name ?? "";
            
            LayoutLoaded?.Invoke(CurrentLayoutName);

            // Baselined AFTER the model writes above, which are all synchronous. The
            // decodes still in flight do not touch the signature — Signature reads
            // clipPath, never the clip or the load state.
            MarkClean();
            
            string summary = $"Layout \"{CurrentLayoutName}\" loaded — {queued} sample(s) loading";
            if (missing    > 0) summary += $", {missing} file(s) missing";
            if (unresolved > 0) summary += $", {unresolved} needing a library root";

            Notify(summary + ".",
                   (missing > 0 || unresolved > 0) ? NoticeLevel.Warning : NoticeLevel.Success);
        }

        /// <summary>
        /// Empties all 64 pads through the controller and forgets the current name, so
        /// the next Save offers a fresh file rather than overwriting the last one.
        /// </summary>
        public void NewLayout()
        {
            if (controller == null) return;

            controller.StopAllVoices();
            foreach (int note in NoteGrid.All) controller.ClearPad(note);

            model?.RaiseReloaded();

            CurrentLayoutName = "";
            CurrentLayoutPath = "";
            LayoutCleared?.Invoke();

            MarkClean();      // an empty grid is not unsaved work
            
            Notify("Grid cleared — new layout.", NoticeLevel.Info);
        }

        /// <summary>
        /// Relative entries resolve against the CURRENT library root, not the one recorded
        /// in the file — so re-pointing the library fixes every layout at once instead of
        /// requiring each to be re-saved. Returns null when a relative path has no root.
        /// </summary>
        private static string Resolve(LayoutPad saved, string root)
        {
            if (!saved.relative) return saved.clipPath;
            if (string.IsNullOrEmpty(root)) return null;

            return Path.Combine(root, saved.clipPath).Replace('\\', '/');
        }

        // --------------------------------------------------------------- deleting

        public bool Delete(string layoutName)
        {
            string path = PathFor(layoutName);

            try
            {
                if (!File.Exists(path))
                {
                    Notify($"No layout named \"{layoutName}\".", NoticeLevel.Warning);
                    return false;
                }

                File.Delete(path);
                Notify($"Layout \"{layoutName}\" deleted.", NoticeLevel.Info);
                return true;
            }
            catch (Exception e)
            {
                Notify($"Could not delete \"{layoutName}\": {e.Message}", NoticeLevel.Error);
                return false;
            }
        }

        // ------------------------------------------------------------------ utils
        
        /// <summary>Parse check for AtomicFile, which knows nothing about layout shapes.</summary>
        private static bool IsReadableLayout(string candidate)
        {
            try
            {
                var probe = JsonUtility.FromJson<PadLayoutFile>(File.ReadAllText(candidate));
                return probe != null && probe.pads != null;
            }
            catch { return false; }
        }
        
        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (!announce) { Debug.Log("[Layout] " + message, this); return; }

            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        [ContextMenu("Log Layout Folder")]
        private void LogFolder() =>
            Debug.Log($"[Layout] {FolderPath} — {List().Count} layout(s).", this);
        // Stopgap until LayoutControls is in the scene: the service is reachable from
        // the component menu so the file format can be exercised without any UI.
        [ContextMenu("Save Layout (Quick Test)")]
        private void QuickSave() => Save("Quick Test");

        [ContextMenu("Load Layout (Quick Test)")]
        private void QuickLoad() => Load("Quick Test");
        
        // ------------------------------------------------------------ dirty state

        /// <summary>
        /// Raised when the grid starts or stops matching the last save or load. The
        /// strip uses it for the "*", the browser uses it to decide whether a click
        /// needs to ask first — one fact, two readers (DESIGN.md §1.1).
        /// </summary>
        public event Action<bool> DirtyChanged;

        /// <summary>True when the grid no longer matches what is on disk.</summary>
        public bool IsDirty { get; private set; }

        private string _cleanSignature = "";
        private float  _nextDirtyCheck;

        /// <summary>
        /// Polled, not flagged. PadChanged also fires for load-state transitions, so a
        /// flag would light up while a layout was still decoding and the "*" would
        /// appear on a kit nobody had touched (DESIGN.md §1.10).
        /// </summary>
        private void Update()
        {
            if (!trackUnsavedChanges || model == null) return;
            if (Time.unscaledTime < _nextDirtyCheck) return;

            _nextDirtyCheck = Time.unscaledTime + Mathf.Max(0.1f, dirtyCheckSeconds);

            SetDirty(PadSerialization.Signature(model) != _cleanSignature);
        }

        /// <summary>
        /// Re-baselines: the grid as it stands now IS the saved state. Called after
        /// every save, load and clear, and once in Start.
        /// </summary>
        public void MarkClean()
        {
            _cleanSignature = model != null ? PadSerialization.Signature(model) : "";
            _nextDirtyCheck = Time.unscaledTime + Mathf.Max(0.1f, dirtyCheckSeconds);
            SetDirty(false);
        }

        private void SetDirty(bool value)
        {
            if (IsDirty == value) return;

            IsDirty = value;
            DirtyChanged?.Invoke(value);
        }

        /// <summary>
        /// Baselined in Start, not Awake: SessionService also loads in Start, and a
        /// baseline taken before it ran would make a freshly restored session look
        /// unsaved. Start ordering between two components is not guaranteed, so the
        /// first poll is deferred a further half-second by MarkClean above — by which
        /// time the session's synchronous model writes have certainly landed.
        /// </summary>
        private void Start() => MarkClean();

        /// <summary>True when any pad has something on it. Asked before a destructive clear.</summary>
        public bool HasContent()
        {
            if (model == null) return false;

            foreach (int note in NoteGrid.All)
                if (PadSerialization.IsInteresting(model.Get(note))) return true;

            return false;
        }
        
        /// <summary>
        /// Adopt a name without reading a file or touching the grid — the session
        /// restore path. The pads the session just put back ARE the pads that layout was
        /// saved with, so the name is a fact about the grid rather than a guess.
        ///
        /// Raises LayoutLoaded because every listener wants exactly the repaint a load
        /// gives it: the strip repaints its caption, the browser re-marks the current
        /// row. There is nothing either could sensibly do differently, and a second
        /// event meaning almost the same thing is how two of them drift apart.
        /// </summary>
        public void AdoptName(string layoutName, bool markClean = true, string layoutPath = null)
        {
            CurrentLayoutName = string.IsNullOrWhiteSpace(layoutName)
                ? ""
                : layoutName.Trim();

            // The path is trusted only if the file is still there. A session from before
            // paths were stored falls back to the default-folder file of that name, and
            // failing that to no path, so Save asks where instead of guessing.
            if (!string.IsNullOrWhiteSpace(layoutPath) && File.Exists(layoutPath))
                CurrentLayoutPath = layoutPath.Replace('\\', '/');
            else if (CurrentLayoutName.Length > 0 && File.Exists(PathFor(CurrentLayoutName)))
                CurrentLayoutPath = PathFor(CurrentLayoutName);
            else
                CurrentLayoutPath = "";

            if (markClean) MarkClean();

            LayoutLoaded?.Invoke(CurrentLayoutName);
        }
    }
    
}