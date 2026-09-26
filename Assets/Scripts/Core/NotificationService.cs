using System;
using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    public enum NoticeLevel { Info, Success, Warning, Error }

    /// <summary>One user-facing message. Immutable: posted, never edited.</summary>
    public readonly struct Notice
    {
        public readonly string      Message;
        public readonly NoticeLevel Level;
        public readonly float       PostedAt;

        public Notice(string message, NoticeLevel level)
        {
            Message  = message;
            Level    = level;
            PostedAt = Time.unscaledTime;
        }

        public override string ToString() => $"[{Level}] {Message}";
    }

    /// <summary>
    /// The single place user-facing messages are posted. Views subscribe; nothing
    /// polls. Every notice is ALSO echoed to the console, so the ticker is a
    /// convenience and never the only record — DESIGN.md §1 rule 3 stands.
    ///
    /// Core knows nothing about the UI: this raises an event and stops.
    /// </summary>
    [DisallowMultipleComponent]
    public class NotificationService : MonoBehaviour
    {
        /// <summary>
        /// Raised on the main thread, in post order.
        ///
        /// Custom accessors because the ticker subscribes in OnEnable, which runs AFTER
        /// every Awake in the scene: anything posted during Awake used to raise into an
        /// empty event and vanish from the ticker entirely. Notices posted before the
        /// first subscriber are now held and replayed to it in order, so a startup
        /// banner posted in Awake is the first thing the ticker shows.
        /// </summary>
        public event Action<Notice> NoticePosted
        {
            add
            {
                _posted += value;
                FlushBacklog();
            }
            remove => _posted -= value;
        }

        private Action<Notice> _posted;

        /// <summary>
        /// Notices posted before anything was listening. Capped: if nothing ever
        /// subscribes, this must not grow for the life of the run.
        /// </summary>
        private readonly List<Notice> _backlog = new List<Notice>();

        private const int MaxBacklog = 32;

        private void FlushBacklog()
        {
            if (_backlog.Count == 0 || _posted == null) return;

            // Copied and cleared BEFORE raising: a subscriber that posts from its own
            // handler would otherwise re-enter this and replay the same notice twice.
            var replay = _backlog.ToArray();
            _backlog.Clear();

            foreach (var notice in replay) _posted(notice);
        }

        [Tooltip("Mirror every notice into the Unity console at a matching log level.")]
        [SerializeField] private bool echoToConsole = true;

        [Tooltip("How many notices to keep for a future log window. 0 = keep none.")]
        [SerializeField, Min(0)] private int historyLimit = 200;

        private readonly List<Notice> _history = new List<Notice>();

        public IReadOnlyList<Notice> History => _history;

        /// <summary>
        /// Convenience handle for static callers (AudioFileLoader is a static class and
        /// cannot hold a reference). Instance methods remain the sanctioned path.
        /// </summary>
        private static NotificationService _instance;

        private void Awake()
        {
            if (_instance == null) _instance = this;
            else if (_instance != this)
                Debug.LogWarning("Second NotificationService; the first one keeps the " +
                                 "static handle. Static posts will go to the other one.", this);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        public void Post(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var notice = new Notice(message, level);

            if (echoToConsole)
            {
                switch (level)
                {
                    case NoticeLevel.Error:   Debug.LogError(message, this);   break;
                    case NoticeLevel.Warning: Debug.LogWarning(message, this); break;
                    default:                  Debug.Log(message, this);        break;
                }
            }

            if (historyLimit > 0)
            {
                _history.Add(notice);
                if (_history.Count > historyLimit) _history.RemoveAt(0);
            }

            if (_posted != null) _posted(notice);
            else if (_backlog.Count < MaxBacklog) _backlog.Add(notice);
        }

        public void Info(string message)    => Post(message, NoticeLevel.Info);
        public void Success(string message) => Post(message, NoticeLevel.Success);
        public void Warn(string message)    => Post(message, NoticeLevel.Warning);
        public void Error(string message)   => Post(message, NoticeLevel.Error);

        /// <summary>
        /// Static entry point for code that has no reference. Falls back to the console
        /// alone if no service exists, so a message is never lost.
        /// </summary>
        public static void Say(string message, NoticeLevel level = NoticeLevel.Info)
        {
            if (_instance != null) { _instance.Post(message, level); return; }

            if (level == NoticeLevel.Error)        Debug.LogError(message);
            else if (level == NoticeLevel.Warning) Debug.LogWarning(message);
            else                                   Debug.Log(message);
        }

        public void ClearHistory() => _history.Clear();
    }
}