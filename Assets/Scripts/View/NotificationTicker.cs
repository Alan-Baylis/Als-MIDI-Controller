using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;

namespace LaunchpadStudio
{
    /// <summary>
    /// Scrolling status line. A notice enters from the right, comes to rest against the
    /// left edge, holds, then leaves — and the next one follows. Click to dismiss the
    /// current notice immediately and advance.
    ///
    /// Position is driven directly in Update rather than by a coroutine so that speed
    /// changes take effect mid-scroll and a click can interrupt any phase cleanly.
    /// </summary>
    [DisallowMultipleComponent]
    public class NotificationTicker : MonoBehaviour, IPointerClickHandler
    {
        [Header("References")]
        [SerializeField] private NotificationService service;

        [Tooltip("The masked area the text travels through. Defaults to this RectTransform.")]
        [SerializeField] private RectTransform viewport;

        [SerializeField] private TextMeshProUGUI label;

        [Header("Motion")]
        [Tooltip("Pixels per second. Applies to both the entry and the exit.")]
        [SerializeField, Range(10f, 1000f)] private float scrollSpeed = 120f;

        [Tooltip("Seconds to pause once the text reaches its resting position.")]
        [SerializeField, Min(0f)] private float holdSeconds = 2.5f;

        [Tooltip("0 = hold forever until clicked or another notice arrives.")]
        [SerializeField, Min(0f)] private float extraHoldWhenQueued = 0.5f;

        [Tooltip("Skip the entry scroll and snap to rest. Useful at high message rates.")]
        [SerializeField] private bool instantWhenBacklogged = true;

        [SerializeField, Min(1)] private int backlogThreshold = 4;

        [Header("Queue")]
        [Tooltip("Oldest pending notices are dropped beyond this. 0 = unlimited.")]
        [SerializeField, Min(0)] private int maxQueued = 32;

        [Tooltip("Errors jump the queue and interrupt whatever is showing.")]
        [SerializeField] private bool errorsPreempt = true;
        
        private Color ColourFor(NoticeLevel level) => NoticePalette.ColourFor(level);

        [Header("Colours")]
        [SerializeField] private Color infoColour = NoticePalette.Success;

        [Header("Standby")]
        [Tooltip("Shown when nothing is queued. Leave empty for a blank line.")]
        [SerializeField, FormerlySerializedAs("idleMessage")]
        private string standbyMessage = "Ready.";

        [Tooltip("Seconds the last notice stays up once the queue is empty, before the ticker " +
                 "returns to the standby message. 0 = leave it up forever.")]
        [SerializeField, Min(0f)] private float clearAfterSeconds = 8f;
        
        private enum Phase { Idle, ScrollIn, Hold, ScrollOut }

        private readonly Queue<Notice> _queue = new Queue<Notice>();

        private Phase  _phase = Phase.Idle;
        private float  _holdTimer;
        private float  _restX;
        private float  _textWidth;

        /// <summary>Runtime speed control, for a future slider on ManagerView.</summary>
        public float ScrollSpeed
        {
            get => scrollSpeed;
            set => scrollSpeed = Mathf.Max(1f, value);
        }

        public int PendingCount => _queue.Count;

        private void Awake()
        {
            if (service  == null) service  = FindAnyObjectByType<NotificationService>();
            if (viewport == null) viewport = transform as RectTransform;
            if (label    == null) label    = GetComponentInChildren<TextMeshProUGUI>(true);

            if (label == null)
            {
                Debug.LogError($"{nameof(NotificationTicker)} has no TextMeshProUGUI.", this);
                enabled = false;
                return;
            }

            // Left-anchored, left-pivoted: anchoredPosition.x is then the distance from
            // the viewport's left edge, which is the only number this class reasons about.
            var rt = label.rectTransform;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot     = new Vector2(0f, 0.5f);

            label.enableWordWrapping = false;
            label.overflowMode       = TextOverflowModes.Overflow;

            ShowIdle();
        }

        private void OnEnable()
        {
            if (service != null) service.NoticePosted += OnNoticePosted;
        }

