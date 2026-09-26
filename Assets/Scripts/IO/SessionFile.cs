using System;
using System.Collections.Generic;

namespace LaunchpadStudio
{
    /// <summary>
    /// The on-disk session, as plain serialisable data. JsonUtility only, so: no
    /// properties, no dictionaries, no nullables — and colours travel as hex strings
    /// rather than as four bytes, because a hex string survives being hand-edited.
    ///
    /// Version is written first and checked on load. Add fields freely; JsonUtility
    /// leaves anything missing at its C# default, which is why every field here has a
    /// sane one — a version 1 file loaded by version 2 simply gets the defaults.
    /// </summary>
    [Serializable]
    public class SessionFile
    {
        public const int CurrentVersion = 2;

        public int    version = CurrentVersion;
        public string savedAt;
        public string savedBy = "LaunchpadStudio";

        public SessionSettings settings = new SessionSettings();
        public SessionNotices  notices  = new SessionNotices();

        public List<SessionPalette> palette = new List<SessionPalette>();
        public List<SessionPad>     pads    = new List<SessionPad>();
    }

    [Serializable]
    public class SessionSettings
    {
        public float  bpm                  = 120f;
        public float  masterVolume         = 1f;
        public bool   metronomeEnabled     = true;
        public bool   metronomeClickEnabled;
        public bool   debugEnabled;
        public bool   vSync                = true;
        public int    vSyncInterval        = 1;     // v2
        public int    targetFrameRate;
        /// <summary>Clip names drawn on the pads. v2.</summary>
        public bool showPadLabels = true;
        /// <summary>Dismissible feature-note hints. v2.</summary>
        public bool showFeatureNotes = true;
        
        public string libraryRoot          = "";
        public string layoutsFolder        = "Layouts";      // v2
        public string lightshowsFolder     = "Lightshows";   // v2
        public string stylesFolder         = "Styles";       // v2

        /// <summary>Port names last used, so a known device reconnects without the dropdown.</summary>
        public string midiProfile          = "";             // v2

        /// <summary>0 = nothing selected.</summary>
        public int selectedPad;
        /// <summary>
        /// The layout the grid came from, so the strip says "Techno" again on the next
        /// run instead of "Untitled". The NAME only — the layout file is deliberately
        /// not reloaded, because the session has already restored these exact pads and
        /// re-reading the file would discard anything edited after the layout was
        /// loaded. v2.
        /// </summary>
        public string lastLayoutName = "";
        
        /// <summary>The exact file, so Save targets it even in a subfolder. Empty in old files.</summary>
        public string lastLayoutPath = "";
    }

    /// <summary>One palette ROLE, by role name — not by asset name, which is free to change.</summary>
    [Serializable]
    public class SessionPalette
    {
        public string role;      // empty | hasClip | loaded | editing | loading | missing | flash
        public string colour;    // RRGGBBAA
    }

    [Serializable]
    public class SessionNotices
    {
        public string info    = "";
        public string success = "";
        public string warning = "";
        public string error   = "";
    }

    [Serializable]
    public class SessionPad
    {
        public int    note;
        public string clipPath;
        public string label;
        public string category = "None";   // by NAME, so the enum can be reordered
        public string colour   = "";       // RRGGBBAA, only meaningful with useCustomColour
        public bool   useCustomColour;
        public bool   loop;
        public float  volume = 1f;
        public float  pitch  = 1f;
        public bool   quantizeToBeat;
        public int    startBeat = 1;
    }
}