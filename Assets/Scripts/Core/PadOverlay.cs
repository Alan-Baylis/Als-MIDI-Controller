using System;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// A colour layer that sits ON TOP of the palette, for the screen and the hardware
    /// at once. While it is Active, PadView and PadLedView paint from here and ignore
    /// load state, selection, flash and pulse.
    ///
    /// It holds no rules and no timing: whatever drives it (the screensaver now, the
    /// lightshow player later) owns those. This exists so an animation never has to
    /// write into PadModel — nothing an animation does should be able to alter a
    /// session, and nothing about a session should have to be restored afterwards.
    /// </summary>
    [DisallowMultipleComponent]
    public class PadOverlay : MonoBehaviour
    {
        /// <summary>One pad changed.</summary>
        public event Action<int> NoteChanged;

        /// <summary>The whole layer changed, or was switched on or off. Repaint everything.</summary>
        public event Action Changed;

        private readonly Color32[] _colours = new Color32[NoteGrid.ArraySize];
        private readonly bool[]    _set     = new bool[NoteGrid.ArraySize];

        public bool Active { get; private set; }

        public bool TryGet(int note, out Color32 colour)
        {
            colour = default;

            if (!Active || !NoteGrid.IsGridNote(note) || !_set[note]) return false;

            colour = _colours[note];
            return true;
        }

        public void Set(int note, Color32 colour)
        {
            if (!NoteGrid.IsGridNote(note)) return;

            _colours[note] = colour;
            _set[note]     = true;

            bool wasActive = Active;
            Active = true;

            if (wasActive) NoteChanged?.Invoke(note);
            else           Changed?.Invoke();
        }

        /// <summary>
        /// Whole-grid write in one go — ONE Changed event, so PadLedView coalesces the
        /// frame into a single batched SysEx instead of 64 of them.
        /// </summary>
        public void Apply(Color32[] byNote)
        {
            if (byNote == null || byNote.Length < NoteGrid.ArraySize) return;

            foreach (int note in NoteGrid.All)
            {
                _colours[note] = byNote[note];
                _set[note]     = true;
            }

            Active = true;
            Changed?.Invoke();
        }

        public void Clear(int note)
        {
            if (!NoteGrid.IsGridNote(note) || !_set[note]) return;

            _set[note] = false;
            NoteChanged?.Invoke(note);
        }

        /// <summary>Hands every pad back to the palette.</summary>
        public void ClearAll()
        {
            if (!Active) return;

            Active = false;
            Array.Clear(_set, 0, _set.Length);

            Changed?.Invoke();
        }
    }
}
