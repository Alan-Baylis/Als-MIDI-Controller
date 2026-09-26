using System;
using System.Collections.Generic;

namespace LaunchpadStudio
{
    /// <summary>
    /// Every device the app knows how to drive. Manager.MidiDeviceId holds one of these
    /// ids, or "" for auto-detect.
    ///
    /// ORDER MATTERS: auto-detect takes the first profile with a matching port, so the
    /// most specific names come first and the catch-all red/green profile comes last.
    /// "Launchpad" alone matches everything Novation ever made.
    ///
    /// Port names are the Windows ones, with the macOS forms covered by "contains"
    /// matching ("Launchpad X LPX MIDI" contains "LPX MIDI"). Use MidiPadInput's
    /// "Log MIDI Ports" context menu to see what a given machine actually reports.
    ///
    /// Adding a device = one line here plus, if its bytes are genuinely different, one
    /// PadDeviceProfile subclass. Nothing else in the project changes.
    /// </summary>
    public static class MidiDeviceCatalog
    {
        /// <summary>The value of Manager.MidiDeviceId meaning "whatever is plugged in".</summary>
        public const string AutoId = "";

        public static readonly PadDeviceProfile ProMk3 = new NovationRgbProfile(
            "novation-pro-mk3", "Launchpad Pro MK3", 0x0E, new[] { "LPProMK3 MIDI" });

        public static readonly PadDeviceProfile X = new NovationRgbProfile(
            "novation-x", "Launchpad X", 0x0C, new[] { "LPX MIDI" });

        public static readonly PadDeviceProfile MiniMk3 = new NovationRgbProfile(
            "novation-mini-mk3", "Launchpad Mini MK3", 0x0D, new[] { "LPMiniMK3 MIDI" });

        // Programmer layout is 2C 03, but only in Standalone mode (21 01); Live mode
        // (21 00) is what hands it back to Ableton.
        public static readonly PadDeviceProfile ProMk2 = new NovationMk2Profile(
            "novation-pro-mk2", "Launchpad Pro (MK2)", 0x10,
            ports:      new[] { "Launchpad Pro" },
            excludes:   new[] { "MK3", "LPPro" },
            topRowBase: 91,
            enter:      new[] { new byte[] { 0x21, 0x01 }, new byte[] { 0x2C, 0x03 } },
            exit:       new[] { new byte[] { 0x21, 0x00 } });

        // Session layout (22 00) is the MK2's power-on default and already addresses
        // the grid as 11-88, so there is nothing to hand back on exit.
        public static readonly PadDeviceProfile Mk2 = new NovationMk2Profile(
            "novation-mk2", "Launchpad MK2", 0x18,
            ports:      new[] { "Launchpad MK2" },
            excludes:   null,
            topRowBase: 104,
            enter:      new[] { new byte[] { 0x22, 0x00 } },
            exit:       null);

        public static readonly PadDeviceProfile Legacy = new NovationLegacyProfile();

        public static readonly IReadOnlyList<PadDeviceProfile> All = new[]
        {
            ProMk3, X, MiniMk3, ProMk2, Mk2, Legacy
        };

        /// <summary>The profile with this id, or null for "" and for ids this build does not know.</summary>
        public static PadDeviceProfile Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;

            foreach (var profile in All)
                if (string.Equals(profile.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                    return profile;

            return null;
        }

        public static int IndexOf(string id)
        {
            var profile = Find(id);
            if (profile == null) return -1;

            for (int i = 0; i < All.Count; i++)
                if (ReferenceEquals(All[i], profile)) return i;

            return -1;
        }
    }
}
