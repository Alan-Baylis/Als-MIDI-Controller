using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Writes every Unity log message to a file on disk. In the Editor there is a
    /// console; in a BUILD there is nothing, and every safeguard in this project
    /// reports through Debug.LogError or the ticker — both of which die with the
    /// process. Player.log exists, but no user will ever find it.
    ///
    /// One rotation: the previous run is kept as previous.log, the current run is
    /// latest.log. That is deliberately the whole retention policy — a folder of
    /// forty timestamped files is a folder nobody reads.
    ///
    /// Subscribes to logMessageReceivedThreaded, NOT logMessageReceived, because
    /// MidiPadInput logs from the DryWetMIDI callback thread and those lines are
    /// exactly the ones worth having. The handler therefore only ever enqueues;
    /// the file is touched on the main thread in Update.
    ///
    /// Must live on an always-active scene root. On a page that switches off it
    /// stops logging, and the messages posted while it was off are gone for good.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public class LogFileWriter : MonoBehaviour
    {
        /// <summary>Which messages carry their stack trace.</summary>
        public enum StackTracePolicy { Never, WarningsAndErrors, Always }

        [Header("Folder")]
        [Tooltip("Relative paths resolve next to the project in the Editor and next to " +
                 "the .exe in a build — the same rule as Manager's folders.")]
        [SerializeField] private string folderName = "Logs";

        [Tooltip("In the Editor, write next to the project instead of into " +
                 "persistentDataPath, so the file is easy to find and diff.")]
        [SerializeField] private bool useProjectFolderInEditor = true;

        [SerializeField] private string currentFileName  = "latest.log";
        [SerializeField] private string previousFileName = "previous.log";

        [Header("Contents")]
        [SerializeField] private StackTracePolicy stackTraces = StackTracePolicy.WarningsAndErrors;

        [Tooltip("Wall-clock time on every line. Off = seconds since the log opened.")]
        [SerializeField] private bool wallClockTimestamps = true;

        [Tooltip("Machine, Unity version, paths and settings at the top of the file. " +
                 "This is the half of a bug report people forget to include.")]
        [SerializeField] private bool writeHeader = true;

        [Tooltip("An identical message repeated in a row is counted, not repeated. One " +
                 "per-frame error can otherwise fill a log with the same line 40,000 times " +
                 "and bury the cause above it.")]
        [SerializeField] private bool collapseRepeats = true;

        [Header("Flushing")]
        [Tooltip("Seconds between flushes to disk. Buffered writes are fast, but a crash " +
                 "loses whatever is still in the buffer — which is the run you most want.")]
        [SerializeField, Min(0.1f)] private float flushSeconds = 2f;

        [Tooltip("Flush the instant an error or exception is written, whatever the timer " +
                 "says. An error is frequently the last thing that happens.")]
        [SerializeField] private bool flushOnError = true;

        [Tooltip("Rotate into previous.log when the file passes this size. 0 = no cap. " +
                 "A runaway loop would otherwise fill the disk.")]
        [SerializeField, Min(0)] private int maxMegabytes = 16;

        [Header("Announce")]
        [Tooltip("Post the log's path on the ticker at startup, so the file can be found " +
                 "without knowing it exists.")]
        [SerializeField] private bool announcePathOnStart = true;

        [Tooltip("Optional.")]
        [SerializeField] private NotificationService notifications;

        // ---------------------------------------------------------------- state

        private readonly struct Entry
        {
            public readonly string   Condition;
            public readonly string   Stack;
            public readonly LogType  Type;
            public readonly DateTime When;
            public readonly int      Thread;

            public Entry(string condition, string stack, LogType type, int thread)
            {
                Condition = condition;
                Stack     = stack;
                Type      = type;
                When      = DateTime.Now;
                Thread    = thread;
            }
        }

        private readonly List<Entry> _pending = new List<Entry>(64);
        private readonly List<Entry> _drain   = new List<Entry>(64);
        private readonly object _gate = new object();

        private StreamWriter _writer;
        private string  _path;
        private float   _nextFlush;
        private long    _bytes;
        private bool    _failed;          // writing is off; never retried, never spammed
        private bool    _closed;
        private float   _startedAt;
        private DateTime _startedWall;

        private string  _lastLine;
        private LogType _lastType;
        private int     _repeats;

        private int _errors, _warnings, _lines;

        /// <summary>The one in the scene, for the "Open log folder" button.</summary>
        public static LogFileWriter Instance { get; private set; }

        /// <summary>Absolute path of the file being written. Null if it could not be opened.</summary>
        public string CurrentPath => _path;

        public int ErrorCount   => _errors;
        public int WarningCount => _warnings;
        public int LineCount    => _lines;

        /// <summary>
        /// Resolved log folder. Relative names hang off the project (Editor) or the
        /// folder containing the .exe (build), matching Manager.ResolveFolder.
        /// </summary>
        public string FolderPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_fallbackFolder)) return _fallbackFolder;
                
                string folder = string.IsNullOrWhiteSpace(folderName) ? "Logs" : folderName.Trim();

                if (Path.IsPathRooted(folder)) return folder.Replace('\\', '/');

                string baseDir = Application.persistentDataPath;

