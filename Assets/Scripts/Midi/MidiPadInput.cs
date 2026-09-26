using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using LaunchpadStudio;
using UnityEngine;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

/// <summary>
/// Owns the one and only connection to the Launchpad: the input port that pads are
/// played from, and the output port every LED in the project is lit through.
///
/// Windows MIDI ports are exclusive, process-wide, so this is the ONLY class that
/// opens one. Everything else (PadLedView, MetronomeLedView) asks it to send.
///
/// The device is treated as a PERIPHERAL, never as part of the session. Unplugging it
/// closes the port and nothing else: clips keep playing, pads keep their content, the
/// selection stands. Plugging it back in re-opens, re-enters Programmer mode, and the
/// views repaint themselves from the model — no state is rebuilt, because none was lost.
///
/// Connection is a state machine driven from two sources:
///   1. DevicesWatcher (DryWetMIDI) — the OS device list, event-driven and cheap.
///   2. A slow rescan, used ONLY when the watcher is unavailable on this platform.
/// Both run on a background/callback thread, so both only ever enqueue. Update does
/// the work.
/// </summary>
public class MidiPadInput : MonoBehaviour
{
    public enum LinkState { Disconnected, Connecting, Connected }

    /// <summary>Raised on the main thread when the hardware appears or disappears.</summary>
    public event Action<bool> ConnectionChanged;

    // ------------------------------------------------------------- inspector

    [Header("References")]
    [SerializeField] private PadController controller;

    [Tooltip("Optional. Connection notices go to the ticker as well as the console.")]
    [SerializeField] private NotificationService notifications;

    [Header("Device")]
    [Tooltip("Optional. Found automatically. Holds the chosen device (MidiDeviceId). " +
             "Port names now come from the device profile, not from here.")]
    [SerializeField] private Manager manager;

    [Tooltip("Match any port whose name CONTAINS the text above. Windows decorates " +
             "port names (\"MIDIIN2 (LPProMK3 MIDI)\"), so exact matching is brittle.")]
    [SerializeField] private bool matchByContains = true;

    [Header("Hot-plug")]
    [Tooltip("Watch the OS device list so the Launchpad can be plugged in at any time.")]
    [SerializeField] private bool watchForDevices = true;

    [Tooltip("Windows announces the device a moment before the port can be opened. " +
             "This is that moment.")]
    [SerializeField, Min(0f)] private float openDelaySeconds = 0.5f;

    [SerializeField, Min(1)]     private int   openAttempts     = 5;
    [SerializeField, Min(0.05f)] private float openRetrySeconds = 0.4f;
    
    [Tooltip("Consecutive failed sends before the link is treated as lost. A yanked " +
             "cable usually surfaces as a send error before the watcher catches up.")]
    [SerializeField, Min(1)] private int sendFailuresBeforeDrop = 3;

    [Tooltip("Post connect/disconnect notices to the ticker.")]
    [SerializeField] private bool announceConnection = true;

    [Header("Device Mode")]
    [SerializeField] private bool enterProgrammerModeOnStart = true;
    [SerializeField] private bool restoreLiveModeOnExit = true;

    [Tooltip("Frames to wait after opening the port before sending any SysEx.")]
    [SerializeField, Min(0)] private int modeSwitchDelayFrames = 2;

    [SerializeField, Min(1)] private int modeSwitchAttempts = 3;

    [Header("LED Output")]
    [Tooltip("Master switch for all outgoing LED updates.")]
    [SerializeField] private bool ledOutputEnabled = true;

    [Tooltip("Turn off every LED this component lit before restoring Live mode.")]
    [SerializeField] private bool clearLedsOnExit = true;

    [Header("Velocity")]
    [Tooltip("Minimum playback volume for the lightest hit.")]
    [SerializeField, Range(0f, 1f)] private float velocityFloor = 0.25f;

    [Tooltip("1 = linear. Below 1 boosts soft hits.")]
    [SerializeField, Range(0.2f, 2f)] private float velocityCurve = 0.5f;

    [Header("Logging")]
    [SerializeField] private bool logIncoming = false;

