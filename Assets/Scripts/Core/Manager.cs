using UnityEngine;
using UnityEngine.Serialization;
using System.IO;

namespace LaunchpadStudio
{
    /// <summary>
    /// Global session settings and the pad colour palette. Not a view —
    /// the side-panel page that edits these values is ManagerView.
    /// </summary>
    [DisallowMultipleComponent]
    public class Manager : MonoBehaviour
    {
        public const float MinBpm = 1f;
        public const float MaxBpm = 300f;

        [Header("Transport")]
        [SerializeField, FormerlySerializedAs("BPM"), Range(MinBpm, MaxBpm)]
        private float bpm = 120f;

        [Header("Mix")]
        [Tooltip("Master output level. Scales EVERYTHING: pads, library previews, " +
                 "and the metronome click.")]
        [SerializeField, Range(0f, 1f)] private float masterVolume = 1f;

        public event System.Action<float> MasterVolumeChanged;

        [Tooltip("Audible metronome click. The LEDs are governed by Metronome Enabled. " +
                 "This field IS the startup default — the ManagerView toggle only displays it.")]
        [SerializeField] private bool metronomeClickEnabled = false;

        public event System.Action<bool> MetronomeClickEnabledChanged;

        public bool MetronomeClickEnabled
        {
            get => metronomeClickEnabled;
            set
            {
                if (metronomeClickEnabled == value) return;
                metronomeClickEnabled = value;
                MetronomeClickEnabledChanged?.Invoke(value);
            }
        }

        [Tooltip("Master switch for the metronome LEDs (and optional click).")]
        [SerializeField] private bool metronomeEnabled = true;

        /// <summary>Raised whenever BPM changes, from any source.</summary>
        public event System.Action<float> BpmChanged;

        /// <summary>Raised whenever the metronome is switched on or off.</summary>
        public event System.Action<bool> MetronomeEnabledChanged;

        /// <summary>The one and only BPM. Everything else reads this.</summary>
        public float BPM
        {
            get => bpm;
            set
            {
                float clamped = Mathf.Clamp(value, MinBpm, MaxBpm);
                if (Mathf.Approximately(bpm, clamped)) return;
                bpm = clamped;
                BpmChanged?.Invoke(bpm);
            }
        }

        /// <summary>Convenience for anything that needs beat length. Never divides by zero.</summary>
        public float SecondsPerBeat => 60f / Mathf.Max(MinBpm, bpm);

        // ------------------------------------------------------------------ display

        [Header("Display")]
        [Tooltip("V-Sync. On = frames are presented in step with the monitor, which is what " +
                 "removes the tearing you can see when a lot of pads change colour at once.")]
        [SerializeField] private bool vSync = true;

        [Tooltip("Present after this many blanking intervals. 1 = every frame (the monitor's " +
                 "full rate). 2 = every second frame, so a 120 Hz panel runs a rock-steady " +
                 "60 fps with no tearing — which a frame cap alone cannot give you. " +
                 "Ignored while V-Sync is off.")]
        [SerializeField, Range(1, 4)] private int vSyncInterval = 1;

        [Tooltip("Frame cap used ONLY when V-Sync is off. 0 = follow the monitor's refresh " +
                 "rate when Follow Display Refresh Rate is on, otherwise uncapped.")]
        [SerializeField, Min(0)] private int targetFrameRate = 0;

        [Tooltip("Read the monitor's actual refresh rate and use it as the default cap. " +
                 "Re-read every second, so dragging the window to a different monitor " +
                 "is picked up without a restart.")]
        [SerializeField] private bool followDisplayRefreshRate = true;

        public event System.Action<bool> VSyncChanged;
        public event System.Action<int>  VSyncIntervalChanged;
        public event System.Action<int>  TargetFrameRateChanged;

        /// <summary>Raised when the monitor's reported refresh rate changes.</summary>
        public event System.Action<int> DisplayRefreshRateChanged;

        private int   _refreshHz = 60;
        private float _nextRefreshPoll;

        /// <summary>The monitor's refresh rate in Hz, rounded. Never 0.</summary>
        public int DisplayRefreshRate => _refreshHz;