#if UNITY_EDITOR
                if (useProjectFolderInEditor)
                {
                    var parent = Directory.GetParent(Application.dataPath);
                    if (parent != null) baseDir = parent.FullName;
                }
#else
                var exeDir = Directory.GetParent(Application.dataPath);
                if (exeDir != null) baseDir = exeDir.FullName;
#endif
                return Path.Combine(baseDir, folder).Replace('\\', '/');
            }
        }
        
        /// <summary>Set once when the configured folder cannot be written. See Open.</summary>
        private string _fallbackFolder;

        // ------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this)
                Debug.LogWarning("[LogFileWriter] Second writer in the scene. The first one " +
                                 "keeps the static handle, and both are writing to the same " +
                                 "file — delete one.", this);

            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            _startedAt   = Time.realtimeSinceStartup;
            _startedWall = DateTime.Now;

            Open();
        }

        private void OnEnable()
        {
            // Threaded, so a log from the MIDI callback thread is not silently dropped.
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        private void OnDisable()
        {
            Application.logMessageReceivedThreaded -= OnLogMessage;
            DrainAndFlush();
        }

        private void Start()
        {
            if (!announcePathOnStart || _path == null) return;

            // Posted rather than Debug.Logged so it reaches the ticker too — and it
            // lands in this very file, which proves the file works.
            Notify($"Logging to {_path}");
        }

        private void OnApplicationQuit() => Close("application quit");

        private void OnDestroy()
        {
            Close("component destroyed");
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_writer == null) return;

            bool urgent = DrainQueue();

            if (!urgent && Time.unscaledTime < _nextFlush) return;

            _nextFlush = Time.unscaledTime + flushSeconds;
            SafeFlush();
        }

        // ---------------------------------------------------------------- intake

        // Any thread. Enqueue ONLY — no file IO, no Unity API, no logging (which
        // would come straight back in here).
        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (_failed || _closed) return;

            lock (_gate)
                _pending.Add(new Entry(condition, stackTrace, type,
                                       Thread.CurrentThread.ManagedThreadId));
        }

        /// <summary>Returns true if anything written wants an immediate flush.</summary>
        private bool DrainQueue()
        {
            lock (_gate)
            {
                if (_pending.Count == 0) return false;

                _drain.Clear();
                _drain.AddRange(_pending);
                _pending.Clear();
            }

            bool urgent = false;

            for (int i = 0; i < _drain.Count; i++)
            {
                var entry = _drain[i];
                Write(entry);

                if (flushOnError && IsProblem(entry.Type)) urgent = true;
            }

            _drain.Clear();
            return urgent;
        }

        private static bool IsProblem(LogType type) =>
            type == LogType.Error || type == LogType.Exception || type == LogType.Assert;

        // --------------------------------------------------------------- writing

        private void Write(Entry entry)
        {
            if (_writer == null) return;

            string message = entry.Condition ?? string.Empty;

            // Collapsed against the PREVIOUS line only, which is what a per-frame
            // error loop looks like. Distant duplicates are left alone: they are
            // usually two different occurrences of the same fault, and merging them
            // would hide the interval between them.
            if (collapseRepeats && entry.Type == _lastType && message == _lastLine)
            {
                _repeats++;
                return;
            }

            FlushRepeats();

            _lastLine = message;
            _lastType = entry.Type;

            var line = new StringBuilder(message.Length + 64);

            if (wallClockTimestamps) line.Append(entry.When.ToString("HH:mm:ss.fff"));
            else line.Append('+').Append((Time.realtimeSinceStartup - _startedAt).ToString("0.000"))
                     .Append('s');

            line.Append("  ").Append(Tag(entry.Type)).Append("  ");

            // The MIDI callback thread is the reason this column exists: "which thread"
            // is the first question about anything that arrives out of order.
            if (entry.Thread != 1) line.Append("[t").Append(entry.Thread).Append("] ");

            line.Append(message);

            WriteLine(line.ToString());

            _lines++;
            if (entry.Type == LogType.Warning) _warnings++;
            else if (IsProblem(entry.Type)) _errors++;

            if (!WantsStack(entry.Type) || string.IsNullOrEmpty(entry.Stack)) return;

            foreach (var stackLine in entry.Stack.Split('\n'))
            {
                string trimmed = stackLine.TrimEnd('\r', ' ');
                if (trimmed.Length > 0) WriteLine("        " + trimmed);
            }
        }

        private bool WantsStack(LogType type)
        {
            switch (stackTraces)
            {
                case StackTracePolicy.Always:            return true;
                case StackTracePolicy.WarningsAndErrors: return type != LogType.Log;
                default:                                 return false;
            }
        }

        private void FlushRepeats()
        {
            if (_repeats <= 0) return;

            int count = _repeats;
            _repeats = 0;                       // BEFORE the write, or WriteLine recurses

            WriteLine($"        \u2026 previous line repeated {count} more time(s)");
        }

        private void WriteLine(string text)
        {
            if (_writer == null) return;

            try
            {
                _writer.Write(text);
                _writer.Write('\n');

                _bytes += text.Length + 1;

                if (maxMegabytes > 0 && _bytes > maxMegabytes * 1024L * 1024L) Rotate();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static string Tag(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:   return "WARN";
                case LogType.Error:     return "ERR ";
                case LogType.Exception: return "EXC ";
                case LogType.Assert:    return "ASRT";
                default:                return "INFO";
            }
        }

        // ---------------------------------------------------------------- files

        private void Open()
        {
            try
            {
                string folder = FolderPath;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                string current  = Path.Combine(folder, Safe(currentFileName,  "latest.log"));
                string previous = Path.Combine(folder, Safe(previousFileName, "previous.log"));

                // One rotation. The run before last is deliberately not kept: two files
                // is the most anyone reads, and an unbounded folder is a disk leak.
                if (File.Exists(current))
                {
                    try
                    {
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(current, previous);
                    }
                    catch
                    {
                        // Another instance may hold it open. Appending is better than
                        // refusing to log at all.
                    }
                }

                // FileShare.ReadWrite so the file can be opened in an editor WHILE the
                // app runs — which is the entire point of writing it as we go.
                var stream = new FileStream(current, FileMode.Create, FileAccess.Write,
                                            FileShare.ReadWrite);

                _writer = new StreamWriter(stream, new UTF8Encoding(false))
                {
                    AutoFlush = false            // flushed on a timer and on every error
                };

                _path  = current.Replace('\\', '/');
                _bytes = 0;

                if (writeHeader) WriteHeaderBlock();

                _nextFlush = Time.unscaledTime + flushSeconds;
                SafeFlush();
            }
            catch (Exception e)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;

                // The folder next to the .exe is not writable when the app is installed
                // somewhere protected (Program Files). Fall back ONCE to the per-user
                // data folder rather than running with no log — the run that cannot
                // write its log is exactly the one somebody will need to report.
                string fallback = Path.Combine(Application.persistentDataPath, "Logs")
                    .Replace('\\', '/');

                if (_fallbackFolder == null &&
                    !string.Equals(FolderPath, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"[LogFileWriter] Could not write to \"{FolderPath}\" " +
                                     $"({e.Message}); logging to \"{fallback}\" instead.", this);
                    _fallbackFolder = fallback;
                    Open();
                    return;
                }

                // No Fail() here: it would try to write to the file we just failed to open.
                _failed = true;
                Debug.LogWarning($"[LogFileWriter] Could not open a log file in " +
                                 $"\"{FolderPath}\": {e.Message}. Logging to disk is off " +
                                 "for this run.", this);
            }
        }

        private void WriteHeaderBlock()
        {
            WriteLine("================================================================");
            WriteLine($" {Application.productName} {Application.version}");
            WriteLine($" started        {_startedWall:yyyy-MM-dd HH:mm:ss}");
            WriteLine($" unity          {Application.unityVersion}");
            WriteLine($" platform       {Application.platform}");
            WriteLine($" os             {SystemInfo.operatingSystem}");
            WriteLine($" cpu            {SystemInfo.processorType} " +
                      $"({SystemInfo.processorCount} threads)");
            WriteLine($" memory         {SystemInfo.systemMemorySize} MB");
            WriteLine($" graphics       {SystemInfo.graphicsDeviceName} " +
                      $"({SystemInfo.graphicsDeviceType})");
            WriteLine($" screen         {Screen.width}x{Screen.height} " +
                      $"{(Screen.fullScreen ? "fullscreen" : "windowed")}");
            WriteLine($" audio          {AudioSettings.outputSampleRate} Hz, " +
                      $"{AudioSettings.GetConfiguration().dspBufferSize} sample buffer");
            WriteLine($" dataPath       {Application.dataPath}");
            WriteLine($" persistent     {Application.persistentDataPath}");
            WriteLine($" log            {_path}");
            WriteLine("================================================================");
        }

        /// <summary>Size cap reached. Keeps the newest half of a runaway run, not the oldest.</summary>
        private void Rotate()
        {
            WriteLine($"        \u2026 {maxMegabytes} MB reached; rotating into " +
                      $"{previousFileName}. Everything above this line moves there.");

            Close("rotation", announce: false);

            _failed = false;
            _closed = false;
            Open();

            WriteLine("        (continued after a size rotation — the earlier part of " +
                      "THIS run is in " + previousFileName + ")");
        }

        [ContextMenu("Flush Log Now")]
        public void Flush() => DrainAndFlush();

        private void DrainAndFlush()
        {
            DrainQueue();
            FlushRepeats();
            SafeFlush();
        }

        private void SafeFlush()
        {
            if (_writer == null) return;

            try { _writer.Flush(); }
            catch (Exception e) { Fail(e); }
        }

        private void Close(string reason, bool announce = true)
        {
            if (_writer == null) { _closed = true; return; }

            _closed = true;

            DrainQueue();
            FlushRepeats();

            if (announce)
            {
                var span = DateTime.Now - _startedWall;

                WriteLine("================================================================");
                WriteLine($" closed ({reason}) after {span.TotalSeconds:F1}s \u2014 " +
                          $"{_lines} line(s), {_warnings} warning(s), {_errors} error(s)");
                WriteLine("================================================================");
            }

            try { _writer.Flush(); _writer.Dispose(); } catch { }

            _writer = null;
        }

        /// <summary>
        /// A write failed — a full disk, or the folder was deleted underneath us.
        /// Logging goes off permanently rather than throwing once per line, and the
        /// complaint goes to the console ONCE.
        /// </summary>
        private void Fail(Exception e)
        {
            if (_failed) return;

            _failed = true;

            var writer = _writer;
            _writer = null;                      // before the Debug call, or it recurses

            try { writer?.Dispose(); } catch { }

            Debug.LogWarning($"[LogFileWriter] Writing to \"{_path}\" failed " +
                             $"({e.Message}). Logging to disk is off for this run.", this);
        }

        // ------------------------------------------------------------ the button

        /// <summary>
        /// Opens the log folder in the OS file manager, selecting the file itself on
        /// Windows. Returns false when there is no writer in the scene, so the caller
        /// can say WHY nothing happened instead of looking broken.
        /// </summary>
        public static bool OpenFolder()
        {
            var instance = Instance;
            if (instance == null) return false;

            string folder = instance.FolderPath;
            if (string.IsNullOrEmpty(folder)) return false;

            try { if (!Directory.Exists(folder)) Directory.CreateDirectory(folder); }
            catch { /* the OpenURL below will simply do nothing */ }

            instance.DrainAndFlush();            // so what you open is up to date

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                string file = instance._path;

                if (!string.IsNullOrEmpty(file) && File.Exists(file))
                {
                    System.Diagnostics.Process.Start(
                        "explorer.exe", $"/select,\"{file.Replace('/', '\\')}\"");
                    return true;
                }
            }
            catch { /* fall through to OpenURL */ }
#endif
            Application.OpenURL("file:///" + folder.Replace('\\', '/'));
            return true;
        }

        // ---------------------------------------------------------------- utils

        private static string Safe(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private void Notify(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (notifications != null) notifications.Post(message, level);
            else NotificationService.Say(message, level);
        }

        [ContextMenu("Log File Writer State")]
        private void LogState() =>
            Debug.Log($"[LogFileWriter] {(_writer != null ? "open" : _failed ? "FAILED" : "closed")} " +
                      $"\u2192 {_path ?? "(none)"}; {_lines} line(s), {_warnings} warning(s), " +
                      $"{_errors} error(s), {_bytes / 1024} KB.", this);
    }
}