    [Tooltip("Backstop poll of the device list. Runs EVEN when the OS watcher is attached, " +
             "because a watcher that never fires looks exactly like no device. 0 = never poll.")]
    [SerializeField, Min(0f)] private float rescanSeconds = 2f;

    [Tooltip("Rescan interval is multiplied by this while the OS watcher is attached.")]
    [SerializeField, Min(1f)] private float watchedRescanFactor = 2f;

    [Tooltip("Seconds between attempts to open the half of the link that failed, or to " +
             "re-try Programmer mode, while the other half is open.")]
    [SerializeField, Min(0.25f)] private float repairSeconds = 2f;

    [Tooltip("Log every connection state change, device event and port enumeration.")]
    [SerializeField] private bool logConnection = true;

    private float _nextRepair;
    private Coroutine _repairRoutine;
    private bool _watcherEverFired;
    
    // ---------------------------------------------------------------- state

    /// <summary>
    /// The device on the other end. Chosen on every FRESH connect (both halves closed),
    /// never while repairing a half-open link. Kept after a disconnect so the UI can
    /// still name what it is waiting for.
    /// </summary>
    private PadDeviceProfile _profile;

    /// <summary>Raised on the main thread when a connect settles on a different device.</summary>
    public event Action<PadDeviceProfile> ProfileChanged;

    public PadDeviceProfile Profile => _profile;

    private string ProfileName => _profile != null ? _profile.DisplayName : "Launchpad";
    
    private InputDevice  _input;
    private OutputDevice _output;

    /// <summary>Exact names of the ports we actually opened, for matching removals.</summary>
    private string _openInputName;
    private string _openOutputName;

    private bool _programmerModeReady;
    private LinkState _state = LinkState.Disconnected;
    private Coroutine _connectRoutine;
    private int _sendFailures;

    private bool _watcherAttached;
    private float _nextRescan;

    /// <summary>(added, deviceName). Filled on an OS thread, drained in Update.</summary>
    private readonly ConcurrentQueue<(bool added, string name)> _deviceEvents
        = new ConcurrentQueue<(bool, string)>();

    /// <summary>RAW device numbers, mapped through the profile on the main thread.</summary>
    private readonly ConcurrentQueue<(bool isCc, int number, int velocity)> _queue
        = new ConcurrentQueue<(bool, int, int)>();

    /// <summary>Every index we've lit, so a clean exit can put them all back to black.</summary>
    private readonly HashSet<int> _litLeds = new HashSet<int>();

    private readonly List<LedWrite> _writeScratch = new List<LedWrite>(8);
    
    // ------------------------------------------------------------ public API

    /// <summary>True once the port is open and Programmer mode has been accepted.</summary>
    public bool LedOutputReady =>
        _output != null && _profile != null && _programmerModeReady && ledOutputEnabled;

    public bool Connected => _state == LinkState.Connected;

    public LinkState State => _state;

    /// <summary>One-line summary for a debug overlay or the console.</summary>
    public string DescribeState() =>
        $"[MIDI] {_state}" +
        (_profile != null ? $" [{_profile.DisplayName}]" : "") +
        (_openInputName  != null ? $", in \"{_openInputName}\""   : ", no input") +
        (_openOutputName != null ? $", out \"{_openOutputName}\"" : ", no output") +
        (_programmerModeReady ? ", ready" : "");

    // ------------------------------------------------------------- lifecycle

    private void OnEnable()
    {
        if (controller    == null) controller    = FindAnyObjectByType<PadController>();
        if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();
        if (manager       == null) manager       = FindAnyObjectByType<Manager>();

        if (manager != null)
        {
            manager.MidiDeviceIdChanged += OnDeviceChoiceChanged;
            WarnIfUnknown(manager.MidiDeviceId);
        }

        AttachWatcher();

        // Try immediately — the usual case is that the device was already plugged in
        // when the app started. No delay, no error if it isn't there.
        BeginConnect(0f, announce: false);
    }

