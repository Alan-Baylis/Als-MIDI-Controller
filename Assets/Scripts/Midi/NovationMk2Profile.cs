using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;

namespace LaunchpadStudio
{
    /// <summary>
    /// The previous RGB generation: Launchpad MK2 and Launchpad Pro (MK2). Same idea as
    /// the MK3 family with three differences, each a constructor argument:
    ///
    ///   Lighting   F0 00 20 29 02 &lt;pid&gt; 0B &lt;led&gt; &lt;r&gt; &lt;g&gt; &lt;b&gt; [&lt;led&gt; &lt;r&gt; &lt;g&gt; &lt;b&gt;…] F7
    ///              — colour is 6-bit, 0-63, not 0-127.
    ///   Handshake  a LAYOUT select rather than a Programmer-mode toggle; the MK2 is
    ///              already addressable in its Session layout.
    ///   Top row    CC 104-111 on the MK2, CC 91-98 on the Pro (MK2) in Programmer
    ///              layout. That is the one real remap: logical 91-98 ↔ device CCs.
    ///
    /// The grid is 11-88 on both, and the right-hand column x9 arrives as notes on the
    /// MK2 and CCs on the Pro, so either is accepted.
    /// </summary>
    public sealed class NovationMk2Profile : PadDeviceProfile
    {
        /// <summary>Novation document up to 80 lamps per message; 40 leaves headroom.</summary>
        private const int MaxLedsPerMessage = 40;

        private readonly string   _id;
        private readonly string   _name;
        private readonly byte     _product;
        private readonly string[] _ports;
        private readonly string[] _excludes;
        private readonly int      _topRowBase;
        private readonly byte[][] _enter;
        private readonly byte[][] _exit;

        /// <param name="topRowBase">Device CC of the leftmost top-row button (104 or 91).</param>
        /// <param name="enter">SysEx BODIES (after the product byte) sent on connect, in order.</param>
        /// <param name="exit">SysEx bodies sent on a graceful close. May be null.</param>
        public NovationMk2Profile(string id, string name, byte productId,
                                  string[] ports, string[] excludes, int topRowBase,
                                  byte[][] enter, byte[][] exit)
        {
            _id         = id;
            _name       = name;
            _product    = productId;
            _ports      = ports    ?? new string[0];
            _excludes   = excludes ?? new string[0];
            _topRowBase = topRowBase;
            _enter      = enter    ?? new byte[0][];
            _exit       = exit     ?? new byte[0][];
        }

        public override string Id          => _id;
        public override string DisplayName => _name;
        public override string ColourDepth => "RGB, 64 levels per channel";

        protected override string[] PortNames    => _ports;
        protected override string[] PortExcludes => _excludes;

        public override IEnumerable<MidiEvent> CreateEnterMode()
        {
            foreach (var body in _enter) yield return NovationSysEx(_product, body);
        }

        public override IEnumerable<MidiEvent> CreateExitMode()
        {
            foreach (var body in _exit) yield return NovationSysEx(_product, body);
        }

        public override int ToLogical(DeviceInputKind kind, int number)
        {
            if (kind == DeviceInputKind.Note)
                return NoteGrid.IsGridNote(number) || IsRightColumn(number) ? number : -1;

            if (number >= _topRowBase && number < _topRowBase + 8)
                return 91 + (number - _topRowBase);

            return IsRightColumn(number) ? number : -1;
        }

        private int ToDevice(int logical) =>
            IsTopRow(logical) ? _topRowBase + (logical - 91) : logical;

        public override IEnumerable<MidiEvent> BuildLedMessages(IList<LedWrite> writes)
        {
            if (writes == null || writes.Count == 0) yield break;

            for (int start = 0; start < writes.Count; start += MaxLedsPerMessage)
            {
                int count = System.Math.Min(MaxLedsPerMessage, writes.Count - start);
                var body  = new List<byte>(1 + count * 4) { 0x0B };

                for (int i = 0; i < count; i++)
                {
                    var write = writes[start + i];

                    body.Add(Seven(ToDevice(write.Index)));
                    body.Add(Scale(write.Colour.r, 63));
                    body.Add(Scale(write.Colour.g, 63));
                    body.Add(Scale(write.Colour.b, 63));
                }

                yield return NovationSysEx(_product, body);
            }
        }
    }
}
