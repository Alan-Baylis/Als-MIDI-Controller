using System;
using UnityEngine;

namespace LaunchpadStudio
{
    public enum PadLoadState
    {
        Empty,      // no clip assigned
        Loading,    // decode in flight
        Ready,      // clip assigned and playable
        Missing,    // session referenced a file that is no longer on disk
        Failed      // file exists but could not be decoded
    }

    /// <summary>
    /// Everything the application knows about one pad. Plain serialisable data:
    /// no Unity object references that would break JSON round-tripping.
    /// </summary>
    [Serializable]
    public class PadState
    {
        [SerializeField] private int note;

        /// <summary>Library-relative where possible, absolute otherwise. Null/empty = no clip.</summary>
        public string clipPath;

        /// <summary>Tooltip text. Defaults to the clip filename without extension.</summary>
        public string label;

        public PadCategory category = PadCategory.None;

        [Tooltip("Per-pad colour. Only used when useCustomColour is true.")]
        public Color32 colour = new Color32(140, 20, 30, 255);

        public bool loop;

        [Range(0f, 1f)] public float volume = 1f;

        [Range(0.25f, 4f)] public float pitch = 1f;

        // --------------------------------------------------------------- timing

        [Tooltip("Wait for the metronome instead of playing the instant the pad is hit.")]
        public bool quantizeToBeat;

        [Tooltip("Which beat of the bar this pad starts on, 1-based. 1 is the downbeat.\n" +
                 "0 means the NEXT beat, whichever it turns out to be — for freestyling, " +
                 "where waiting up to a whole bar for beat 1 is the wrong kind of help.")]
        [Min(0)] public int startBeat = 1;

        // ---- runtime only: never serialised into a session file ----
        [NonSerialized] public PadLoadState loadState = PadLoadState.Empty;
        [NonSerialized] public AudioClip    clip;

        public PadState(int note) => this.note = note;

        public int  Note    => note;
        public bool HasClip => !string.IsNullOrEmpty(clipPath);

        /// <summary>
        /// False = follow Manager.padLoaded, so re-theming the palette repaints every
        /// pad that was never individually coloured. Set through SetColour.
        /// </summary>
        public bool useCustomColour;
        
        public void SetColour(Color32 value)
        {
            colour = value;
            useCustomColour = true;
        }

        /// <summary>Hand the pad back to the palette.</summary>
        public void ClearColour() => useCustomColour = false;

        public string DisplayLabel =>
            !string.IsNullOrWhiteSpace(label) ? label
            : HasClip ? System.IO.Path.GetFileNameWithoutExtension(clipPath)
            : string.Empty;

        /// <summary>Deep copy of the content fields, retargeted to another note.</summary>
        public PadState CloneTo(int targetNote) => new PadState(targetNote)
        {
            clipPath        = clipPath,
            label           = label,
            category        = category,
            colour          = colour,
            useCustomColour = useCustomColour,
            loop            = loop,
            volume          = volume,
            pitch           = pitch,
            quantizeToBeat  = quantizeToBeat,
            startBeat       = startBeat,
            loadState       = loadState,
            clip            = clip
        };

        public void ResetToEmpty()
        {
            clipPath        = null;
            label           = null;
            category        = PadCategory.None;
            loop            = false;
            volume          = 1f;
            pitch           = 1f;
            quantizeToBeat  = false;
            startBeat       = 1;
            useCustomColour = false;
            loadState       = PadLoadState.Empty;
            clip            = null;
        }
    }
}