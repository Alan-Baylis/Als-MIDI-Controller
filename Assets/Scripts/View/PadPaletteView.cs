using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// The palette legend on the Manager page: one square per entry, painted from the
    /// PadColour asset it is bound to, clickable to change it.
    ///
    /// A square binds the COLOUR ASSET, not an enum value and not an object name, so
    /// the two failure modes of the old version are gone: you cannot pick the wrong
    /// slot (there is no slot), and renaming a GameObject changes nothing. An unbound
    /// square is visibly empty in the Inspector and says so in the console.
    ///
    /// Buttons are wired in Awake. Leave their OnClick lists EMPTY or they fire twice.
    /// </summary>
    [DisallowMultipleComponent]
    public class PadPaletteView : MonoBehaviour
    {
        [Serializable]
        public class Square
        {
            [Tooltip("The colour asset this square shows and edits.")]
            public PadColour colour;

            [Tooltip("Graphic painted with the colour. Defaults to a Graphic on the button.")]
            public Graphic target;

            [Tooltip("Click target that opens the picker. Defaults to a Button on the graphic.")]
            public Button button;

            [Tooltip("Caption. Only written when Write Labels is on.")]
            public TMP_Text label;
        }

        [Header("References")]
        [SerializeField] private NotificationService notifications;

        [Tooltip("Optional. Only used to cross-check which palette roles have no square.")]
        [SerializeField] private Manager manager;

        [Header("Squares")]
        [Tooltip("Optional entries. One per legend square: the colour asset plus the " +
                 "graphic that shows it.")]
        [SerializeField] private List<Square> squares = new List<Square>();

        [Header("Captions")]
        [Tooltip("Overwrite each caption with the colour's display name. Off by default " +
                 "so hand-written captions survive.")]
        [SerializeField] private bool writeLabels = false;

        [SerializeField] private bool appendHexToLabel = false;

        [Header("Picker")]
        [Tooltip("Expose the alpha slider. Only the \"has clip\" colour's alpha is " +
                 "meaningful (test tones draw at half strength), so this is off by default.")]
        [SerializeField] private bool showAlpha = false;

        [Tooltip("Force Transition = None on bound buttons. A Selectable tint MULTIPLIES " +
                 "the colour you are trying to read, which makes a correct legend look wrong.")]
        [SerializeField] private bool clearButtonTint = true;

        private readonly List<PadColour> _listening = new List<PadColour>(8);

        // ---------------------------------------------------------------- setup

        private void Awake()
        {
            if (manager       == null) manager       = FindAnyObjectByType<Manager>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            Resolve();

            foreach (var square in squares)
            {
                if (square?.button == null) continue;

                if (clearButtonTint) square.button.transition = Selectable.Transition.None;

                var captured = square;                  // closure capture, per square
                square.button.onClick.RemoveAllListeners();
                square.button.onClick.AddListener(() => Pick(captured));
            }

            ReportWiring();
        }

        /// <summary>Fills the optional slots from the ones that were filled. Safe any time.</summary>
        private void Resolve()
        {
            foreach (var square in squares)
            {
                if (square == null) continue;

                if (square.target == null && square.button != null)
                    square.target = square.button.GetComponent<Graphic>();

                if (square.button == null && square.target != null)
                    square.button = square.target.GetComponent<Button>();

                if (square.label == null && square.target != null &&
                    square.target.transform.parent != null)
                    square.label = square.target.transform.parent.GetComponentInChildren<TMP_Text>(true);
            }
        }

        private void OnEnable()
        {
            Listen();
            Refresh();
        }

        private void OnDisable() => Unlisten();

        private void Listen()
        {
            Unlisten();

            foreach (var square in squares)
            {
                if (square?.colour == null) continue;
                if (_listening.Contains(square.colour)) continue;   // shared colour, once

                square.colour.Changed += OnColourChanged;
                _listening.Add(square.colour);
            }
        }

        private void Unlisten()
        {
            foreach (var colour in _listening)
                if (colour != null) colour.Changed -= OnColourChanged;

            _listening.Clear();
        }

        private void OnColourChanged(PadColour _) => Refresh();

        // ------------------------------------------------------------- painting

        /// <summary>Public so a settings-file load can force the legend to catch up.</summary>
        [ContextMenu("Refresh Legend")]
        public void Refresh()
        {
            foreach (var square in squares)
            {
                if (square?.colour == null || square.target == null) continue;

                square.target.color = square.colour.Opaque;

                if (!writeLabels || square.label == null) continue;

                square.label.text = appendHexToLabel
                    ? $"{square.colour.DisplayName}  #{ColorUtility.ToHtmlStringRGB(square.colour.Value)}"
                    : square.colour.DisplayName;
            }
        }

#if UNITY_EDITOR
        /// <summary>So the legend is correct in the Scene view, not only in Play.</summary>
        private void OnValidate()
        {
            Resolve();
            Refresh();
        }
#endif

        // -------------------------------------------------------------- picking

        private void Pick(Square square)
        {
            if (square.colour == null)
            {
                Notify("That square has no colour asset bound. Drag a PadColour into it.",
                       NoticeLevel.Warning);
                return;
            }

            // Runtime picker first, editor picker as the fallback. The runtime one is the
            // only one that exists in a build, and having the two behave differently in
            // the editor is how a build-only fault survives to a release.
            var colour  = square.colour;
            var opening = colour.Value;

            if (ColourPickerView.Show(colour.DisplayName, opening, showAlpha,
                    picked => Apply(colour, picked),
                    picked => Apply(colour, picked),
                    () => Apply(colour, opening)))
                return;

#if UNITY_EDITOR
            // The picker calls back continuously while the user drags, so the grid and
            // the hardware LEDs preview the colour live.
            if (TryShowEditorPicker(opening, showAlpha, picked => Apply(colour, picked)))
                return;

            Notify($"Could not open a colour picker on Unity {Application.unityVersion}. " +
                   $"Edit \"{colour.name}\" in the Project window instead.",
                NoticeLevel.Warning);
#else
            Notify("No ColourPickerView in the scene, so there is no way to change a " +
                   "palette colour. Add one to an always-active object.",
                   NoticeLevel.Error);
#endif
        }

        private void Apply(PadColour colour, Color picked)
        {
            Color32 value = picked;
            if (!showAlpha) value.a = 255;

            colour.Value = value;   // raises Changed → this legend, the grid, the LEDs
        }

        // ------------------------------------------------------------- reporting

        [ContextMenu("Log Palette Wiring")]
        public void ReportWiring()
        {
            var report = new StringBuilder("[PadPaletteView] legend wiring\n");

            int bound = 0;

            for (int i = 0; i < squares.Count; i++)
            {
                var square = squares[i];

                if (square == null)
                {
                    report.Append($"  [{i}] NULL ENTRY\n");
                    continue;
                }

                if (square.colour == null)
                {
                    report.Append($"  [{i}] no colour asset bound")
                          .Append(square.target != null ? $" (graphic \"{square.target.name}\")" : "")
                          .Append('\n');
                    continue;
                }

                if (square.target == null)
                {
                    report.Append($"  [{i}] \"{square.colour.DisplayName}\" has no graphic " +
                                  "to paint\n");
                    continue;
                }

                bound++;

                report.Append($"  [{i}] {square.colour.DisplayName}  " +
                              $"#{ColorUtility.ToHtmlStringRGB(square.colour.Value)}  → " +
                              $"\"{square.target.name}\"" +
                              (square.button != null
                                  ? $"  transition={square.button.transition}" : "  (not clickable)") +
                              (square.target.gameObject.activeInHierarchy ? "" : "  [INACTIVE]") +
                              '\n');
            }

            // Not an error: a role with no square just isn't in the legend yet.
            var palette = manager != null ? manager.Palette : null;

            if (palette != null)
            {
                foreach (var role in palette.Roles)
                {
                    bool shown = squares.Exists(s => s != null && s.colour == role);
                    if (!shown)
                        report.Append($"  role \"{role.DisplayName}\" has no square in this legend\n");
                }
            }

            report.Append($"  {bound} square(s) painting.");
            Debug.Log(report.ToString(), this);
        }

        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        // --------------------------------------------------------- editor picker

#if UNITY_EDITOR
        /// <summary>
        /// UnityEditor.ColorPicker.Show is internal and its signature has shifted
        /// between versions, so it is located by shape (first parameter Action&lt;Color&gt;)
        /// rather than by an exact overload that a future Unity could rename.
        /// </summary>
        private static bool TryShowEditorPicker(Color current, bool withAlpha,
                                                Action<Color> onChanged)
        {
            var type = Type.GetType("UnityEditor.ColorPicker, UnityEditor")
                    ?? Type.GetType("UnityEditor.ColorPicker, UnityEditor.CoreModule");

            if (type == null) return false;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic;

            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != "Show") continue;

                var parameters = method.GetParameters();
                if (parameters.Length < 2) continue;
                if (parameters[0].ParameterType != typeof(Action<Color>)) continue;
                if (parameters[1].ParameterType != typeof(Color)) continue;

                var args = new object[parameters.Length];
                args[0] = onChanged;
                args[1] = current;

                for (int i = 2; i < parameters.Length; i++)
                {
                    var p = parameters[i];

                    if (p.ParameterType == typeof(bool))
                        args[i] = i == 2 ? withAlpha : false;      // (showAlpha, hdr)
                    else if (p.HasDefaultValue)
                        args[i] = p.DefaultValue;
                    else
                        args[i] = p.ParameterType.IsValueType
                            ? Activator.CreateInstance(p.ParameterType) : null;
                }

                try { method.Invoke(null, args); return true; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PadPaletteView] Colour picker refused the call: " +
                                     $"{e.InnerException?.Message ?? e.Message}");
                    return false;
                }
            }

            return false;
        }
#endif
    }
}