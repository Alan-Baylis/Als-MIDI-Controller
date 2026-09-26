using System;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The ONE guard in front of every layout load: clean grid = load now, dirty grid =
    /// ask Save / Don't Save / Cancel first.
    ///
    /// It exists because the guard used to live inside LayoutBrowserView, and the Load
    /// button on LayoutControls went straight to PadLayoutService.LoadFrom — so the same
    /// click that asked first in the list silently discarded work from the button. Two
    /// routes to one action need one rule, not two copies of it (DESIGN.md §1.1).
    ///
    /// Static rather than a component: it holds no scene references, and anything that
    /// can load a layout — the browser, the strip, a future MIDI shortcut — can call it
    /// without being wired to it.
    /// </summary>
    public static class LayoutLoadGuard
    {
        /// <summary>
        /// Fallback window when there is no ConfirmDialogView in the scene: choosing the
        /// same layout again inside it means "load anyway". Long enough to reopen the
        /// file browser and pick the file a second time.
        /// </summary>
        private const float PendingSeconds = 6f;

        private static string _pendingPath;
        private static float  _pendingUntil;

        /// <summary>
        /// Statics survive play-mode exit when domain reload is off (§9.8), and
        /// Time.unscaledTime restarts at zero — so a pending path from the last run
        /// could otherwise still count as "pending" for the first few seconds of this one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => CancelPending();

        /// <summary>
        /// Load <paramref name="path"/>, asking first when the grid has unsaved changes.
        /// displayName is optional; the layout name is derived from the path if omitted.
        /// </summary>
        public static void Request(PadLayoutService layouts, string path,
                                   string displayName = null, bool confirmWhenUnsaved = true)
        {
            if (layouts == null || string.IsNullOrWhiteSpace(path)) return;

            if (string.IsNullOrWhiteSpace(displayName))
                displayName = layouts.NameFromPath(path);

            if (!confirmWhenUnsaved || !layouts.IsDirty)
            {
                CancelPending();
                layouts.LoadFrom(path);
                return;
            }

            // Second request for the SAME file inside the window = "yes, discard". Only
            // reachable when there is no dialog in the scene (see below).
            if (IsPending(path))
            {
                CancelPending();
                layouts.LoadFrom(path);
                return;
            }

            string current = string.IsNullOrWhiteSpace(layouts.CurrentLayoutName)
                ? "The current grid"
                : $"\"{layouts.CurrentLayoutName}\"";

            var request = new ConfirmRequest
            {
                Title       = "Unsaved changes",
                Message     = $"{current} has changes that are not saved.\n\n" +
                              $"Save before loading \"{displayName}\"?",
                YesLabel    = "Save",
                NoLabel     = "Don't Save",
                CancelLabel = "Cancel",
                ShowCancel  = true,

                // Save THEN load, and only load if the save actually succeeded —
                // chaining them blind is how you lose the kit you asked to keep.
                OnYes    = () => SaveThenLoad(layouts, path),
                OnNo     = () => layouts.LoadFrom(path),
                OnCancel = () => NotificationService.Say("Load cancelled.", NoticeLevel.Info)
            };

            if (ConfirmDialogView.Ask(request))
            {
                CancelPending();
                return;
            }

            // No dialog in the scene. Degrade, never disappear (§1.8): warn and require
            // the same choice twice, rather than refusing outright or discarding work.
            _pendingPath  = path;
            _pendingUntil = Time.unscaledTime + PendingSeconds;

            NotificationService.Say(
                $"{current} has unsaved changes. Choose \"{displayName}\" again within " +
                $"{PendingSeconds:0} seconds to load it anyway and lose them, or Save first.",
                NoticeLevel.Warning);
        }

        public static void CancelPending()
        {
            _pendingPath  = null;
            _pendingUntil = 0f;
        }

        private static bool IsPending(string path) =>
            _pendingPath != null &&
            Time.unscaledTime < _pendingUntil &&
            string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Save the grid where it belongs, THEN run <paramref name="then"/> — only if
        /// the save actually happened. A named layout saves over its own file; an
        /// unnamed one goes through the save dialog, and a cancelled dialog runs
        /// nothing. Shared with AppExit, so "Save" means the same thing whether you are
        /// loading another kit or quitting.
        /// </summary>
        /// <param name="abandoned">Finishes "Save failed, so …" — e.g. "nothing was loaded".</param>
        public static void SaveCurrentThen(PadLayoutService layouts, Action then, string abandoned)
        {
            if (layouts == null) return;

            string target = layouts.CurrentLayoutPath;

            // Save over the file the grid actually came from, not wherever its name
            // would put it in the default folder.
            if (!string.IsNullOrWhiteSpace(target))
            {
                if (layouts.SaveTo(target)) then?.Invoke();
                else SaveFailed(abandoned);
                return;
            }

            // Unnamed: ask for a name — the same prompt as the browser's Save As. Cancel,
            // a declined overwrite and a failed write all run the abandoned branch, never
            // `then`, so a load or a quit never goes ahead on a grid that was not saved.
            if (LayoutSavePrompt.Ask(layouts, layouts.FolderPath, then,
                    () => NotificationService.Say($"Not saved, so {abandoned}.",
                        NoticeLevel.Info),
                    "Save current layout"))
                return;

            // No name prompt in the scene: the file dialog is the fallback.
            bool opened = FileBrowserView.SaveFile(
                "Save current layout", layouts.FolderPath, "My Kit", layouts.Extension,
                saved =>
                {
                    if (layouts.SaveTo(saved)) then?.Invoke();
                    else SaveFailed(abandoned);
                });

            if (!opened)
                NotificationService.Say(
                    "No dialog in the scene, so the unnamed layout cannot be " +
                    $"saved — {abandoned}.", NoticeLevel.Error);
        }

        private static void SaveThenLoad(PadLayoutService layouts, string pathToLoad) =>
            SaveCurrentThen(layouts, () => layouts.LoadFrom(pathToLoad), "nothing was loaded");

        private static void SaveFailed(string abandoned) =>
            NotificationService.Say($"Save failed, so {abandoned} — the grid is untouched.",
                                    NoticeLevel.Error);
    }
}
