using System;
using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The seven colour ROLES and the rules that choose between them, in one asset.
    /// Each role is a reference to a PadColour, so two roles can legitimately share
    /// one colour and renaming an asset changes nothing.
    ///
    /// Priority order (also in DESIGN.md §6):
    ///   1. selected        → editing
    ///   2. no PadState     → empty
    ///   3. debug test tone → hasClip at half strength
    ///   4. otherwise loadState
    /// </summary>
    [CreateAssetMenu(fileName = "PadPalette", menuName = "Launchpad/Pad Palette")]
    public class PadPaletteAsset : ScriptableObject
    {
        [Header("Roles — drag a PadColour asset into each")]
        public PadColour empty;

        [Tooltip("Base \"this pad has content\" colour. Drawn at half strength for the " +
                 "debug test tones, so they read as placeholder rather than real.")]
        public PadColour hasClip;

        public PadColour loaded;
        public PadColour editing;
        public PadColour loading;
        public PadColour missing;
        public PadColour flash;

        /// <summary>Raised when ANY member colour changes, or a role is re-assigned.</summary>
        public event Action Changed;

        private readonly List<PadColour> _listening = new List<PadColour>(8);

        // Used only when a role is unassigned, so a half-wired palette still shows
        // something legible instead of black-on-black.
        private static readonly Color32 FallbackEmpty   = new Color32( 60,  60,  60, 255);
        private static readonly Color32 FallbackHasClip = new Color32(140,  20,  30, 255);
        private static readonly Color32 FallbackLoaded  = new Color32( 30, 170,  90, 255);
        private static readonly Color32 FallbackEditing = new Color32(255, 105, 180, 255);
        private static readonly Color32 FallbackLoading = new Color32(200, 160,  40, 255);
        private static readonly Color32 FallbackMissing = new Color32( 90,  40, 140, 255);
        private static readonly Color32 FallbackFlash   = new Color32(255, 255, 255, 255);

        public IEnumerable<PadColour> Roles
        {
            get
            {
                if (empty   != null) yield return empty;
                if (hasClip != null) yield return hasClip;
                if (loaded  != null) yield return loaded;
                if (editing != null) yield return editing;
                if (loading != null) yield return loading;
                if (missing != null) yield return missing;
                if (flash   != null) yield return flash;
            }
        }

        // ------------------------------------------------------------- the rules

        public Color32 ColourFor(PadState pad, bool selected)
        {
            if (selected) return Of(editing, FallbackEditing);
            if (pad == null) return Of(empty, FallbackEmpty);

            // clip without clipPath == generated test tone, not real user content.
            if (pad.clip != null && !pad.HasClip)
            {
                var on = Of(hasClip, FallbackHasClip);
                return new Color32(on.r, on.g, on.b, 128);
            }

            switch (pad.loadState)
            {
                // A pad only overrides the palette if someone deliberately gave it a
                // colour; otherwise re-theming `loaded` repaints the whole grid.
                case PadLoadState.Ready:   return pad.useCustomColour ? pad.colour
                                                                      : Of(loaded, FallbackLoaded);
                case PadLoadState.Loading: return Of(loading, FallbackLoading);
                case PadLoadState.Missing:
                case PadLoadState.Failed:  return Of(missing, FallbackMissing);
                default:                   return Of(empty,   FallbackEmpty);
            }
        }

        public Color32 Flash => Of(flash, FallbackFlash);

        private static Color32 Of(PadColour role, Color32 fallback) =>
            role != null ? role.Value : fallback;

        // ---------------------------------------------------------- subscription

        private void OnEnable()  => Resubscribe();
        private void OnDisable() => Unsubscribe();

#if UNITY_EDITOR
        private void OnValidate() => Resubscribe();   // a role slot was re-dragged
#endif

        private void Resubscribe()
        {
            Unsubscribe();

            foreach (var role in Roles)
            {
                if (_listening.Contains(role)) continue;   // a shared colour, once only
                role.Changed += OnColourChanged;
                _listening.Add(role);
            }
        }

        private void Unsubscribe()
        {
            foreach (var role in _listening)
                if (role != null) role.Changed -= OnColourChanged;

            _listening.Clear();
        }

        private void OnColourChanged(PadColour _) => Changed?.Invoke();

        /// <summary>One-line summary for the console.</summary>
        public string DescribeState()
        {
            int bound = 0;
            foreach (var _ in Roles) bound++;
            return $"[PadPalette] {bound} of 7 roles assigned.";
        }
    }
}
