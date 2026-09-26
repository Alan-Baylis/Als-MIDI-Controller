using System;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>What arrived from the device: a note, or a controller.</summary>
    public enum DeviceInputKind { Note, ControlChange }

    /// <summary>How well a port name matched a profile. Exact beats Contains.</summary>
    public enum PortMatch { None, Contains, Exact }

    /// <summary>
    /// Everything that differs between one grid controller and another, and nothing
    /// else. MidiPadInput owns the port and the connection state machine; a profile
    /// owns the bytes:
    ///
    ///   - which port names belong to it,
    ///   - the handshake that makes it addressable, and the one that hands it back,
    ///   - device number ↔ LOGICAL index, both ways,
    ///   - how a batch of RGB lamps becomes MIDI messages at its colour depth.
    ///
    /// The logical scheme is the Pro MK3 programmer layout (grid 11-88, top row 91-98,
    /// right column 19-89), because that is what NoteGrid, the model, the session files
    /// and MetronomeLedView already speak. A profile for a device that already uses it
    /// is an identity map; one that does not is the remap layer — no interpreter above.
    ///
    /// Profiles are immutable and stateless, so one instance serves the whole run and a
    /// switch between them is just a different reference.
    /// </summary>
    public abstract class PadDeviceProfile
    {
        /// <summary>Stored in Manager.MidiDeviceId and the session. Never rename one.</summary>
        public abstract string Id { get; }

        public abstract string DisplayName { get; }

        /// <summary>For the status line: "full RGB", "red/green, 4 levels each".</summary>
        public abstract string ColourDepth { get; }

        /// <summary>Port names (or fragments) that belong to this device.</summary>
        protected abstract string[] PortNames { get; }

        /// <summary>
        /// Fragments that DISQUALIFY a port even when a pattern matches, for families
        /// whose names nest: "Launchpad Pro" is a prefix of "Launchpad Pro MK3 …" on
        /// macOS, and "Launchpad" is a prefix of all of them.
        /// </summary>
        protected virtual string[] PortExcludes => Array.Empty<string>();

        public PortMatch MatchPort(string portName, bool allowContains)
        {
            if (string.IsNullOrEmpty(portName)) return PortMatch.None;

            foreach (var exclude in PortExcludes)
                if (portName.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0)
                    return PortMatch.None;

            var best = PortMatch.None;

            foreach (var pattern in PortNames)
            {
                if (string.Equals(portName, pattern, StringComparison.OrdinalIgnoreCase))
                    return PortMatch.Exact;

                if (allowContains &&
                    portName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    best = PortMatch.Contains;
            }

            return best;
        }

        /// <summary>
        /// Sent (in order) a few frames after the port opens. Empty = the device is
        /// addressable as soon as the port is open, and is marked ready immediately.
        /// Fresh events on every call: a MidiEvent is a mutable object.
        /// </summary>
        public virtual IEnumerable<MidiEvent> CreateEnterMode() { yield break; }

        /// <summary>Sent on a graceful close, after the lamps are cleared.</summary>
        public virtual IEnumerable<MidiEvent> CreateExitMode() { yield break; }

        /// <summary>Device note/CC number → logical index, or -1 when it is not ours.</summary>
        public abstract int ToLogical(DeviceInputKind kind, int number);

        /// <summary>
        /// A batch of lamps → the fewest messages the device accepts. The RGB Launchpads
        /// take many lamps per SysEx; the red/green ones need one message per lamp.
        /// </summary>
        public abstract IEnumerable<MidiEvent> BuildLedMessages(IList<LedWrite> writes);

        public override string ToString() => DisplayName;

        // ---------------------------------------------------------------- helpers

        protected static bool IsTopRow(int logical) => logical >= 91 && logical <= 98;

        protected static bool IsRightColumn(int logical) =>
            logical >= 19 && logical <= 89 && logical % 10 == 9;

        /// <summary>0-255 → 0-max, rounded. Every byte in a MIDI message must be &lt; 0x80.</summary>
        protected static byte Scale(byte channel255, int max) =>
            (byte)Mathf.Clamp(Mathf.RoundToInt(channel255 / 255f * max), 0, max);

        protected static byte Seven(int value) => (byte)Mathf.Clamp(value, 0, 127);

        /// <summary>
        /// Novation's SysEx frame: F0 00 20 29 02 &lt;product&gt; &lt;body…&gt; F7. Built
        /// WITHOUT the F0 and WITH the F7, which is the shape NormalSysExEvent expects.
        /// </summary>
        protected static NormalSysExEvent NovationSysEx(byte product, IList<byte> body)
        {
            var payload = new byte[5 + body.Count + 1];

            payload[0] = 0x00; payload[1] = 0x20; payload[2] = 0x29;
            payload[3] = 0x02; payload[4] = product;

            for (int i = 0; i < body.Count; i++) payload[5 + i] = body[i];

            payload[payload.Length - 1] = 0xF7;
            return new NormalSysExEvent(payload);
        }
    }
}