        /// <summary>
        /// What the app will ACTUALLY run at, as far as we can know it. With V-Sync on
        /// that is the monitor divided by the interval; with it off it is the cap, or
        /// the monitor if the cap is following it, or 0 for "as fast as it goes".
        /// </summary>
        public int EffectiveFrameCap =>
            vSync ? Mathf.Max(1, Mathf.RoundToInt(_refreshHz / (float)Mathf.Max(1, vSyncInterval)))
                  : targetFrameRate > 0 ? targetFrameRate
                  : followDisplayRefreshRate ? _refreshHz
                  : 0;

        /// <summary>"144 Hz monitor, V-Sync on → 144 fps". For the Manager page.</summary>
        public string DescribeDisplay()
        {
            int cap = EffectiveFrameCap;

            string sync = !vSync ? "off"
                        : vSyncInterval <= 1 ? "on"
                        : $"on (every {Ordinal(vSyncInterval)} frame)";

            return $"{_refreshHz} Hz display, V-Sync {sync} → " +
                   (cap > 0 ? $"{cap} fps" : "uncapped");
        }

        private static string Ordinal(int n) =>
            n == 2 ? "2nd" : n == 3 ? "3rd" : $"{n}th";

        /// <summary>
        /// QualitySettings.vSyncCount is a GLOBAL that survives play-mode exit, exactly like
        /// AudioListener.volume, so it has to be re-asserted from the serialised value.
        /// </summary>
        public bool VSync
        {
            get => vSync;
            set
            {
                if (vSync == value) return;
                vSync = value;
                ApplyDisplaySettings();
                VSyncChanged?.Invoke(vSync);
            }
        }

        /// <summary>1 = every frame, 2 = every second frame, up to 4. Only used while VSync is on.</summary>
        public int VSyncInterval
        {
            get => vSyncInterval;
            set
            {
                int clamped = Mathf.Clamp(value, 1, 4);
                if (vSyncInterval == clamped) return;
                vSyncInterval = clamped;
                ApplyDisplaySettings();
                VSyncIntervalChanged?.Invoke(vSyncInterval);
            }
        }

        public int TargetFrameRate
        {
            get => targetFrameRate;
            set
            {
                int clamped = Mathf.Max(0, value);
                if (targetFrameRate == clamped) return;
                targetFrameRate = clamped;
                ApplyDisplaySettings();
                TargetFrameRateChanged?.Invoke(targetFrameRate);
            }
        }

        public bool FollowDisplayRefreshRate
        {
            get => followDisplayRefreshRate;
            set
            {
                if (followDisplayRefreshRate == value) return;
                followDisplayRefreshRate = value;
                ApplyDisplaySettings();
            }
        }

        /// <summary>
        /// mainWindowDisplayInfo describes the monitor the WINDOW is on, which is the
        /// only one that answers "I dragged it to the 60 Hz screen". currentResolution
        /// is the fallback, and its refreshRateRatio is exact (a rational, 60000/1001
        /// for 59.94) where the deprecated int rounds inconsistently between drivers.
        /// Rounded here only because Application.targetFrameRate is an int.
        /// </summary>
        private static int ReadRefreshRate()
        {
#if UNITY_2021_2_OR_NEWER
            try
            {
                double windowHz = Screen.mainWindowDisplayInfo.refreshRate.value;
                if (windowHz > 0.0) return Mathf.RoundToInt((float)windowHz);
            }
            catch { /* not supported on this platform; fall through */ }
#endif
            var resolution = Screen.currentResolution;

#if UNITY_2022_2_OR_NEWER
            double value = resolution.refreshRateRatio.value;
            int hz = value > 0.0 ? Mathf.RoundToInt((float)value) : 0;
#else
            int hz = resolution.refreshRate;
#endif
            return hz > 0 ? hz : 60;
        }

