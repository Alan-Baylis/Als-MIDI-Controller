using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;

namespace LaunchpadStudio
{
    /// <summary>
    /// The current Novation generation: Launchpad Pro MK3, Launchpad X, Launchpad Mini
    /// MK3. Close cousins that differ only in the product byte and the port name:
    ///
    ///   Programmer mode   F0 00 20 29 02 &lt;pid&gt; 0E 01 F7   (00 = back to Live)
    ///   Lighting          F0 00 20 29 02 &lt;pid&gt; 03 &lt;spec&gt;… F7
    ///   RGB spec          03 &lt;index&gt; &lt;r&gt; &lt;g&gt; &lt;b&gt;   (each 0-127)
    ///
    /// In Programmer mode all three already use the logical scheme — grid 11-88 as
    /// notes, top row 91-98 and right column x9 as CCs — so the map is the identity.
    /// This is exactly what LaunchpadLed and MidiPadInput hard-coded for the Pro MK3.
    /// </summary>
    public sealed class NovationRgbProfile : PadDeviceProfile
    {
        /// <summary>Roughly what the hardware swallows in one SysEx. Batches are chunked.</summary>
        private const int MaxSpecsPerMessage = 48;

        private readonly string   _id;
        private readonly string   _name;
        private readonly byte     _product;
        private readonly string[] _ports;
        private readonly string[] _excludes;

        public NovationRgbProfile(string id, string name, byte productId,
                                  string[] ports, string[] excludes = null)
        {
            _id       = id;
            _name     = name;
            _product  = productId;
            _ports    = ports ?? new string[0];
            _excludes = excludes ?? new string[0];
        }

        public override string Id          => _id;
        public override string DisplayName => _name;
        public override string ColourDepth => "full RGB";

        protected override string[] PortNames    => _ports;
        protected override string[] PortExcludes => _excludes;

        public override IEnumerable<MidiEvent> CreateEnterMode()
        {
            yield return NovationSysEx(_product, new byte[] { 0x0E, 0x01 });
        }

        public override IEnumerable<MidiEvent> CreateExitMode()
        {
            yield return NovationSysEx(_product, new byte[] { 0x0E, 0x00 });
        }

        public override int ToLogical(DeviceInputKind kind, int number)
        {
            if (kind == DeviceInputKind.Note)
                return NoteGrid.IsGridNote(number) ? number : -1;

            return IsTopRow(number) || IsRightColumn(number) ? number : -1;
        }

        public override IEnumerable<MidiEvent> BuildLedMessages(IList<LedWrite> writes)
        {
            if (writes == null || writes.Count == 0) yield break;

            for (int start = 0; start < writes.Count; start += MaxSpecsPerMessage)
            {
                int count = System.Math.Min(MaxSpecsPerMessage, writes.Count - start);
                var body  = new List<byte>(1 + count * 5) { 0x03 };  // command: lighting

                for (int i = 0; i < count; i++)
                {
                    var write = writes[start + i];

                    body.Add(0x03);                                    // spec type: RGB
                    body.Add(Seven(write.Index));
                    body.Add(Scale(write.Colour.r, 127));
                    body.Add(Scale(write.Colour.g, 127));
                    body.Add(Scale(write.Colour.b, 127));
                }

                yield return NovationSysEx(_product, body);
            }
        }
    }
}