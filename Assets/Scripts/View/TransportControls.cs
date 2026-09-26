using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// Always-visible transport strip, OUTSIDE the side-panel pages — a panic button
    /// you have to navigate to is not a panic button.
    ///
    /// Stops every pad voice and the library preview. Future home for Record.
    /// </summary>
    [DisallowMultipleComponent]
    public class TransportControls : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private PadController controller;
        [SerializeField] private NotificationService notifications;

        [Tooltip("Optional. Found automatically, including while its page is hidden.")]
        [SerializeField] private LibraryView library;

        [Header("Buttons (optional — wired automatically)")]
        [SerializeField] private Button stopAllButton;

        [Header("Behaviour")]
        [Tooltip("Post a notice when Stop All is pressed. Off for a silent panic button.")]
        [SerializeField] private bool announce = true;

        private void Awake()
        {
            if (controller    == null) controller    = FindAnyObjectByType<PadController>();
            if (notifications == null) notifications = FindAnyObjectByType<NotificationService>();

            // Include inactive: the Pad page is switched off most of the time, and a
            // preview started before switching away is exactly what needs stopping.
            if (library == null)
                library = FindAnyObjectByType<LibraryView>(FindObjectsInactive.Include);

            if (stopAllButton != null) stopAllButton.onClick.AddListener(StopAll);
        }

        /// <summary>Public and UnityEvent-friendly: also the hook for a future control pad.</summary>
        public void StopAll()
        {
            controller?.StopAllVoices();
            library?.StopPreview();

            if (announce)
                NotificationService.Say("Stopped all playback.", NoticeLevel.Info);
        }
    }
}