        private void ApplyDisplaySettings()
        {
            QualitySettings.vSyncCount = vSync ? Mathf.Clamp(vSyncInterval, 1, 4) : 0;

            // With V-Sync on, targetFrameRate is ignored by Unity anyway; -1 makes that
            // explicit. With it off, 0 means "follow the monitor" rather than "melt the GPU".
            if (vSync)
            {
                Application.targetFrameRate = -1;
                return;
            }

            Application.targetFrameRate =
                targetFrameRate > 0            ? targetFrameRate :
                followDisplayRefreshRate       ? _refreshHz :
                                                 -1;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefreshPoll) return;
            _nextRefreshPoll = Time.unscaledTime + 1f;

            int hz = ReadRefreshRate();
            if (hz == _refreshHz) return;

            _refreshHz = hz;
            ApplyDisplaySettings();
            DisplayRefreshRateChanged?.Invoke(hz);
        }

        // ------------------------------------------------------------------ pad labels

        [Header("Pad Labels")]
        [Tooltip("Draw the clip (or custom) name on every loaded pad. The caption is the " +
                 "pad's own inverse colour, so it stays legible whatever the palette does.")]
        [SerializeField] private bool showPadLabels = true;

        [Tooltip("Show the dismissible \"Feature Notes\" (post-it) hints that point new " +
                 "users at each control. The FeatureNotes button hides them; this field " +
                 "is the master switch, and it is what the session file carries.")]
        [SerializeField] private bool showFeatureNotes = true;

        /// <summary>Raised when the Feature Notes are switched on or off.</summary>
        public event System.Action<bool> ShowFeatureNotesChanged;

        public bool ShowFeatureNotes
        {
            get => showFeatureNotes;
            set
            {
                if (showFeatureNotes == value) return;
                showFeatureNotes = value;
                ShowFeatureNotesChanged?.Invoke(value);
            }
        }
        
        /// <summary>Raised when the pad captions are switched on or off.</summary>
        public event System.Action<bool> ShowPadLabelsChanged;

        public bool ShowPadLabels
        {
            get => showPadLabels;
            set
            {
                if (showPadLabels == value) return;
                showPadLabels = value;
                ShowPadLabelsChanged?.Invoke(value);
            }
        }

        [Header("Pad Palette")]
        [Tooltip("Pad with no clip assigned.")]
        [FormerlySerializedAs("buttonColorOff")]
        public Color32 padOff = new Color32(60, 60, 60, 255);

        [Tooltip("Base \"this pad has content\" colour. Used at half strength for the " +
                 "debug test tones, so they read as placeholder rather than real.")]
        [FormerlySerializedAs("buttonColorOn")]
        public Color32 padOn = new Color32(140, 20, 30, 255);

        [Tooltip("Clip loaded and ready — used unless the pad has a colour of its own.")]
        public Color32 padLoaded = new Color32(30, 170, 90, 255);

        [Tooltip("Pad currently being edited in the side panel.")]
        [FormerlySerializedAs("buttonColorEditing")]
        public Color32 padEditing = new Color32(255, 105, 180, 255);

        [Tooltip("Clip is decoding.")]
        [FormerlySerializedAs("buttonColorLoading")]
        public Color32 padLoading = new Color32(200, 160, 40, 255);

        [Tooltip("Session referenced a file that is no longer on disk, or it would not decode.")]
        [FormerlySerializedAs("buttonColorMissing")]
        public Color32 padMissing = new Color32(90, 40, 140, 255);

        [Tooltip("Momentary flash when a pad is triggered.")]
        [FormerlySerializedAs("buttonColorFlash")]
        public Color32 padFlash = new Color32(255, 255, 255, 255);

        public bool MetronomeEnabled
        {
            get => metronomeEnabled;
            set
            {
                if (metronomeEnabled == value) return;
                metronomeEnabled = value;
                MetronomeEnabledChanged?.Invoke(value);
            }
        }

        [Header("Pad Palette")]
        [Tooltip("The colour roles. One asset, shared by the grid, the legend and the LEDs.")]
        [SerializeField] private PadPaletteAsset palette;

        public PadPaletteAsset Palette => palette;

        /// <summary>Convenience for the two flash call-sites, which want one colour, not a rule.</summary>
        public Color32 FlashColour =>
            palette != null ? palette.Flash : new Color32(255, 255, 255, 255);

        [Range(0.02f, 0.5f)] public float flashSeconds = 0.08f;

