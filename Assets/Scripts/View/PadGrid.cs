using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Registry of the 64 PadViews. Put this on the "Pads" GameObject.
    /// Replaces every FindObjectsByType&lt;Button&gt;() + name-compare loop.
    /// </summary>
    [DisallowMultipleComponent]
    public class PadGrid : MonoBehaviour
    {
        private readonly Dictionary<int, PadView> _views = new Dictionary<int, PadView>(64);

        public PadView Get(int note) => _views.TryGetValue(note, out var view) ? view : null;

        public IEnumerable<PadView> All => _views.Values;

        public int RegisteredCount => _views.Count;

        [SerializeField] private Manager manager;

        private void OnEnable()
        {
            if (manager == null) manager = FindAnyObjectByType<Manager>();
            if (manager != null) manager.PaletteChanged += RepaintAll;
        }

        private void OnDisable()
        {
            if (manager != null) manager.PaletteChanged -= RepaintAll;
        }
        
        public void Register(PadView view)
        {
            if (view == null || !NoteGrid.IsGridNote(view.Note)) return;

            if (_views.TryGetValue(view.Note, out var existing) && existing != view)
                Debug.LogWarning($"Duplicate PadView for note {view.Note}: " +
                                 $"\"{existing.name}\" and \"{view.name}\".", view);

            _views[view.Note] = view;
        }

        public void Unregister(PadView view)
        {
            if (view == null) return;
            if (_views.TryGetValue(view.Note, out var existing) && existing == view)
                _views.Remove(view.Note);
        }

        public void RepaintAll()
        {
            foreach (var view in _views.Values) view.Repaint();
        }
    }
}