    private void OnDisable()
    {
        if (manager != null) manager.MidiDeviceIdChanged -= OnDeviceChoiceChanged;

        DetachWatcher();

        if (_connectRoutine != null) { StopCoroutine(_connectRoutine); _connectRoutine = null; }

        CloseLink(graceful: true, reason: null);
    }

    private void Update()
    {
        PumpDeviceEvents();
        PumpRescan();
        PumpRepair();
        PumpInput();
    }

    // -------------------------------------------------------- device watcher

    [ContextMenu("Log MIDI Ports")]
    public void LogPorts()
    {
        var sb = new System.Text.StringBuilder(DescribeState()).AppendLine();
        sb.Append("watcher attached: ").Append(_watcherAttached)
            .Append(", ever fired: ").Append(_watcherEverFired).AppendLine();

        Append(sb, "inputs",  true);
        Append(sb, "outputs", false);

        Debug.Log(sb.ToString(), this);

        void Append(System.Text.StringBuilder b, string title, bool input)
        {
            b.Append(title).AppendLine(":");
            try
            {
                var names = new List<string>();
                if (input) foreach (var d in InputDevice.GetAll())  { names.Add(d.Name); try { d.Dispose(); } catch { } }
                else       foreach (var d in OutputDevice.GetAll()) { names.Add(d.Name); try { d.Dispose(); } catch { } }

                if (names.Count == 0) b.AppendLine("  (none)");

                foreach (var n in names)
                {
                    var owner = OwnerOf(n);
                    b.Append("  \"").Append(n).Append('"')
                        .Append(owner != null ? $"   ← {owner.DisplayName}" : "").AppendLine();
                }
            }
            catch (Exception e) { b.Append("  enumeration failed: ").Append(e.Message).AppendLine(); }
        }
    }
    
    /// <summary>
    /// A link with one port open is not a working link. Keep trying for the missing half,
    /// and for a Programmer-mode handshake that never landed.
    /// </summary>
    private void PumpRepair()
    {
        if (_state != LinkState.Connected) return;
        if (_input != null && _output != null && _programmerModeReady) return;
        if (Time.unscaledTime < _nextRepair) return;

        _nextRepair = Time.unscaledTime + repairSeconds;

        bool hadOutput = _output != null;
        TryOpenPorts();                       // only fills whichever half is null

        if (_output == null) return;

        if (!hadOutput && logConnection)
            Debug.Log("[MIDI] Output port appeared after the input; completing the handshake.", this);

        if (!_programmerModeReady && enterProgrammerModeOnStart && _repairRoutine == null)
            _repairRoutine = StartCoroutine(RepairProgrammerMode());
    }

    private IEnumerator RepairProgrammerMode()
    {
        yield return EnterProgrammerModeDeferred();
        _repairRoutine = null;

        if (!_programmerModeReady) yield break;

        Notify($"{ProfileName} ready.", NoticeLevel.Success);
        ConnectionChanged?.Invoke(true);       // views repaint off the false→true edge
    }

    /// <summary>
    /// DevicesWatcher is a singleton over the OS device list. It is not supported on
    /// every platform, and asking for Instance is what throws, so the attach itself
    /// has to be guarded rather than the subscription.
    /// </summary>
    private void AttachWatcher()
    {
        if (!watchForDevices || _watcherAttached) return;

        try
        {
            DevicesWatcher.Instance.DeviceAdded   += OnDeviceAdded;
            DevicesWatcher.Instance.DeviceRemoved += OnDeviceRemoved;
            _watcherAttached = true;
        }
        catch (Exception e)
        {
            // Not fatal: the rescan below covers it, just less promptly.
            Debug.LogWarning($"[MIDI] Device watcher unavailable ({e.GetType().Name}: " +
                             $"{e.Message}). Falling back to a {rescanSeconds:F1}s rescan.", this);
            _watcherAttached = false;
        }
    }

    private void DetachWatcher()
    {
        if (!_watcherAttached) return;

        try
        {
            DevicesWatcher.Instance.DeviceAdded   -= OnDeviceAdded;
            DevicesWatcher.Instance.DeviceRemoved -= OnDeviceRemoved;
        }
        catch { /* shutting down; nothing useful to do */ }

        _watcherAttached = false;
    }

