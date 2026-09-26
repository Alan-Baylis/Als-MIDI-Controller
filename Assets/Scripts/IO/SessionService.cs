using System;
using System.IO;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Reads and writes the one session file. Loads in Start (every Awake has run by
    /// then, so the model exists and the controller is listening) and saves in
    /// OnApplicationQuit, which fires for a real quit AND for leaving play mode.
    ///
    /// Clips are re-assigned through PadController.AssignClipAsync, so a loaded session
    /// is refcounted exactly like a drag-and-drop — there is no second code path for
    /// getting audio onto a pad. A file that has moved since the save becomes
    /// PadLoadState.Missing and keeps its path, so nothing is silently discarded.
    ///
    /// DURABILITY. Three rules, all of them about never destroying what you already had:
    ///   1. The write is atomic (AtomicFile), so the file on disk is always either the
    ///      old session or the new one — never neither.
    ///   2. An unreadable session is RENAMED, not overwritten. "session.corrupt-…json"
    ///      can be examined or hand-edited; a file overwritten on exit cannot.
    ///   3. A ".tmp" left by an interrupted save is newer than the file beside it, so
    ///      it is checked and used rather than deleted.
    /// </summary>
    [DisallowMultipleComponent]
    public class SessionService : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Manager manager;
        [SerializeField] private PadModel model;
        [SerializeField] private PadController controller;
        [SerializeField] private SelectionService selection;
        [SerializeField] private NotificationService notifications;

        [Tooltip("Optional. Found automatically. Only used to store and restore the " +
                 "name of the layout the grid came from — the layout file itself is " +
                 "never reloaded from a session.")]
        [SerializeField] private PadLayoutService layoutService;
        
        [Header("File")]
        [SerializeField] private string fileName = "session.json";

        [Tooltip("In the Editor, write next to the project instead of into " +
                 "persistentDataPath, so the file is easy to find and diff.")]
        [SerializeField] private bool useProjectFolderInEditor = true;

        [Header("Behaviour")]
        [SerializeField] private bool loadOnStart = true;
        [SerializeField] private bool saveOnQuit  = true;

        [Tooltip("Also write whenever the app loses focus. Cheap insurance against a crash.")]
        [SerializeField] private bool saveOnFocusLost = false;

        [Tooltip("Restore the pad that was being edited.")]
        [SerializeField] private bool restoreSelection = false;

        [Tooltip("Announce every load and save on the ticker. Off = console only.")]
        [SerializeField] private bool announce = true;

        [Header("Recovery")]
        [Tooltip("Use a newer, valid \".tmp\" left behind by an interrupted save instead " +
                 "of the older file beside it. Off = the partial file is discarded.")]
        [SerializeField] private bool adoptInterruptedSaves = true;

        [Tooltip("Rename an unreadable session to session.corrupt-<timestamp>.json rather " +
                 "than letting the next save overwrite it. Off = it is lost on exit.")]
        [SerializeField] private bool keepCorruptFiles = true;

        [Tooltip("When the session is unreadable, fall back to the .bak written by the " +
                 "previous save. This is what turns \"I lost my session\" into \"I lost " +
                 "the last few minutes\".")]
        [SerializeField] private bool recoverFromBackup = true;

        private bool _loaded;

        /// <summary>
        /// True once the session has been read and applied, or definitively given up on.
        /// StartupNotice waits for this so its greeting is the LAST startup message
        /// rather than the first one buried by everything else.
        /// </summary>
        public bool HasLoaded => _loaded;

        public string FilePath
        {
            get
            {
                string folder = Application.persistentDataPath;

#if UNITY_EDITOR
                if (useProjectFolderInEditor)
                {
                    var parent = Directory.GetParent(Application.dataPath);
                    if (parent != null) folder = parent.FullName;
                }
#endif
                return Path.Combine(folder, string.IsNullOrWhiteSpace(fileName)
                    ? "session.json" : fileName).Replace('\\', '/');
            }
        }

        private void Awake()
        {
            if (manager       == null) manager       = FindAnyObjectByType<Manager>();
            if (model         == null) model         = FindAnyObjectByType<PadModel>();
            if (controller    == null) controller    = FindAnyObjectByType<PadController>();
            if (selection     == null) selection     = FindAnyObjectByType<SelectionService>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();
            
            // Include inactive is not needed today — PadLayoutService is a scene root —
            // but a browser or service that silently finds nothing is the fault that
            // cost us a session, so ask for it anyway.
            if (layoutService == null)
                layoutService = FindAnyObjectByType<PadLayoutService>(
                    FindObjectsInactive.Include);
        }

        private void Start()
        {
            if (loadOnStart) Load();
        }

        private void OnApplicationQuit()
        {
            if (saveOnQuit) Save();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused && saveOnFocusLost && _loaded) Save();
        }

        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (!announce) { Debug.Log("[Session] " + message, this); return; }

            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        // ----------------------------------------------------------------- saving

        [ContextMenu("Save Session Now")]
        public void Save()
        {
            string path = FilePath;

            try
            {
                var file = Capture();

                // Atomic: the destination is the old session or the new one at every
                // instant, and the previous contents survive as session.json.bak.
                AtomicFile.WriteText(path, JsonUtility.ToJson(file, true));

                Notify($"Session saved — {file.pads.Count} pad(s) → {path}", NoticeLevel.Success);
            }
            catch (Exception e)
            {
                Notify($"Could not save the session: {e.Message}", NoticeLevel.Error);
            }
        }

        private SessionFile Capture()
        {
            var file = new SessionFile
            {
                version = SessionFile.CurrentVersion,
                savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            if (manager != null)
            {
                file.settings.bpm                   = manager.BPM;
                file.settings.masterVolume          = manager.MasterVolume;
                file.settings.metronomeEnabled      = manager.MetronomeEnabled;
                file.settings.metronomeClickEnabled = manager.MetronomeClickEnabled;
                file.settings.debugEnabled          = manager.DebugEnabled;
                file.settings.libraryRoot           = manager.libraryRoot ?? "";
                file.settings.layoutsFolder         = manager.layoutsFolder ?? "";
                file.settings.vSync                 = manager.VSync;
                file.settings.vSyncInterval         = manager.VSyncInterval;
                file.settings.targetFrameRate       = manager.TargetFrameRate;
                file.settings.showPadLabels         = manager.ShowPadLabels;
                file.settings.showFeatureNotes      = manager.ShowFeatureNotes;
                file.settings.midiProfile           = manager.MidiDeviceId;
            }

            if (selection != null && selection.Selected.HasValue)
                file.settings.selectedPad = selection.Selected.Value;
            
            // Which layout the grid came from, so the next run opens with its name in
            // the strip rather than "Untitled".
            if (layoutService != null)
                file.settings.lastLayoutName = layoutService.CurrentLayoutName ?? "";

            if (layoutService != null)
                file.settings.lastLayoutPath = layoutService.CurrentLayoutPath ?? "";
            
            // Notice colours
            file.notices.info    = Hex(NoticePalette.Info);
            file.notices.success = Hex(NoticePalette.Success);
            file.notices.warning = Hex(NoticePalette.Warning);
            file.notices.error   = Hex(NoticePalette.Error);

            // Palette roles
            var palette = manager != null ? manager.Palette : null;
            if (palette != null)
            {
                AddRole(file, "empty",   palette.empty);
                AddRole(file, "hasClip", palette.hasClip);
                AddRole(file, "loaded",  palette.loaded);
                AddRole(file, "editing", palette.editing);
                AddRole(file, "loading", palette.loading);
                AddRole(file, "missing", palette.missing);
                AddRole(file, "flash",   palette.flash);
            }

            // Pads. Only the ones with something on them — an empty grid is a 200-byte
            // file. The "interesting" rule and the field mapping both live in
            // PadSerialization, so sessions and layouts cannot disagree about either.
            file.pads = PadSerialization.Capture(model);
            return file;
        }

        private static void AddRole(SessionFile file, string role, PadColour colour)
        {
            if (colour == null) return;
            file.palette.Add(new SessionPalette { role = role, colour = Hex(colour.Value) });
        }

        // ---------------------------------------------------------------- loading

        [ContextMenu("Load Session Now")]
        public void Load()
        {
            string path = FilePath;

            // A ".tmp" beside the session is an interrupted save, and it is NEWER than
            // the file next to it. Check it before deciding what to read.
            if (adoptInterruptedSaves)
            {
                path = AtomicFile.ResolveForRead(path, IsReadableSession, out string recovery);
                if (recovery != null) Notify(recovery, NoticeLevel.Warning);
            }

            if (!File.Exists(path))
            {
                // No session, but possibly a backup from a run whose file was removed.
                if (recoverFromBackup && AtomicFile.HasBackup(path) &&
                    AtomicFile.RestoreBackup(path))
                {
                    Notify("The session file was missing, so the backup beside it has been " +
                           "restored.", NoticeLevel.Warning);
                }
                else
                {
                    Notify($"No session file yet — one will be written to {path} on exit.");
                    _loaded = true;
                    return;
                }
            }

            var file = TryRead(path, out string error);

            if (file == null)
            {
                // NEVER just carry on and overwrite it on exit: whatever is in there is
                // the only copy of the user's last session.
                if (keepCorruptFiles)
                {
                    string kept = AtomicFile.SetAside(path, "corrupt");

                    Notify($"Session file is unreadable ({error}). It has been kept as " +
                           $"\"{(kept != null ? Path.GetFileName(kept) : "(could not rename it)")}\" " +
                           "so nothing is lost.", NoticeLevel.Error);
                }
                else
                {
                    Notify($"Session file is unreadable ({error}). Starting empty; it will " +
                           "be overwritten on exit.", NoticeLevel.Error);
                }

                if (recoverFromBackup && AtomicFile.HasBackup(path) &&
                    AtomicFile.RestoreBackup(path))
                {
                    file = TryRead(path, out string backupError);

                    if (file != null)
                        Notify("Recovered the previous save from the backup file — you have " +
                               "lost the changes made after it, not the session.",
                               NoticeLevel.Warning);
                    else
                        Notify($"The backup file is unreadable too ({backupError}). " +
                               "Starting empty.", NoticeLevel.Error);
                }

                if (file == null) { _loaded = true; return; }
            }

            if (file.version > SessionFile.CurrentVersion)
                Notify($"Session file is version {file.version}, newer than this build " +
                       $"({SessionFile.CurrentVersion}). Anything it does not recognise is " +
                       "dropped on the next save.", NoticeLevel.Warning);

            Apply(file);
            _loaded = true;
        }

        /// <summary>Null on any failure, with the reason. Never throws.</summary>
        private static SessionFile TryRead(string path, out string error)
        {
            error = null;

            try
            {
                var file = JsonUtility.FromJson<SessionFile>(File.ReadAllText(path));

                if (file == null) { error = "the file is empty"; return null; }

                return file;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>Parse check for AtomicFile, which knows nothing about session shapes.</summary>
        private static bool IsReadableSession(string candidate) =>
            TryRead(candidate, out _) != null;

        private void Apply(SessionFile file)
        {
            // ---- settings ----
            if (manager != null && file.settings != null)
            {
                manager.BPM                   = file.settings.bpm;
                manager.MasterVolume          = file.settings.masterVolume;
                manager.MetronomeEnabled      = file.settings.metronomeEnabled;
                manager.MetronomeClickEnabled = file.settings.metronomeClickEnabled;
                manager.DebugEnabled          = file.settings.debugEnabled;
                manager.VSync                 = file.settings.vSync;
                manager.VSyncInterval         = Mathf.Max(1, file.settings.vSyncInterval);
                manager.TargetFrameRate       = file.settings.targetFrameRate;
                manager.ShowPadLabels         = file.settings.showPadLabels;
                manager.ShowFeatureNotes      = file.settings.showFeatureNotes;

                if (!string.IsNullOrWhiteSpace(file.settings.libraryRoot))
                    manager.libraryRoot = file.settings.libraryRoot;
                
                // Written by every save since v2 but never read back, so a folder
                // chosen in the Layouts browser reset to "Layouts" on every launch.
                if (!string.IsNullOrWhiteSpace(file.settings.layoutsFolder))
                    manager.layoutsFolder = file.settings.layoutsFolder;

                // The session may name different folders from the scene defaults that
                // Manager.Awake just made sure of.
                manager.EnsureStartupFolders();
                
                // The CHOICE, not the device: "" stays auto. MidiPadInput only
                // reconnects when this differs from what it already found, so restoring
                // the device you are already connected to does not flash the grid.
                manager.MidiDeviceId = file.settings.midiProfile;
            }

            // ---- notice colours ----
            if (file.notices != null)
            {
                if (TryColour(file.notices.info,    out var i)) NoticePalette.Info    = i;
                if (TryColour(file.notices.success, out var s)) NoticePalette.Success = s;
                if (TryColour(file.notices.warning, out var w)) NoticePalette.Warning = w;
                if (TryColour(file.notices.error,   out var e)) NoticePalette.Error   = e;
            }

            // ---- palette ----
            var palette = manager != null ? manager.Palette : null;

            if (palette != null && file.palette != null)
            {
                foreach (var entry in file.palette)
                {
                    if (entry == null || !TryColour(entry.colour, out var colour)) continue;

                    var role = RoleFor(palette, entry.role);
                    if (role != null) role.Value = colour;      // raises Changed → repaints
                }
            }

            // ---- pads ----
            // One mapping for both file formats now — see PadSerialization. clearFirst
            // is false because a session load runs on a grid that is already empty, and
            // clearing it again would fire 64 PadChanged events for nothing.
            PadSerialization.Apply(file.pads, model, controller,
                clearFirst: false,
                out int queued, out int missing);

            model?.RaiseReloaded();

            if (restoreSelection && selection != null &&
                NoteGrid.IsGridNote(file.settings?.selectedPad ?? 0))
                selection.Select(file.settings.selectedPad);
            
            // The NAME, not the file. markClean is left true because this runs after
            // every pad has been written: the clips still decoding do not affect the
            // signature, which reads clipPath and never the clip or the load state.
            if (layoutService != null)
                layoutService.AdoptName(file.settings?.lastLayoutName, true,
                    file.settings?.lastLayoutPath);

            string summary = $"Session loaded — {queued} sample(s) loading";
            if (missing > 0) summary += $", {missing} file(s) missing";

            Notify(summary + ".", missing > 0 ? NoticeLevel.Warning : NoticeLevel.Success);
        }

        private static PadColour RoleFor(PadPaletteAsset palette, string role)
        {
            switch (role)
            {
                case "empty":   return palette.empty;
                case "hasClip": return palette.hasClip;
                case "loaded":  return palette.loaded;
                case "editing": return palette.editing;
                case "loading": return palette.loading;
                case "missing": return palette.missing;
                case "flash":   return palette.flash;
                default:        return null;
            }
        }

        // ------------------------------------------------------------------ utils

        private static string Hex(Color32 colour) => ColorUtility.ToHtmlStringRGBA(colour);

        private static bool TryColour(string hex, out Color32 colour)
        {
            colour = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            if (!hex.StartsWith("#")) hex = "#" + hex;

            if (!ColorUtility.TryParseHtmlString(hex, out Color parsed)) return false;

            colour = parsed;
            return true;
        }

        [ContextMenu("Log Session Path")]
        private void LogPath() => Debug.Log($"[Session] {FilePath}", this);
    }
}