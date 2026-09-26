using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LaunchpadStudio
{
    /// <summary>Three pages: global settings, the pad workspace, or configuration.</summary>
    public enum SidePanelPage { Manager, Pad, Settings }

    /// <summary>
    /// Docked panel to the right of the grid. Owns page switching only.
    ///
    /// Each page also owns its CHROME — panel artwork that is visible only while that
    /// page is showing.
    ///
    /// LAMP OWNERSHIP. The SelectorSwitch owns the lamps: it lights one and darkens the
    /// rest from its own position list, so this class never touches them. Two owners
    /// toggling one GameObject is a lamp that sticks on, and "the swap didn't happen"
    /// is the hardest fault here to see.
    ///
    /// The Pad page holds the inspector and the file browser TOGETHER — they are the
    /// same task (choose a clip, tune the pad), so splitting them meant three-way
    /// navigation for one job.
    /// </summary>
    [DisallowMultipleComponent]
    public class SidePanel : MonoBehaviour
    {
        [Header("Pages")]
        [SerializeField] private GameObject managerView;

        [Tooltip("Parent of PadInspectorView and LibraryView. Both children must be " +
                 "left ACTIVE in the editor — this object is what gets toggled.")]
        [SerializeField] private GameObject padPage;

        [Tooltip("Optional. Configuration page — display, palette, folders, MIDI. " +
                 "Leave empty until it exists; the selector will simply have nowhere " +
                 "to go and will say so once.")]
        [SerializeField] private GameObject settingsPage;

        [Header("Selector")]
        [Tooltip("Optional — found by name when empty. The three-way selector: " +
                 "position 0 = Manager, 1 = Pad, 2 = Settings. It owns the lamps.")]
        [SerializeField] private SelectorSwitch selector;

        [Header("Extra Chrome (optional)")]
        [Tooltip("Optional. Anything visible only on the Manager page.")]
        [SerializeField] private GameObject[] managerChrome;

        [Tooltip("Optional. Anything visible only on the Pad page.")]
        [SerializeField] private GameObject[] padChrome;

        [Tooltip("Optional. Anything visible only on the Settings page.")]
        [SerializeField] private GameObject[] settingsChrome;

        [Tooltip("Find the selector by object name (\"SelectorSwitch\") when its slot " +
                 "is empty.")]
        [SerializeField] private bool autoFindChrome = true;

        [Tooltip("Strip click behaviour from any Button found in the chrome lists. " +
                 "Chrome is artwork, and a leftover OnClick entry on one would fight " +
                 "the selector.")]
        [SerializeField] private bool neutraliseChromeButtons = true;

        [Header("Switch Sound")]
        [Tooltip("Optional. Click heard when the selector moves to the Pad page.")]
        [SerializeField] private AudioClip switchToPadClip;

        [Tooltip("Optional. Click heard when the selector moves to the Manager page.")]
        [SerializeField] private AudioClip switchToManagerClip;

        [Tooltip("Optional. Click heard when the selector moves to the Settings page.")]
        [SerializeField] private AudioClip switchToSettingsClip;

        [SerializeField, Range(0f, 1f)] private float switchVolume = 0.7f;

        [Tooltip("Synthesise a short click when no clips are assigned, so the switch is " +
                 "never silent just because an asset hasn't been imported yet.")]
        [SerializeField] private bool generateFallbackClick = true;

        [Header("References")]
        [SerializeField] private SelectionService selection;
        [SerializeField] private PadInspectorView inspector;

        [Tooltip("Returning to the Pad page re-selects the pad you were last editing, " +
                 "so the library's right-click-to-load has a target straight away.")]
        [SerializeField] private bool restoreLastPad = true;

        public SidePanelPage CurrentPage { get; private set; } = SidePanelPage.Manager;

        private int _lastPad = -1;
        private bool _shown;
        private bool _warnedNoSettings;

        private readonly List<GameObject> _managerChrome  = new List<GameObject>(4);
        private readonly List<GameObject> _padChrome      = new List<GameObject>(4);
        private readonly List<GameObject> _settingsChrome = new List<GameObject>(4);

        private AudioSource _clickSource;
        private readonly AudioClip[] _generated = new AudioClip[3];

        // Distinct enough to tell apart without being musical. A real selector does not
        // sound the same going left as it does coming back.
        private static readonly float[] ClickFrequencies = { 1050f, 1400f, 1250f };

        private void Awake()
        {
            if (selection == null) selection = FindAnyObjectByType<SelectionService>();
            if (inspector == null && padPage != null)
                inspector = padPage.GetComponentInChildren<PadInspectorView>(true);

            if (autoFindChrome && selector == null) FindSelector();

            if (selector == null)
                Debug.LogWarning($"{nameof(SidePanel)} has no {nameof(SelectorSwitch)}, so " +
                                 "the panel can only be moved from code or by right-clicking " +
                                 "a pad. Assign one, or name an object \"SelectorSwitch\".",
                                 this);

            BuildChrome();

            if (neutraliseChromeButtons)
            {
                Neutralise(_managerChrome);
                Neutralise(_padChrome);
                Neutralise(_settingsChrome);
            }

            EnsureClickSource();
        }

        private void OnEnable()
        {
            if (selector != null) selector.PositionChanged += OnSelectorMoved;
        }

        private void OnDisable()
        {
            if (selector != null) selector.PositionChanged -= OnSelectorMoved;
        }

        // This object is a scene root and never deactivates, so the click is safe here:
        // an AudioSource on a page would be silenced the instant that page hid itself.
        private void Start() => Show(SidePanelPage.Pad, playSound: false);

        private void OnDestroy()
        {
            for (int i = 0; i < _generated.Length; i++) DestroyGenerated(ref _generated[i]);
        }

        // ------------------------------------------------------------- switching

        private void OnSelectorMoved(int index) => Show(PageFor(index));

        public static SidePanelPage PageFor(int index)
        {
            switch (index)
            {
                case 1:  return SidePanelPage.Pad;
                case 2:  return SidePanelPage.Settings;
                default: return SidePanelPage.Manager;
            }
        }

        public static int IndexFor(SidePanelPage page)
        {
            switch (page)
            {
                case SidePanelPage.Pad:      return 1;
                case SidePanelPage.Settings: return 2;
                default:                     return 0;
            }
        }

        public void Show(SidePanelPage page) => Show(page, playSound: true);

        private void Show(SidePanelPage page, bool playSound)
        {
            // A page that does not exist yet must not become an empty panel with no way
            // back. Say so once and stay where we are.
            if (page == SidePanelPage.Settings && settingsPage == null)
            {
                if (!_warnedNoSettings)
                {
                    _warnedNoSettings = true;
                    NotificationService.Say("The Settings page has no panel assigned yet.",
                                            NoticeLevel.Warning);
                }

                selector?.SetPositionWithoutNotify(IndexFor(CurrentPage));
                return;
            }

            bool changing = !_shown || page != CurrentPage;

            // Remember where we were BEFORE a non-pad page clears the selection.
            if (page != SidePanelPage.Pad && selection != null && selection.Selected.HasValue)
                _lastPad = selection.Selected.Value;

            CurrentPage = page;
            _shown      = true;

            if (managerView  != null) managerView.SetActive(page == SidePanelPage.Manager);
            if (padPage      != null) padPage.SetActive(page == SidePanelPage.Pad);
            if (settingsPage != null) settingsPage.SetActive(page == SidePanelPage.Settings);

            // Hide first, then show: if the same object were listed on two pages it ends
            // up visible rather than dark, which is the easier fault to spot.
            SetChromeActive(_managerChrome,  false);
            SetChromeActive(_padChrome,      false);
            SetChromeActive(_settingsChrome, false);
            SetChromeActive(ChromeFor(page), true);

            selector?.SetPositionWithoutNotify(IndexFor(page));

            if (playSound && changing) PlaySwitchSound(page);

            // Manager and Settings are the "nothing selected" pages.
            if (page != SidePanelPage.Pad)
            {
                selection?.ClearSelection();
                return;
            }

            // Arriving from the switch rather than from a right-click: nothing is
            // selected, so the inspector and the library would both have no target.
            if (restoreLastPad && selection != null && !selection.Selected.HasValue &&
                NoteGrid.IsGridNote(_lastPad))
            {
                selection.Select(_lastPad);
                inspector?.Bind(_lastPad);
            }
        }

        private List<GameObject> ChromeFor(SidePanelPage page)
        {
            switch (page)
            {
                case SidePanelPage.Pad:      return _padChrome;
                case SidePanelPage.Settings: return _settingsChrome;
                default:                     return _managerChrome;
            }
        }

        /// <summary>Step to the next page. Wraps.</summary>
        public void Toggle()
        {
            int next = IndexFor(CurrentPage) + 1;

            if (next > 2 || (next == 2 && settingsPage == null)) next = 0;

            Show(PageFor(next));
        }

        /// <summary>Select a pad and show the workspace. The inspector follows the selection.</summary>
        public void ShowPadInspector(int note)
        {
            selection?.Select(note);
            Show(SidePanelPage.Pad);

            // Explicit bind covers the case where `note` was ALREADY selected, so
            // Select() raised no event for the inspector to hear.
            inspector?.Bind(note);
        }

        // ---- UnityEvent-friendly wrappers ----
        public void ShowManagerView()  => Show(SidePanelPage.Manager);
        public void ShowPadPage()      => Show(SidePanelPage.Pad);
        public void ShowSettingsPage() => Show(SidePanelPage.Settings);

        public void ShowPadInspectorView()
        {
            if (selection != null && selection.Selected.HasValue)
                ShowPadInspector(selection.Selected.Value);
            else
                NotificationService.Say("No pad selected — right-click a pad first.");
        }

        // ---------------------------------------------------------------- chrome

        private void BuildChrome()
        {
            _managerChrome.Clear();
            _padChrome.Clear();
            _settingsChrome.Clear();

            AddRange(_managerChrome,  managerChrome);
            AddRange(_padChrome,      padChrome);
            AddRange(_settingsChrome, settingsChrome);

            WarnIfShared(_managerChrome, _padChrome,      "Manager", "Pad");
            WarnIfShared(_managerChrome, _settingsChrome, "Manager", "Settings");
            WarnIfShared(_padChrome,     _settingsChrome, "Pad",     "Settings");

            WarnIfSelectorChrome(_managerChrome,  "Manager");
            WarnIfSelectorChrome(_padChrome,      "Pad");
            WarnIfSelectorChrome(_settingsChrome, "Settings");
        }

        /// <summary>
        /// The lamps belong to the selector. Listing one as page chrome gives it two
        /// owners, and the loser of that race is always the one you can see.
        /// </summary>
        private void WarnIfSelectorChrome(List<GameObject> chrome, string pageName)
        {
            if (selector == null) return;

            foreach (var go in chrome)
            {
                if (go == null) continue;
                if (go.transform.IsChildOf(selector.transform) || go == selector.gameObject)
                    Debug.LogWarning($"\"{go.name}\" is listed as {pageName} chrome but " +
                                     "belongs to the SelectorSwitch, which already owns it. " +
                                     "Remove it from the chrome list.", go);
            }
        }

        private static void WarnIfShared(List<GameObject> a, List<GameObject> b,
                                         string nameA, string nameB)
        {
            foreach (var go in a)
                if (go != null && b.Contains(go))
                    Debug.LogWarning($"\"{go.name}\" is chrome for BOTH the {nameA} and " +
                                     $"{nameB} pages, so it will never switch off. Remove " +
                                     "it from one of the lists.", go);
        }

        private static void Add(List<GameObject> list, GameObject go)
        {
            if (go == null || list.Contains(go)) return;
            list.Add(go);
        }

        private static void AddRange(List<GameObject> list, GameObject[] source)
        {
            if (source == null) return;
            for (int i = 0; i < source.Length; i++) Add(list, source[i]);
        }

        private static void SetChromeActive(List<GameObject> chrome, bool active)
        {
            for (int i = 0; i < chrome.Count; i++)
                if (chrome[i] != null && chrome[i].activeSelf != active)
                    chrome[i].SetActive(active);
        }

        /// <summary>
        /// Last-resort wiring by name. Uses FindObjectsOfTypeAll because the selector
        /// may sit under something that starts inactive; the scene filter excludes
        /// prefab assets, which that call would otherwise hand back.
        /// </summary>
        private void FindSelector()
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                var go = t.gameObject;
                if (!go.scene.IsValid()) continue;
                if (go.name != "SelectorSwitch") continue;

                selector = go.GetComponent<SelectorSwitch>();
                if (selector != null) return;
            }

            // Named differently? Take the only one in the scene rather than nothing.
            var found = FindObjectsByType<SelectorSwitch>(FindObjectsInactive.Include,
                                                          FindObjectsSortMode.None);
            if (found.Length == 1) selector = found[0];
        }

        /// <summary>
        /// Turns a chrome Button into plain artwork: no listeners, no focus, no tint,
        /// and no raycast, so it cannot swallow a click meant for the switch behind it.
        /// Transition is cleared BEFORE interactable, or the disabled tint dims the art.
        /// </summary>
        private void Neutralise(List<GameObject> chrome)
        {
            foreach (var go in chrome)
            {
                if (go == null) continue;
                if (selector != null && go == selector.gameObject) continue;

                var button = go.GetComponent<Button>();
                if (button == null) continue;

                button.onClick.RemoveAllListeners();
                button.transition   = Selectable.Transition.None;
                button.interactable = false;

                var graphic = go.GetComponent<Graphic>();
                if (graphic != null) graphic.raycastTarget = false;
            }
        }

        [ContextMenu("Log Chrome Wiring")]
        private void LogChromeWiring()
        {
            if (_managerChrome.Count == 0 && _padChrome.Count == 0 &&
                _settingsChrome.Count == 0) BuildChrome();

            Debug.Log($"[SidePanel] page={CurrentPage}, " +
                      $"selector={(selector != null ? selector.name : "NONE")}\n" +
                      $"[SidePanel] Manager chrome:  {Describe(_managerChrome)}\n" +
                      $"[SidePanel] Pad chrome:      {Describe(_padChrome)}\n" +
                      $"[SidePanel] Settings chrome: {Describe(_settingsChrome)}", this);
        }

        private static string Describe(List<GameObject> chrome)
        {
            if (chrome.Count == 0) return "(nothing)";

            var text = new System.Text.StringBuilder();
            foreach (var go in chrome)
                text.Append(go != null ? go.name : "(null)").Append("  ");
            return text.ToString();
        }

        // ----------------------------------------------------------------- sound

        private void EnsureClickSource()
        {
            _clickSource = GetComponent<AudioSource>();
            if (_clickSource == null) _clickSource = gameObject.AddComponent<AudioSource>();

            _clickSource.playOnAwake  = false;
            _clickSource.loop         = false;
            _clickSource.spatialBlend = 0f;   // 2D: a panel click must not be positioned
        }

        private void PlaySwitchSound(SidePanelPage page)
        {
            if (_clickSource == null) return;

            int index = IndexFor(page);

            AudioClip clip =
                page == SidePanelPage.Pad      ? switchToPadClip      :
                page == SidePanelPage.Settings ? switchToSettingsClip :
                                                 switchToManagerClip;

            // Any assigned clip beats silence, whichever page it was meant for.
            if (clip == null) clip = switchToPadClip ?? switchToManagerClip ?? switchToSettingsClip;

            if (clip == null && generateFallbackClick)
            {
                if (_generated[index] == null)
                    _generated[index] = BuildClick($"SwitchClick{index}", ClickFrequencies[index]);

                clip = _generated[index];
            }

            if (clip != null) _clickSource.PlayOneShot(clip, switchVolume);
        }

        /// <summary>
        /// A mechanical click is a noise burst with a very fast decay and a little tone
        /// for body. 15 ms is enough; anything longer reads as a thud.
        /// </summary>
        private static AudioClip BuildClick(string name, float frequency)
        {
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 44100;
            int samples = Mathf.Max(1, Mathf.CeilToInt(0.015f * rate));

            var data = new float[samples];
            var random = new System.Random(name.GetHashCode());

            for (int i = 0; i < samples; i++)
            {
                float t     = (float)i / samples;
                float decay = Mathf.Exp(-14f * t);
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                float tone  = Mathf.Sin(2f * Mathf.PI * frequency * i / rate);

                data[i] = 0.6f * decay * (0.75f * noise + 0.25f * tone);
            }

            var clip = AudioClip.Create(name, samples, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static void DestroyGenerated(ref AudioClip clip)
        {
            if (clip == null) return;
            if (Application.isPlaying) Destroy(clip); else DestroyImmediate(clip);
            clip = null;
        }
    }
}