    // OS callback thread. Enqueue ONLY — no Unity API, no logging with a context object.
    private void OnDeviceAdded(object sender, DeviceAddedRemovedEventArgs e) =>
        _deviceEvents.Enqueue((true, e.Device?.Name));

    private void OnDeviceRemoved(object sender, DeviceAddedRemovedEventArgs e) =>
        _deviceEvents.Enqueue((false, e.Device?.Name));

    private void PumpDeviceEvents()
    {
        while (_deviceEvents.TryDequeue(out var change))
        {
            _watcherEverFired = true;

            if (logConnection)
                Debug.Log($"[MIDI] Watcher: {(change.added ? "added" : "removed")} " +
                          $"\"{change.name}\"", this);
            
            if (string.IsNullOrEmpty(change.name)) continue;

            if (change.added)
            {
                if (_state != LinkState.Disconnected) continue;
                if (!WantsPort(change.name)) continue;

                // The settle delay is per-appearance, not per-attempt: the port is
                // enumerated before the driver will hand it over.
                BeginConnect(openDelaySeconds, announce: true);
                continue;
            }

            if (_state == LinkState.Disconnected) continue;
            if (!IsOpenPort(change.name) && !WantsPort(change.name)) continue;

            HandleLinkLost($"\"{change.name}\" was disconnected");
        }
    }
    
    /// <summary>
    /// Runs regardless of the watcher. A watcher that attaches but stays silent is the
    /// failure mode this exists for: it is invisible, and it used to disable the fallback.
    /// </summary>
    private void PumpRescan()
    {
        if (rescanSeconds <= 0f) return;
        if (_state != LinkState.Disconnected) return;
        if (Time.unscaledTime < _nextRescan) return;

        _nextRescan = Time.unscaledTime +
                      (_watcherAttached ? rescanSeconds * watchedRescanFactor : rescanSeconds);

        if (ResolveProfile(ListPorts(input: true), ListPorts(input: false)) == null) return;

        if (_watcherAttached && !_watcherEverFired)
            Debug.LogWarning("[MIDI] The backstop rescan found the Launchpad, but the OS " +
                             "device watcher has never fired. It is attached and silent on " +
                             "this machine — the rescan is doing all the work.", this);

        BeginConnect(0f, announce: true);
    }

    // ------------------------------------------------------------ connecting

    private void BeginConnect(float delay, bool announce)
    {
        if (_state != LinkState.Disconnected) return;
        if (!isActiveAndEnabled) return;

        _state = LinkState.Connecting;
        _connectRoutine = StartCoroutine(ConnectRoutine(delay, announce));
    }

    private IEnumerator ConnectRoutine(float delay, bool announce)
    {
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);

        for (int attempt = 1; attempt <= openAttempts; attempt++)
        {
            if (TryOpenPorts()) break;

            if (attempt == openAttempts)
            {
                // Not an error. "No Launchpad plugged in" is a legitimate way to run
                // the app — the mouse plays the grid perfectly well.
                _state = LinkState.Disconnected;
                _connectRoutine = null;

                if (announce)
                    Notify($"No {LookingFor()} found. Plug it in at any time — it will " +
                           "connect itself.", NoticeLevel.Info);
                else
                    Debug.Log($"[MIDI] No {LookingFor()} yet. Watching for it.", this);

                yield break;
            }

            yield return new WaitForSecondsRealtime(openRetrySeconds);
        }

        _state        = LinkState.Connected;
        _sendFailures = 0;

        if (enterProgrammerModeOnStart && _output != null)
            yield return EnterProgrammerModeDeferred();

        _connectRoutine = null;

        if (announceConnection)
            Notify($"Connected: {ProfileName} on \"{_openInputName ?? _openOutputName}\"" +
                   (_programmerModeReady ? "." : " — it refused the mode switch, so its lights will stay off."),
                _programmerModeReady ? NoticeLevel.Success : NoticeLevel.Warning);

