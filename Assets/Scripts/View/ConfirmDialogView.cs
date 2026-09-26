using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>
    /// One modal question. Yes / No / Cancel, because the question this was built for —
    /// "save the current layout before loading another?" — has three honest answers and
    /// the project's existing confirms (a second press that changes a caption) can only
    /// express two.
    ///
    /// Optionally it also asks for a line of text (InputText != null): the "Save As"
    /// name prompt is this same dialog with a field showing, rather than a second modal
    /// with its own backdrop, keyboard rules and centring to keep in step.
    /// </summary>
    public class ConfirmRequest
    {
        public string Title   = "Are you sure?";
        public string Message = "";

        public string YesLabel    = "Yes";
        public string NoLabel     = "No";
        public string CancelLabel = "Cancel";

        /// <summary>False hides the third button, making this a plain two-way confirm.</summary>
        public bool ShowCancel = true;

        /// <summary>False hides the No button — a text prompt is OK / Cancel.</summary>
        public bool ShowNo = true;

        /// <summary>
        /// Non-null shows the text field, pre-filled with this value (all selected, so
        /// typing replaces it). Null — the default — is a plain question with no field.
        /// </summary>
        public string InputText;

        /// <summary>
        /// Checked when Yes is pressed on a text prompt. Return null to accept, or a
        /// sentence saying what is wrong: the dialog then STAYS OPEN with that sentence
        /// under the message, so a typo costs a correction rather than a restart.
        /// </summary>
        public Func<string, string> Validate;

        /// <summary>Receives the typed text (trimmed) on Yes. Runs before OnYes.</summary>
        public Action<string> OnYesText;

        public Action OnYes;
        public Action OnNo;
        public Action OnCancel;
    }

    /// <summary>
    /// The only modal question box in the app, and like FileBrowserView it is the only
    /// one that works in a BUILD — EditorUtility.DisplayDialog does not exist there.
    ///
    /// Callers never construct UI: they call Ask, which returns FALSE when there is no
    /// dialog in the scene so the caller can degrade to something (DESIGN.md §1.8)
    /// instead of silently doing nothing.
    ///
    /// Put this component on an ALWAYS-ACTIVE object and point Window at the panel it
    /// shows. A component that hides itself cannot be asked to show itself again.
    ///
    /// Buttons are wired in Awake — leave their OnClick lists EMPTY (§9.4).
    /// </summary>
    [DisallowMultipleComponent]
    public class ConfirmDialogView : MonoBehaviour
    {
        public static ConfirmDialogView Instance { get; private set; }

        [Header("Window")]
        [Tooltip("The panel that is shown and hidden. NOT this object.")]
        [SerializeField] private GameObject window;

        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text messageText;

        [Tooltip("Optional. A single-line TMP_InputField inside the window, shown only " +
                 "when a request asks for text (Save As). Hidden for plain questions.")]
        [SerializeField] private TMP_InputField inputField;

        [Header("Buttons (optional — wired automatically, leave OnClick EMPTY)")]
        [SerializeField] private Button yesButton;
        [SerializeField] private Button noButton;
        [SerializeField] private Button cancelButton;

        [Tooltip("Optional. Captions, so one dialog can say Save/Don't Save/Cancel.")]
        [SerializeField] private TMP_Text yesLabel;
        [SerializeField] private TMP_Text noLabel;
        [SerializeField] private TMP_Text cancelLabel;

        [Header("Behaviour")]
        [Tooltip("Escape cancels, Return answers Yes.")]
        [SerializeField] private bool keyboardShortcuts = true;

        [Tooltip("Colour of the 'what is wrong' line a rejected text entry adds.")]
        [SerializeField] private Color errorColour = new Color(1f, 0.5f, 0.5f, 1f);

        private ConfirmRequest _request;
        private Coroutine _focusRoutine;

        public bool IsOpen => window != null && window.activeSelf;

        // ------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this)
                Debug.LogWarning("Second ConfirmDialogView; the first keeps the static " +
                                 "handle and this one will never be asked to open.", this);

            if (window == null)
                Debug.LogError($"{nameof(ConfirmDialogView)} on \"{name}\" has no Window " +
                               "object, so there is nothing to show.", this);
            else if (window == gameObject)
                Debug.LogError($"{nameof(ConfirmDialogView)} on \"{name}\" has Window set to " +
                               "its OWN object. Hiding it would make the dialog impossible " +
                               "to reopen. Put the component on an always-active parent.",
                               this);

            if (yesButton    != null) { yesButton.onClick.RemoveAllListeners();    yesButton.onClick.AddListener(Yes); }
            if (noButton     != null) { noButton.onClick.RemoveAllListeners();     noButton.onClick.AddListener(No); }
            if (cancelButton != null) { cancelButton.onClick.RemoveAllListeners(); cancelButton.onClick.AddListener(Cancel); }

            if (inputField != null) inputField.gameObject.SetActive(false);
            if (window != null) window.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!keyboardShortcuts || !IsOpen) return;

#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.escapeKey.wasPressedThisFrame) { Cancel(); return; }

            if (keyboard.enterKey.wasPressedThisFrame ||
                keyboard.numpadEnterKey.wasPressedThisFrame) Yes();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape)) { Cancel(); return; }

            if (Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter)) Yes();
