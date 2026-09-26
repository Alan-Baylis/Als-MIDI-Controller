using System.Collections.Generic;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The red/green Launchpads: the original, the S, and the Mini (MK1/MK2). The
    /// palette-only path. No SysEx and no RGB — a lamp is a Note On whose VELOCITY is
    /// the colour:
    ///
    ///   velocity = 16 × green + red + 12      (green, red 0-3; 12 = "copy + clear",
    ///                                          Novation's value for normal use)
    ///
    /// Addressing is the X-Y layout, rows counted from the TOP: note = 16 × row + col,
    /// with column 8 being the round buttons on the right. The top row is CC 104-111.
    /// Reset (CC 0 = 0) turns everything off and selects X-Y, so it is the handshake
    /// and the goodbye both.
    ///
    /// Colour is approximate by nature. Blue has no LED, so it is folded half into red
    /// and half into green — a purple "missing" pad reads as dim amber rather than as
    /// off, which would be indistinguishable from empty.
    ///
    /// Not velocity-sensitive: presses arrive as 127, releases as 0.
    /// </summary>
    public sealed class NovationLegacyProfile : PadDeviceProfile
    {
        private const int TopRowBase = 104;
        private const byte Flags     = 12;

        private static readonly string[] Ports    = { "Launchpad Mini", "Launchpad S", "Launchpad" };
        private static readonly string[] Excludes = { "MK2", "MK3", "Pro", "LPX", "LPMini", "LPPro" };

        public override string Id          => "novation-legacy";
        public override string DisplayName => "Launchpad / S / Mini (red-green)";
        public override string ColourDepth => "red/green, 4 levels each";

        protected override string[] PortNames    => Ports;
        protected override string[] PortExcludes => Excludes;

        public override IEnumerable<MidiEvent> CreateEnterMode() { yield return Reset(); }
        public override IEnumerable<MidiEvent> CreateExitMode()  { yield return Reset(); }

        private static MidiEvent Reset() =>
            new ControlChangeEvent((SevenBitNumber)0, (SevenBitNumber)0);

        // ------------------------------------------------------------- mapping

        public override int ToLogical(DeviceInputKind kind, int number)
        {
            if (kind == DeviceInputKind.ControlChange)
                return number >= TopRowBase && number < TopRowBase + 8
                    ? 91 + (number - TopRowBase)
                    : -1;

            int deviceRow = number >> 4;          // 0 = top
            int deviceCol = number & 0x0F;

            if (deviceRow > 7 || deviceCol > 8) return -1;

            int row = 8 - deviceRow;              // logical rows count from the bottom
            return deviceCol == 8 ? row * 10 + 9 : row * 10 + deviceCol + 1;
        }

        /// <summary>Logical → device note. Only the grid and the right column are notes.</summary>
        private static int ToDeviceNote(int logical)
        {
            int row = logical / 10;
            int col = logical % 10;

            if (row < 1 || row > 8 || col < 1 || col > 9) return -1;

            return 16 * (8 - row) + (col == 9 ? 8 : col - 1);
        }

        // ---------------------------------------------------------------- lamps

        /// <summary>
        /// One message per lamp — the device has no batch form that suits random
        /// access. A full 64-pad repaint is 64 three-byte messages, which the old units
        /// take in their stride.
        /// </summary>
        public override IEnumerable<MidiEvent> BuildLedMessages(IList<LedWrite> writes)
        {
            if (writes == null) yield break;

            foreach (var write in writes)
            {
                var velocity = (SevenBitNumber)ToVelocity(write.Colour);

                if (IsTopRow(write.Index))
                {
                    yield return new ControlChangeEvent(
                        (SevenBitNumber)(TopRowBase + write.Index - 91), velocity);
                    continue;
                }

                int note = ToDeviceNote(write.Index);
                if (note < 0) continue;

                yield return new NoteOnEvent((SevenBitNumber)note, velocity);
            }
        }

        private static byte ToVelocity(Color32 colour)
        {
            float blueShare = colour.b * 0.5f;

            byte red   = Scale((byte)Mathf.Min(255f, Mathf.Max(colour.r, blueShare)), 3);
            byte green = Scale((byte)Mathf.Min(255f, Mathf.Max(colour.g, blueShare)), 3);

            return (byte)(16 * green + red + Flags);
        }
    }
}
