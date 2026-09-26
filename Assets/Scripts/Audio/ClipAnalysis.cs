using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Read-only measurements taken from a decoded AudioClip.
    ///
    /// Nothing here changes a clip. A trim is a PLAYBACK START OFFSET, never an edit to
    /// the sample data — the clip belongs to ClipCache and is shared by every pad that
    /// references it, so editing it would change a pad nobody touched. An offset costs
    /// nothing, needs no undo, and lets two pads disagree about where the sound starts.
    /// </summary>
    public static class ClipAnalysis
    {
        public struct SilenceResult
        {
            /// <summary>First sample worth playing. 0 = play from the top.</summary>
            public int StartSample;

            public bool  Trimmed;
            public float Seconds;

            /// <summary>False = the samples could not be read; StartSample is then 0.</summary>
            public bool Scanned;

            /// <summary>Human-readable explanation, for the console and the inspector.</summary>
            public string Reason;

            public override string ToString() => Reason;
        }

        /// <summary>
        /// Reused across calls: a session load scans up to 64 clips back to back, and a
        /// fresh 400 KB array each time is pure garbage for no benefit. Main thread only,
        /// which is where every decode completes.
        /// </summary>
        private static float[] _scratch = new float[0];

        /// <summary>
        /// Finds the first sample above <paramref name="thresholdDb"/>, minus a small
        /// backoff so the attack is not clipped.
        ///
        /// Only the first <paramref name="maxScanSeconds"/> are examined, so that value
        /// is ALSO the hard cap on how much can ever be skipped — a clip that is silent
        /// for longer than that is silent deliberately, and guessing at it would be worse
        /// than doing nothing.
        /// </summary>
        public static SilenceResult FindStart(AudioClip clip,
                                              float thresholdDb    = -48f,
                                              float maxScanSeconds = 1f,
                                              float backoffSeconds = 0.002f)
        {
            var result = new SilenceResult { StartSample = 0 };

            if (clip == null || clip.samples <= 0 || clip.channels <= 0)
            {
                result.Reason = "no clip to measure";
                return result;
            }

            // GetData logs an error of its own on a streamed or half-decoded clip, so
            // both are refused here rather than tripped over.
            if (clip.loadType == AudioClipLoadType.Streaming)
            {
                result.Reason = "clip is streamed, so its samples cannot be read";
                return result;
            }

            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                result.Reason = "clip is not decoded yet";
                return result;
            }

            int channels = clip.channels;
            int rate     = Mathf.Max(1, clip.frequency);

            int frames = Mathf.Min(clip.samples,
                                   Mathf.CeilToInt(Mathf.Max(0.01f, maxScanSeconds) * rate));

            int needed = frames * channels;
            if (_scratch.Length < needed) _scratch = new float[needed];

            // GetData fills data.Length/channels frames, so the window is the array's
            // length — which is why the scratch buffer is only ever read up to `needed`.
            var window = needed == _scratch.Length ? _scratch : new float[needed];

            if (!clip.GetData(window, 0))
            {
                result.Reason = "the samples could not be read";
                return result;
            }

            result.Scanned = true;

            float threshold   = Mathf.Pow(10f, thresholdDb / 20f);
            int   firstAudible = -1;

            for (int frame = 0; frame < frames && firstAudible < 0; frame++)
            {
                int offset = frame * channels;

                for (int c = 0; c < channels; c++)
                {
                    if (Mathf.Abs(window[offset + c]) < threshold) continue;
                    firstAudible = frame;
                    break;
                }
            }

            if (firstAudible < 0)
            {
                result.Reason = $"nothing above {thresholdDb:0} dBFS in the first " +
                                $"{frames / (float)rate:0.##}s — left alone";
                return result;
            }

            if (firstAudible == 0)
            {
                result.Reason = "starts on the first sample";
                return result;
            }

            int backoff = Mathf.Max(0, Mathf.RoundToInt(backoffSeconds * rate));
            int start   = Mathf.Clamp(firstAudible - backoff, 0, clip.samples - 1);

            result.StartSample = start;
            result.Trimmed     = start > 0;
            result.Seconds     = start / (float)rate;

            result.Reason = result.Trimmed
                ? $"skipping {result.Seconds * 1000f:0} ms of leading silence"
                : "starts on the first sample";

            return result;
        }
    }
}