using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LaunchpadStudio.EditorTools
{
    /// <summary>
    /// Finds the wiring faults Unity does NOT shout about: empty Inspector slots,
    /// broken references, missing scripts, and UnityEvents aimed at deleted methods.
    ///
    /// Every issue is logged with its context object, so clicking the console line
    /// selects the culprit in the Hierarchy.
    ///
    /// By default only project scripts are scanned. Unity's own components are full of
    /// legitimately-empty slots (Image.sprite, Button.targetGraphic on a plain Image,
    /// every unused Text field), and drowning the real faults in that noise defeats
    /// the point. The "Include Unity Components" variant is there when you want it.
    /// </summary>
    public static class SceneWiringCheck
    {
        private const string MenuRoot = "Tools/Scene Wiring Check/";

        /// <summary>
        /// Slots that are optional BY DESIGN — reported as info, not warnings.
        /// Format: "TypeName.fieldName". Add to this list as the project grows;
        /// a field whose [Tooltip] contains the word "optional" is detected
        /// automatically and does not need an entry.
        /// </summary>
        private static readonly HashSet<string> KnownOptional = new HashSet<string>
        {
            "PadView.labelText",
            "LibraryItemView.icon",
            "LibraryItemView.button",
            "LibraryView.folderIcon",
            "LibraryView.fileIcon",
            "LibraryView.scrollRect",
            "PadInspectorView.loadButton",
            "PadInspectorView.previewButton",
            "PadInspectorView.clearButton",
            "PadInspectorView.clipText",
            "PadInspectorView.labelField",
            "PadInspectorView.volumeSlider",
            "PadInspectorView.loopToggle",
            "PadInspectorView.titleText",
            "ManagerView.debugToggle",
            "ManagerView.metronomeToggle",
            "Metronome.accentClip",
            "Metronome.clickClip",
            "BpmLabel.label",
            "PadView.background",
            "PadLedView.midi",
            "LibraryView.notifications",
            "NotificationTicker.viewport",
            "ManagerView.masterVolumeSlider",
            "ManagerView.metronomeClickToggle",
            "TransportControls.library",
            "TransportControls.stopAllButton",
            "SidePanel.inspector",
            "SidePanel.managerChrome",
            "SidePanel.padChrome",
            "SidePanel.switchToPadClip",
            "SidePanel.switchToManagerClip",
            "PadPaletteView.notifications",
            "MidiPadInput.notifications",
            "MidiPadInput.controller",
            "MidiPadInput.manager",
            "MidiDeviceView.manager",
            "MidiDeviceView.midi",
            "MidiDeviceView.statusLabel",
            "NotificationLogView.scrollRect",
            "NotificationLogView.openButton",
            "NotificationLogView.closeButton",
            "NotificationLogView.clearButton",
            "NotificationLogView.copyButton",
            "NotificationLogView.countLabel",
            "NotificationLogView.alertLamp",
            "NotificationLogView.infoToggle",
            "NotificationLogView.successToggle",
            "NotificationLogView.warningToggle",
            "NotificationLogView.errorToggle",
            "NotificationLogRow.timeText",
            "NotificationLogRow.levelText",
            "NotificationLogRow.background",
            "PadInspectorView.clipSettings",
            "NotificationLogView.toggleButton",
            "PadInspectorView.quantizeToggle",
            "PadInspectorView.beatDropdown",
            "PadInspectorView.metronome",
            "ManagerView.vSyncToggle",
            "PadController.metronome",
            "PadLedView.overlay",
            "ScreensaverMatrix.overlay",
            "ScreensaverMatrix.controller",
            "ScreensaverMatrix.selection",
            "SessionService.notifications",
            "SessionService.selection",
            "AppExit.quitButton",
            "AppExit.confirmPanel",
            "AppExit.confirmYesButton",
            "AppExit.confirmNoButton",
            "AppExit.layouts",
            "PadLayoutService.manager",
            "PadLayoutService.notifications",
            "SessionService.layoutService",
            "ManagerView.vSyncDropdown",
            "ManagerView.displayLabel",
            "ManagerView.padLabelsToggle",
            "PadInspectorView.manager",
            "LayoutControls.saveButton",
            "LayoutControls.loadButton",
            "LayoutControls.newButton",
            "LayoutControls.nameLabel",
            "LayoutControls.newLabel",
            "LayoutControls.notifications",
            "FileBrowserView.nameRow",
            "FileBrowserView.folderIcon",
            "FileBrowserView.fileIcon",
            "FileBrowserView.notifications",
            "FileBrowserView.acceptLabel",
            "FileBrowserView.newFolderButton",
            "SidePanel.selector",
            "SidePanel.settingsPage",
            "SidePanel.switchToSettingsClip",
            "LogFileWriter.notifications",
            "NotificationLogView.logFolderButton",
            "ManagerView.featureNotesToggle",
            "FeatureNotes.notesRoot",
            "FeatureNotes.dismissButton",
            "ConfirmDialogView.titleText",
            "ConfirmDialogView.messageText",
            "ConfirmDialogView.cancelButton",
            "ConfirmDialogView.yesLabel",
            "ConfirmDialogView.noLabel",
            "ConfirmDialogView.cancelLabel",
            "LayoutBrowserView.manager",
            "LayoutBrowserView.notifications",
            "LayoutBrowserView.chooseRootButton",
            "LayoutBrowserView.scrollRect",
            "LayoutBrowserView.folderIcon",
            "LayoutBrowserView.fileIcon",
            "StartupNotice.session",
            "StartupNotice.notifications",
            "ColourPickerView.titleText",
            "ColourPickerView.original",
            "ColourPickerView.alphaSlider",
            "ColourPickerView.alphaRow",
            "ColourPickerView.hexField",
            "ColourPickerView.revertButton",
        };

        /// <summary>Components whose name alone is a problem, per DESIGN.md §7.</summary>
        private static readonly Dictionary<string, string> BannedComponents =
            new Dictionary<string, string>
            {
                { "MidiDeviceProbe",
                  "MidiDeviceProbe opens every MIDI port. Windows ports are exclusive, " +
                  "so it will collide with MidiPadInput. Disable or delete it." },
            };

        private enum Severity { Info, Warning, Error }

        private struct Issue
        {
            public Severity Severity;
            public string   Message;
            public Object   Context;
        }

        private static List<Issue> _issues;

        // ------------------------------------------------------------ menu items

        [MenuItem(MenuRoot + "Check Open Scenes", false, 100)]
        public static void CheckOpenScenes() => Run(CollectSceneObjects(), false, true);

        [MenuItem(MenuRoot + "Check Open Scenes (Include Unity Components)", false, 101)]
        public static void CheckOpenScenesVerbose() => Run(CollectSceneObjects(), true, true);

        [MenuItem(MenuRoot + "Check Selected Objects and Prefabs", false, 102)]
        public static void CheckSelection()
        {
            var roots = new List<GameObject>();

            foreach (var go in Selection.gameObjects)
                roots.AddRange(Descendants(go));

            if (roots.Count == 0)
            {
                Debug.LogWarning("[WiringCheck] Nothing selected. Select a GameObject " +
                                 "or a prefab asset first.");
                return;
            }

            Run(roots, false, false);
        }

        // ------------------------------------------------------------- the check

        private static void Run(List<GameObject> objects, bool includeUnityComponents,
                                bool runProjectChecks)
        {
            _issues = new List<Issue>();

            int componentsScanned = 0;

            foreach (var go in objects)
            {
                var components = go.GetComponents<Component>();

                for (int i = 0; i < components.Length; i++)
                {
                    var component = components[i];

                    // A null entry is a component whose script was deleted or failed to
                    // compile. Unity shows "Missing (Mono Script)" and every serialised
                    // value it held is already gone.
                    if (component == null)
                    {
                        Add(Severity.Error,
                            $"{Path(go)} has a MISSING SCRIPT in slot {i}. Its serialised " +
                            "references are lost and cannot be recovered by re-adding the script.",
                            go);
                        continue;
                    }

                    CheckBanned(component, go);

                    if (!includeUnityComponents && !IsProjectComponent(component)) continue;

                    componentsScanned++;
                    CheckSerializedFields(component, go);
                }
            }

            if (runProjectChecks) RunProjectChecks();

            Report(objects.Count, componentsScanned);
            _issues = null;
        }

        private static void CheckBanned(Component component, GameObject go)
        {
            string name = component.GetType().Name;
            if (!BannedComponents.TryGetValue(name, out string reason)) return;

            var behaviour = component as Behaviour;
            bool active = go.activeInHierarchy && (behaviour == null || behaviour.enabled);

            Add(active ? Severity.Error : Severity.Info,
                $"{Path(go)} has {name}{(active ? " and it is ENABLED" : " (disabled)")}. {reason}",
                component);
        }

        private static void CheckSerializedFields(Component component, GameObject go)
        {
            var so = new SerializedObject(component);
            var property = so.GetIterator();
            bool enterChildren = true;

            while (property.NextVisible(enterChildren))
            {
                enterChildren = true;

                if (property.propertyPath == "m_Script") continue;

                // UnityEvents are handled whole; descending into them would report
                // every empty listener slot as an orphaned reference.
                if (IsUnityEvent(property))
                {
                    CheckUnityEvent(property.Copy(), component, go);
                    enterChildren = false;
                    continue;
                }

                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (property.objectReferenceValue != null) continue;

                string field = NormalizePath(property.propertyPath);
                string key   = $"{component.GetType().Name}.{RootField(property.propertyPath)}";

                // A non-zero instance ID with a null value means the target was deleted
                // or lives in an unloaded scene: a genuinely broken link, not a blank slot.
                if (property.objectReferenceInstanceIDValue != 0)
                {
                    Add(Severity.Error,
                        $"{Path(go)} → {component.GetType().Name}.{field} points at a " +
                        $"MISSING object (instance {property.objectReferenceInstanceIDValue}).",
                        component);
                    continue;
                }

                bool optional = KnownOptional.Contains(key) || HasOptionalTooltip(component, property);

                Add(optional ? Severity.Info : Severity.Warning,
                    $"{Path(go)} → {component.GetType().Name}.{field} is empty " +
                    $"({TypeFromPPtr(property.type)}){(optional ? " — optional" : "")}.",
                    component);
            }
        }

        /// <summary>
        /// The trap from DESIGN.md §9: a UnityEvent pointing at a deleted method shows
        /// "&lt;Missing&gt;" in the Inspector and fails silently at runtime.
        /// </summary>
        private static void CheckUnityEvent(SerializedProperty eventProperty,
                                            Component component, GameObject go)
        {
            var calls = eventProperty.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (calls == null || !calls.isArray) return;

            string eventName = NormalizePath(eventProperty.propertyPath);

            for (int i = 0; i < calls.arraySize; i++)
            {
                var call = calls.GetArrayElementAtIndex(i);

                var targetProperty = call.FindPropertyRelative("m_Target");
                var methodProperty = call.FindPropertyRelative("m_MethodName");
                var stateProperty  = call.FindPropertyRelative("m_CallState");

                if (targetProperty == null || methodProperty == null) continue;
                if (stateProperty != null && stateProperty.enumValueIndex == 0) continue;  // Off

                var    target     = targetProperty.objectReferenceValue;
                string methodName = methodProperty.stringValue;

                if (target == null)
                {
                    Add(Severity.Error,
                        $"{Path(go)} → {component.GetType().Name}.{eventName} listener {i} " +
                        "has NO TARGET object.",
                        component);
                    continue;
                }

                if (string.IsNullOrEmpty(methodName))
                {
                    Add(Severity.Error,
                        $"{Path(go)} → {component.GetType().Name}.{eventName} listener {i} " +
                        $"targets \"{target.name}\" but NO METHOD is chosen.",
                        component);
                    continue;
                }

                if (MemberExists(target.GetType(), methodName)) continue;

                Add(Severity.Error,
                    $"{Path(go)} → {component.GetType().Name}.{eventName} listener {i} calls " +
                    $"\"{target.GetType().Name}.{methodName}\", which DOES NOT EXIST. " +
                    "The Inspector shows this as <Missing> and it fails silently.",
                    component);
            }
        }

        // -------------------------------------------------------- project checks

        private static void RunProjectChecks()
        {
            RequireSingle<Manager>();
            RequireSingle<PadModel>();
            RequireSingle<SelectionService>();
            RequireSingle<Rack>();
            RequireSingle<PadController>();
            RequireSingle<SidePanel>();
            RequireSingle<Metronome>();
            RequireSingle<MidiPadInput>();
            RequireSingle<ClipCache>();
            RequireSingle<NotificationService>();
            RequireSingle<TransportControls>();
            RequireSingle<NotificationLogView>();
            RequireSingle<SessionService>();
            RequireSingle<PadOverlay>();
            RequireSingle<PadLayoutService>();
            RequireSingle<LogFileWriter>();
            RequireSingle<ManagerView>();
            // The strip is the only route to a layout: without it the whole feature is
            // present in code and unreachable in the build, which is exactly how it
            // shipped unnoticed.
            RequireSingle<LayoutControls>();
            // The dialog hides its own window, so a component that hid itself could
            // never show it again — and every caller degrades silently if the static
            // Instance is null, which is the failure that is hardest to notice.
            RequireSingle<ConfirmDialogView>();
            // The browser legitimately lives on the System page and goes inactive with
            // it, so it is single-only — no always-active rule.
            RequireSingle<LayoutBrowserView>();
            // Two greetings is worse than none.
            RequireSingle<StartupNotice>();
            RequireSingle<ColourPickerView>();
            RequireSingle<MidiDeviceView>();
            
            RequireAlwaysActive<ColourPickerView>(
                "it can never be asked to open, so every palette square falls back to " +
                "the editor-only picker");
            RequireAlwaysActive<ConfirmDialogView>(
                "it can never be asked to open, so every confirm silently falls back");
            // Components that must never be switched off with a page. Metronome is the
            // one that bites: parented under a side-panel page, switching pages stops
            // the transport, and every quantised pad then plays instantly with no
            // message at all.
            RequireAlwaysActive<Metronome>(
                "the transport clock stops dead, so quantised pads silently play immediately");
            RequireAlwaysActive<ClipCache>("in-flight decodes and refcounts are stranded");
            RequireAlwaysActive<PadController>("nothing can trigger a pad");
            RequireAlwaysActive<PadModel>("the model stops raising changes");
            RequireAlwaysActive<NotificationService>("messages are lost, not queued");
            RequireAlwaysActive<PadOverlay>("a running animation freezes mid-frame");
            RequireAlwaysActive<SessionService>("the session is never written");
            RequireAlwaysActive<LogFileWriter>(
                "nothing reaches the log file, and in a build there is no console to fall " +
                "back on");
            RequireAlwaysActive<ManagerView>("its toggles stop following the model the moment the page it sits on is hidden");
            RequireAlwaysActive<LayoutControls>("the unsaved * marker stops updating and " +
                                                "the buttons vanish with the page");
            
            CheckPadGrid();
            CheckMetronomeViews();
        }

        private static void RequireSingle<T>() where T : Component
        {
            var found = Object.FindObjectsByType<T>(FindObjectsInactive.Include,
                                                    FindObjectsSortMode.None);

            if (found.Length == 1) return;

            if (found.Length == 0)
            {
                Add(Severity.Warning, $"No {typeof(T).Name} in the scene.", null);
                return;
            }

            var builder = new StringBuilder();
            foreach (var instance in found) builder.Append(Path(instance.gameObject)).Append("  ");

            // Two MidiPadInputs means two attempts to open an exclusive port.
            Add(Severity.Error,
                $"{found.Length} {typeof(T).Name} components in the scene: {builder}",
                found[0]);
        }

        /// <summary>
        /// Anything on this list must be live for the whole run. A scene root that
        /// starts inactive is a warning; being parented under other UI is an ERROR,
        /// because whatever hides that UI also kills this.
        /// </summary>
        private static void RequireAlwaysActive<T>(string consequence) where T : Component
        {
            foreach (var instance in Object.FindObjectsByType<T>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                var go = instance.gameObject;
                bool root = go.transform.parent == null;

                if (root && go.activeSelf) continue;

                if (!root)
                {
                    var chain = new StringBuilder();
                    for (var t = go.transform.parent; t != null; t = t.parent)
                    {
                        if (chain.Length > 0) chain.Append(" ← ");
                        chain.Append(t.name);
                        if (!t.gameObject.activeSelf) chain.Append(" [INACTIVE]");
                    }

                    Add(Severity.Error,
                        $"{Path(go)} carries {typeof(T).Name}, which must never be " +
                        $"deactivated — {consequence}.\n" +
                        $"  Ancestors: {chain}\n" +
                        "  Move it to a scene-root object of its own.",
                        instance);
                    continue;
                }

                Add(Severity.Warning,
                    $"{Path(go)} carries {typeof(T).Name} and is a scene root, but starts " +
                    $"INACTIVE — {consequence}.",
                    instance);
            }
        }
        private static void CheckPadGrid()
        {
            var views = Object.FindObjectsByType<PadView>(FindObjectsInactive.Include,
                                                          FindObjectsSortMode.None);

            if (views.Length == 0)
            {
                Add(Severity.Warning, "No PadViews found.", null);
                return;
            }

            var grids = Object.FindObjectsByType<PadGrid>(FindObjectsInactive.Include,
                                                          FindObjectsSortMode.None);

            var seen    = new Dictionary<int, PadView>(64);
            var orphans = new List<PadView>();

            foreach (var view in views)
            {
                // PadView derives its note in Awake, so in edit mode a 0 means
                // "will be parsed from the name at runtime". Parse it the same way.
                int fromName = NoteGrid.ParseTrailingInt(view.name);
                int note     = view.Note != 0 ? view.Note : fromName;

                // A serialised note that disagrees with the name is not fatal — the
                // FIELD wins at runtime — but after a bulk rename it is almost always
                // the rename that was meant, and nothing else in the project will ever
                // mention the discrepancy.
                if (view.Note != 0 && fromName >= 0 && fromName != view.Note)
                    Add(Severity.Warning,
                        $"{Path(view.gameObject)} is NAMED for note {fromName} but its " +
                        $"serialised note field is {view.Note}. The field wins, so this " +
                        $"pad behaves as {view.Note}. Set the field to 0 to follow the " +
                        "name, or rename the object to match.",
                        view);

                if (!NoteGrid.IsGridNote(note))
                {
                    Add(Severity.Error,
                        $"{Path(view.gameObject)} resolves to note {note}, which is not a " +
                        "grid note (11-88, no 0 or 9 in the units column).",
                        view);
                    continue;
                }

                if (seen.TryGetValue(note, out var existing))
                    Add(Severity.Error,
                        $"Duplicate PadView for note {note}: \"{existing.name}\" and \"{view.name}\".",
                        view);
                else
                    seen[note] = view;

                if (view.GetComponentInParent<PadGrid>(true) == null) orphans.Add(view);
            }
            
            // ONE report for the whole grid, with evidence instead of a guess: 64
            // identical errors told you what I assumed rather than what is true.
            if (orphans.Count > 0)
            {
                var sample = orphans[0];

                var chain = new StringBuilder();
                for (var t = sample.transform.parent; t != null; t = t.parent)
                {
                    if (chain.Length > 0) chain.Append(" ← ");
                    chain.Append(t.name);

                    var grid = t.GetComponent<PadGrid>();
                    chain.Append(grid != null ? " [HAS PadGrid]" : " [no PadGrid]");
                }

                if (chain.Length == 0) chain.Append("(no parent — this PadView is a scene root)");

                var where = new StringBuilder();
                if (grids.Length == 0)
                {
                    where.Append("There is NO PadGrid component anywhere in the open scenes.");
                }
                else
                {
                    where.Append($"PadGrid exists on: ");
                    for (int i = 0; i < grids.Length; i++)
                    {
                        if (i > 0) where.Append(", ");
                        where.Append(Path(grids[i].gameObject));
                    }
                    where.Append(". It must be on an ANCESTOR of the pads, not a sibling.");
                }

                Add(Severity.Error,
                    $"{orphans.Count} of {views.Length} PadViews have no PadGrid ancestor, so " +
                    $"they will never register and every colour update will be skipped.\n" +
                    $"  Example: {Path(sample.gameObject)}\n" +
                    $"  Ancestors: {chain}\n" +
                    $"  {where}",
                    sample);
            }

            if (grids.Length > 1)
                Add(Severity.Warning,
                    $"{grids.Length} PadGrid components in the scene. Pads register with the " +
                    "nearest ancestor, so the registry will be split.",
                    grids[0]);

            if (seen.Count != NoteGrid.Count)
                Add(Severity.Warning,
                    $"{seen.Count} of {NoteGrid.Count} distinct pad notes present.", null);
            else if (orphans.Count == 0)
                Add(Severity.Info,
                    $"All {NoteGrid.Count} pad notes present, unique, and registered.", null);
        }

        private static void CheckMetronomeViews()
        {
            var metronome = Object.FindAnyObjectByType<Metronome>();
            if (metronome == null) return;

            int beatsPerBar = ReadInt(metronome, "beatsPerBar", 4);

            var bulbs = Object.FindObjectsByType<FlashingBulbs>(FindObjectsInactive.Include,
                                                                FindObjectsSortMode.None);

            foreach (var view in bulbs)
            {
                var so    = new SerializedObject(view);
                var list  = so.FindProperty("bulbs");
                int count = list != null && list.isArray && list.arraySize > 0
                    ? list.arraySize
                    : view.transform.childCount;      // empty list = auto-collect children

                if (count == 0)
                {
                    Add(Severity.Error,
                        $"{Path(view.gameObject)} → FlashingBulbs has no bulbs and no children.",
                        view);
                    continue;
                }

                if (count != beatsPerBar)
                    Add(Severity.Warning,
                        $"{Path(view.gameObject)} → FlashingBulbs has {count} bulbs but " +
                        $"Metronome.beatsPerBar is {beatsPerBar}. The chase will run, " +
                        "it just won't line up with the bar.",
                        view);

                int downbeat = ReadInt(view, "downbeatIndex", 0);
                if (downbeat >= count)
                    Add(Severity.Error,
                        $"{Path(view.gameObject)} → FlashingBulbs.downbeatIndex is {downbeat}, " +
                        $"out of range for {count} bulbs.",
                        view);
            }

            var leds = Object.FindObjectsByType<MetronomeLedView>(FindObjectsInactive.Include,
                                                                  FindObjectsSortMode.None);

            foreach (var view in leds)
            {
                var so   = new SerializedObject(view);
                var list = so.FindProperty("beatLeds");
                int count = list != null && list.isArray ? list.arraySize : 0;

                if (count == 0)
                    Add(Severity.Error,
                        $"{Path(view.gameObject)} → MetronomeLedView has no beat LEDs.", view);
                else if (count != beatsPerBar)
                    Add(Severity.Warning,
                        $"{Path(view.gameObject)} → MetronomeLedView has {count} LED indices " +
                        $"but Metronome.beatsPerBar is {beatsPerBar}.",
                        view);
            }
        }

        // ------------------------------------------------------------- reporting

        private static void Add(Severity severity, string message, Object context) =>
            _issues.Add(new Issue { Severity = severity, Message = message, Context = context });

        private static void Report(int objectCount, int componentCount)
        {
            int errors = 0, warnings = 0, infos = 0;

            foreach (var issue in _issues)
            {
                string line = "[WiringCheck] " + issue.Message;

                switch (issue.Severity)
                {
                    case Severity.Error:   errors++;   Debug.LogError(line, issue.Context);   break;
                    case Severity.Warning: warnings++; Debug.LogWarning(line, issue.Context); break;
                    default:               infos++;    Debug.Log(line, issue.Context);        break;
                }
            }

            string summary = $"[WiringCheck] Scanned {componentCount} project components on " +
                             $"{objectCount} objects: {errors} error(s), {warnings} warning(s), " +
                             $"{infos} note(s). Click any line to select the object.";

            if (errors > 0) Debug.LogError(summary);
            else if (warnings > 0) Debug.LogWarning(summary);
            else Debug.Log(summary);
        }

        // ----------------------------------------------------------- collection

        private static List<GameObject> CollectSceneObjects()
        {
            var result = new List<GameObject>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                    result.AddRange(Descendants(root));
            }

            return result;
        }

        /// <summary>Includes inactive objects: a disabled object is still mis-wired.</summary>
        private static List<GameObject> Descendants(GameObject root)
        {
            var result = new List<GameObject>();

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                result.Add(transform.gameObject);

            return result;
        }

        // ---------------------------------------------------------------- utils

        private static bool IsProjectComponent(Component component)
        {
            string ns = component.GetType().Namespace ?? string.Empty;

            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
            if (ns.StartsWith("TMPro", StringComparison.Ordinal))       return false;
            if (ns.StartsWith("Melanchall", StringComparison.Ordinal))  return false;

            return true;   // our own namespace, or global (MidiPadInput lives there)
        }

        private static bool IsUnityEvent(SerializedProperty property) =>
            property.propertyType == SerializedPropertyType.Generic &&
            property.type != null &&
            property.type.StartsWith("UnityEvent", StringComparison.Ordinal);

        private static bool MemberExists(Type type, string memberName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

            foreach (var method in type.GetMethods(flags))
                if (method.Name == memberName) return true;

            // UnityEvent can also target a property setter, stored as the bare name.
            foreach (var property in type.GetProperties(flags))
                if (property.Name == memberName && property.CanWrite) return true;

            return false;
        }

        private static bool HasOptionalTooltip(Component component, SerializedProperty property)
        {
            var field = FindField(component.GetType(), RootField(property.propertyPath));
            if (field == null) return false;

            var tooltip = field.GetCustomAttribute<TooltipAttribute>();

            return tooltip != null && tooltip.tooltip != null &&
                   tooltip.tooltip.IndexOf("optional", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic;

            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, flags);
                if (field != null) return field;
            }

            return null;
        }

        /// <summary>"bulbs.Array.data[2]" → "bulbs".</summary>
        private static string RootField(string propertyPath)
        {
            int dot = propertyPath.IndexOf('.');
            return dot < 0 ? propertyPath : propertyPath.Substring(0, dot);
        }

        /// <summary>"bulbs.Array.data[2]" → "bulbs[2]", for readable messages.</summary>
        private static string NormalizePath(string propertyPath) =>
            propertyPath.Replace(".Array.data[", "[");

        /// <summary>"PPtr&lt;$Metronome&gt;" → "Metronome".</summary>
        private static string TypeFromPPtr(string type)
        {
            if (string.IsNullOrEmpty(type)) return "object";

            int start = type.IndexOf('$');
            if (start < 0) return type;

            int end = type.IndexOf('>', start);
            return end < 0 ? type.Substring(start + 1) : type.Substring(start + 1, end - start - 1);
        }

        private static int ReadInt(Component component, string fieldName, int fallback)
        {
            var property = new SerializedObject(component).FindProperty(fieldName);
            return property != null ? property.intValue : fallback;
        }

        private static string Path(GameObject go)
        {
            if (go == null) return "(null)";

            var builder = new StringBuilder(go.name);

            for (var parent = go.transform.parent; parent != null; parent = parent.parent)
                builder.Insert(0, parent.name + "/");

            return builder.ToString();
        }
    }
}
