using System;
using System.IO;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// "Save As" without a file dialog: ask for a NAME, save into a folder the caller
    /// already knows — the one the Layouts browser is showing. No navigation, because
    /// the browser the button sits in IS the navigation.
    ///
    /// Used by the browser's Save As button, by LayoutControls' Save when the grid has
    /// never been saved, and by the "Save before loading / quitting?" answer on an
    /// unnamed grid — so every "what should this be called?" is the same question.
    ///
    /// Static, like LayoutLoadGuard: no scene references of its own, and ConfirmDialogView
    /// supplies the window.
    /// </summary>
    public static class LayoutSavePrompt
    {
        private const string DefaultName = "New Layout";

        /// <summary>
        /// Ask for a name and save into <paramref name="folder"/> (the default layouts
        /// folder when null or gone). onSaved runs only after a successful write;
        /// onAbandoned runs on Cancel, on a failed write, and on a declined overwrite —
        /// every way of NOT saving — so a caller waiting to load or quit can stand down.
        ///
        /// Returns false only when there is no dialog to ask with; the caller should
        /// fall back (the file browser) rather than save under a name nobody chose.
        /// </summary>
        public static bool Ask(PadLayoutService layouts, string folder,
                               Action onSaved = null, Action onAbandoned = null,
                               string title = "Save layout as")
        {
            if (layouts == null) return false;

            // Checked BEFORE asking: typing a name and then being told there was never
            // anything to save is the worst order to learn it in.
            if (!layouts.HasContent())
            {
                NotificationService.Say("Nothing on the grid to save — put a sample on a " +
                                        "pad first.", NoticeLevel.Warning);
                onAbandoned?.Invoke();
                return true;
            }

            string target = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                ? LibraryScanner.NormalizeDir(folder)
                : layouts.FolderPath;

            string suggested = string.IsNullOrWhiteSpace(layouts.CurrentLayoutName)
                ? DefaultName
                : layouts.CurrentLayoutName;

            return ConfirmDialogView.AskText(
                title,
                $"Name for the layout.\nIt will be saved in \"{Path.GetFileName(target)}\".",
                suggested,
                "Save",
                name => SaveOrConfirmOverwrite(layouts, target, name, onSaved, onAbandoned),
                Validate,
                () =>
                {
                    NotificationService.Say("Save cancelled.", NoticeLevel.Info);
                    onAbandoned?.Invoke();
                });
        }

        /// <summary>Null = fine. Only emptiness is refused: odd characters are replaced, not rejected.</summary>
        private static string Validate(string name) =>
            string.IsNullOrWhiteSpace(name) ? "Type a name for the layout." : null;

        private static void SaveOrConfirmOverwrite(PadLayoutService layouts, string folder,
                                                   string name, Action onSaved,
                                                   Action onAbandoned)
        {
            string path = layouts.PathIn(folder, name);

            // Saving over the file the grid came from is an ordinary Save, not a
            // replacement of something else — no question needed.
            bool isCurrent = string.Equals(path, layouts.CurrentLayoutPath,
                                           StringComparison.OrdinalIgnoreCase);

            if (!isCurrent && File.Exists(path))
            {
                string shown = layouts.NameFromPath(path);

                bool asked = ConfirmDialogView.Ask(new ConfirmRequest
                {
                    Title      = "Replace layout?",
                    Message    = $"A layout called \"{shown}\" already exists here.\n\n" +
                                 "Replace it? The old one is kept as a .bak file.",
                    YesLabel   = "Replace",
                    NoLabel    = "Cancel",
                    ShowCancel = false,
                    OnYes      = () => Write(layouts, path, onSaved, onAbandoned),
                    OnNo       = () =>
                    {
                        NotificationService.Say("Save cancelled — nothing was replaced.",
                                                NoticeLevel.Info);
                        onAbandoned?.Invoke();
                    }
                });

                if (!asked)
                {
                    // The dialog vanished between the two questions. Refuse rather than
                    // overwrite unasked.
                    NotificationService.Say($"\"{shown}\" already exists and there is no " +
                                            "dialog to confirm replacing it. Nothing was saved.",
                                            NoticeLevel.Warning);
                    onAbandoned?.Invoke();
                }
                return;
            }

            Write(layouts, path, onSaved, onAbandoned);
        }

        /// <summary>SaveTo reports its own failure, so only the outcome is routed here.</summary>
        private static void Write(PadLayoutService layouts, string path,
                                  Action onSaved, Action onAbandoned)
        {
            if (layouts.SaveTo(path)) onSaved?.Invoke();
            else onAbandoned?.Invoke();
        }
    }
}