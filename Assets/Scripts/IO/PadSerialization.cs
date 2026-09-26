using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The ONE place a PadState becomes a record and back again. Sessions and pad
    /// layouts are the same data with a different wrapper, and the last time they were
    /// written twice, two fields (quantizeToBeat, startBeat) quietly stopped loading.
    ///
    /// They were being written THREE times when this was consolidated: SessionService
    /// and PadLayoutService each carried a copy, and the pair here — the copy that was
    /// supposed to be canonical — was called by nobody at all. A guard rail bolted to
    /// nothing does not guard anything.
    ///
    /// Everything now routes through Fill and ApplyMetadata, so adding a PadState field
    /// means touching SessionPad and those two methods, and BOTH file formats get it.
    ///
    /// Clips always re-enter through PadController.AssignClipAsync, so anything applied
    /// here is refcounted exactly like a drag-and-drop.
    /// </summary>
    public static class PadSerialization
    {
        /// <summary>
        /// A pad worth writing down. An empty grid should be a 200-byte file, not 64
        /// records of nothing.
        /// </summary>
        public static bool IsInteresting(PadState pad) =>
            pad != null && (pad.HasClip ||
                            !string.IsNullOrWhiteSpace(pad.label) ||
                            pad.useCustomColour ||
                            pad.category != PadCategory.None);

        // ------------------------------------------------------------------ capture

        public static List<SessionPad> Capture(PadModel model, bool onlyInteresting = true)
        {
            var list = new List<SessionPad>(16);
            if (model == null) return list;

            foreach (int note in NoteGrid.All)
            {
                var pad = model.Get(note);
                if (pad == null) continue;
                if (onlyInteresting && !IsInteresting(pad)) continue;

                list.Add(ToRecord(note, pad));
            }

            return list;
        }

        public static SessionPad ToRecord(int note, PadState pad) =>
            Fill(new SessionPad(), note, pad);

        /// <summary>
        /// The layout flavour. Identical to a session record except the path, which the
        /// caller has already made library-relative (or not) — and that is the ONLY
        /// thing a layout genuinely does differently.
        /// </summary>
        public static LayoutPad ToLayoutRecord(int note, PadState pad,
                                               string storedPath, bool relative)
        {
            var record = Fill(new LayoutPad(), note, pad);

            record.clipPath = storedPath;
            record.relative = relative;

            return record;
        }

        /// <summary>
        /// PadState → record. Generic over the record type so the layout flavour cannot
        /// drift from the session one: one field list, both callers.
        /// </summary>
        private static T Fill<T>(T record, int note, PadState pad) where T : SessionPad
        {
            record.note            = note;
            record.clipPath        = pad.clipPath;
            record.label           = pad.label;
            record.category        = pad.category.ToString();
            record.colour          = ColorUtility.ToHtmlStringRGBA(pad.colour);
            record.useCustomColour = pad.useCustomColour;
            record.loop            = pad.loop;
            record.volume          = pad.volume;
            record.pitch           = pad.pitch;
            record.quantizeToBeat  = pad.quantizeToBeat;
            record.startBeat       = pad.startBeat;

            return record;
        }

        // -------------------------------------------------------------------- apply

        /// <summary>
        /// Record → PadState, EXCEPT the clip path. The path is left out deliberately:
        /// a session stores it absolute and writes it straight through, while a layout
        /// stores it relative and has to resolve it against the CURRENT library root
        /// first. That is the one real difference between the formats, so it stays with
        /// the caller — and everything else is shared, which is the point.
        ///
        /// Call this inside a PadModel.Set callback: it writes the pad in place and
        /// raises nothing itself.
        /// </summary>
        public static void ApplyMetadata(SessionPad saved, PadState pad)
        {
            if (saved == null || pad == null) return;

            pad.label          = saved.label;
            pad.loop           = saved.loop;
            pad.volume         = Mathf.Clamp01(saved.volume);
            pad.pitch          = Mathf.Clamp(saved.pitch, 0.25f, 4f);
            pad.quantizeToBeat = saved.quantizeToBeat;
            pad.startBeat      = Mathf.Max(0, saved.startBeat);   // 0 = next beat

            // By NAME, so the enum can be reordered. An unrecognised name leaves the
            // pad's current category alone rather than forcing None — a file from a
            // newer build should lose the value, not overwrite it with a wrong one.
            if (Enum.TryParse(saved.category, out PadCategory category))
                pad.category = category;

            if (saved.useCustomColour && TryColour(saved.colour, out var padColour))
                pad.SetColour(padColour);
            else
                pad.ClearColour();
        }

        /// <summary>
        /// Writes records onto the model and kicks off the decodes. This is the SESSION
        /// path: paths are absolute as stored. Layouts resolve first and drive
        /// PadLayoutService.Apply, but both share ApplyMetadata above.
        ///
        /// clearFirst empties every pad through the controller, so cache references are
        /// released — a load that skipped that would leak one reference per replaced clip.
        /// </summary>
        public static void Apply(IList<SessionPad> records, PadModel model,
                                 PadController controller, bool clearFirst,
                                 out int queued, out int missing)
        {
            queued = 0;
            missing = 0;

            if (model == null) return;

            if (clearFirst)
            {
                foreach (int note in NoteGrid.All)
                {
                    if (controller != null) controller.ClearPad(note);
                    else model.Clear(note);
                }
            }

            if (records == null) return;

            foreach (var saved in records)
            {
                if (saved == null || !NoteGrid.IsGridNote(saved.note)) continue;

                int note = saved.note;

                model.Set(note, pad =>
                {
                    ApplyMetadata(saved, pad);
                    pad.clipPath = saved.clipPath;     // absolute, in a session
                });

                if (string.IsNullOrWhiteSpace(saved.clipPath)) continue;

                if (!File.Exists(saved.clipPath))
                {
                    // Path is DELIBERATELY kept: the user may plug the drive back in,
                    // and a discarded path cannot be recovered.
                    controller?.SetLoadState(note, PadLoadState.Missing);
                    missing++;
                    continue;
                }

                queued++;
                controller?.AssignClipAsync(note, saved.clipPath);
            }
        }

        // ---------------------------------------------------------------- signature

        /// <summary>
        /// A cheap fingerprint of everything a layout stores. Comparing two of these is
        /// how "unsaved changes" is answered without a dirty flag that PadChanged would
        /// trip on every load-state transition.
        /// </summary>
        public static string Signature(PadModel model)
        {
            if (model == null) return string.Empty;

            var sb = new StringBuilder(512);

            foreach (int note in NoteGrid.All)
            {
                var pad = model.Get(note);
                if (!IsInteresting(pad)) continue;

                sb.Append(note).Append('|')
                  .Append(pad.clipPath).Append('|')
                  .Append(pad.label).Append('|')
                  .Append(pad.category).Append('|')
                  .Append(pad.useCustomColour ? ColorUtility.ToHtmlStringRGBA(pad.colour) : "-")
                  .Append('|')
                  .Append(pad.loop ? '1' : '0').Append('|')
                  .Append(pad.volume.ToString("0.###")).Append('|')
                  .Append(pad.pitch.ToString("0.###")).Append('|')
                  .Append(pad.quantizeToBeat ? '1' : '0').Append('|')
                  .Append(pad.startBeat).Append(';');
            }

            return sb.ToString();
        }

        public static bool TryColour(string hex, out Color32 colour)
        {
            colour = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            if (!hex.StartsWith("#")) hex = "#" + hex;
            if (!ColorUtility.TryParseHtmlString(hex, out Color parsed)) return false;

            colour = parsed;
            return true;
        }
    }
}