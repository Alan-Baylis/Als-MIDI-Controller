using System.Collections.Generic;

namespace LaunchpadStudio
{
    /// <summary>
    /// Programmer-mode grid addressing for the Launchpad Pro MK3.
    /// Rows 1-8 bottom-to-top, columns 1-8 left-to-right: note = row*10 + col.
    /// </summary>
    public static class NoteGrid
    {
        public const int Min = 11;
        public const int Max = 88;
        public const int Count = 64;
        public const int ArraySize = 89;   // sparse note-indexed arrays

        public static bool IsGridNote(int note) =>
            note >= Min && note <= Max && (note % 10) >= 1 && (note % 10) <= 8;

        public static int Row(int note) => note / 10;
        public static int Col(int note) => note % 10;

        public static int FromRowCol(int row, int col) => row * 10 + col;

        /// <summary>11 → 0 … 88 → 63. Returns -1 for non-grid notes.</summary>
        public static int ToIndex(int note) =>
            IsGridNote(note) ? (Row(note) - 1) * 8 + (Col(note) - 1) : -1;

        /// <summary>0 → 11 … 63 → 88.</summary>
        public static int FromIndex(int index) =>
            (uint)index < Count ? FromRowCol(index / 8 + 1, index % 8 + 1) : -1;

        public static IEnumerable<int> All
        {
            get
            {
                for (int row = 1; row <= 8; row++)
                for (int col = 1; col <= 8; col++)
                    yield return FromRowCol(row, col);
            }
        }

        /// <summary>
        /// Single canonical trailing-integer parser.
        /// "Button 47", "Voice_47", "Pad47" → 47. Returns -1 if there are no trailing digits.
        /// </summary>
        public static int ParseTrailingInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;

            int end = s.Length;
            int i = end;
            while (i > 0 && char.IsDigit(s[i - 1])) i--;

            return i < end && int.TryParse(s.Substring(i, end - i), out int v) ? v : -1;
        }
    }
}