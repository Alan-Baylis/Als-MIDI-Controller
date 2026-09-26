using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// A physical N-position selector. It owns two things and nothing else: the switch
    /// artwork for the current position, and the lamp that is lit for it.
    ///
    /// It knows nothing about pages. Whatever is listening to PositionChanged decides
    /// what a position MEANS — which is what lets the same component drive a future
    /// bank selector or a layout A/B without a second copy of this logic.
    ///
    /// Clicks are handled here rather than through Button.onClick, because a Button's
    /// onClick is left-only and cannot tell us WHERE in the switch you clicked. A
    /// three-way selector where clicking the left third selects the left position is
    /// the difference between a switch and a button that cycles.
    ///
    /// Any Button on this object is stripped of its listeners in Awake, so nothing
    /// fires twice. Leave its OnClick list EMPTY.
    /// </summary>
    [DisallowMultipleComponent]
    public class SelectorSwitch : MonoBehaviour, IPointerClickHandler
    {
        [Serializable]
        public class Position
        {
            [Tooltip("Caption for the console and the Inspector. Documentation only.")]
            public string name;

            [Tooltip("Switch artwork shown while this position is selected.")]
            public Sprite sprite;

            [Tooltip("Optional. The lit lamp for this position — ManagerButton, " +
                     "LibraryButton, SettingsButton. Shown alone, all others hidden.")]
            public GameObject lamp;
        }

        [Header("Artwork")]
        [Tooltip("Optional. The Image showing the switch itself. Resolved from this " +
                 "object, then its children, when empty.")]
        [SerializeField] private Image switchImage;

        [Header("Positions — in left-to-right order")]
        [SerializeField] private List<Position> positions = new List<Position>(3);

        [SerializeField, Min(0)] private int startPosition = 0;

        [Header("Behaviour")]
        [Tooltip("Clicking the left third selects position 0, the middle third 1, and " +
                 "so on. Off = every click advances by one.")]
        [SerializeField] private bool clickSelectsByThirds = true;

        [Tooltip("Right-click steps backwards. Handy when the switch has more positions " +
                 "than you want to cycle through.")]
        [SerializeField] private bool rightClickGoesBack = true;

        [Tooltip("Advancing past the last position returns to the first.")]
        [SerializeField] private bool wrap = true;

        [Tooltip("Force Transition = None on a Button here. A Selectable tint MULTIPLIES " +
                 "the sprite, which makes correct artwork look wrong.")]
        [SerializeField] private bool clearButtonTint = true;

        /// <summary>Raised when the position changes, from a click or from code.</summary>
        public event Action<int> PositionChanged;

        public int Current { get; private set; } = -1;

        public int Count => positions.Count;

        public string CurrentName =>
            Current >= 0 && Current < positions.Count ? positions[Current].name : "(none)";

        private RectTransform _rect;

        private void Awake()
        {
            _rect = transform as RectTransform;

            if (switchImage == null) switchImage = GetComponent<Image>();
            if (switchImage == null) switchImage = GetComponentInChildren<Image>(true);

            var button = GetComponent<Button>();
            if (button != null)
            {
                // Clicks arrive through OnPointerClick. A listener left in the Inspector
                // would fire a second time and fight the position we just chose.
                button.onClick.RemoveAllListeners();
                if (clearButtonTint) button.transition = Selectable.Transition.None;
            }

            if (positions.Count == 0)
                Debug.LogWarning($"{nameof(SelectorSwitch)} on \"{name}\" has no positions, " +
                                 "so it can never do anything.", this);
        }

        private void Start()
        {
            // Start, not Awake: whatever is listening subscribes in ITS Awake or OnEnable,
            // and a position raised before that is a position nobody hears.
            SetPosition(Mathf.Clamp(startPosition, 0, Mathf.Max(0, positions.Count - 1)));
        }

        // ---------------------------------------------------------------- input

        public void OnPointerClick(PointerEventData eventData)
        {
            if (positions.Count == 0) return;

            if (rightClickGoesBack && eventData.button == PointerEventData.InputButton.Right)
            {
                Previous();
                return;
            }

            if (eventData.button != PointerEventData.InputButton.Left) return;

            if (clickSelectsByThirds && TryPositionFromClick(eventData, out int index))
            {
                SetPosition(index);
                return;
            }

            Next();
        }

        private bool TryPositionFromClick(PointerEventData eventData, out int index)
        {
            index = Current;

            if (_rect == null) return false;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _rect, eventData.position, eventData.pressEventCamera, out var local))
                return false;

            float width = _rect.rect.width;
            if (width <= 0f) return false;

            float t = (local.x - _rect.rect.xMin) / width;

            index = Mathf.Clamp(Mathf.FloorToInt(t * positions.Count), 0, positions.Count - 1);
            return true;
        }

        // ------------------------------------------------------------- movement

        public void Next() => Step(+1);
        public void Previous() => Step(-1);

        private void Step(int direction)
        {
            if (positions.Count == 0) return;

            int next = Current + direction;

            if (next >= positions.Count) next = wrap ? 0 : positions.Count - 1;
            if (next < 0)                next = wrap ? positions.Count - 1 : 0;

            SetPosition(next);
        }

        /// <summary>Moves the switch and tells everyone. Out-of-range is ignored, not clamped.</summary>
        public void SetPosition(int index) => SetPosition(index, true);

        /// <summary>
        /// Moves the switch to follow something else — a page opened by a right-click on
        /// a pad, say — WITHOUT raising the event that would send us straight back.
        /// </summary>
        public void SetPositionWithoutNotify(int index) => SetPosition(index, false);

        private void SetPosition(int index, bool notify)
        {
            if (positions.Count == 0) return;
            if (index < 0 || index >= positions.Count) return;
            if (index == Current) { Apply(); return; }   // re-assert the artwork anyway

            Current = index;
            Apply();

            if (notify) PositionChanged?.Invoke(Current);
        }

        /// <summary>Paints the switch and lights exactly one lamp.</summary>
        private void Apply()
        {
            for (int i = 0; i < positions.Count; i++)
            {
                var position = positions[i];
                if (position?.lamp == null) continue;

                bool lit = i == Current;
                if (position.lamp.activeSelf != lit) position.lamp.SetActive(lit);
            }

            if (switchImage == null || Current < 0) return;

            var sprite = positions[Current]?.sprite;
            if (sprite != null && switchImage.sprite != sprite) switchImage.sprite = sprite;
        }

        [ContextMenu("Log Selector Wiring")]
        private void LogWiring()
        {
            var text = new System.Text.StringBuilder(
                $"[SelectorSwitch] \"{name}\" at position {Current} ({CurrentName}), " +
                $"{positions.Count} position(s)\n");

            for (int i = 0; i < positions.Count; i++)
            {
                var p = positions[i];

                text.Append($"  [{i}] \"{(p?.name ?? "(unnamed)")}\"  ")
                    .Append(p?.sprite != null ? $"sprite=\"{p.sprite.name}\"" : "NO SPRITE")
                    .Append("  ")
                    .Append(p?.lamp != null
                        ? $"lamp=\"{p.lamp.name}\"{(p.lamp.activeSelf ? " [LIT]" : "")}"
                        : "no lamp")
                    .Append('\n');
            }

            text.Append($"  image={(switchImage != null ? switchImage.name : "NONE")}");
            Debug.Log(text.ToString(), this);
        }
    }
}
