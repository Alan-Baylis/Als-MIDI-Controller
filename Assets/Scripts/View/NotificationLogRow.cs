using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// One line in the log window. Pure display: it holds its Notice so a future
    /// "copy this line" or click-to-reveal has something to read, and does nothing else.
    ///
    /// Rows are POOLED by NotificationLogView, so Bind must overwrite every visible
    /// field — a row reused from the top of the list must not keep any of the old one.
    /// </summary>
    [DisallowMultipleComponent]
    public class NotificationLogRow : MonoBehaviour
    {
        [Tooltip("Optional. Timestamp column.")]
        [SerializeField] private TMP_Text timeText;

        [Tooltip("Optional. Fixed-width level tag: INFO / OK / WARN / ERR.")]
        [SerializeField] private TMP_Text levelText;

        [SerializeField] private TMP_Text messageText;

        [Tooltip("Optional. Row wash, tinted by level.")]
        [SerializeField] private Image background;

        public Notice Notice { get; private set; }

        public void Bind(Notice notice, string timestamp)
        {
            Notice = notice;

            Color colour = NoticePalette.ColourFor(notice.Level);

            if (timeText != null)
            {
                timeText.text  = timestamp;
                timeText.color = NoticePalette.Info;
            }

            if (levelText != null)
            {
                levelText.text  = NoticePalette.Tag(notice.Level);
                levelText.color = colour;
            }

            if (messageText != null)
            {
                messageText.text  = TextGlyphs.Fit(messageText, notice.Message);
                messageText.color = colour;
            }

            if (background != null)
                background.color = NoticePalette.RowFor(notice.Level);
        }
    }
}
