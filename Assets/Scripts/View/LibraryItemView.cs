using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// One row in a file list: a folder to descend into, or a file.
    ///
    /// Clicks are reported with the mouse button that produced them, because the two
    /// buttons mean different things: left previews, right loads into the selected pad.
    ///
    /// A row can be INERT — shown for recognition but not choosable. The folder picker
    /// uses this to list the files in a folder without letting you pick one.
    ///
    /// In step 3 this becomes the drag-source (IBeginDragHandler et al.); Entry is
    /// exposed now so that addition is a pure superset with no re-wiring.
    /// </summary>
    [DisallowMultipleComponent]
    public class LibraryItemView : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private TextMeshProUGUI label;
        [SerializeField] private Image icon;          // optional
        [SerializeField] private Button button;       // optional: hover/press visuals only

        [Tooltip("Opacity of an inert row's label and icon.")]
        [SerializeField, Range(0.1f, 1f)] private float inertAlpha = 0.45f;

        public LibraryEntry Entry { get; private set; }

        public bool IsInert { get; private set; }

        private Action<LibraryItemView, PointerEventData.InputButton> _onActivate;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();

            // Clicks arrive through OnPointerClick, not onClick — a Button's onClick
            // is left-only and can't tell us which button fired. Any listener left in
            // the Inspector's OnClick list would double-fire the left-click path.
            if (button != null) button.onClick.RemoveAllListeners();
        }

        public void Bind(LibraryEntry entry, Sprite iconSprite,
                         Action<LibraryItemView, PointerEventData.InputButton> onActivate)
        {
            Entry = entry;
            _onActivate = onActivate;

            if (label != null) label.text = TextGlyphs.Fit(label, entry.Name);
            if (icon  != null && iconSprite != null) { icon.enabled = true; icon.sprite = iconSprite; }
            else if (icon != null) icon.enabled = false;
        }

        /// <summary>
        /// Dim the row and stop it responding. Alpha only, never RGB: the label colour
        /// may be styled, and inert should read as "faded", not "recoloured".
        /// </summary>
        public void SetInert(bool inert)
        {
            IsInert = inert;

            float a = inert ? inertAlpha : 1f;

            if (label != null) label.alpha = a;

            if (icon != null)
            {
                var c = icon.color;
                c.a = a;
                icon.color = c;
            }

            // Also kills the hover highlight, so the row does not invite a click.
            if (button != null) button.interactable = !inert;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (IsInert) return;
            if (button != null && !button.interactable) return;
            _onActivate?.Invoke(this, eventData.button);
        }
    }
}