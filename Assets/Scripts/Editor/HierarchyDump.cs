using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LaunchpadStudio.EditorTools
{
    /// <summary>
    /// Writes the hierarchy out as readable text: the tree, the components on each
    /// object, the serialised values that govern UI layout, and — the reason this was
    /// rewritten — WHICH OBJECT IS IN EACH INSPECTOR SLOT.
    ///
    /// Scene YAML technically contains all of this, but parent/child links are fileID
    /// cross-references and every asset is a GUID, so it is unreadable without the
    /// project open. This is the same information in the form a human (or an assistant
    /// you are pasting it to) can act on.
    ///
    /// It also dumps the SOURCE, because the two questions are always asked together:
    /// "what does this code do" and "what is it plugged into". The combined output is
    /// every .cs file under Assets/Scripts, a break line, then the hierarchy — which is
    /// exactly the file that used to be assembled by hand.
    ///
    /// "Scripts + Scene + Wiring" is the one to use when handing the project to
    /// someone else: it emits all four parts with a header explaining the layout, so
    /// the file explains itself without you writing a covering note.
    ///
    /// Three hierarchy modes:
    ///   Full        — tree + UI layout + every field of every project component.
    ///   Wiring Only — project components and their fields, with branches that contain
    ///                 none of them pruned. This is the one to paste when the question
    ///                 is "what is plugged into what".
    ///   No Collapsing — identical sibling runs (the 64-pad grid) are normally
    ///                 summarised after the second; this lists them all.
    /// </summary>
    public static class HierarchyDump
    {
        private const string MenuRoot = "Tools/Hierarchy Dump/";

        /// <summary>Identical siblings beyond this many are summarised, not listed.</summary>
        private const int CollapseAfter = 2;

        /// <summary>Array entries printed before the rest are summarised.</summary>
        private const int MaxArrayElements = 12;

        /// <summary>Fields printed inside a nested struct/class summary.</summary>
        private const int MaxNestedFields = 8;

        private const int MaxStringLength = 60;

        // ---------------------------------------------------------------- source

        /// <summary>Everything below this is dumped, recursively.</summary>
        private const string ScriptsFolder = "Assets/Scripts";

        /// <summary>Separates the source half from the hierarchy half.</summary>
        private const string BreakLine = "***** Break *****";

        /// <summary>
        /// Folder NAMES skipped anywhere in the path. Third-party code is not ours to
        /// paste, and it is the bulk of the character count when it is present.
        /// </summary>
        private static readonly string[] ExcludedScriptFolders =
            { "Plugins", "ThirdParty", "Melanchall", "DryWetMidi", "Generated" };

        /// <summary>Past this the clipboard becomes unreliable on Windows; say so.</summary>
        private const int ClipboardWarningChars = 900_000;

        private static StringBuilder _out;
        private static bool _collapse;
        private static bool _wiringOnly;
        private static int  _objectCount;
        private static int  _componentCount;
        private static int  _scriptCount;

        // ------------------------------------------------------------ menu items

        [MenuItem(MenuRoot + "Selection \u2192 Clipboard", false, 200)]
        public static void SelectionToClipboard() =>
            ToClipboard(BuildSelection(true, false), SceneSummary());

        [MenuItem(MenuRoot + "Selection \u2192 Clipboard (No Collapsing)", false, 201)]
        public static void SelectionToClipboardFull() =>
            ToClipboard(BuildSelection(false, false), SceneSummary());

        [MenuItem(MenuRoot + "Selection \u2192 Clipboard (Wiring Only)", false, 202)]
        public static void SelectionWiringToClipboard() =>
            ToClipboard(BuildSelection(true, true), SceneSummary());

        [MenuItem(MenuRoot + "Open Scenes \u2192 Clipboard", false, 220)]
        public static void ScenesToClipboard() =>
            ToClipboard(BuildScenes(true, false), SceneSummary());

        [MenuItem(MenuRoot + "Open Scenes \u2192 Clipboard (Wiring Only)", false, 221)]
        public static void ScenesWiringToClipboard() =>
            ToClipboard(BuildScenes(true, true), SceneSummary());

        [MenuItem(MenuRoot + "Open Scenes \u2192 File\u2026", false, 240)]
        public static void ScenesToFile() =>
            ToFile(BuildScenes(true, false), "_Hierarchy", SceneSummary());

        [MenuItem(MenuRoot + "Open Scenes \u2192 File\u2026 (Wiring Only)", false, 241)]
        public static void ScenesWiringToFile() =>
            ToFile(BuildScenes(true, true), "_Wiring", SceneSummary());

        // ---- source ----

        [MenuItem(MenuRoot + "Scripts \u2192 Clipboard", false, 260)]
        public static void ScriptsToClipboard() =>
            ToClipboard(BuildScripts(), ScriptSummary());

        [MenuItem(MenuRoot + "Scripts \u2192 File\u2026", false, 261)]
        public static void ScriptsToFile() =>
            ToFile(BuildScripts(), "_Source", ScriptSummary());

        [MenuItem(MenuRoot + "Scripts + Open Scenes \u2192 File\u2026", false, 280)]
        public static void ScriptsAndScenesToFile() =>
            ToFile(BuildScriptsAndScenes(true, false), "_Source_and_Hierarchy",
                   CombinedSummary());

        [MenuItem(MenuRoot + "Scripts + Open Scenes \u2192 Clipboard", false, 281)]
        public static void ScriptsAndScenesToClipboard() =>
            ToClipboard(BuildScriptsAndScenes(true, false), CombinedSummary());

        [MenuItem(MenuRoot + "Scripts + Scene + Wiring \u2192 File\u2026", false, 300)]
        public static void EverythingToFile() =>
            ToFile(BuildEverything(), "_Source_Scene_and_Wiring", EverythingSummary());

        [MenuItem(MenuRoot + "Scripts + Scene + Wiring \u2192 Clipboard", false, 301)]
        public static void EverythingToClipboard() =>
            ToClipboard(BuildEverything(), EverythingSummary());
        
        [MenuItem(MenuRoot + "Selection \u2192 Clipboard", true)]
        [MenuItem(MenuRoot + "Selection \u2192 Clipboard (No Collapsing)", true)]
        [MenuItem(MenuRoot + "Selection \u2192 Clipboard (Wiring Only)", true)]
        private static bool ValidateSelection() => Selection.gameObjects.Length > 0;

        // ---------------------------------------------------------- source build

        /// <summary>
        /// Every .cs under <see cref="ScriptsFolder"/>, sorted by path so two dumps of
        /// an unchanged project are byte-identical and can be diffed.
        /// </summary>
        private static string BuildScripts()
        {
            _scriptCount = 0;

            if (!Directory.Exists(ScriptsFolder))
            {
                Debug.LogWarning($"[HierarchyDump] No \"{ScriptsFolder}\" folder in this " +
                                 "project, so there is no source to dump.");
                return null;
            }

            var files = new List<string>(
                Directory.GetFiles(ScriptsFolder, "*.cs", SearchOption.AllDirectories));

            files.Sort(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder(512 * 1024);

            foreach (var file in files)
            {
                string path = file.Replace('/', '\\');
                if (IsExcluded(path)) continue;

                builder.Append("===== ").Append(path).Append(" =====").AppendLine();

                try
                {
                    // TrimEnd so the blank line below is the only separator, however
                    // the file itself ends.
                    builder.AppendLine(File.ReadAllText(file).TrimEnd());
                }
                catch (Exception e)
                {
                    builder.Append("(could not read this file: ").Append(e.Message)
                           .Append(')').AppendLine();
                }

                builder.AppendLine();
                _scriptCount++;
            }

            if (_scriptCount == 0)
            {
                Debug.LogWarning($"[HierarchyDump] No .cs files found under " +
                                 $"\"{ScriptsFolder}\".");
                return null;
            }

            return builder.ToString();
        }

        private static bool IsExcluded(string path)
        {
            foreach (var folder in ExcludedScriptFolders)
                if (path.IndexOf("\\" + folder + "\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            return false;
        }

        /// <summary>Source, the break line, then the hierarchy — one paste, whole picture.</summary>
        private static string BuildScriptsAndScenes(bool collapse, bool wiringOnly)
        {
            string scripts = BuildScripts();
            string scenes  = BuildScenes(collapse, wiringOnly);

            if (scripts == null) return scenes;
            if (scenes  == null) return scripts;

            return scripts + Environment.NewLine +
                   BreakLine + Environment.NewLine + Environment.NewLine +
                   scenes;
        }

        /// <summary>
        /// The whole picture in one operation: this note, the source, the full
        /// hierarchy, then the same hierarchy pruned to wiring. Three questions get
        /// asked together every single time, so answering them takes one menu click.
        /// </summary>
        private static string BuildEverything()
        {
            string scripts     = BuildScripts();
            int    scriptCount = _scriptCount;

            string scenes  = BuildScenes(true, false);
            int    objects = _objectCount;
            int    comps   = _componentCount;

            string wiring = BuildScenes(true, true);

            // BuildScenes ran twice and the wiring pass left ITS (much smaller) counts
            // in the statics. The header and the log line describe the whole file, so
            // put the full-hierarchy numbers back before either is built.
            _scriptCount    = scriptCount;
            _objectCount    = objects;
            _componentCount = comps;

            if (scripts == null && scenes == null && wiring == null) return null;

            var builder = new StringBuilder((scripts?.Length ?? 0) +
                                            (scenes?.Length  ?? 0) +
                                            (wiring?.Length  ?? 0) + 4096);

            builder.Append(CombinedHeader(scriptCount, objects)).AppendLine();

            Section(builder, scripts);
            Section(builder, scenes);
            Section(builder, wiring);

            return builder.ToString();
        }

        /// <summary>
        /// Break line, blank line, then the part. A part that failed to build is skipped
        /// rather than separated by a break with nothing after it.
        /// </summary>
        private static void Section(StringBuilder builder, string part)
        {
            if (part == null) return;

            builder.Append(BreakLine).AppendLine().AppendLine();
            builder.AppendLine(part.TrimEnd());
            builder.AppendLine();
        }

        /// <summary>
        /// The note at the top of a combined dump. Without it the reader — often an
        /// assistant being handed 700,000 characters — has to infer where source stops
        /// and scene begins, and has no way at all to know the third section is the
        /// same scene pruned rather than a different one.
        /// </summary>
        private static string CombinedHeader(int scripts, int objects)
        {
            var b = new StringBuilder(2048);

            b.Append("LAUNCHPAD STUDIO \u2014 COMBINED DUMP  \u2014  Unity ")
             .Append(Application.unityVersion).Append("  \u2014  dumped ")
             .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).AppendLine();
            b.AppendLine();

            b.AppendLine("This is one dump of the whole project in FOUR parts: this note, the");
            b.AppendLine("source, the full scene hierarchy, and the wiring-only hierarchy.");
            b.AppendLine();
            b.Append("Parts are separated by a line whose ENTIRE content is:  ")
             .Append(BreakLine).AppendLine();
            b.AppendLine();
            b.AppendLine("Split on such a line and nothing else. That same string also appears");
            b.AppendLine("inside HierarchyDump.cs as a string literal, and that occurrence is");
            b.AppendLine("not a separator.");
            b.AppendLine();

            b.Append("  1. SOURCE  \u2014 ").Append(scripts)
             .AppendLine(" .cs file(s) under Assets/Scripts, sorted by path so");
            b.AppendLine("               two dumps of an unchanged project diff cleanly. Each");
            b.AppendLine("               file opens with \"===== <relative path> =====\".");
            b.AppendLine("               Third-party folders are excluded.");
            b.AppendLine();

            b.Append("  2. SCENE   \u2014 the full hierarchy of every open scene (")
             .Append(objects).AppendLine(" objects): the");
            b.AppendLine("               tree, UI layout values, and every serialised field of");
            b.AppendLine("               every project component, including which object sits in");
            b.AppendLine("               each Inspector slot.");
            b.AppendLine();

            b.AppendLine("  3. WIRING  \u2014 the SAME hierarchy with every branch containing no");
            b.AppendLine("               project component pruned. This is the short one to read");
            b.AppendLine("               when the question is \"what is plugged into what\".");
            b.AppendLine();

            b.AppendLine("Identical sibling runs are summarised after the second in both");
            b.AppendLine("hierarchy sections, so a line reading \"+62 more sibling(s) with");
            b.AppendLine("identical components\" means 62 real objects differing only in the");
            b.AppendLine("field named on the line above it (for the pad grid, Note).");

            return b.ToString();
        }
                
        // ---------------------------------------------------------------- build

        private static string BuildSelection(bool collapse, bool wiringOnly)
        {
            var roots = Selection.gameObjects;

            if (roots.Length == 0)
            {
                Debug.LogWarning("[HierarchyDump] Nothing selected.");
                return null;
            }

            Begin(collapse, wiringOnly);

            foreach (var go in roots)
            {
                // Full path first, so the fragment can be located in the whole scene.
                _out.Append("=== ").Append(PathOf(go)).Append(" ===").AppendLine();
                Walk(go.transform, 0);
                _out.AppendLine();
            }

            return End();
        }

        private static string BuildScenes(bool collapse, bool wiringOnly)
        {
            Begin(collapse, wiringOnly);

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                _out.Append("=== SCENE: ").Append(scene.name).Append(" ===").AppendLine();

                foreach (var root in scene.GetRootGameObjects())
                    Walk(root.transform, 0);

                _out.AppendLine();
            }

            return End();
        }

        private static void Begin(bool collapse, bool wiringOnly)
        {
            _out = new StringBuilder(32 * 1024);
            _collapse = collapse;
            _wiringOnly = wiringOnly;
            _objectCount = 0;
            _componentCount = 0;

            _out.Append("Unity ").Append(Application.unityVersion)
                .Append("  \u2014  dumped ")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                .Append(wiringOnly ? "  \u2014  WIRING ONLY (UI-only branches pruned)" : "")
                .AppendLine().AppendLine();
        }

        private static string End()
        {
            _out.Append("(").Append(_objectCount).Append(" object(s), ")
                .Append(_componentCount).Append(" project component(s)");

            if (_collapse) _out.Append(", identical sibling runs collapsed");
            _out.Append(")").AppendLine();

            string text = _out.ToString();
            _out = null;
            return text;
        }

        // ------------------------------------------------------------- summaries

        private static string SceneSummary() =>
            $"{_objectCount} object(s), {_componentCount} project component(s)";

        private static string ScriptSummary() => $"{_scriptCount} script(s)";

        private static string CombinedSummary() =>
            $"{_scriptCount} script(s) + {_objectCount} object(s)";
        
        private static string EverythingSummary() =>
            $"{_scriptCount} script(s) + {_objectCount} object(s) + wiring";

        // ---------------------------------------------------------------- output

        private static void ToClipboard(string text, string what)
        {
            if (text == null) return;

            EditorGUIUtility.systemCopyBuffer = text;

            if (text.Length > ClipboardWarningChars)
                Debug.LogWarning($"[HierarchyDump] {text.Length:N0} characters is more than " +
                                 "the clipboard reliably carries on Windows. Use the " +
                                 "\u2192 File\u2026 variant if the paste comes out truncated.");

            Debug.Log($"[HierarchyDump] {what}, {text.Length:N0} chars copied to the clipboard.");
        }

        private static void ToFile(string text, string suffix, string what)
        {
            if (text == null) return;

            string suggested = SceneManager.GetActiveScene().name + suffix + ".txt";

            string path = EditorUtility.SaveFilePanel(
                "Save dump", Application.dataPath + "/..", suggested, "txt");

            if (string.IsNullOrEmpty(path)) return;

            File.WriteAllText(path, text);
            Debug.Log($"[HierarchyDump] {what}, {text.Length:N0} chars written to {path}");
        }

        // ----------------------------------------------------------- the walker

        private static void Walk(Transform t, int depth)
        {
            // Wiring mode prints the tree only where there is something to wire. The
            // 64-pad grid's TMP children are 64 objects of pure noise otherwise.
            if (_wiringOnly && !BranchHasProjectComponent(t)) return;

            _objectCount++;

            string indent = new string(' ', depth * 2);
            var go = t.gameObject;

            _out.Append(indent).Append(go.name);
            if (!go.activeSelf) _out.Append("  [INACTIVE]");
            _out.AppendLine();

            string detailIndent = indent + "  | ";

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    _out.Append(detailIndent).Append("<MISSING SCRIPT>").AppendLine();
                    continue;
                }

                if (IsProjectComponent(component))
                {
                    _componentCount++;
                    DescribeProject(component, detailIndent);
                    continue;
                }

                if (_wiringOnly)
                {
                    // A stock Button with an Inspector-wired listener is a wiring fact
                    // even in wiring-only mode — it is exactly the double-fire trap.
                    DescribeUnityEventsIfAny(component, detailIndent);
                    continue;
                }

                Describe(component, detailIndent);
            }

            if (t.childCount == 0) return;

            // Group by component signature so repeated siblings can be summarised.
            var bySignature = new Dictionary<string, List<Transform>>();

            foreach (Transform child in t)
            {
                string signature = SignatureOf(child.gameObject);

                if (!bySignature.TryGetValue(signature, out var list))
                    bySignature[signature] = list = new List<Transform>();

                list.Add(child);
            }

            foreach (Transform child in t)
            {
                var group = bySignature[SignatureOf(child.gameObject)];

                if (!_collapse || group.Count <= CollapseAfter)
                {
                    Walk(child, depth + 1);
                    continue;
                }

                int index = group.IndexOf(child);

                if (index < CollapseAfter)
                {
                    Walk(child, depth + 1);
                }
                else if (index == CollapseAfter)
                {
                    int remaining = group.Count - CollapseAfter;

                    _out.Append(new string(' ', (depth + 1) * 2))
                        .Append("\u2026 +").Append(remaining)
                        .Append(" more sibling(s) with identical components: \"")
                        .Append(group[CollapseAfter].name).Append("\" \u2026 \"")
                        .Append(group[group.Count - 1].name)
                        .Append("\" (not expanded)")
                        .AppendLine();
                }
            }
        }

        private static bool BranchHasProjectComponent(Transform t)
        {
            foreach (var component in t.GetComponentsInChildren<Component>(true))
            {
                if (component == null) return true;              // a missing script counts
                if (IsProjectComponent(component)) return true;
                if (HasPersistentListeners(component)) return true;
            }

            return false;
        }

        /// <summary>Component type names in Inspector order. Two objects sharing one are interchangeable for our purposes.</summary>
        private static string SignatureOf(GameObject go)
        {
            var builder = new StringBuilder();

            foreach (var component in go.GetComponents<Component>())
                builder.Append(component == null ? "MISSING" : component.GetType().Name)
                       .Append('|');

            builder.Append('#').Append(go.transform.childCount);
            return builder.ToString();
        }

        // ------------------------------------------------ project component dump

        /// <summary>
        /// Everything the Inspector shows for one of our own components, with object
        /// references resolved to a scene path or an asset name. This is the part scene
        /// YAML cannot give you without the project open.
        /// </summary>
        private static void DescribeProject(Component component, string indent)
        {
            var behaviour = component as Behaviour;

            Line(indent, component.GetType().Name +
                         (behaviour != null && !behaviour.enabled ? "   [COMPONENT DISABLED]" : ""));

            string fieldIndent = indent + "    ";

            SerializedObject so;

            try { so = new SerializedObject(component); }
            catch (Exception e)
            {
                Line(fieldIndent, "(could not read serialised data: " + e.Message + ")");
                return;
            }

            var property = so.GetIterator();
            bool enterChildren = true;
            bool any = false;

            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;                       // top-level fields only
                if (property.propertyPath == "m_Script") continue;

                any = true;
                DescribeField(property, fieldIndent, component);
            }

            if (!any) Line(fieldIndent, "(no serialised fields)");
        }

        private static void DescribeField(SerializedProperty property, string indent,
                                          Component owner)
        {
            if (IsUnityEvent(property))
            {
                DescribeEvent(property, indent);
                return;
            }

            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                Line(indent, $"{property.displayName}: {property.arraySize} " +
                             (property.arraySize == 1 ? "entry" : "entries"));

                int shown = Mathf.Min(property.arraySize, MaxArrayElements);

                for (int i = 0; i < shown; i++)
                {
                    var element = property.GetArrayElementAtIndex(i);
                    Line(indent + "  ",
                         $"[{i}] {ValueOf(element, owner) ?? NestedSummary(element, owner)}");
                }

                if (property.arraySize > shown)
                    Line(indent + "  ", $"\u2026 +{property.arraySize - shown} more");

                return;
            }

            string value = ValueOf(property, owner);

            Line(indent, $"{property.displayName}: " +
                         (value ?? NestedSummary(property, owner)));
        }

        /// <summary>Null means "not a simple value" — the caller falls back to a nested summary.</summary>
        private static string ValueOf(SerializedProperty p, Component owner)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:   return p.intValue.ToString();
                case SerializedPropertyType.Boolean:   return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:     return N(p.floatValue);
                case SerializedPropertyType.String:    return "\"" + Short(p.stringValue) + "\"";
                case SerializedPropertyType.Color:     return C(p.colorValue);
                case SerializedPropertyType.Vector2:   return V(p.vector2Value);
                case SerializedPropertyType.Vector3:   return $"({N(p.vector3Value.x)},{N(p.vector3Value.y)},{N(p.vector3Value.z)})";
                case SerializedPropertyType.Vector4:   return p.vector4Value.ToString();
                case SerializedPropertyType.Rect:      return p.rectValue.ToString();
                case SerializedPropertyType.Bounds:    return p.boundsValue.ToString();
                case SerializedPropertyType.Quaternion:return p.quaternionValue.eulerAngles.ToString();
                case SerializedPropertyType.LayerMask: return "mask " + p.intValue;
                case SerializedPropertyType.ArraySize: return p.intValue.ToString();
                case SerializedPropertyType.Character: return "'" + (char)p.intValue + "'";
                case SerializedPropertyType.AnimationCurve: return "(curve)";
                case SerializedPropertyType.ObjectReference: return RefText(p, owner);

                case SerializedPropertyType.Enum:
                    return p.enumValueIndex >= 0 && p.enumDisplayNames != null &&
                           p.enumValueIndex < p.enumDisplayNames.Length
                        ? p.enumDisplayNames[p.enumValueIndex]
                        : p.intValue.ToString();

                default: return null;
            }
        }

        /// <summary>
        /// The whole point of the rewrite: an object slot reports WHICH object, by full
        /// scene path, and says so loudly when the slot is empty or the link is broken.
        /// </summary>
        private static string RefText(SerializedProperty p, Component owner)
        {
            var target = p.objectReferenceValue;

            if (target == null)
            {
                // A non-zero instance ID with a null value means the target was deleted
                // or lives in an unloaded scene: broken, not blank.
                return p.objectReferenceInstanceIDValue != 0
                    ? $"\u2190 MISSING LINK (instance {p.objectReferenceInstanceIDValue})"
                    : $"NONE ({TypeFromPPtr(p.type)})";
            }

            if (target is Component component)
            {
                bool self = owner != null && component.gameObject == owner.gameObject;

                return $"{component.GetType().Name} on \"{PathOf(component.gameObject)}\"" +
                       (self ? " (self)" : "") +
                       (component.gameObject.activeInHierarchy ? "" : " [INACTIVE]");
            }

            if (target is GameObject go)
                return $"GameObject \"{PathOf(go)}\"" +
                       (go.activeInHierarchy ? "" : " [INACTIVE]");

            return $"{target.GetType().Name} asset \"{target.name}\"";
        }

        /// <summary>One line for a nested [Serializable] struct or class, e.g. PadPaletteView.Square.</summary>
        private static string NestedSummary(SerializedProperty property, Component owner)
        {
            if (!property.hasVisibleChildren) return property.type;

            var builder = new StringBuilder("{ ");
            var iterator = property.Copy();
            var end = property.GetEndProperty();

            bool enterChildren = true;
            int count = 0;

            while (iterator.NextVisible(enterChildren) &&
                   !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;

                if (count > 0) builder.Append(", ");

                builder.Append(iterator.displayName).Append('=')
                       .Append(ValueOf(iterator, owner) ?? iterator.type);

                if (++count >= MaxNestedFields) { builder.Append(", \u2026"); break; }
            }

            return builder.Append(" }").ToString();
        }

        // ------------------------------------------------------------- UnityEvents

        private static bool IsUnityEvent(SerializedProperty property) =>
            property.propertyType == SerializedPropertyType.Generic &&
            property.type != null &&
            property.type.StartsWith("UnityEvent", StringComparison.Ordinal);

        private static void DescribeEvent(SerializedProperty eventProperty, string indent)
        {
            var calls = eventProperty.FindPropertyRelative("m_PersistentCalls.m_Calls");
            int count = calls != null && calls.isArray ? calls.arraySize : 0;

            if (count == 0)
            {
                Line(indent, $"{eventProperty.displayName}: (no persistent listeners)");
                return;
            }

            Line(indent, $"{eventProperty.displayName}: {count} persistent listener(s)");

            for (int i = 0; i < count; i++)
            {
                var call = calls.GetArrayElementAtIndex(i);

                var target = call.FindPropertyRelative("m_Target")?.objectReferenceValue;
                string method = call.FindPropertyRelative("m_MethodName")?.stringValue;
                var state = call.FindPropertyRelative("m_CallState");

                string stateText = state != null && state.enumDisplayNames != null &&
                                   state.enumValueIndex >= 0 &&
                                   state.enumValueIndex < state.enumDisplayNames.Length
                    ? state.enumDisplayNames[state.enumValueIndex]
                    : "?";

                Line(indent + "  ",
                     $"[{i}] {(target != null ? target.name : "NO TARGET")}." +
                     $"{(string.IsNullOrEmpty(method) ? "NO METHOD" : method)}  ({stateText})");
            }
        }

        /// <summary>
        /// Stock UI components whose event lists should normally be EMPTY — every view
        /// in this project wires its buttons in Awake, so anything here fires twice.
        /// </summary>
        private static readonly string[] EventPaths =
            { "m_OnClick", "m_OnValueChanged", "onValueChanged", "m_OnEndEdit", "m_OnSubmit" };

        private static bool HasPersistentListeners(Component component)
        {
            if (!(component is Selectable || component is TMPro.TMP_Dropdown ||
                  component is TMPro.TMP_InputField)) return false;

            var so = new SerializedObject(component);

            foreach (var path in EventPaths)
            {
                var calls = so.FindProperty(path + ".m_PersistentCalls.m_Calls");
                if (calls != null && calls.isArray && calls.arraySize > 0) return true;
            }

            return false;
        }

        private static void DescribeUnityEventsIfAny(Component component, string indent)
        {
            if (!HasPersistentListeners(component)) return;

            Line(indent, component.GetType().Name + "   \u2190 has Inspector-wired listeners");

            var so = new SerializedObject(component);

            foreach (var path in EventPaths)
            {
                var property = so.FindProperty(path);
                if (property == null) continue;

                var calls = property.FindPropertyRelative("m_PersistentCalls.m_Calls");
                if (calls == null || !calls.isArray || calls.arraySize == 0) continue;

                DescribeEvent(property, indent + "    ");
            }
        }

        // ------------------------------------------------------------ describers

        private static void Describe(Component component, string indent)
        {
            switch (component)
            {
                case RectTransform rt:                  DescribeRect(rt, indent);      return;
                case Transform _:                       return;   // covered by the name line
                case LayoutElement le:                  DescribeLayoutElement(le, indent); return;
                case HorizontalOrVerticalLayoutGroup g: DescribeHvGroup(g, indent);    return;
                case GridLayoutGroup grid:              DescribeGrid(grid, indent);    return;
                case ContentSizeFitter fitter:          DescribeFitter(fitter, indent); return;
                case ScrollRect scroll:                 DescribeScroll(scroll, indent); return;
                case Canvas canvas:                     DescribeCanvas(canvas, indent); return;
                case CanvasScaler scaler:               DescribeScaler(scaler, indent); return;
                case Image image:                       DescribeImage(image, indent);  return;
                case Slider slider:
                    DescribeSlider(slider, indent);
                    DescribeSelectable(slider, indent);
                    DescribeUnityEventsIfAny(slider, indent);
                    return;
                case Toggle toggle:
                    Line(indent, $"Toggle: isOn={toggle.isOn} " +
                                 $"listeners={toggle.onValueChanged.GetPersistentEventCount()}");
                    DescribeSelectable(toggle, indent);
                    DescribeUnityEventsIfAny(toggle, indent);
                    return;
                case Button button:
                    Line(indent, $"Button: interactable={button.interactable} " +
                                 $"listeners={button.onClick.GetPersistentEventCount()}");
                    DescribeSelectable(button, indent);
                    DescribeUnityEventsIfAny(button, indent);
                    return;
                case RectMask2D _:
                    Line(indent, "RectMask2D");
                    return;
                case Mask mask:
                    Line(indent, $"Mask: showMaskGraphic={mask.showMaskGraphic}");
                    return;
            }

            DescribeTmp(component, indent);
        }

        /// <summary>
        /// Selectable's ColorTint writes the CanvasRenderer, which MULTIPLIES Image.color.
        /// A non-white normalColor therefore repaints a graphic that code is painting
        /// deliberately, and nothing in the Inspector's Image row hints at it — which is the
        /// whole reason this is printed.
        /// </summary>
        private static void DescribeSelectable(Selectable selectable, string indent)
        {
            if (selectable.transition == Selectable.Transition.None)
            {
                Line(indent, "        transition=None");
                return;
            }

            if (selectable.transition != Selectable.Transition.ColorTint)
            {
                Line(indent, $"        transition={selectable.transition} " +
                             $"target={Name(selectable.targetGraphic)}");
                return;
            }

            var c = selectable.colors;

            bool white = Mathf.Approximately(c.normalColor.r, 1f) &&
                         Mathf.Approximately(c.normalColor.g, 1f) &&
                         Mathf.Approximately(c.normalColor.b, 1f);

            Line(indent, $"        transition=ColorTint target={Name(selectable.targetGraphic)} " +
                         $"normal={C(c.normalColor)} highlighted={C(c.highlightedColor)} " +
                         $"pressed={C(c.pressedColor)} x{N(c.colorMultiplier)}" +
                         (white ? "" : "   \u2190 NON-WHITE: multiplies Image.color"));
        }

        /// <summary>
        /// TextMeshPro in its own method: if TMP is ever removed from the project this
        /// is the single thing to delete.
        /// </summary>
        private static void DescribeTmp(Component component, string indent)
        {
            if (component is TMPro.TMP_Text text)
            {
                string preview = text.text ?? string.Empty;
                if (preview.Length > 40) preview = preview.Substring(0, 40) + "\u2026";
                preview = preview.Replace("\n", "\\n");

                // Overflow, margins and auto-size are exactly the fields that make text
                // DISAPPEAR rather than look wrong, and none of them were in this dump
                // when the Clip Title went blank. Ellipsis truncates anything that does
                // not fit the rect, so "overflow=Ellipsis" next to a rect one pixel too
                // short is the whole fault, visible at a glance.
                var m = text.margin;                     // x=left y=top z=right w=bottom
                string margins = m == Vector4.zero
                    ? "0"
                    : $"L{N(m.x)} T{N(m.y)} R{N(m.z)} B{N(m.w)}";

                Line(indent, $"{component.GetType().Name}: \"{preview}\" " +
                             $"size={N(text.fontSize)} wrap={text.enableWordWrapping} " +
                             $"overflow={text.overflowMode} margins={margins}" +
                             (text.enableAutoSizing
                                 ? $" autoSize={N(text.fontSizeMin)}-{N(text.fontSizeMax)}"
                                 : string.Empty));
                return;
            }

            Line(indent, component.GetType().Name);
        }

        private static void DescribeRect(RectTransform rt, string indent)
        {
            Line(indent, $"RectTransform: anchors {V(rt.anchorMin)}\u2013{V(rt.anchorMax)} " +
                         $"[{AnchorWord(rt)}]  pivot {V(rt.pivot)}");

            Line(indent, $"               offMin {V(rt.offsetMin)} offMax {V(rt.offsetMax)} " +
                         $"sizeDelta {V(rt.sizeDelta)} \u2192 rect {N(rt.rect.width)}\u00d7{N(rt.rect.height)}");

            // Z, scale and rotation are invisible in an Overlay canvas at run time but
            // decide what the SCENE view shows — which is exactly the class of fault
            // that looks like "the text is floating behind the panel".
            var p = rt.localPosition;
            var s = rt.localScale;
            var e = rt.localEulerAngles;

            bool oddZ     = Mathf.Abs(p.z) > 0.001f;
            bool oddScale = Mathf.Abs(s.x - 1f) > 0.001f || Mathf.Abs(s.y - 1f) > 0.001f ||
                            Mathf.Abs(s.z - 1f) > 0.001f;
            bool oddRot   = e.sqrMagnitude > 0.001f;

            if (!oddZ && !oddScale && !oddRot) return;

            Line(indent, $"               z={N(p.z)} scale=({N(s.x)},{N(s.y)},{N(s.z)}) " +
                         $"rot=({N(e.x)},{N(e.y)},{N(e.z)})" +
                         (oddZ ? "   \u2190 NON-ZERO Z: floats in the Scene view" : ""));
        }

        /// <summary>"stretch/stretch", "top-left/fixed" etc. The shape, not the numbers.</summary>
        private static string AnchorWord(RectTransform rt)
        {
            bool stretchX = !Mathf.Approximately(rt.anchorMin.x, rt.anchorMax.x);
            bool stretchY = !Mathf.Approximately(rt.anchorMin.y, rt.anchorMax.y);

            if (stretchX && stretchY) return "stretch both";
            if (stretchX)             return "stretch X, fixed height";
            if (stretchY)             return "stretch Y, fixed width";
            return "fixed size";
        }

        private static void DescribeLayoutElement(LayoutElement le, string indent) =>
            Line(indent, $"LayoutElement: ignore={le.ignoreLayout} " +
                         $"min({N(le.minWidth)},{N(le.minHeight)}) " +
                         $"pref({N(le.preferredWidth)},{N(le.preferredHeight)}) " +
                         $"flex({N(le.flexibleWidth)},{N(le.flexibleHeight)})  [-1 = unset]");

        private static void DescribeHvGroup(HorizontalOrVerticalLayoutGroup g, string indent)
        {
            Line(indent, $"{g.GetType().Name}: spacing={N(g.spacing)} " +
                         $"padding(L{g.padding.left} R{g.padding.right} " +
                         $"T{g.padding.top} B{g.padding.bottom}) align={g.childAlignment}");

            Line(indent, $"               controlSize(W={g.childControlWidth},H={g.childControlHeight}) " +
                         $"forceExpand(W={g.childForceExpandWidth},H={g.childForceExpandHeight})");
        }

        private static void DescribeGrid(GridLayoutGroup grid, string indent) =>
            Line(indent, $"GridLayoutGroup: cell {V(grid.cellSize)} spacing {V(grid.spacing)} " +
                         $"start={grid.startCorner}/{grid.startAxis} " +
                         $"constraint={grid.constraint}({grid.constraintCount})");

        private static void DescribeFitter(ContentSizeFitter fitter, string indent) =>
            Line(indent, $"ContentSizeFitter: horizontal={fitter.horizontalFit} " +
                         $"vertical={fitter.verticalFit}");

        private static void DescribeScroll(ScrollRect scroll, string indent) =>
            Line(indent, $"ScrollRect: h={scroll.horizontal} v={scroll.vertical} " +
                         $"movement={scroll.movementType} " +
                         $"viewport={Name(scroll.viewport)} content={Name(scroll.content)}");

        private static void DescribeCanvas(Canvas canvas, string indent) =>
            Line(indent, $"Canvas: mode={canvas.renderMode} sortingOrder={canvas.sortingOrder} " +
                         $"override={canvas.overrideSorting}");

        private static void DescribeScaler(CanvasScaler scaler, string indent) =>
            Line(indent, $"CanvasScaler: mode={scaler.uiScaleMode} " +
                         $"ref={V(scaler.referenceResolution)} " +
                         $"match={N(scaler.matchWidthOrHeight)}");

        private static void DescribeImage(Image image, string indent) =>
            Line(indent, $"Image: sprite={(image.sprite != null ? image.sprite.name : "none")} " +
                         $"type={image.type} colour={C(image.color)} " +
                         $"raycast={image.raycastTarget}");

        private static void DescribeSlider(Slider slider, string indent) =>
            Line(indent, $"Slider: value={N(slider.value)} " +
                         $"range({N(slider.minValue)}\u2013{N(slider.maxValue)}) " +
                         $"listeners={slider.onValueChanged.GetPersistentEventCount()}");

        // ----------------------------------------------------------------- utils

        /// <summary>
        /// Our own scripts, plus anything in the global namespace (MidiPadInput lives
        /// there). Unity's own components are described by the curated methods above,
        /// because a full field dump of a Canvas is 60 lines of nothing.
        /// </summary>
        private static bool IsProjectComponent(Component component)
        {
            string ns = component.GetType().Namespace ?? string.Empty;

            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
            if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) return false;
            if (ns.StartsWith("TMPro", StringComparison.Ordinal))       return false;
            if (ns.StartsWith("Melanchall", StringComparison.Ordinal))  return false;

            return true;
        }

        private static void Line(string indent, string text) =>
            _out.Append(indent).Append(text).AppendLine();

        private static string Name(UnityEngine.Object o) => o != null ? o.name : "NONE";

        private static string N(float v) =>
            v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string V(Vector2 v) => $"({N(v.x)},{N(v.y)})";

        private static string C(Color c) => $"#{ColorUtility.ToHtmlStringRGBA(c)}";

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            s = s.Replace("\n", "\\n").Replace("\r", "");
            return s.Length > MaxStringLength ? s.Substring(0, MaxStringLength) + "\u2026" : s;
        }

        /// <summary>"PPtr&lt;$Metronome&gt;" \u2192 "Metronome".</summary>
        private static string TypeFromPPtr(string type)
        {
            if (string.IsNullOrEmpty(type)) return "object";

            int start = type.IndexOf('$');
            if (start < 0) return type;

            int end = type.IndexOf('>', start);
            return end < 0 ? type.Substring(start + 1) : type.Substring(start + 1, end - start - 1);
        }

        private static string PathOf(GameObject go)
        {
            if (go == null) return "(null)";

            var builder = new StringBuilder(go.name);

            for (var parent = go.transform.parent; parent != null; parent = parent.parent)
                builder.Insert(0, parent.name + "/");

            return builder.ToString();
        }
    }
}