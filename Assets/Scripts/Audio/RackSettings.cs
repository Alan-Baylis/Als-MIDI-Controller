using System;
using System.Collections.Generic;
using UnityEngine;

namespace LaunchpadStudio
{
    [CreateAssetMenu(fileName = "RackSettings", menuName = "Audio/Rack Settings")]
    public class RackSettings : ScriptableObject
    {
        [Header("Voices")]
        [Tooltip("2D is correct for a sample player. Only raise this for spatial experiments.")]
        [Range(0f, 1f)] public float spatialBlend = 0f;

        [Range(0f, 1f)] public float defaultVolume = 1f;
        
        [Min(1)]    public int   sampleRate = 44100;
        [Min(0.1f)] public float generatedClipLengthSeconds = 2f;
        [Min(1f)]   public float testToneBaseFrequency = 110f;

        [Tooltip("Semitones added per pad index across the grid (11 → 88).")]
        [Range(0f, 3f)] public float testToneSemitoneStep = 0.5f;
    }
}
