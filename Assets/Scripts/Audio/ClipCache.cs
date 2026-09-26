using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Refcounted, deduplicating AudioClip store keyed on normalised absolute path.
    /// Replaces the fire-and-forget decoding in AudioFileLoader as the ONLY way user
    /// clips enter the application.
    ///
    /// Three guarantees:
    ///   1. One decode per file, however many pads use it.
    ///   2. Concurrent requests for the same path share one in-flight decode.
    ///   3. A clip is destroyed only when nothing holds it AND it has been idle
    ///      for lingerSeconds — so pad-to-pad moves and undo-ish churn don't thrash.
    ///
    /// Acquire/Release must be balanced. PadController owns that balance for pads;
    /// nothing else should call Release on a key it did not Acquire.
    /// </summary>
    [DisallowMultipleComponent]
    public class ClipCache : MonoBehaviour
    {
        [Header("Eviction")]
        [Tooltip("Seconds an unreferenced clip is kept before being destroyed. " +
                 "Covers the gap while a pad is being re-assigned.")]
        [SerializeField, Min(0f)] private float lingerSeconds = 30f;

        [Tooltip("Hard cap on unreferenced clips. The oldest are evicted early. 0 = no cap.")]
        [SerializeField, Min(0)] private int maxIdleClips = 32;

        [Tooltip("Log every acquire/release/evict. Noisy, but the fastest way to find " +
                 "an unbalanced Release.")]
        [SerializeField] private bool logTraffic = false;

        private class Entry
        {
            public string    Key;
            public string    SourcePath;      // as given, for messages
            public AudioClip Clip;
            public int       RefCount;
            public bool      Loading;
            public float     IdleSince;
            public readonly List<Action<AudioClip, string>> Waiters =
                new List<Action<AudioClip, string>>(2);
        }

        // Windows paths are case-insensitive; two spellings of one file must share an entry.
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _sweepScratch = new List<string>();

        private float _nextSweep;

        // ------------------------------------------------------------ diagnostics

        public int EntryCount => _entries.Count;

        public int LoadedCount
        {
            get
            {
                int n = 0;
                foreach (var entry in _entries.Values) if (entry.Clip != null) n++;
                return n;
            }
        }

        public int IdleCount
        {
            get
            {
                int n = 0;
                foreach (var entry in _entries.Values) if (entry.RefCount <= 0) n++;
                return n;
            }
        }

        /// <summary>Canonical key. Null for empty input; never throws on a malformed path.</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try { return Path.GetFullPath(path.Trim()).Replace('\\', '/'); }
            catch { return path.Trim().Replace('\\', '/'); }
        }

        // ---------------------------------------------------------------- API

        /// <summary>
        /// Takes a reference and delivers the clip. The callback fires immediately when the
        /// clip is already resident, otherwise when the decode finishes. On failure the
        /// reference is NOT taken and the callback receives (null, reason) — so callers
        /// must not Release after a failure.
        /// </summary>
        public void Acquire(string path, Action<AudioClip, string> onComplete)
        {
            string key = Normalize(path);

            if (key == null)
            {
                onComplete?.Invoke(null, "empty path");
                return;
            }

            if (_entries.TryGetValue(key, out var entry))
            {
                entry.RefCount++;
                entry.IdleSince = 0f;

                if (entry.Loading)
                {
                    if (onComplete != null) entry.Waiters.Add(onComplete);
                    Trace($"queued (loading) {key} refs={entry.RefCount}");
                    return;
                }

                Trace($"hit {key} refs={entry.RefCount}");
                onComplete?.Invoke(entry.Clip, null);
                return;
            }

            entry = new Entry
            {
                Key        = key,
                SourcePath = path,
                RefCount   = 1,
                Loading    = true
            };

            if (onComplete != null) entry.Waiters.Add(onComplete);
            _entries[key] = entry;

            Trace($"miss {key} — decoding");
            StartCoroutine(LoadRoutine(entry));
        }

        /// <summary>
        /// Takes an extra reference on an already-resident clip. Returns false if the key
        /// is unknown or still loading, in which case the caller must use Acquire.
        /// </summary>
        public bool Retain(string path)
        {
            string key = Normalize(path);
            if (key == null || !_entries.TryGetValue(key, out var entry)) return false;
            if (entry.Clip == null) return false;

            entry.RefCount++;
            entry.IdleSince = 0f;

            Trace($"retain {key} refs={entry.RefCount}");
            return true;
        }

        /// <summary>Drops one reference. Safe with unknown keys and with null.</summary>
        public void Release(string path)
        {
            string key = Normalize(path);
            if (key == null || !_entries.TryGetValue(key, out var entry)) return;

            entry.RefCount--;

            if (entry.RefCount < 0)
            {
                Debug.LogWarning($"[ClipCache] Unbalanced Release for \"{key}\" " +
                                 "(refcount went negative). Clamped to 0.", this);
                entry.RefCount = 0;
            }

            if (entry.RefCount == 0) entry.IdleSince = Time.unscaledTime;

            Trace($"release {key} refs={entry.RefCount}");
        }

        /// <summary>Destroys every unreferenced clip now, ignoring lingerSeconds.</summary>
        public void EvictIdle()
        {
            _sweepScratch.Clear();

            foreach (var pair in _entries)
                if (pair.Value.RefCount <= 0 && !pair.Value.Loading)
                    _sweepScratch.Add(pair.Key);

            foreach (var key in _sweepScratch) Evict(key);
        }

        // ------------------------------------------------------------- loading

        private IEnumerator LoadRoutine(Entry entry)
        {
            AudioClip loaded = null;

            yield return AudioFileLoader.Load(entry.SourcePath, clip => loaded = clip);

            entry.Loading = false;

            if (loaded == null)
            {
                // AudioFileProbe has already explained the reason in the console.
                string reason = $"could not decode \"{Path.GetFileName(entry.SourcePath)}\"";

                _entries.Remove(entry.Key);

                // Not cached as a failure: the user may fix the file and retry without
                // restarting, and a stale negative entry would hide the fix.
                foreach (var waiter in entry.Waiters) waiter(null, reason);
                entry.Waiters.Clear();

                Trace($"failed {entry.Key}");
                yield break;
            }

            entry.Clip = loaded;

            // A caller may have released before the decode finished; start the clock now.
            if (entry.RefCount <= 0) entry.IdleSince = Time.unscaledTime;

            foreach (var waiter in entry.Waiters) waiter(loaded, null);
            entry.Waiters.Clear();

            Trace($"loaded {entry.Key} refs={entry.RefCount}");
        }

        // ------------------------------------------------------------ eviction

        private void Update()
        {
            if (Time.unscaledTime < _nextSweep) return;
            _nextSweep = Time.unscaledTime + 1f;

            Sweep();
        }

        private void Sweep()
        {
            float now = Time.unscaledTime;

            _sweepScratch.Clear();

            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (entry.Loading || entry.RefCount > 0) continue;
                if (now - entry.IdleSince >= lingerSeconds) _sweepScratch.Add(pair.Key);
            }

            foreach (var key in _sweepScratch) Evict(key);

            if (maxIdleClips <= 0) return;

            // Over the cap: evict the longest-idle first until we're back under it.
            while (IdleCount > maxIdleClips)
            {
                string oldestKey = null;
                float  oldest    = float.MaxValue;

                foreach (var pair in _entries)
                {
                    var entry = pair.Value;
                    if (entry.Loading || entry.RefCount > 0) continue;

                    if (entry.IdleSince < oldest)
                    {
                        oldest    = entry.IdleSince;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey == null) break;
                Evict(oldestKey);
            }
        }

        private void Evict(string key)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;

            _entries.Remove(key);

            if (entry.Clip == null) return;

            if (Application.isPlaying) Destroy(entry.Clip);
            else DestroyImmediate(entry.Clip);

            Trace($"evict {key}");
        }

        private void OnDestroy()
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Clip == null) continue;
                if (Application.isPlaying) Destroy(entry.Clip);
                else DestroyImmediate(entry.Clip);
            }

            _entries.Clear();
        }

        private void Trace(string message)
        {
            if (logTraffic) Debug.Log($"[ClipCache] {message}", this);
        }

        /// <summary>One-line summary for a debug overlay or the console.</summary>
        public string DescribeState() =>
            $"[ClipCache] {LoadedCount} clip(s) resident, {IdleCount} idle, " +
            $"{EntryCount} tracked.";
    }
}