#endif
        }

        // ------------------------------------------------------------ static API

        /// <summary>
        /// Returns false when there is no dialog in the scene. Callers MUST handle that
        /// — usually by refusing the destructive half of the action rather than doing it
        /// unasked.
        /// </summary>
        public static bool Ask(ConfirmRequest request)
        {
            if (Instance == null || request == null) return false;

            // A text prompt with nowhere to type is not a prompt. Say no, so the caller
            // falls back (the file browser, for Save As) instead of saving "My Kit"
            // under a name nobody chose.
            if (request.InputText != null && Instance.inputField == null) return false;

            Instance.Open(request);
            return true;
        }

        /// <summary>Two-way convenience: Yes or nothing.</summary>
        public static bool Ask(string title, string message, string yes, Action onYes) =>
            Ask(new ConfirmRequest
            {
                Title      = title,
                Message    = message,
                YesLabel   = yes,
                NoLabel    = "Cancel",
                ShowCancel = false,
                OnYes      = onYes
            });

        /// <summary>
        /// One line of text: OK / Cancel. onAccept receives the trimmed text; validate
        /// (optional) returns null to accept or a sentence explaining the problem.
        /// </summary>
        public static bool AskText(string title, string message, string initial,
                                   string okLabel, Action<string> onAccept,
                                   Func<string, string> validate = null,
                                   Action onCancel = null) =>
            Ask(new ConfirmRequest
            {
                Title       = title,
                Message     = message,
                InputText   = initial ?? "",
                YesLabel    = okLabel,
                CancelLabel = "Cancel",
                ShowNo      = false,
                ShowCancel  = true,
                Validate    = validate,
                OnYesText   = onAccept,
                OnCancel    = onCancel
            });

        // --------------------------------------------------------------- opening

        public void Open(ConfirmRequest request)
        {
            if (request == null || window == null) return;

            // A second question over the top of a first would lose the first one's
            // callbacks silently. Answering it Cancel is the honest resolution.
            if (IsOpen) Cancel();

            _request = request;

            if (titleText   != null) titleText.text   = request.Title;
            if (messageText != null) messageText.text = request.Message;

            if (yesLabel    != null) yesLabel.text    = request.YesLabel;
            if (noLabel     != null) noLabel.text     = request.NoLabel;
            if (cancelLabel != null) cancelLabel.text = request.CancelLabel;

            if (noButton     != null) noButton.gameObject.SetActive(request.ShowNo);
            if (cancelButton != null) cancelButton.gameObject.SetActive(request.ShowCancel);

            bool wantsText = request.InputText != null && inputField != null;

            if (inputField != null)
            {
                inputField.gameObject.SetActive(wantsText);
                if (wantsText) inputField.SetTextWithoutNotify(request.InputText);
            }

            window.SetActive(true);

            if (wantsText)
            {
                if (_focusRoutine != null) StopCoroutine(_focusRoutine);
                _focusRoutine = StartCoroutine(FocusInputNextFrame());
            }
        }

        /// <summary>
        /// A frame late on purpose. The prompt is usually opened from a click, or from
        /// the Return that answered the PREVIOUS question — focusing in that same frame
        /// either loses the focus to the click or feeds that Return into the new field.
        /// </summary>
        private IEnumerator FocusInputNextFrame()
        {
            yield return null;
            _focusRoutine = null;

            if (!IsOpen || inputField == null || !inputField.gameObject.activeInHierarchy)
                yield break;

            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(inputField.gameObject);

            inputField.ActivateInputField();

            // onFocusSelectAll does this too, but only when the flag is on in the
            // Inspector; doing it here means typing always REPLACES the suggestion.
            inputField.selectionAnchorPosition = 0;
            inputField.selectionFocusPosition  = inputField.text.Length;
        }

        // --------------------------------------------------------------- answers

        public void Yes()
        {
            if (_request != null && _request.InputText != null && inputField != null)
            {
                string text = (inputField.text ?? "").Trim();

                string problem = _request.Validate?.Invoke(text);
                if (!string.IsNullOrEmpty(problem))
                {
                    ShowProblem(problem);
                    return;                              // stay open, keep the typing
                }

                var accept = _request.OnYesText;
                Answer(r => () => { accept?.Invoke(text); r.OnYes?.Invoke(); });
                return;
            }

            Answer(r => r.OnYes);
        }

        public void No()     => Answer(r => r.OnNo);
        public void Cancel() => Answer(r => r.OnCancel);

        private void ShowProblem(string problem)
        {
            if (messageText != null)
                messageText.text = $"{_request.Message}\n\n" +
                                   $"<color=#{ColorUtility.ToHtmlStringRGB(errorColour)}>" +
                                   $"{problem}</color>";

            // Echoed so the log has it too (DESIGN.md §1.3).
            NotificationService.Say(problem, NoticeLevel.Warning);

            if (inputField != null) inputField.ActivateInputField();
        }

        /// <summary>
        /// The request is cleared and the window hidden BEFORE the callback runs: the
        /// callback commonly opens the file browser or another dialog, and a dialog that
        /// is still nominally open when that happens closes the new one instead.
        /// </summary>
        private void Answer(Func<ConfirmRequest, Action> pick)
        {
            if (_focusRoutine != null) { StopCoroutine(_focusRoutine); _focusRoutine = null; }

            if (inputField != null && inputField.isFocused) inputField.DeactivateInputField();

            if (_request == null)
            {
                if (window != null) window.SetActive(false);
                return;
            }

            var request = _request;
            _request = null;

            if (window != null) window.SetActive(false);

            pick(request)?.Invoke();
        }

        [ContextMenu("Log Confirm Wiring")]
        private void LogWiring() =>
            Debug.Log($"[{nameof(ConfirmDialogView)}] window=" +
                      $"{(window != null ? window.name : "NONE")} open={IsOpen} " +
                      $"yes={(yesButton != null)} no={(noButton != null)} " +
                      $"cancel={(cancelButton != null)} input={(inputField != null)}", this);
    }
}