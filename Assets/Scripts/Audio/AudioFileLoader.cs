using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace LaunchpadStudio
{
    /// <summary>
    /// Minimal path → AudioClip decode. Replaced by a refcounted, deduplicating
    /// ClipCache in the drag-and-drop pass.
    /// </summary>
    public static class AudioFileLoader
    {
        public static IEnumerator Load(string absolutePath, Action<AudioClip> onComplete)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                Debug.LogError($"No such file: \"{absolutePath}\"");
                onComplete?.Invoke(null);
                yield break;
            }

            var type = GuessAudioType(absolutePath);

            if (type == AudioType.UNKNOWN)
            {
                Debug.LogError($"Unsupported extension \"{Path.GetExtension(absolutePath)}\" " +
                               $"({AudioFileProbe.Inspect(absolutePath)})");
                onComplete?.Invoke(null);
                yield break;
            }

            // Uri handles spaces and non-ASCII that naive "file://" + path does not.
            var uri = new Uri(absolutePath).AbsoluteUri;

            using (var request = UnityWebRequestMultimedia.GetAudioClip(uri, type))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Failed to load \"{absolutePath}\": {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);

                // FMOD's own message says only "Unsupported file or audio format".
                // The probe says which, so the fix is obvious from the console.
                if (clip == null || clip.samples == 0 ||
                    clip.loadState == AudioDataLoadState.Failed)
                {
                    Debug.LogError($"Could not decode \"{Path.GetFileName(absolutePath)}\": " +
                                   AudioFileProbe.Inspect(absolutePath).Summary);

                    if (clip != null) UnityEngine.Object.Destroy(clip);
                    onComplete?.Invoke(null);
                    yield break;
                }

                clip.name = Path.GetFileNameWithoutExtension(absolutePath);
                onComplete?.Invoke(clip);
            }
        }

        public static AudioType GuessAudioType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".wav":               return AudioType.WAV;
                case ".ogg":               return AudioType.OGGVORBIS;
                case ".aif": case ".aiff": return AudioType.AIFF;
                case ".mp3":               return AudioType.MPEG;
                default:                   return AudioType.UNKNOWN;            }
        }
    }
}