using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// One lamp, in the project's own terms: a LOGICAL index (grid 11-88, top row
    /// 91-98, right column x9 — the Pro MK3 programmer scheme, which is also NoteGrid's)
    /// and a plain RGB colour. What that becomes on the wire is the device profile's
    /// business, and nothing that lights a lamp needs to know.
    /// </summary>
    public readonly struct LedWrite
    {
        public readonly int     Index;
        public readonly Color32 Colour;

        public LedWrite(int index, Color32 colour)
        {
            Index  = index;
            Colour = colour;
        }

        public static LedWrite Off(int index) => new LedWrite(index, new Color32(0, 0, 0, 255));
    }
}
