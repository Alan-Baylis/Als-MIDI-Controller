namespace LaunchpadStudio
{
    /// <summary>
    /// Instrument/style tag for a pad. Drives the future overlay icon and a
    /// default pad colour. Serialised by NAME, not by ordinal, so values can be
    /// reordered later without breaking saved sessions.
    /// </summary>
    public enum PadCategory
    {
        None = 0,
        Kick,
        Snare,
        Clap,
        HiHat,
        Cymbal,
        Percussion,
        Tom,
        Bass,
        Sub,
        Lead,
        Pad,
        Keys,
        Arp,
        Pluck,
        Chord,
        Vocal,
        Sfx,
        Riser,
        Impact,
        Loop,
        Sequence,
        Ambience,
        Foley,
        Other
    }

    public static class PadCategories
    {
        /// <summary>Best-effort guess from a filename, used when a clip is first dropped.</summary>
        public static PadCategory Guess(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return PadCategory.None;
            string n = fileName.ToLowerInvariant();

            if (Has(n, "kick", "bd", "909bd", "808bd")) return PadCategory.Kick;
            if (Has(n, "snare", "sd", "rim"))           return PadCategory.Snare;
            if (Has(n, "clap", "cp"))                   return PadCategory.Clap;
            if (Has(n, "hat", "hh", "closed", "open"))  return PadCategory.HiHat;
            if (Has(n, "crash", "ride", "cym"))         return PadCategory.Cymbal;
            if (Has(n, "tom"))                          return PadCategory.Tom;
            if (Has(n, "perc", "shaker", "conga"))      return PadCategory.Percussion;
            if (Has(n, "sub"))                          return PadCategory.Sub;
            if (Has(n, "bass", "303"))                  return PadCategory.Bass;
            if (Has(n, "lead"))                         return PadCategory.Lead;
            if (Has(n, "arp"))                          return PadCategory.Arp;
            if (Has(n, "pluck"))                        return PadCategory.Pluck;
            if (Has(n, "chord", "stab"))                return PadCategory.Chord;
            if (Has(n, "key", "piano", "rhodes"))       return PadCategory.Keys;
            if (Has(n, "pad", "string"))                return PadCategory.Pad;
            if (Has(n, "vox", "vocal", "voice"))        return PadCategory.Vocal;
            if (Has(n, "riser", "uplift", "sweep"))     return PadCategory.Riser;
            if (Has(n, "impact", "hit", "boom"))        return PadCategory.Impact;
            if (Has(n, "amb", "atmos", "drone"))        return PadCategory.Ambience;
            if (Has(n, "foley"))                        return PadCategory.Foley;
            if (Has(n, "seq"))                          return PadCategory.Sequence;
            if (Has(n, "loop"))                         return PadCategory.Loop;
            if (Has(n, "fx", "sfx"))                    return PadCategory.Sfx;

            return PadCategory.None;
        }

        private static bool Has(string haystack, params string[] needles)
        {
            foreach (var needle in needles)
                if (haystack.Contains(needle)) return true;
            return false;
        }
    }
}