        // PadLedView repaints the whole grid on the LedOutputReady false→true edge,
        // so the hardware catches up with the model without anyone rebuilding state.
        ConnectionChanged?.Invoke(true);
    }

    /// <summary>
    /// Input and output are opened independently: a Launchpad with only one of the two
    /// available is still worth having, and reporting which half failed is more useful
    /// than an all-or-nothing error.
    /// </summary>
    private bool TryOpenPorts()
    {
        var inputs  = _input  == null ? ListPorts(input: true)  : null;
        var outputs = _output == null ? ListPorts(input: false) : null;

        // Both halves closed = a fresh connect, which is the ONLY place the device is
        // chosen. With one half open we are repairing, and the profile must not change
        // under a port that is already talking to it.
        if (_input == null && _output == null)
        {
            var found = ResolveProfile(inputs, outputs);
            if (found == null) return false;

            SetProfile(found);
        }

        if (_profile == null) return false;

        if (_input == null)
        {
            string name = PickPort(inputs, _profile);

            if (name != null)
            {
                try
                {
                    _input = InputDevice.GetByName(name);
                    _input.EventReceived += OnEventReceived;
                    _input.StartEventsListening();
                    _openInputName = name;

                    Debug.Log($"[MIDI] Input open: \"{name}\"", this);
                }
                catch (Exception e)
                {
                    DisposeInput();
                    Debug.LogWarning($"[MIDI] Could not open input \"{name}\": {e.Message}", this);
                }
            }
        }

        if (_output == null)
        {
            string name = PickPort(outputs, _profile);

            if (name != null)
            {
                try
                {
                    _output = OutputDevice.GetByName(name);
                    _output.PrepareForEventsSending();
                    _openOutputName = name;

                    Debug.Log($"[MIDI] Output open: \"{name}\"", this);
                }
                catch (Exception e)
                {
                    DisposeOutput();

                    // The single most common cause, by a mile, is another application
                    // holding the exclusive port.
                    Debug.LogWarning($"[MIDI] Could not open output \"{name}\": {e.Message}\n" +
                                     "Close Novation Components, any DAW, and make sure " +
                                     "MidiDeviceProbe is disabled.", this);
                }
            }
        }

        return _input != null || _output != null;
    }

    // -------------------------------------------------------------- dropping

    /// <summary>
    /// The device went away while we were using it. Closes the ports WITHOUT trying to
    /// restore Live mode — there is nothing on the other end to receive it, and the
    /// attempt would just throw. Session state is deliberately untouched.
    /// </summary>
    private void HandleLinkLost(string reason)
    {
        if (_state == LinkState.Disconnected) return;

        CloseLink(graceful: false, reason: reason);

        if (announceConnection)
            Notify($"{ProfileName} disconnected — {reason}. Playback and pad contents are " +
                   "unaffected; it will reconnect itself when plugged back in.",
                   NoticeLevel.Warning);

        ConnectionChanged?.Invoke(false);
    }

    /// <summary>
    /// graceful = we are shutting down and the device is still present, so it gets its
    /// LEDs cleared and Live mode restored. Otherwise the hardware is already gone.
    /// </summary>
    private void CloseLink(bool graceful, string reason)
    {
        if (graceful && _output != null)
        {
            if (clearLedsOnExit) ClearAllLitLeds();

            if (restoreLiveModeOnExit)
            {
                EnterLiveMode();
                // Give the driver time to flush; a Launchpad left in Programmer mode
                // looks broken to every other application.
                Thread.Sleep(60);
            }
        }

        DisposeInput();
        DisposeOutput();

        _programmerModeReady = false;
        _sendFailures        = 0;
        _litLeds.Clear();
        _state = LinkState.Disconnected;

        // Stale presses must not fire after the device has gone: a note queued a
        // millisecond before the cable came out would otherwise trigger a pad.
        while (_queue.TryDequeue(out _)) { }

        if (reason != null) Debug.LogWarning($"[MIDI] Link closed — {reason}.", this);
    }

    private void DisposeInput()
    {
        if (_input == null) { _openInputName = null; return; }

        try { _input.EventReceived -= OnEventReceived; } catch { }
        try { _input.Dispose(); } catch { }

        _input = null;
        _openInputName = null;
    }

    private void DisposeOutput()
    {
        if (_output == null) { _openOutputName = null; return; }

        try { _output.Dispose(); } catch { }

        _output = null;
        _openOutputName = null;
    }

    [ContextMenu("Reconnect Now")]
    public void Reconnect() => Restart("manual reconnect");

    /// <summary>
    /// Close gracefully (the OLD profile clears its lamps and hands the device back),
    /// then connect afresh, which re-chooses the device. Running coroutines are stopped
    /// first — the old Reconnect left a connect routine running against a link that
    /// CloseLink had just torn down.
    /// </summary>
    private void Restart(string reason)
    {
        if (_connectRoutine != null) { StopCoroutine(_connectRoutine); _connectRoutine = null; }
        if (_repairRoutine  != null) { StopCoroutine(_repairRoutine);  _repairRoutine  = null; }

        bool wasConnected = _state == LinkState.Connected;

        CloseLink(graceful: true, reason: reason);

        // Views repaint off the false→true edge, so they must see the false.
        if (wasConnected) ConnectionChanged?.Invoke(false);

        BeginConnect(0f, announce: true);
    }

    private void OnDeviceChoiceChanged(string id)
    {
        WarnIfUnknown(id);

        // Already talking to an acceptable device: leave the link alone. This is the
        // usual startup case — auto-detect found the Pro MK3, then the session restored
        // "novation-pro-mk3" — and a reconnect there would blank the grid for nothing.
        var chosen = MidiDeviceCatalog.Find(id);

        if (_state != LinkState.Disconnected && _profile != null &&
            (chosen == null || ReferenceEquals(chosen, _profile)))
            return;

        Restart($"device changed to {(chosen != null ? chosen.DisplayName : "auto-detect")}");
    }

    // --------------------------------------------------------- port matching
    // Port names belong to PROFILES now. With a device chosen, only its names count;
    // on auto-detect the catalog is walked in order, most specific first.

    private IEnumerable<PadDeviceProfile> Candidates()
    {
        var chosen = MidiDeviceCatalog.Find(manager != null ? manager.MidiDeviceId : null);

        if (chosen != null)
        {
            yield return chosen;
            yield break;
        }

        foreach (var profile in MidiDeviceCatalog.All) yield return profile;
    }

    /// <summary>The first candidate profile that claims this port name, or null.</summary>
    private PadDeviceProfile OwnerOf(string portName)
    {
        foreach (var profile in Candidates())
            if (profile.MatchPort(portName, matchByContains) != PortMatch.None) return profile;

        return null;
    }

    private bool WantsPort(string name) => OwnerOf(name) != null;

    private bool IsOpenPort(string name) =>
        (_openInputName  != null && string.Equals(name, _openInputName,  StringComparison.OrdinalIgnoreCase)) ||
        (_openOutputName != null && string.Equals(name, _openOutputName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first candidate with at least one port present, or null.</summary>
    private PadDeviceProfile ResolveProfile(List<string> inputs, List<string> outputs)
    {
        foreach (var profile in Candidates())
            if (PickPort(inputs, profile) != null || PickPort(outputs, profile) != null)
                return profile;

        return null;
    }

    /// <summary>Exact name first, otherwise the first "contains" match.</summary>
    private string PickPort(List<string> names, PadDeviceProfile profile)
    {
        if (names == null || profile == null) return null;

        string loose = null;

        foreach (var name in names)
        {
            var match = profile.MatchPort(name, matchByContains);

            if (match == PortMatch.Exact) return name;
            if (match == PortMatch.Contains && loose == null) loose = name;
        }

        return loose;
    }

    /// <summary>
    /// Port NAMES only; the handles are disposed straight away and the chosen one is
    /// re-acquired by name. GetAll hands back unopened handles, so disposing is free.
    /// </summary>
    private List<string> ListPorts(bool input)
    {
        var names = new List<string>();

        try
        {
            if (input)
                foreach (var d in InputDevice.GetAll())  { names.Add(d.Name); try { d.Dispose(); } catch { } }
            else
                foreach (var d in OutputDevice.GetAll()) { names.Add(d.Name); try { d.Dispose(); } catch { } }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MIDI] Could not enumerate {(input ? "input" : "output")} " +
                             $"devices: {e.Message}", this);
        }

        return names;
    }

    private void SetProfile(PadDeviceProfile profile)
    {
        if (ReferenceEquals(_profile, profile)) return;

        _profile = profile;

        if (logConnection && profile != null)
            Debug.Log($"[MIDI] Device profile: {profile.DisplayName} ({profile.Id}).", this);

        ProfileChanged?.Invoke(profile);
    }

    /// <summary>For messages: the chosen device's name, or a generic phrase on auto.</summary>
    private string LookingFor()
    {
        var chosen = MidiDeviceCatalog.Find(manager != null ? manager.MidiDeviceId : null);
        return chosen != null ? chosen.DisplayName : "supported Launchpad";
    }

    /// <summary>A session from a newer build may name a device this one does not know.</summary>
    private void WarnIfUnknown(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || MidiDeviceCatalog.Find(id) != null) return;

        Notify($"Unknown MIDI device \"{id}\" — using auto-detect instead.",
               NoticeLevel.Warning);
    }

    // ----------------------------------------------------------- device mode
    // "Programmer mode" is kept as the name because it is what the Pro MK3 calls it,
    // but it now means "whatever handshake this profile needs" — which for some
    // devices is nothing at all, and they are ready the moment the port opens.

    /// <summary>Sends the profile's handshake. An empty handshake counts as success.</summary>
    private bool SendEnterMode()
    {
        if (_profile == null) return false;

        foreach (var message in _profile.CreateEnterMode())
            if (!Send(message, "Programmer")) return false;

        return true;
    }

    [ContextMenu("Enter Programmer Mode")]
    public void EnterProgrammerMode()
    {
        if (SendEnterMode()) _programmerModeReady = true;
    }

    [ContextMenu("Enter Live Mode")]
    public void EnterLiveMode()
    {
        if (_profile != null)
            foreach (var message in _profile.CreateExitMode()) Send(message, "Live");

        _programmerModeReady = false;
    }

    /// <summary>
    /// SysEx sent in the same call stack as the open gets lost or errors with
    /// OUT_SENDSYSEXRESULT_UNKNOWNERROR. Wait a couple of frames, then retry.
    /// </summary>
    private IEnumerator EnterProgrammerModeDeferred()
    {
        for (int i = 0; i < modeSwitchDelayFrames; i++) yield return null;

        for (int attempt = 1; attempt <= modeSwitchAttempts; attempt++)
        {
            if (_output == null || _profile == null) yield break;   // pulled mid-handshake

            if (SendEnterMode())
            {
                _programmerModeReady = true;
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.1f);
        }

        Debug.LogError($"[MIDI] The {ProfileName} refused its mode switch. LED output is " +
                       "disabled. A wedged unit needs a physical unplug/replug.", this);
    }

    // ------------------------------------------------------------- LED output
    // Callers speak LOGICAL indices (grid 11-88, top row 91-98) and plain RGB. The
    // profile turns that into whatever this device wants — batched SysEx on the RGB
    // Launchpads, one Note On per lamp on the red/green ones. Nothing above this
    // line knows which.

    /// <summary>Sets one LED by logical index (grid 11-88, top row 91-98).</summary>
    public void SetLed(int index, Color32 colour)
    {
        if (!LedOutputReady) return;

        _writeScratch.Clear();
        _writeScratch.Add(new LedWrite(index, colour));
        SendWrites(_writeScratch);

        _litLeds.Add(index);
    }

    /// <summary>Batched update — as few messages as the device allows, so lamps change together.</summary>
    public void SetLeds(IList<LedWrite> writes, IEnumerable<int> touchedIndices = null)
    {
        if (!LedOutputReady || writes == null || writes.Count == 0) return;

        SendWrites(writes);

        if (touchedIndices == null) return;
        foreach (int index in touchedIndices) _litLeds.Add(index);
    }

    public void ClearLed(int index)
    {
        if (!LedOutputReady) return;

        _writeScratch.Clear();
        _writeScratch.Add(LedWrite.Off(index));
        SendWrites(_writeScratch);

        _litLeds.Remove(index);
    }

    public void ClearLeds(IEnumerable<int> indices)
    {
        if (!LedOutputReady || indices == null) return;

        var writes  = new List<LedWrite>();
        var cleared = new List<int>();

        foreach (int index in indices)
        {
            writes.Add(LedWrite.Off(index));
            cleared.Add(index);
        }

        if (writes.Count == 0) return;

        SendWrites(writes);
        foreach (int index in cleared) _litLeds.Remove(index);
    }

    private void ClearAllLitLeds()
    {
        if (_litLeds.Count == 0) return;

        var writes = new List<LedWrite>(_litLeds.Count);
        foreach (int index in _litLeds) writes.Add(LedWrite.Off(index));

        SendWrites(writes);
        _litLeds.Clear();
    }

    private void SendWrites(IList<LedWrite> writes)
    {
        if (_profile == null) return;

        foreach (var message in _profile.BuildLedMessages(writes))
        {
            // A failure can drop the link mid-batch; stop rather than throw the rest
            // of the batch at a port that has just been closed.
            if (_output == null) return;
            Send(message, null);
        }
    }

    /// <summary>
    /// Every write goes through here, which makes it the natural place to notice the
    /// device has gone: PadLedView sends up to once a frame, so a yanked cable becomes
    /// sendFailuresBeforeDrop errors and then ONE disconnect, not a console flood.
    /// </summary>
    private bool Send(MidiEvent midiEvent, string label)
    {
        if (_output == null || midiEvent == null) return false;

        try
        {
            _output.SendEvent(midiEvent);
            _sendFailures = 0;

            if (label != null) Debug.Log($"[MIDI] Sent \"{label}\" mode.", this);
            return true;
        }
        catch (Exception e)
        {
            _sendFailures++;

            if (_sendFailures >= sendFailuresBeforeDrop)
                HandleLinkLost($"{_sendFailures} consecutive send failures ({e.Message})");
            else if (label != null)
                Debug.LogWarning($"[MIDI] Send failed: {e.Message}", this);

            return false;
        }
    }

    // ------------------------------------------------------------------ input

    private void PumpInput()
    {
        while (_queue.TryDequeue(out var hit))
        {
            if (controller == null || _profile == null) continue;

            var kind = hit.isCc ? DeviceInputKind.ControlChange : DeviceInputKind.Note;
            int note = _profile.ToLogical(kind, hit.number);

            // Only the 8x8 plays. The top row and side buttons are MAPPED — the
            // lightshow will want them — but PadController has nothing on them yet.
            if (!NoteGrid.IsGridNote(note)) continue;

            if (hit.velocity == 0)                     // Note On vel 0 == Note Off
            {
                controller.ReleasePad(note);
                continue;
            }

            var t = Mathf.Pow(hit.velocity / 127f, velocityCurve);
            controller.TriggerPad(note, Mathf.Lerp(velocityFloor, 1f, t));
        }
    }

    // Background thread — queue only, never touch Unity API here. RAW device numbers
    // are queued; the profile maps them on the main thread, so a device switch can
    // never race a half-done translation on another thread.
    private void OnEventReceived(object sender, MidiEventReceivedEventArgs e)
    {
        switch (e.Event)
        {
            case NoteOnEvent on:
                _queue.Enqueue((false, on.NoteNumber, on.Velocity));
                break;

            case NoteOffEvent off:                      // some devices send real Note Offs
                _queue.Enqueue((false, off.NoteNumber, 0));
                break;

            case ControlChangeEvent cc:                 // top row / side buttons
                _queue.Enqueue((true, cc.ControlNumber, cc.ControlValue));
                break;

            default:
                return;
        }

        if (logIncoming)
            Debug.unityLogger.Log($"MIDI in: {e.Event}");
    }

    // ------------------------------------------------------------------ utils

    private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
    {
        if (notifications != null) notifications.Post(message, level);
        else NotificationService.Say(message, level);
    }
}