using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace LaunchpadStudio
{
    /// <summary>
    /// The four metronome LEDs at the top of the interface.
    ///
    /// The UNLIT lamps are painted into the background image; each child of this
    /// object is an illuminated sprite sitting exactly on top of one of them, so
    /// "lighting a bulb" is SetActive(true) and nothing more. No colour or alpha
    /// tinting — the artwork already looks the way it should.
    ///
    /// Pure view: owns no timing, only reacts to Metronome.Beat. ONE of these on
    /// the "Bulbs" parent.
    /// </summary>
    [DisallowMultipleComponent]
    public class FlashingBulbs : MonoBehaviour
    {
        [Header("References")]
        [SerializeField, FormerlySerializedAs("managerView")] private Manager manager;
        [SerializeField] private Metronome metronome;

        [Header("Bulbs — hierarchy order, NOT beat order")]
        [Tooltip("Leave empty to auto-collect children in hierarchy order.")]
        [SerializeField] private List<GameObject> bulbs = new List<GameObject>();

        [Tooltip("Which entry in Bulbs lights on the downbeat (beat 1).\n" +
                 "1 = first green, for artwork with the red lamp on the right.\n" +
                 "0 = red, once the red lamp moves to the left.")]
        [SerializeField, Min(0)] private int downbeatIndex = 0;

        [Tooltip("Walk the list backwards. Useful if the artwork order is ever mirrored.")]
        [SerializeField] private bool reverse = false;

        [Header("Timing")]
        [Tooltip("Seconds a lamp stays lit.")]
        [SerializeField, Min(0.01f)] private float onDuration = 0.1f;

        [Tooltip("Hard cap on lit time as a fraction of one beat, so fast tempos " +
                 "don't leave every lamp on at once.")]
        [SerializeField, Range(0.05f, 1f)] private float maxBeatFraction = 0.9f;

        [Tooltip("Keep the lamp lit until the next beat instead of using On Duration.")]
        [SerializeField] private bool holdUntilNextBeat = false;

        private int   _lit = -1;
        private float _offTimer;

        private void Awake()
        {
            if (manager   == null) manager   = FindAnyObjectByType<Manager>();
            if (metronome == null) metronome = FindAnyObjectByType<Metronome>();

            if (bulbs.Count == 0) CollectChildren();

            if (bulbs.Count == 0)
                Debug.LogWarning($"{nameof(FlashingBulbs)} on \"{name}\" found no bulb children.", this);

            if (metronome == null)
                Debug.LogWarning($"{nameof(FlashingBulbs)} on \"{name}\" found no {nameof(Metronome)}; " +
                                 "the lamps will never light.", this);
            else if (bulbs.Count > 0 && metronome.BeatsPerBar != bulbs.Count)
                Debug.LogWarning($"{nameof(FlashingBulbs)}: {bulbs.Count} bulbs but " +
                                 $"{metronome.BeatsPerBar} beats per bar. The chase will " +
                                 "still run, it just won't line up with the bar.", this);
        }

        private void OnEnable()
        {
            if (metronome != null)
            {
                metronome.Beat    += OnBeat;
                metronome.Stopped += AllOff;
            }

            AllOff();
        }

        private void OnDisable()
        {
            if (metronome != null)
            {
                metronome.Beat    -= OnBeat;
                metronome.Stopped -= AllOff;
            }

            AllOff();
        }

        /// <summary>
        /// Children become bulbs in sibling order. Inactive children are included:
        /// this script owns their active state, so a lamp you left switched off in
        /// the editor is still picked up and will light on its beat.
        /// </summary>
        private void CollectChildren()
        {
            foreach (Transform child in transform)
                bulbs.Add(child.gameObject);
        }

        /// <summary>Maps a 0-based beat to an index into <see cref="bulbs"/>.</summary>
        public int BulbIndexForBeat(int beat)
        {
            int count = bulbs.Count;
            if (count == 0) return -1;

            int offset = reverse ? -beat : beat;
            int index  = (downbeatIndex + offset) % count;

            return index < 0 ? index + count : index;   // C# % can return negative
        }

        private void OnBeat(int beat)
        {
            int index = BulbIndexForBeat(beat);
            if (index < 0) return;

            // Recomputed per beat so a BPM change shortens the very next flash
            // rather than waiting for a restart.
            float beatSeconds = manager != null ? manager.SecondsPerBeat : 0.5f;
            _offTimer = Mathf.Min(onDuration, beatSeconds * maxBeatFraction);

            Light(index);
        }

        private void Update()
        {
            if (holdUntilNextBeat || _lit < 0) return;

            _offTimer -= Time.unscaledDeltaTime;
            if (_offTimer > 0f) return;

            AllOff();
        }

        private void Light(int index)
        {
            _lit = index;

            for (int i = 0; i < bulbs.Count; i++)
                SetBulb(i, i == index);
        }

        private void AllOff()
        {
            _lit = -1;

            for (int i = 0; i < bulbs.Count; i++)
                SetBulb(i, false);
        }

        private void SetBulb(int index, bool on)
        {
            var bulb = bulbs[index];
            if (bulb == null) return;
            if (bulb.activeSelf != on) bulb.SetActive(on);   // avoids pointless dirtying
        }
    }
}