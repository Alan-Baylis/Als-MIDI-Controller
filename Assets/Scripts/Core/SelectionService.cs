using System;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Which pad, if any, is currently being edited. One source of truth — this
    /// replaces Manager.padNumber and the colour-scrubbing loops in ContextMenu.
    /// </summary>
    [DisallowMultipleComponent]
    public class SelectionService : MonoBehaviour
    {
        /// <summary>(previous, current). Either may be null.</summary>
        public event Action<int?, int?> SelectionChanged;

        public int? Selected { get; private set; }

        public bool IsSelected(int note) => Selected.HasValue && Selected.Value == note;

        public void Select(int note)
        {
            if (!NoteGrid.IsGridNote(note)) { ClearSelection(); return; }
            if (Selected.HasValue && Selected.Value == note) return;

            var previous = Selected;
            Selected = note;
            SelectionChanged?.Invoke(previous, Selected);
        }

        public void ClearSelection()
        {
            if (!Selected.HasValue) return;

            var previous = Selected;
            Selected = null;
            SelectionChanged?.Invoke(previous, null);
        }
    }
}
