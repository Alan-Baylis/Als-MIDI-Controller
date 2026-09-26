using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Falling-code screensaver for the 8x8 grid. Drops start at the top row and walk
    /// down, leaving a decaying trail behind them, on screen and on the hardware at
    /// once through PadOverlay.
    ///
    /// It writes to the OVERLAY, never to PadModel, so it cannot touch a session: when
    /// it stops, one ClearAll hands all 64 pads back to the palette exactly as they
    /// were. Nothing is saved or restored, because nothing was changed.
    ///
    /// Two things now hold it back beyond plain input:
    ///   • sounding voices — a running loop is the app being USED, and dropping the
    ///     grid into a screensaver mid-performance is indefensible;
    ///   • an already-active overlay — the lightshow player owns the same layer, and
    ///     two animations writing one overlay is a flicker, not a feature.
    /// </summary>
    [DisallowMultipleComponent]
    public class ScreensaverMatrix : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private PadOverlay overlay;
        [SerializeField] private PadController controller;
        [SerializeField] private SelectionService selection;

        [Header("Idle")]
        [Tooltip("Start after this long with no mouse, no key and no pad. 0 = never start " +
                 "by itself (use the context menu or call StartShow).")]
        [SerializeField, Min(0f)] private float idleSeconds = 120f;

        [Tooltip("Master switch.")]
        [SerializeField] private bool enableScreensaver = true;

        [Tooltip("Any activity stops it instantly. Off = it keeps running until something " +
                 "calls StopShow — useful for a demo on a stand.")]
        [SerializeField] private bool wakeOnActivity = true;

        [Tooltip("Audio counts as activity: while ANY pad is sounding the idle clock is " +
                 "held at zero, so the countdown starts from the last sound, not the " +
                 "last click. A one-shot that finished two minutes ago will not hold it.")]
        [SerializeField] private bool playbackCountsAsActivity = true;

        [Tooltip("Pads waiting on the metronome also count. A queued start is a pad that " +
                 "is ABOUT to sound.")]
        [SerializeField] private bool queuedCountsAsActivity = true;

        [Tooltip("Stop a RUNNING show the moment something starts playing. Off = the rain " +
                 "keeps falling over the top of the music until you touch something.")]
        [SerializeField] private bool wakeOnPlayback = true;

        [Header("Colours")]
        [Tooltip("The unlit grid behind the rain.")]
        [SerializeField] private Color32 backgroundColour = new Color32(0, 12, 4, 255);

        [Tooltip("The leading pad of a drop.")]
        [SerializeField] private Color32 headColour = new Color32(200, 255, 210, 255);

        [Tooltip("What the trail fades through on its way back to the background.")]
        [SerializeField] private Color32 trailColour = new Color32(0, 200, 70, 255);

        [Header("Motion")]
        [Tooltip("Rows per second. 8 rows in the grid, so 4 is a two-second fall.")]
        [SerializeField, Min(0.1f)] private float rowsPerSecond = 5f;

        [Tooltip("Trail brightness kept per step. Higher = longer tail.")]
        [SerializeField, Range(0.5f, 0.99f)] private float trailPersistence = 0.78f;

        [Tooltip("Chance per step that an idle column starts a new drop.")]
        [SerializeField, Range(0.01f, 1f)] private float spawnChance = 0.12f;

        [Tooltip("Simulation steps per second. This is also the LED update rate, so keep " +
                 "it modest — the port is the bottleneck, not the GPU.")]
        [SerializeField, Range(5f, 60f)] private float stepsPerSecond = 20f;

        [Tooltip("Optional. The library preview voice is not a pad voice, so it has to " +
                 "be asked separately.")]
        [SerializeField] private LibraryView library;
        
        public bool Running { get; private set; }

        /// <summary>Seconds until it would start, or -1 when something is holding it off.</summary>
        public float SecondsUntilStart
        {
            get
            {
                if (!enableScreensaver || idleSeconds <= 0f || Running) return -1f;
                if (Busy) return -1f;

                return Mathf.Max(0f, idleSeconds - (Time.unscaledTime - _lastActivity));
            }
        }

        /// <summary>Is anything going on that should keep the screensaver away?</summary>
        public bool Busy
        {
            get
            {
                if (controller != null)
                {
                    if (playbackCountsAsActivity && controller.AnyVoicePlaying) return true;
                    if (queuedCountsAsActivity   && controller.AnyQueued)       return true;
                }

                if (playbackCountsAsActivity && library != null && library.IsPreviewing)
                    return true;

                // A modal question counts as the app being used just as much as an open file
                // browser does — the screensaver starting over a "save first?" dialog is the
                // sort of thing that makes a user answer it wrong.
                if (ConfirmDialogView.Instance != null && ConfirmDialogView.Instance.IsOpen)
                    return true;
                
                // The picker previews live on the pads, so the screensaver would paint
                // over the very colours the user is judging.
                if (ColourPickerView.Instance != null && ColourPickerView.Instance.IsOpen)
                    return true;

                // A modal dialog on screen is the user mid-task, however still the mouse is.
                if (FileBrowserView.Instance != null && FileBrowserView.Instance.IsOpen)
                    return true;

                // Someone else already owns the layer — a lightshow, most likely.
                return !Running && overlay != null && overlay.Active;
            }
        }

        private const int Size = 8;

        private readonly float[] _brightness = new float[Size * Size];   // [rowFromTop*8 + col]
        private readonly float[] _position   = new float[Size];          // rows from the top
        private readonly float[] _lastRow    = new float[Size];
        private readonly bool[]  _active     = new bool[Size];

        private Color32[] _frame;

        private float _lastActivity;
        private float _stepAccumulator;
        private Vector2 _lastMouse;

        private void Awake()
        {
            if (overlay    == null) overlay    = FindAnyObjectByType<PadOverlay>();
            if (controller == null) controller = FindAnyObjectByType<PadController>();
            if (selection  == null) selection  = FindAnyObjectByType<SelectionService>();

            if (overlay == null)
                Debug.LogWarning($"{nameof(ScreensaverMatrix)} found no {nameof(PadOverlay)}; " +
                                 "there is nothing to draw on. Add one to a scene-root object.",
                                 this);

            // Include inactive: the Pad page is hidden most of the time and a preview
            // started before switching away is exactly what must hold the screensaver off.
            if (library == null)
                library = FindAnyObjectByType<LibraryView>(FindObjectsInactive.Include);
            
            _frame        = new Color32[NoteGrid.ArraySize];
            _lastActivity = Time.unscaledTime;
        }

        private void OnEnable()
        {
            // A pad hit is activity even when it came from the hardware, where there is
            // no mouse and no keyboard to notice.
            if (controller != null)
            {
                controller.PadTriggered += OnPadTriggered;
                controller.PadPulsed    += OnPadPulsed;
            }

            if (selection != null) selection.SelectionChanged += OnSelectionChanged;

            _lastActivity = Time.unscaledTime;
        }

        private void OnDisable()
        {
            if (controller != null)
            {
                controller.PadTriggered -= OnPadTriggered;
                controller.PadPulsed    -= OnPadPulsed;
            }

            if (selection != null) selection.SelectionChanged -= OnSelectionChanged;

            StopShow();
        }

        private void OnPadTriggered(int note, float velocity) => Notice();
        private void OnSelectionChanged(int? previous, int? current) => Notice();

        /// <summary>
        /// A loop wrapping is not an input event, so without this a four-bar loop left
        /// running would be invisible to the idle clock after its first press.
        /// </summary>
        private void OnPadPulsed(int note)
        {
            _lastActivity = Time.unscaledTime;
            if (Running && wakeOnPlayback) StopShow();
        }

        /// <summary>Something happened. Restart the idle clock and wake up.</summary>
        public void Notice()
        {
            _lastActivity = Time.unscaledTime;
            if (Running && wakeOnActivity) StopShow();
        }

        // ------------------------------------------------------------- driving

        private void Update()
        {
            if (!enableScreensaver) return;

            if (DetectInput()) Notice();

            if (!Running)
            {
                // Held off rather than cancelled: the clock is pinned to NOW for as long
                // as something is sounding, so the countdown restarts from silence.
                if (Busy)
                {
                    _lastActivity = Time.unscaledTime;
                    return;
                }

                if (idleSeconds > 0f && Time.unscaledTime - _lastActivity >= idleSeconds)
                    StartShow();

                return;
            }

            if (wakeOnPlayback && controller != null && controller.AnyVoicePlaying)
            {
                StopShow();
                return;
            }

            float stepSeconds = 1f / Mathf.Max(1f, stepsPerSecond);
            _stepAccumulator += Time.unscaledDeltaTime;

            // Clamped rather than looped: a long frame must not fire twenty steps and
            // shoot every drop off the bottom at once.
            if (_stepAccumulator < stepSeconds) return;

            _stepAccumulator = 0f;
            Step(stepSeconds);
            Paint();
        }

        [ContextMenu("Start Screensaver")]
        public void StartShow()
        {
            if (Running || overlay == null) return;

            // Refuse rather than fight: whoever has the overlay keeps it.
            if (overlay.Active)
            {
                _lastActivity = Time.unscaledTime;
                return;
            }

            System.Array.Clear(_brightness, 0, _brightness.Length);

            for (int col = 0; col < Size; col++)
            {
                _active[col]   = false;
                _position[col] = 0f;
                _lastRow[col]  = -1f;
            }

            Running          = true;
            _stepAccumulator = 0f;

            Paint();
        }

        [ContextMenu("Stop Screensaver")]
        public void StopShow()
        {
            if (!Running) return;

            Running = false;
            overlay?.ClearAll();          // one event, whole grid back to the palette
        }

        // ---------------------------------------------------------- simulation

        private void Step(float stepSeconds)
        {
            for (int i = 0; i < _brightness.Length; i++)
            {
                _brightness[i] *= trailPersistence;
                if (_brightness[i] < 0.02f) _brightness[i] = 0f;
            }

            float advance = rowsPerSecond * stepSeconds;

            for (int col = 0; col < Size; col++)
            {
                if (!_active[col])
                {
                    if (Random.value < spawnChance)
                    {
                        _active[col]   = true;
                        _position[col] = 0f;
                        _lastRow[col]  = -1f;
                    }
                    continue;
                }

                float previous = _position[col];
                _position[col] += advance;

                // Light every row the head crossed, not just the one it landed on: at a
                // high speed a drop would otherwise leave gaps in its own trail.
                int from = Mathf.Max(0, Mathf.FloorToInt(previous));
                int to   = Mathf.FloorToInt(_position[col]);

                for (int row = from; row <= to && row < Size; row++)
                    if (row >= 0) _brightness[row * Size + col] = 1f;

                if (_position[col] >= Size) _active[col] = false;
            }
        }

        private void Paint()
        {
            if (overlay == null) return;

            foreach (int note in NoteGrid.All)
            {
                int col        = NoteGrid.Col(note) - 1;
                int rowFromTop = Size - NoteGrid.Row(note);   // grid row 8 is the top

                float b = _brightness[rowFromTop * Size + col];

                _frame[note] = b >= 0.999f
                    ? headColour
                    : Color32.Lerp(backgroundColour, trailColour, b);
            }

            overlay.Apply(_frame);
        }

        // -------------------------------------------------------------- input

        /// <summary>
        /// Deliberately package-agnostic. The project is on the Input System package
        /// today, but a build switched back to the legacy manager should not silently
        /// lose its wake-up.
        /// </summary>
        private bool DetectInput()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;

            if (mouse != null)
            {
                Vector2 position = mouse.position.ReadValue();

                if ((position - _lastMouse).sqrMagnitude > 4f)
                {
                    _lastMouse = position;
                    return true;
                }

                if (mouse.leftButton.isPressed || mouse.rightButton.isPressed ||
                    mouse.middleButton.isPressed) return true;

                if (mouse.scroll.ReadValue().sqrMagnitude > 0.01f) return true;
            }

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.anyKey.isPressed) return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            Vector2 position = Input.mousePosition;

            if ((position - _lastMouse).sqrMagnitude > 4f)
            {
                _lastMouse = position;
                return true;
            }

            if (Input.anyKey) return true;
            if (Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f) return true;
#endif
            return false;
        }
    }
}