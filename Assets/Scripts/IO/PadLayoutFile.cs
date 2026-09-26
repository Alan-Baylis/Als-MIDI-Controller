using System;
using System.Collections.Generic;

namespace LaunchpadStudio
{
    /// <summary>
    /// One saved pad layout, as plain serialisable data. Same constraints as
    /// SessionFile: JsonUtility only, so no properties, no dictionaries, no
    /// nullables, and colours travel as hex strings so the file survives being
    /// hand-edited.
    ///
    /// A layout is the 64 PADS and nothing else. It carries no BPM, no palette and
    /// no display settings — loading a kit must not re-tempo the project.
    ///
    /// Paths are stored RELATIVE to libraryRoot whenever the file sits inside it,
    /// which is what makes a layout portable between machines that share a sample
    /// pack. Anything outside the root is stored absolute and is understood to be
    /// machine-local.
    /// </summary>
    [Serializable]
    public class PadLayoutFile
    {
        public const int CurrentVersion = 1;

        public int    version = CurrentVersion;

        /// <summary>Display name. The FILE name is derived from this, not the other way round.</summary>
        public string name    = "";

        public string savedAt;
        public string savedBy = "LaunchpadStudio";

        /// <summary>
        /// The library root the relative paths below were resolved against, recorded
        /// for diagnostics only. On load the CURRENT root is used, so moving your
        /// samples and re-pointing the library fixes every layout at once.
        /// </summary>
        public string libraryRoot = "";

        public List<LayoutPad> pads = new List<LayoutPad>();
    }

    /// <summary>
    /// A layout pad IS a session pad plus one extra fact: whether the path is stored
    /// relative to the library root. The field list used to be written out twice, which
    /// is exactly how a new PadState field gets added to one format and silently
    /// forgotten in the other. Now there is one list, and LayoutPad says only what is
    /// DIFFERENT about a layout.
    ///
    /// JsonUtility serialises the inherited fields of a [Serializable] class and JSON is
    /// keyed by name, so version 1 files written before this change still load. The only
    /// visible difference is field ORDER in newly written files: the shared fields come
    /// first and "relative" moves to the end.
    /// </summary>
    [Serializable]
    public class LayoutPad : SessionPad
    {
        /// <summary>
        /// Stored explicitly rather than inferred from the string, because
        /// "Drums/Kick.wav" and a Linux absolute path are not distinguishable by shape,
        /// and guessing wrong turns a working layout into 64 missing files.
        ///
        /// When true, the inherited <see cref="SessionPad.clipPath"/> is library-relative.
        /// </summary>
        public bool relative;
    }
}