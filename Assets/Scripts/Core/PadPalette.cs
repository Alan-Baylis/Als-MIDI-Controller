using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Thin front door to Manager.Palette, kept so PadView (screen) and PadLedView
    /// (hardware) still share one call and cannot drift apart. The rules themselves
    /// now live on PadPaletteAsset, where the colours are.
    /// </summary>
    public static class PadPalette
    {
        public static Color32 ColourFor(PadState pad, Manager manager, bool selected)
        {
            var palette = manager != null ? manager.Palette : null;

            // No palette assigned is a wiring fault, not a colour: black makes it
            // obvious rather than quietly showing plausible defaults forever.
            if (palette == null) return new Color32(0, 0, 0, 255);

            return palette.ColourFor(pad, selected);
        }

        /// <summary>
        /// Moves a colour toward white by <paramref name="amount"/>. Lerping rather than
        /// scaling keeps the hue and cannot overflow, so a pulsing pad still reads as
        /// ITS colour rather than as a white flash.
        /// </summary>
        public static Color32 Brighten(Color32 colour, float amount)
        {
            float t = Mathf.Clamp01(amount);

            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(colour.r, 255f, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(colour.g, 255f, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(colour.b, 255f, t)),
                colour.a);
        }
        
        /// <summary>
        /// Folds alpha and a brightness scalar into RGB. The Launchpad has no alpha
        /// channel, so a 50%-alpha pad must become a 50%-RGB pad or the "half
        /// strength" test-tone state looks identical to a fully loaded one.
        /// </summary>
        public static Color32 Flatten(Color32 colour, float brightness = 1f)
        {
            float scale = Mathf.Clamp01(colour.a / 255f) * Mathf.Clamp01(brightness);

            return new Color32(
                (byte)Mathf.RoundToInt(colour.r * scale),
                (byte)Mathf.RoundToInt(colour.g * scale),
                (byte)Mathf.RoundToInt(colour.b * scale),
                255);
        }

        // ------------------------------------------------------------- label colour

        /// <summary>Perceived brightness, 0-1. Alpha is ignored — a pad is never see-through.</summary>
        public static float Luminance(Color32 colour) =>
            (0.299f * colour.r + 0.587f * colour.g + 0.114f * colour.b) / 255f;

        /// <summary>Straight channel inversion, opaque.</summary>
        public static Color32 Invert(Color32 colour) =>
            new Color32((byte)(255 - colour.r), (byte)(255 - colour.g), (byte)(255 - colour.b), 255);

        /// <summary>
        /// A caption colour for text drawn ON a pad. The inverse reads as "the opposite
        /// of this pad", which is what was asked for — but the inverse of a mid grey is
        /// another mid grey, and that is unreadable. So the inverse is used only when it
        /// actually contrasts, and plain black or white is the fallback.
        /// </summary>
        public static Color32 LabelColourFor(Color32 background, bool preferInverted = true)
        {
            float backgroundLuma = Luminance(background);

            if (preferInverted)
            {
                var inverted = Invert(background);
                if (Mathf.Abs(Luminance(inverted) - backgroundLuma) >= 0.35f) return inverted;
            }

            return backgroundLuma > 0.5f
                ? new Color32(0, 0, 0, 255)
                : new Color32(255, 255, 255, 255);
        }
    }
}