        private PadPaletteAsset _boundPalette;

        private void OnEnable()  => BindPalette();
        private void OnDisable() => UnbindPalette();

        private void BindPalette()
        {
            if (_boundPalette == palette) return;

            UnbindPalette();
            if (palette == null) return;

            palette.Changed += RaisePaletteChanged;
            _boundPalette = palette;
        }

        private void UnbindPalette()
        {
            if (_boundPalette == null) return;
            _boundPalette.Changed -= RaisePaletteChanged;
            _boundPalette = null;
        }

        private void RaisePaletteChanged() => PaletteChanged?.Invoke();

        // ------------------------------------------------------------------ folders

        [Header("Folders")]
        [Tooltip("Root folder mirrored by the Library view. Relative paths resolve next to " +
                 "the project in the Editor and next to the .exe in a build — \"Samples\" " +
                 "is the portable choice. \"StreamingAssets/…\" resolves inside the build's " +
                 "data folder.")]
        
        public string libraryRoot = "";

        [Tooltip("Where pad layouts are saved and loaded. Relative paths resolve next to " +
                 "the project in the Editor and next to the .exe in a build.")]
        public string layoutsFolder = "Layouts";

        [Tooltip("Where lightshow (.lightshow.json) files live.")]
        public string animationsFolder = "Animations";

        [Tooltip("Where UI theme assets and exported theme JSON live.")]
        public string stylesFolder = "Styles";

        /// <summary>
        /// Resolves a folder field the same way libraryRoot resolves: absolute paths are
        /// used as given, relative ones hang off the project/exe folder. Unlike
        /// ResolvedLibraryRoot this CREATES the folder, because a Save that fails because
        /// nobody made a directory is a bad first experience.
        /// </summary>
        public string ResolveFolder(string value, bool createIfMissing = true)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string p = value.Trim();

            // Same rule as the library root, so a folder picked inside StreamingAssets
            // resolves there rather than to a brand-new empty "<project>/StreamingAssets".
            p = AbsoluteFrom(p);
            if (p == null) return null;

            try
            {
                if (!Directory.Exists(p))
                {
                    if (!createIfMissing) return null;
                    Directory.CreateDirectory(p);
                }

                return LibraryScanner.NormalizeDir(p);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Manager] Could not resolve folder \"{value}\": {e.Message}", this);
                return null;
            }
        }

        public string ResolvedLayoutsFolder    => ResolveFolder(layoutsFolder);
        public string ResolvedAnimationsFolder => ResolveFolder(animationsFolder);
        public string ResolvedStylesFolder     => ResolveFolder(stylesFolder);

        // ------------------------------------------------------------------ hardware

        [Header("Hardware")]
        [Tooltip("Id of the chosen MIDI device profile (see MidiDeviceCatalog), or empty " +
                 "for auto-detect. At runtime change it through MidiDeviceId, so " +
                 "MidiPadInput hears about it.")]
        [SerializeField] private string midiDeviceId = "";

        /// <summary>Raised when the device choice changes. MidiPadInput reconnects if it needs to.</summary>
        public event System.Action<string> MidiDeviceIdChanged;

        public string MidiDeviceId
        {
            get => midiDeviceId ?? "";
            set
            {
                string id = value?.Trim() ?? "";
                if (id == MidiDeviceId) return;

                midiDeviceId = id;
                MidiDeviceIdChanged?.Invoke(id);
            }
        }

        // ------------------------------------------------------------------ debug

        [Header("Debug")]
        [SerializeField] private bool debug = false;

        /// <summary>Raised whenever debug mode changes, including from the Inspector during Play.</summary>
        public event System.Action<bool> DebugChanged;

        /// <summary>
        /// Raised when any palette colour changes, from the swatch UI or the Inspector.
        /// Views repaint from this; the colour rules themselves stay in PadPalette.
        /// </summary>
        public event System.Action PaletteChanged;

        public bool DebugEnabled
        {
            get => debug;
            set
            {
                if (debug == value) return;
                debug = value;
                DebugChanged?.Invoke(value);
            }
        }

        private void Awake()
        {
            // The serialised value is the truth at startup; AudioListener.volume and
            // QualitySettings.vSyncCount are static and survive play-mode exit, so both
            // must be re-asserted.
            AudioListener.volume = masterVolume;

            _refreshHz = ReadRefreshRate();
            ApplyDisplaySettings();

            // First run, or the user deleted them: the folders exist before any view
            // looks for them. SessionService repeats this once the saved paths are back.
            EnsureStartupFolders();
            
            // A MIDI instrument must keep working when the window is not focused — the
            // Launchpad is often played while another app (a DAW, a browser with the
            // tutorial) has focus. Unity pauses an unfocused standalone player unless
            // this is set, and input, the metronome's beats and the LEDs all run in
            // Update. Set in code so it cannot depend on a Player Settings checkbox that
            // is easy to miss or reset.
            Application.runInBackground = true;
            
#if UNITY_EDITOR
            _lastBpm       = bpm;
            _lastDebug     = debug;
            _lastMetronome = metronomeEnabled;
            _lastMaster    = masterVolume;
            _lastClick     = metronomeClickEnabled;
            _lastVSync     = vSync;
            _lastInterval  = vSyncInterval;
            _lastFps       = targetFrameRate;
            _lastLabels    = showPadLabels;
            _lastFeature   = showFeatureNotes;
#endif
        }

