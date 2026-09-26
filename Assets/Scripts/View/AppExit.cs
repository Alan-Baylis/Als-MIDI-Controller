using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// The way out, with one question on the way: if the LAYOUT has unsaved changes,
    /// Save / Don't Save / Cancel — the same three answers, and the same Save, as
    /// loading another layout over it (LayoutLoadGuard.SaveCurrentThen).
    ///
    /// The session is NOT saved here: SessionService writes on OnApplicationQuit, which
    /// fires for a real quit AND for leaving play mode, so there is exactly one session
    /// save path however the app ends. What this guards is the layout FILE, which the
    /// session does not update.
    ///
    /// Closing the window (X, Alt+F4, Cmd+Q) is routed through the same question via
    /// Application.wantsToQuit, so the Quit button is not the only honest exit. In the
    /// Editor, pressing Play to stop does not raise wantsToQuit — only the Quit button
    /// asks there.
    ///
    /// A clean grid quits in one click unless confirmCleanQuit is on. An empty grid is
    /// never asked about: there is nothing a layout file could hold.
    ///
    /// Buttons are wired in Awake — leave their OnClick lists EMPTY.
    /// </summary>
    [DisallowMultipleComponent]
    public class AppExit : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional. Found automatically. Used to ask about unsaved layout changes.")]
        [SerializeField] private PadLayoutService layouts;

        [Header("Buttons (optional — wired automatically)")]
        [SerializeField] private Button quitButton;

        [Header("Legacy confirmation panel (optional)")]
        [Tooltip("Optional. Only used for the plain 'Quit?' question when there is no " +
                 "ConfirmDialogView in the scene. Leave empty.")]
        [SerializeField] private GameObject confirmPanel;

        [SerializeField] private Button confirmYesButton;
        [SerializeField] private Button confirmNoButton;

        [Header("Behaviour")]
        [Tooltip("Ask Save / Don't Save / Cancel when the layout has unsaved changes.")]
        [SerializeField] private bool confirmUnsavedLayout = true;

        [Tooltip("Also ask 'Quit?' when nothing is unsaved. Off = a clean grid quits in " +
                 "one click, which is what the session auto-save makes safe.")]
        [SerializeField] private bool confirmCleanQuit = false;

        [Tooltip("Route closing the window (X, Alt+F4, Cmd+Q) through the same question " +
                 "in a build.")]
        [SerializeField] private bool guardWindowClose = true;

        [Tooltip("Post a notice on the way out. Mostly useful in the Editor, where the " +
                 "console outlives the run.")]
        [SerializeField] private bool announce = true;

        /// <summary>Fallback with no dialog in the scene: press Quit twice within this.</summary>
        private const float PendingSeconds = 4f;

        /// <summary>Set immediately before the real quit, so wantsToQuit lets it through.</summary>
        private bool  _approved;
        private float _pendingUntil;

        // ------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (layouts == null)
                layouts = FindAnyObjectByType<PadLayoutService>(FindObjectsInactive.Include);

            if (quitButton != null)
            {
                quitButton.onClick.RemoveAllListeners();
                quitButton.onClick.AddListener(RequestQuit);
            }

            if (confirmYesButton != null)
            {
                confirmYesButton.onClick.RemoveAllListeners();
                confirmYesButton.onClick.AddListener(Quit);
            }

            if (confirmNoButton != null)
            {
                confirmNoButton.onClick.RemoveAllListeners();
                confirmNoButton.onClick.AddListener(CancelQuit);
            }

            if (confirmPanel != null) confirmPanel.SetActive(false);
        }

        private void OnEnable()  => Application.wantsToQuit += OnWantsToQuit;
        private void OnDisable() => Application.wantsToQuit -= OnWantsToQuit;

        /// <summary>
        /// The OS asked us to close. Only an unsaved layout holds it up — asking "Quit?"
        /// on a window's X button would be nagging, since the X already means it.
        /// </summary>
        private bool OnWantsToQuit()
        {
            if (_approved || !guardWindowClose || !HasUnsavedLayout()) return true;

            AskAboutUnsaved();
            return false;
        }

        // ------------------------------------------------------------------ flow

        private bool HasUnsavedLayout() =>
            confirmUnsavedLayout && layouts != null && layouts.IsDirty && layouts.HasContent();

        /// <summary>UnityEvent-friendly. Asks whatever needs asking, then quits.</summary>
        public void RequestQuit()
        {
            if (HasUnsavedLayout()) { AskAboutUnsaved(); return; }
            if (confirmCleanQuit)   { AskPlainQuit();    return; }

            Quit();
        }

        private void AskAboutUnsaved()
        {
            string current = string.IsNullOrWhiteSpace(layouts.CurrentLayoutName)
                ? "The current grid"
                : $"\"{layouts.CurrentLayoutName}\"";

            var request = new ConfirmRequest
            {
                Title       = "Unsaved changes",
                Message     = $"{current} has changes that are not saved.\n\n" +
                              "Save before quitting?",
                YesLabel    = "Save",
                NoLabel     = "Don't Save",
                CancelLabel = "Cancel",
                ShowCancel  = true,

                // Quit only if the save really happened — a failed or cancelled save
                // leaves the app open with the grid intact.
                OnYes    = () => LayoutLoadGuard.SaveCurrentThen(layouts, Quit,
                                                                 "the app is still open"),
                OnNo     = Quit,
                OnCancel = () => NotificationService.Say("Quit cancelled.", NoticeLevel.Info)
            };

            if (ConfirmDialogView.Ask(request)) { _pendingUntil = 0f; return; }

            // No dialog in the scene. Degrade, never disappear (§1.8): the same choice
            // twice means "yes, lose it".
            if (Time.unscaledTime < _pendingUntil) { Quit(); return; }

            _pendingUntil = Time.unscaledTime + PendingSeconds;

            NotificationService.Say(
                $"{current} has unsaved changes. Quit again within {PendingSeconds:0} " +
                "seconds to quit anyway and lose them, or Save first.",
                NoticeLevel.Warning);
        }

        private void AskPlainQuit()
        {
            if (ConfirmDialogView.Ask("Quit",
                    $"Quit {Application.productName}?\n\nThe session is saved automatically.",
                    "Quit", Quit))
                return;

            if (confirmPanel != null) { confirmPanel.SetActive(true); return; }

            Quit();
        }

        public void CancelQuit()
        {
            if (confirmPanel != null) confirmPanel.SetActive(false);
        }

        public void Quit()
        {
            if (confirmPanel != null) confirmPanel.SetActive(false);

            _approved = true;

            if (announce) NotificationService.Say("Closing — saving the session.");

#if UNITY_EDITOR
            // Leaving play mode raises OnApplicationQuit, so the session is still written.
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}