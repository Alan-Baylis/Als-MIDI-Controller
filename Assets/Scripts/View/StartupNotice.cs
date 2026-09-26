using System.Collections;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>When the greeting is posted.</summary>
    public enum StartupNoticeTiming
    {
        /// <summary>Awake, ahead of everything. Held by NotificationService's backlog.</summary>
        First,

        /// <summary>After SessionService has finished loading, plus a short settle.</summary>
        AfterSessionLoad,

        /// <summary>A fixed wall-clock delay after Start, for anything else noisy.</summary>
        AfterDelay
    }

    /// <summary>
    /// The greeting on the ticker. Put it on an always-active object.
    ///
    /// It defaults to AfterSessionLoad, not First. "First" is easy and wrong: the
    /// session load, the log path, the MIDI connection and the layouts folder all
    /// announce themselves during startup, so the earliest message is the one buried
    /// soonest. Worse, NotificationTicker switches to instantWhenBacklogged once four
    /// notices are queued, so a greeting posted first gets flicked past rather than
    /// read. Posted last, it is the line left on screen when the noise stops.
    ///
    /// First is kept because it is still the right answer for a message about a startup
    /// FAILURE, which has to be said before whatever it warns about happens.
    ///
    /// The Awake path relies on NotificationService's pre-subscriber backlog: the ticker
    /// subscribes in OnEnable, which is after every Awake, so an Awake post would
    /// otherwise raise into an empty event and never reach the ticker at all.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10000)]     // only matters for Timing.First
    public class StartupNotice : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional. Found automatically, including on inactive objects.")]
        [SerializeField] private NotificationService notifications;

        [Tooltip("Optional. Found automatically. Only used to know when the session has " +
                 "finished loading, for the AfterSessionLoad timing.")]
        [SerializeField] private SessionService session;

        [Header("Message")]
        [Tooltip("{version} is replaced with Application.version, {product} with the " +
                 "product name.")]
        [SerializeField, TextArea(1, 3)]
        private string message = "{product} {version} — drop a sample on a pad to begin.";

        [SerializeField] private NoticeLevel level = NoticeLevel.Info;

        [Header("Timing")]
        [Tooltip("When to post. AfterSessionLoad waits for the session to finish so the " +
                 "greeting is the last thing said, not the first thing buried.")]
        [SerializeField] private StartupNoticeTiming timing = StartupNoticeTiming.AfterSessionLoad;

        [Tooltip("Quiet gap after the session finishes before the greeting is posted. " +
                 "Long enough for the session's own notice to be queued ahead of it.")]
        [SerializeField, Min(0f)] private float settleSeconds = 0.35f;

        [Tooltip("Delay used by the AfterDelay timing, and the safety cap on waiting " +
                 "for the session — a greeting that never appears because something " +
                 "else never finished is worse than one posted a beat early.")]
        [SerializeField, Min(0.1f)] private float maxWaitSeconds = 5f;

        [Header("Hardware")]
        [Tooltip("Optional. Say so when there is no MIDI input component at all, so a " +
                 "user with no hardware is told rather than left wondering.")]
        [SerializeField] private bool announceHardware = false;

        private void Awake()
        {
            // Include inactive: FindAnyObjectByType skips inactive objects (§9.11), and a
            // greeting that silently does not appear is worse than no greeting.
            if (notifications == null)
                notifications = FindAnyObjectByType<NotificationService>(
                    FindObjectsInactive.Include);

            if (session == null)
                session = FindAnyObjectByType<SessionService>(FindObjectsInactive.Include);

            if (timing == StartupNoticeTiming.First) PostGreeting();
        }

        private void Start()
        {
            if (timing == StartupNoticeTiming.First)
            {
                if (announceHardware) CheckHardware();
                return;
            }

            StartCoroutine(PostWhenQuiet());
        }

        /// <summary>
        /// Realtime waits throughout: this runs during startup, where a stalled or
        /// zero timeScale is entirely possible and would strand the greeting forever.
        /// </summary>
        private IEnumerator PostWhenQuiet()
        {
            float deadline = Time.realtimeSinceStartup + maxWaitSeconds;

            if (timing == StartupNoticeTiming.AfterSessionLoad && session != null)
            {
                // Poll rather than subscribe: SessionService has several early-return
                // paths (no file, corrupt file, recovered backup) and HasLoaded is true
                // after every one of them, which an event per path would not be.
                while (!session.HasLoaded && Time.realtimeSinceStartup < deadline)
                    yield return null;

                if (!session.HasLoaded)
                    Debug.Log($"[{nameof(StartupNotice)}] Session still loading after " +
                              $"{maxWaitSeconds:F1}s; greeting posted anyway.", this);
            }
            else if (timing == StartupNoticeTiming.AfterDelay)
            {
                yield return new WaitForSecondsRealtime(maxWaitSeconds);
            }

            if (settleSeconds > 0f) yield return new WaitForSecondsRealtime(settleSeconds);

            PostGreeting();

            if (announceHardware) CheckHardware();
        }

        private void PostGreeting()
        {
            string text = Expand(message);
            if (string.IsNullOrWhiteSpace(text)) return;

            // The service itself, not the static Say: during Awake the static handle may
            // not be set yet, because NotificationService.Awake may not have run.
            if (notifications != null) notifications.Post(text, level);
            else Debug.Log(text, this);
        }

        /// <summary>
        /// Deliberately silent when a port IS open: MidiPadInput announces that itself
        /// (announceConnection), and two lines saying the same thing is noise.
        /// </summary>
        private void CheckHardware()
        {
            var midi = FindAnyObjectByType<MidiPadInput>(FindObjectsInactive.Include);

            if (midi == null)
                Post("No MidiPadInput in the scene — the on-screen grid still works.",
                     NoticeLevel.Warning);
        }

        private string Expand(string raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? raw
                : raw.Replace("{version}", Application.version)
                     .Replace("{product}", Application.productName);

        private void Post(string text, NoticeLevel notice)
        {
            if (notifications != null) notifications.Post(text, notice);
            else NotificationService.Say(text, notice);
        }
    }
}