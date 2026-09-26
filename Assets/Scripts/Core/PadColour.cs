using System;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// One colour in the pad palette, as an ASSET. Views bind the asset itself, so no
    /// enum ordinal, no GameObject name, and no dropdown sits between a square and the
    /// colour it claims to show. Rename anything you like.
    ///
    /// Edits raise Changed, which is how the legend, the on-screen grid and the
    /// hardware LEDs all repaint from one write.
    /// </summary>
    [CreateAssetMenu(fileName = "PadColour", menuName = "Launchpad/Pad Colour")]
    public class PadColour : ScriptableObject
    {
        [Tooltip("Caption for the legend. Empty = use the asset's own name.")]
        [SerializeField] private string displayName;

        [SerializeField] private Color32 colour = new Color32(255, 255, 255, 255);

        [Tooltip("What this colour MEANS. Documentation only — nothing reads it yet.")]
        [SerializeField, TextArea(1, 3)] private string meaning;

        /// <summary>Raised whenever the value changes, from the picker or the Inspector.</summary>
        public event Action<PadColour> Changed;

        public string DisplayName =>
            string.IsNullOrWhiteSpace(displayName) ? name : displayName;

        public string Meaning => meaning;

        public Color32 Value
        {
            get => colour;
            set
            {
                if (Same(colour, value)) return;
                colour = value;
#if UNITY_EDITOR
                _last = colour;
                UnityEditor.EditorUtility.SetDirty(this);
#endif
                Changed?.Invoke(this);
            }
        }

        /// <summary>
        /// Alpha forced to 255. A legend square must read as the colour it stands for;
        /// a half-alpha square looks like a DIFFERENT colour rather than like a rule.
        /// </summary>
        public Color32 Opaque => new Color32(colour.r, colour.g, colour.b, 255);

        public static bool Same(Color32 a, Color32 b) =>
            a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

#if UNITY_EDITOR
        private Color32 _last;

        private void OnEnable() => _last = colour;

        /// <summary>Inspector edits behave exactly like picker edits, including in Play.</summary>
        private void OnValidate()
        {
            if (Same(_last, colour)) return;
            _last = colour;
            Changed?.Invoke(this);
        }

        /// <summary>Editor-only construction helper, used by the bootstrap tool.</summary>
        public void EditorInitialise(string caption, Color32 value, string what = null)
        {
            displayName = caption;
            colour      = value;
            meaning     = what;
            _last       = value;
        }
#endif
    }
}