using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Owns exactly 64 AudioSource voices, one per grid note, and nothing else.
    /// Voices are generated from the note grid, so no name parsing and no
    /// hand-maintained list of entries.
    ///
    /// Voices start with NO clip. Debug test tones are driven by Manager.DebugEnabled
    /// through PadController.SetTestTonesEnabled, which routes them via the model so
    /// they respect "real content always wins" — Rack only manufactures them on request.
    /// </summary>
    [DisallowMultipleComponent]
    public class Rack : MonoBehaviour
    {
        [SerializeField] private RackSettings settings;

        private readonly AudioSource[] _byNote = new AudioSource[NoteGrid.ArraySize];
        private readonly List<AudioSource> _voices = new List<AudioSource>(NoteGrid.Count);
        private readonly HashSet<AudioClip> _generatedClips = new HashSet<AudioClip>();

        private bool _initialized;

        public IReadOnlyList<AudioSource> Voices => _voices;

        private void Awake() => InitializeVoices();

        /// <summary>Idempotent. Safe to call from PadController.Awake regardless of order.</summary>
        public void InitializeVoices()
        {
            if (_initialized) return;

            DestroyVoices();

            float blend  = settings != null ? settings.spatialBlend  : 0f;
            float volume = settings != null ? settings.defaultVolume : 1f;

            foreach (int note in NoteGrid.All)
            {
                var go = new GameObject($"Voice {note}");
                go.transform.SetParent(transform, false);

                var source = go.AddComponent<AudioSource>();
                source.playOnAwake  = false;
                source.loop         = false;
                source.volume       = volume;
                source.spatialBlend = blend;

                _byNote[note] = source;
                _voices.Add(source);
            }

            _initialized = true;
        }

        /// <summary>
        /// Generated on demand for debug mode. Tracked so DisposeIfGenerated can reclaim it.
        /// The CALLER owns the returned clip and must get it onto a voice (via the model)
        /// or dispose it — Rack does not hold it anywhere a sweep would find it.
        /// </summary>
        public AudioClip CreateTestTone(int note)
        {
            if (settings == null || !NoteGrid.IsGridNote(note)) return null;

            int sampleRate = AudioSettings.outputSampleRate > 0
                ? AudioSettings.outputSampleRate : settings.sampleRate;

            float semitones = NoteGrid.ToIndex(note) * settings.testToneSemitoneStep;
            float frequency = settings.testToneBaseFrequency * Mathf.Pow(2f, semitones / 12f);

            var clip = CreateSineClip($"TestTone_{note}", frequency,
                settings.generatedClipLengthSeconds, sampleRate, loop: false);

            _generatedClips.Add(clip);
            return clip;
        }

        public void ReloadVoices()
        {
            DestroyVoices();
            InitializeVoices();
        }

        public AudioSource GetVoice(int note) =>
            (uint)note < (uint)_byNote.Length ? _byNote[note] : null;

        /// <summary>Destroys a generated test tone if the given clip is one. Safe otherwise.</summary>
        public void DisposeIfGenerated(AudioClip clip)
        {
            if (clip == null || !_generatedClips.Remove(clip)) return;
            if (Application.isPlaying) Destroy(clip); else DestroyImmediate(clip);
        }

        // ------------------------------------------------------------ teardown

        private void DestroyVoices()
        {
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                var source = _voices[i];
                if (source == null) continue;

                source.Stop();

                var clip = source.clip;
                source.clip = null;
                DisposeIfGenerated(clip);

                if (Application.isPlaying) Destroy(source.gameObject);
                else DestroyImmediate(source.gameObject);
            }

            _voices.Clear();

            // Anything still tracked was handed out and never reached a voice. Destroy
            // it rather than clearing the set and orphaning the clips.
            foreach (var clip in _generatedClips)
            {
                if (clip == null) continue;
                if (Application.isPlaying) Destroy(clip); else DestroyImmediate(clip);
            }

            _generatedClips.Clear();
            System.Array.Clear(_byNote, 0, _byNote.Length);
            _initialized = false;
        }

        private void OnDestroy()
        {
            if (!Application.isPlaying) return;
            DestroyVoices();
        }

        // --------------------------------------------------------- test tones

        /// <summary>
        /// Builds a complete sine buffer up front: no streaming callback, no
        /// cross-thread state, no phase drift.
        /// </summary>
        private static AudioClip CreateSineClip(
            string name, float frequency, float lengthSeconds, int sampleRate, bool loop)
        {
            const float amplitude = 0.2f;

            int lengthSamples = Mathf.Max(1, Mathf.CeilToInt(lengthSeconds * sampleRate));

            // Round to a whole number of cycles so looping is click-free.
            if (loop && frequency > 0f)
            {
                int cycles = Mathf.Max(1, Mathf.RoundToInt(lengthSamples * frequency / sampleRate));
                lengthSamples = Mathf.Max(1, Mathf.RoundToInt(cycles * sampleRate / frequency));
            }

            var data = new float[lengthSamples];

            double phase = 0.0;
            double increment = 2.0 * Mathf.PI * frequency / sampleRate;

            for (int i = 0; i < lengthSamples; i++)
            {
                data[i] = amplitude * (float)System.Math.Sin(phase);
                phase += increment;
                if (phase >= 2.0 * Mathf.PI) phase -= 2.0 * Mathf.PI;
            }

            if (!loop) ApplyEdgeFades(data, sampleRate);

            var clip = AudioClip.Create(name, lengthSamples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>~5 ms fade in/out to prevent clicks on one-shot playback.</summary>
        private static void ApplyEdgeFades(float[] data, int sampleRate)
        {
            int fade = Mathf.Min(sampleRate / 200, data.Length / 2);

            for (int i = 0; i < fade; i++)
            {
                float g = (float)i / fade;
                data[i] *= g;
                data[data.Length - 1 - i] *= g;
            }
        }
    }
}