#if UNITY_EDITOR
        private float _lastBpm;
        private bool  _lastDebug;
        private bool  _lastMetronome;
        private float _lastMaster;
        private bool  _lastClick;
        private bool  _lastVSync;
        private int   _lastInterval;
        private int   _lastFps;
        private bool  _lastLabels;
        private bool  _lastFeature;
        
        /// <summary>Lets Inspector edits during Play behave exactly like UI edits.</summary>
        private void OnValidate()
        {
            bpm           = Mathf.Clamp(bpm, MinBpm, MaxBpm);
            masterVolume  = Mathf.Clamp01(masterVolume);
            vSyncInterval = Mathf.Clamp(vSyncInterval, 1, 4);

            if (!Application.isPlaying) return;

            if (!Mathf.Approximately(_lastBpm, bpm))
            {
                _lastBpm = bpm;
                BpmChanged?.Invoke(bpm);
            }

            if (_lastDebug != debug)
            {
                _lastDebug = debug;
                DebugChanged?.Invoke(debug);
            }

            if (_lastMetronome != metronomeEnabled)
            {
                _lastMetronome = metronomeEnabled;
                MetronomeEnabledChanged?.Invoke(metronomeEnabled);
            }

            if (!Mathf.Approximately(_lastMaster, masterVolume))
            {
                _lastMaster = masterVolume;
                AudioListener.volume = masterVolume;
                MasterVolumeChanged?.Invoke(masterVolume);
            }

            if (_lastClick != metronomeClickEnabled)
            {
                _lastClick = metronomeClickEnabled;
                MetronomeClickEnabledChanged?.Invoke(metronomeClickEnabled);
            }

            if (_lastVSync != vSync)
            {
                _lastVSync = vSync;
                ApplyDisplaySettings();
                VSyncChanged?.Invoke(vSync);
            }

            if (_lastInterval != vSyncInterval)
            {
                _lastInterval = vSyncInterval;
                ApplyDisplaySettings();
                VSyncIntervalChanged?.Invoke(vSyncInterval);
            }

            if (_lastFps != targetFrameRate)
            {
                _lastFps = targetFrameRate;
                ApplyDisplaySettings();
                TargetFrameRateChanged?.Invoke(targetFrameRate);
            }

            if (_lastLabels != showPadLabels)
            {
                _lastLabels = showPadLabels;
                ShowPadLabelsChanged?.Invoke(showPadLabels);
            }

            if (_lastFeature != showFeatureNotes)
            {
                _lastFeature = showFeatureNotes;
                ShowFeatureNotesChanged?.Invoke(showFeatureNotes);
            }
            
            if (Application.isPlaying) { BindPalette(); PaletteChanged?.Invoke(); }
        }