        private void OnDisable()
        {
            if (service != null) service.NoticePosted -= OnNoticePosted;
        }

        // ------------------------------------------------------------- intake

        private void OnNoticePosted(Notice notice)
        {
            if (errorsPreempt && notice.Level == NoticeLevel.Error)
            {
                Begin(notice);
                return;
            }

            _queue.Enqueue(notice);

            while (maxQueued > 0 && _queue.Count > maxQueued) _queue.Dequeue();

            if (_phase == Phase.Idle) Advance();
        }

        /// <summary>Public so a UnityEvent or another system can push a line in directly.</summary>
        public void Show(string message, NoticeLevel level = NoticeLevel.Info) =>
            OnNoticePosted(new Notice(message, level));

        // -------------------------------------------------------------- input

        public void OnPointerClick(PointerEventData eventData)
        {
            // Dismiss: whatever is on screen leaves now and the next one follows.
            if (_phase == Phase.Idle) return;

            _phase = Phase.ScrollOut;
        }

        /// <summary>Drop everything pending and go blank.</summary>
        public void ClearAll()
        {
            _queue.Clear();
            ShowIdle();
        }

        // ------------------------------------------------------------- motion

        private void Update()
        {
            if (label == null) return;

            float step = scrollSpeed * Time.unscaledDeltaTime;
            var   rt   = label.rectTransform;
            var   pos  = rt.anchoredPosition;

            switch (_phase)
            {
                case Phase.ScrollIn:
                    pos.x -= step;

                    if (pos.x <= _restX)
                    {
                        pos.x = _restX;
                        _phase = Phase.Hold;
                        _holdTimer = 0f;
                    }

                    rt.anchoredPosition = pos;
                    break;

                case Phase.Hold:
                    _holdTimer += Time.unscaledDeltaTime;

                    if (_queue.Count == 0)
                    {
                        // Held as a status line, but not forever: a stale error sitting there for
                        // ten minutes reads as a current one.
                        if (clearAfterSeconds > 0f && _holdTimer >= clearAfterSeconds)
                            _phase = Phase.ScrollOut;
                        break;
                    }

                    if (_holdTimer >= holdSeconds + extraHoldWhenQueued)
                        _phase = Phase.ScrollOut;
                    break;
                
                case Phase.ScrollOut:
                    pos.x -= step;
                    rt.anchoredPosition = pos;

                    if (pos.x <= -_textWidth) Advance();
                    break;
            }
        }

        private void Advance()
        {
            if (_queue.Count == 0) { ShowIdle(); return; }

            Begin(_queue.Dequeue());
        }

        private void Begin(Notice notice)
        {
            label.text  = TextGlyphs.Fit(label, notice.Message);
            label.color = ColourFor(notice.Level);

            Measure();

            var rt  = label.rectTransform;
            float viewportWidth = viewport != null ? viewport.rect.width : 0f;

            // Rest position: flush left for short text; for text wider than the viewport,
            // keep going until the TAIL is visible, otherwise the end is never readable.
            _restX = Mathf.Min(0f, viewportWidth - _textWidth);

            bool snap = instantWhenBacklogged && _queue.Count >= backlogThreshold;

            rt.anchoredPosition = new Vector2(snap ? _restX : viewportWidth, 0f);

            _phase     = snap ? Phase.Hold : Phase.ScrollIn;
            _holdTimer = 0f;
        }

        private void ShowIdle()
        {
            label.text  = standbyMessage ?? string.Empty;
            label.color = infoColour;

            Measure();

            label.rectTransform.anchoredPosition = Vector2.zero;
            _phase = Phase.Idle;
        }

        /// <summary>
        /// TMP lays out lazily, so preferredWidth is stale for one frame after a text
        /// change. ForceMeshUpdate makes the measurement correct immediately, which
        /// matters because _restX is computed from it right away.
        /// </summary>
        private void Measure()
        {
            label.ForceMeshUpdate();

            _textWidth = label.preferredWidth;

            var rt = label.rectTransform;
            rt.sizeDelta = new Vector2(_textWidth, rt.sizeDelta.y);
        }
    }
}