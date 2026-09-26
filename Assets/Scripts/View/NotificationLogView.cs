using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// The scrollback the ticker cannot be. NotificationService already keeps the
    /// history; this is the only thing that reads it.
    ///
    /// This component MUST live on an always-active object, NOT on the window it
    /// shows. OnEnable is where the subscription lives, and a component on a hidden
    /// window never sees the notices posted while it was hidden — the same trap that
    /// put LibraryView's preview AudioSource on its own scene-root object.
    ///
    /// Rows are pooled and only built while the window is open: closed, this costs
    /// one event handler and an int.
    /// </summary>
    [DisallowMultipleComponent]
    public class NotificationLogView : MonoBehaviour
    {
        [Tooltip("Optional. One button that opens AND closes — e.g. the strip beside " +
                 "the ticker. Use this instead of Open/Close when there is only one.")]
        [SerializeField] private Button toggleButton;
        
        [Header("References")]
        [SerializeField] private NotificationService service;

        [Tooltip("The panel that is shown and hidden. NOT this object.")]
        [SerializeField] private GameObject window;

        [Header("List")]
        [Tooltip("ScrollRect content with a VerticalLayoutGroup + ContentSizeFitter.")]
        [SerializeField] private RectTransform content;

        [SerializeField] private NotificationLogRow rowPrefab;

        [Tooltip("Optional. Needed once the list is long enough to scroll.")]
        [SerializeField] private ScrollRect scrollRect;

        [Header("Buttons (optional — wired automatically, leave OnClick EMPTY)")]
        [SerializeField] private Button openButton;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button clearButton;

        [Tooltip("Optional. Copies the whole visible log to the clipboard.")]
        [SerializeField] private Button copyButton;

        [Tooltip("Optional. Opens the folder containing latest.log in Explorer. Needs a " +
                 "LogFileWriter in the scene.")]
        [SerializeField] private Button logFolderButton;
        
        [Header("Chrome (optional)")]
        [Tooltip("Optional. \"12 notices, 2 errors\".")]
        [SerializeField] private TMP_Text countLabel;

        [Tooltip("Optional. Lit while unseen errors or warnings are waiting.")]
        [SerializeField] private GameObject alertLamp;

        [Header("Filters")]
        [SerializeField] private bool showInfo    = true;
        [SerializeField] private bool showSuccess = true;
        [SerializeField] private bool showWarning = true;
        [SerializeField] private bool showError   = true;

        [Tooltip("Optional. Live filter toggles.")]
        [SerializeField] private Toggle infoToggle;
        [SerializeField] private Toggle successToggle;
        [SerializeField] private Toggle warningToggle;
        [SerializeField] private Toggle errorToggle;

        [Header("Behaviour")]
        [Tooltip("Newest at the top. Off = console order, oldest first.")]
        [SerializeField] private bool newestFirst = true;

        [Tooltip("Hard cap on ROWS, independent of the service's history limit. " +
                 "The oldest are dropped from the view, never from the history.")]
        [SerializeField, Min(1)] private int maxRows = 200;

        [Tooltip("Scroll to the newest line when one arrives while the window is open. " +
                 "Ignored while the user is dragging the scrollbar away from the end.")]
        [SerializeField] private bool autoScroll = true;

        [Tooltip("Open the window by itself the first time an error is posted.")]
        [SerializeField] private bool openOnError = false;

        [Tooltip("Timestamps as wall clock. Off = seconds since the app started.")]
        [SerializeField] private bool wallClockTimestamps = true;

        private readonly List<NotificationLogRow> _rows = new List<NotificationLogRow>();
        private readonly List<Notice> _shown = new List<Notice>();

        private int _unseenErrors;
        private int _unseenWarnings;

        public bool IsOpen => window != null && window.activeSelf;

        // ------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (service == null) service = FindAnyObjectByType<NotificationService>();

            if (service == null)
                Debug.LogWarning($"{nameof(NotificationLogView)} found no " +
                                 $"{nameof(NotificationService)}; there is nothing to log.", this);

            if (window == null)
                Debug.LogWarning($"{nameof(NotificationLogView)} on \"{name}\" has no Window " +
                                 "object, so there is nothing to show or hide.", this);
            else if (window == gameObject)
                Debug.LogError($"{nameof(NotificationLogView)} on \"{name}\" has Window set to " +
                               "its OWN object. Hiding the window would unsubscribe this " +
                               "component, and every notice posted while closed would be lost. " +
                               "Put the component on an always-active parent.", this);
            else if (window.transform.childCount == 0 &&
                     window.GetComponent<UnityEngine.UI.Graphic>() == null)
                Debug.LogWarning($"{nameof(NotificationLogView)}: Window \"{window.name}\" has " +
                                 "no children and nothing to draw, so opening the log will " +
                                 "change nothing on screen. Point it at the panel that " +
                                 "actually contains the list.", this);
            
            if (toggleButton != null) { toggleButton.onClick.RemoveAllListeners(); toggleButton.onClick.AddListener(Toggle); }
            
            // Wired here, not in the Inspector — a listener left in an OnClick list
            // fires a second time, per the PadInspectorView note.
            if (openButton      != null) { openButton.onClick.RemoveAllListeners();  openButton.onClick.AddListener(Open); }
            if (closeButton     != null) { closeButton.onClick.RemoveAllListeners(); closeButton.onClick.AddListener(Close); }
            if (clearButton     != null) { clearButton.onClick.RemoveAllListeners(); clearButton.onClick.AddListener(Clear); }
            if (copyButton      != null) { copyButton.onClick.RemoveAllListeners();  copyButton.onClick.AddListener(CopyToClipboard); }
            if (logFolderButton != null) { logFolderButton.onClick.RemoveAllListeners(); logFolderButton.onClick.AddListener(OpenLogFolder); }
            
            Wire(infoToggle,    showInfo,    v => { showInfo    = v; Rebuild(); });
            Wire(successToggle, showSuccess, v => { showSuccess = v; Rebuild(); });
            Wire(warningToggle, showWarning, v => { showWarning = v; Rebuild(); });
            Wire(errorToggle,   showError,   v => { showError   = v; Rebuild(); });

            if (window != null) window.SetActive(false);
        }

        private void Wire(Toggle toggle, bool initial, System.Action<bool> onChanged)
        {
            if (toggle == null) return;

            toggle.SetIsOnWithoutNotify(initial);          // model → widget, never an echo
            toggle.onValueChanged.RemoveAllListeners();
            toggle.onValueChanged.AddListener(v => onChanged(v));
        }

        private void OnEnable()
        {
            if (service != null) service.NoticePosted += OnNoticePosted;
            RefreshChrome();
        }

        private void OnDisable()
        {
            if (service != null) service.NoticePosted -= OnNoticePosted;
        }

        // --------------------------------------------------------------- intake

        private void OnNoticePosted(Notice notice)
        {
            if (notice.Level == NoticeLevel.Error)        _unseenErrors++;
            else if (notice.Level == NoticeLevel.Warning) _unseenWarnings++;

            if (openOnError && notice.Level == NoticeLevel.Error && !IsOpen)
            {
                Open();                                   // Open() rebuilds and clears the counts
                return;
            }

            if (IsOpen)
            {
                bool atEnd = WasAtEnd();
                if (Passes(notice.Level)) Append(notice);
                RefreshChrome();
                if (autoScroll && atEnd) ScrollToNewest();
                return;
            }

            RefreshChrome();
        }

        // ---------------------------------------------------------------- open

        public void Open()
        {
            if (window == null) return;

            window.SetActive(true);

            _unseenErrors = _unseenWarnings = 0;

            Rebuild();
            ScrollToNewest();
            RefreshChrome();
        }

        public void Close()
        {
            if (window == null) return;

            window.SetActive(false);

            // Rows are cheap to rebuild and are pure duplication of the history, so
            // nothing is kept alive behind a closed window.
            ReleaseAllRows();
            RefreshChrome();
        }

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        /// <summary>Empties the SERVICE history as well — this is the only view of it.</summary>
        public void Clear()
        {
            service?.ClearHistory();

            _unseenErrors = _unseenWarnings = 0;

            ReleaseAllRows();
            _shown.Clear();
            RefreshChrome();
        }

        public void CopyToClipboard()
        {
            if (_shown.Count == 0)
            {
                NotificationService.Say("Nothing in the log to copy.");
                return;
            }

            var sb = new StringBuilder(_shown.Count * 64);

            // Always oldest-first in the clipboard, whatever the view order: a log you
            // paste into a bug report reads forwards.
            for (int i = 0; i < _shown.Count; i++)
            {
                var notice = newestFirst ? _shown[_shown.Count - 1 - i] : _shown[i];
                sb.Append(Timestamp(notice)).Append("  ")
                  .Append(NoticePalette.Tag(notice.Level)).Append("  ")
                  .AppendLine(notice.Message);
            }

            GUIUtility.systemCopyBuffer = sb.ToString();
            NotificationService.Say($"{_shown.Count} log line(s) copied to the clipboard.",
                                    NoticeLevel.Success);
        }
        
        /// <summary>
        /// Shows the log file in the OS file manager. The in-memory history dies with the
        /// process; the file does not, which is the difference between "it crashed" and a
        /// bug report someone can act on.
        /// </summary>
        public void OpenLogFolder()
        {
            if (LogFileWriter.OpenFolder()) return;

            NotificationService.Say("There is no LogFileWriter in the scene, so nothing is " +
                                    "being written to disk. Add one to a scene-root object.",
                NoticeLevel.Warning);
        }

        // -------------------------------------------------------------- building

        private void Rebuild()
        {
            ReleaseAllRows();
            _shown.Clear();

            if (service == null || content == null || rowPrefab == null) return;

            var history = service.History;

            // Walk backwards to honour maxRows from the NEWEST end, then present in
            // whichever order was asked for.
            int start = Mathf.Max(0, history.Count - maxRows);

            for (int i = start; i < history.Count; i++)
            {
                var notice = history[i];
                if (Passes(notice.Level)) Append(notice, rebuilding: true);
            }

            RefreshChrome();
        }

        private void Append(Notice notice, bool rebuilding = false)
        {
            if (content == null || rowPrefab == null) return;

            while (_shown.Count >= maxRows && _shown.Count > 0)
            {
                int oldest = newestFirst ? _shown.Count - 1 : 0;
                _shown.RemoveAt(oldest);
                ReleaseRow(oldest);
            }

            var row = Instantiate(rowPrefab, content);
            row.gameObject.SetActive(true);
            row.Bind(notice, Timestamp(notice));

            if (newestFirst && !rebuilding)
            {
                row.transform.SetAsFirstSibling();
                _rows.Insert(0, row);
                _shown.Insert(0, notice);
            }
            else if (newestFirst)
            {
                row.transform.SetAsFirstSibling();
                _rows.Insert(0, row);
                _shown.Insert(0, notice);
            }
            else
            {
                _rows.Add(row);
                _shown.Add(notice);
            }
        }

        private void ReleaseRow(int index)
        {
            if (index < 0 || index >= _rows.Count) return;

            var row = _rows[index];
            _rows.RemoveAt(index);

            if (row == null) return;

            row.transform.SetParent(null, false);   // leaves the layout group immediately
            Destroy(row.gameObject);
        }

        private void ReleaseAllRows()
        {
            for (int i = _rows.Count - 1; i >= 0; i--) ReleaseRow(i);
            _rows.Clear();
        }

        private bool Passes(NoticeLevel level)
        {
            switch (level)
            {
                case NoticeLevel.Success: return showSuccess;
                case NoticeLevel.Warning: return showWarning;
                case NoticeLevel.Error:   return showError;
                default:                  return showInfo;
            }
        }

        // --------------------------------------------------------------- chrome

        private void RefreshChrome()
        {
            if (countLabel != null)
            {
                int total = service != null ? service.History.Count : 0;

                countLabel.text = _unseenErrors > 0 || _unseenWarnings > 0
                    ? $"{total} notice(s) — {_unseenErrors} new error(s), " +
                      $"{_unseenWarnings} new warning(s)"
                    : $"{total} notice(s)";
            }

            if (alertLamp == null) return;

            bool alert = !IsOpen && (_unseenErrors > 0 || _unseenWarnings > 0);
            if (alertLamp.activeSelf != alert) alertLamp.SetActive(alert);
        }

        // -------------------------------------------------------------- scrolling

        /// <summary>
        /// Only auto-scroll when the user was already parked at the newest end. Yanking
        /// the list out from under someone reading back through it is worse than a
        /// missed line.
        /// </summary>
        private bool WasAtEnd()
        {
            if (scrollRect == null) return true;

            float p = scrollRect.verticalNormalizedPosition;
            return newestFirst ? p >= 0.999f : p <= 0.001f;
        }

        private void ScrollToNewest()
        {
            if (scrollRect == null) return;

            // The content's height is a frame stale after an Instantiate, so the
            // position has to be set against a fresh layout or it lands short.
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = newestFirst ? 1f : 0f;
        }

        // ------------------------------------------------------------- utilities

        /// <summary>
        /// Notice.PostedAt is Time.unscaledTime, not a date, so wall clock is
        /// RECONSTRUCTED from the current time and is only as accurate as the frame it
        /// is read in. Fine for "when did that happen"; not a forensic record.
        /// </summary>
        private string Timestamp(Notice notice)
        {
            if (!wallClockTimestamps) return $"+{notice.PostedAt:0.0}s";

            var when = System.DateTime.Now.AddSeconds(notice.PostedAt - Time.unscaledTime);
            return when.ToString("HH:mm:ss");
        }

        [ContextMenu("Log Window Wiring")]
        private void LogWiring() =>
            Debug.Log($"[NotificationLogView] service={(service != null ? "yes" : "NONE")} " +
                      $"window={(window != null ? window.name : "NONE")} " +
                      $"content={(content != null ? content.name : "NONE")} " +
                      $"rowPrefab={(rowPrefab != null ? rowPrefab.name : "NONE")} " +
                      $"open={IsOpen} rows={_rows.Count} shown={_shown.Count} " +
                      $"history={(service != null ? service.History.Count : 0)}", this);
    }
}