#endif

        // NOTE: padNumber is gone. Use SelectionService.Selected.

        /// <summary>
        /// libraryRoot as an absolute, normalised path. Relative values resolve against
        /// the project root in the Editor and the folder containing the .exe in a build.
        /// Returns null when unset or missing on disk. Does NOT create the folder — an
        /// empty library that silently appears is worse than an honest "not set".
        /// </summary>
        public string ResolvedLibraryRoot
        {
            get
            {
                if (string.IsNullOrWhiteSpace(libraryRoot)) return null;

                string p = libraryRoot.Trim();

                p = AbsoluteFrom(p);
                if (p == null) return null;

                return Directory.Exists(p) ? LibraryScanner.NormalizeDir(p) : null;
            }
        }

        /// <summary>
        /// Makes the folders a first run expects actually exist: the layouts folder
        /// always, the library root only when it is RELATIVE ("Samples" — ours to make).
        /// An absolute library root the user chose elsewhere is left alone when missing:
        /// an empty stand-in would hide the unplugged drive or renamed folder that the
        /// Library's "not found" message exists to report.
        ///
        /// Called from Awake (scene defaults) and again by SessionService after it has
        /// restored the paths, since the session may name different folders.
        /// </summary>
        public void EnsureStartupFolders()
        {
            ResolveFolder(layoutsFolder);                  // creates; logs on failure

            if (string.IsNullOrWhiteSpace(libraryRoot)) return;

            string lib = libraryRoot.Trim();
            if (Path.IsPathRooted(lib)) return;

            ResolveFolder(lib);
        }
        
        /// <summary>
        /// Relative → absolute, the ONE rule every folder field uses:
        ///   "StreamingAssets/…"  → the real StreamingAssets folder (editor and build differ)
        ///   anything else        → next to the project in the editor, next to the .exe
        ///                          in a build
        /// Absolute paths pass through. Null only when the base folder cannot be found.
        /// </summary>
        private static string AbsoluteFrom(string p)
        {
            if (Path.IsPathRooted(p)) return p;

            const string Token = "StreamingAssets";
            string norm = p.Replace('\\', '/');

            if (norm.Equals(Token, System.StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(Token + "/", System.StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Application.streamingAssetsPath,
                                    norm.Substring(Token.Length).TrimStart('/'));

            var baseDir = Directory.GetParent(Application.dataPath);
            return baseDir == null ? null : Path.Combine(baseDir.FullName, p);
        }

        /// <summary>
        /// The reverse of AbsoluteFrom, for storing a folder the user PICKED. Inside
        /// StreamingAssets → "StreamingAssets/…"; inside the project/exe folder → relative
        /// ("Samples"); anywhere else stays absolute. Stored absolute, a choice made in
        /// the editor points at the developer's desktop in every build, and moving the
        /// app folder silently breaks it. StreamingAssets is checked FIRST because it
        /// sits inside the project folder in the editor, and "Assets/StreamingAssets/…"
        /// would not exist in a build.
        /// </summary>
        public static string ToPortablePath(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;

            string norm = LibraryScanner.NormalizeDir(dir);

            string streaming = LibraryScanner.NormalizeDir(Application.streamingAssetsPath);
            if (norm.Equals(streaming, System.StringComparison.OrdinalIgnoreCase))
                return "StreamingAssets";
            if (norm.StartsWith(streaming + "/", System.StringComparison.OrdinalIgnoreCase))
                return "StreamingAssets/" + norm.Substring(streaming.Length + 1);

            var baseDir = Directory.GetParent(Application.dataPath);
            if (baseDir == null) return norm;

            string root = LibraryScanner.NormalizeDir(baseDir.FullName);
            return norm.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(root.Length + 1)
                : norm;
        }
        
        /// <summary>
        /// Drives AudioListener.volume, so it is genuinely global — nothing has to
        /// subscribe to be affected by it.
        /// </summary>
        public float MasterVolume
        {
            get => masterVolume;
            set
            {
                float clamped = Mathf.Clamp01(value);
                if (Mathf.Approximately(masterVolume, clamped)) return;

                masterVolume = clamped;
                AudioListener.volume = masterVolume;
                MasterVolumeChanged?.Invoke(masterVolume);
            }
        }
    }
}