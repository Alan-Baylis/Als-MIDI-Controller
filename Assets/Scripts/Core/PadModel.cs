using System;
using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The single source of truth for pad content. Knows nothing about AudioSources,
    /// Buttons or files — it only holds 64 PadState objects and shouts when one changes.
    /// </summary>
    [DisallowMultipleComponent]
    public class PadModel : MonoBehaviour
    {
        /// <summary>Raised with the note whose state changed.</summary>
        public event Action<int> PadChanged;

        /// <summary>Raised after a bulk change (session load). Views should repaint everything.</summary>
        public event Action ModelReloaded;

        private readonly PadState[] _pads = new PadState[NoteGrid.ArraySize];

        private void Awake() => EnsureBuilt();

        private void EnsureBuilt()
        {
            foreach (int note in NoteGrid.All)
                if (_pads[note] == null)
                    _pads[note] = new PadState(note);
        }

        public PadState Get(int note)
        {
            if (!NoteGrid.IsGridNote(note)) return null;
            EnsureBuilt();
            return _pads[note];
        }

        public bool IsEmpty(int note)
        {
            var pad = Get(note);
            return pad == null || !pad.HasClip;
        }

        /// <summary>Mutate a pad and raise PadChanged. The only sanctioned write path.</summary>
        public void Set(int note, Action<PadState> mutate)
        {
            var pad = Get(note);
            if (pad == null)
            {
                Debug.LogWarning($"PadModel.Set called with non-grid note {note}.", this);
                return;
            }

            mutate?.Invoke(pad);
            PadChanged?.Invoke(note);
        }

        public void Clear(int note)
        {
            var pad = Get(note);
            if (pad == null) return;

            pad.ResetToEmpty();
            PadChanged?.Invoke(note);
        }

        // ---------------------------------------------------------- transfers

        public void Move(int from, int to)
        {
            if (from == to) return;
            var src = Get(from);
            var dst = Get(to);
            if (src == null || dst == null) return;

            CopyContent(src, dst);
            src.ResetToEmpty();

            PadChanged?.Invoke(from);
            PadChanged?.Invoke(to);
        }

        public void Copy(int from, int to)
        {
            if (from == to) return;
            var src = Get(from);
            var dst = Get(to);
            if (src == null || dst == null) return;

            CopyContent(src, dst);
            PadChanged?.Invoke(to);
        }

        public void Swap(int a, int b)
        {
            if (a == b) return;
            var padA = Get(a);
            var padB = Get(b);
            if (padA == null || padB == null) return;

            var temp = padA.CloneTo(a);
            CopyContent(padB, padA);
            CopyContent(temp, padB);

            PadChanged?.Invoke(a);
            PadChanged?.Invoke(b);
        }

        private static void CopyContent(PadState src, PadState dst)
        {
            dst.clipPath        = src.clipPath;
            dst.label           = src.label;
            dst.category        = src.category;
            dst.colour          = src.colour;
            dst.useCustomColour = src.useCustomColour;
            dst.loop            = src.loop;
            dst.volume          = src.volume;
            dst.pitch           = src.pitch;
            dst.quantizeToBeat  = src.quantizeToBeat;
            dst.startBeat       = src.startBeat;
            dst.loadState       = src.loadState;
            dst.clip            = src.clip;
        }

        // ---------------------------------------------------------- bulk ops

        public IEnumerable<PadState> Occupied
        {
            get
            {
                EnsureBuilt();
                foreach (int note in NoteGrid.All)
                    if (_pads[note].HasClip)
                        yield return _pads[note];
            }
        }

        public void ClearAll()
        {
            EnsureBuilt();
            foreach (int note in NoteGrid.All)
                _pads[note].ResetToEmpty();

            ModelReloaded?.Invoke();
        }

        /// <summary>Call once after applying a loaded session, instead of 64 PadChanged events.</summary>
        public void RaiseReloaded() => ModelReloaded?.Invoke();
    }
}
