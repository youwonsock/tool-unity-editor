using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Authoring data of one path event. A path copies it when it builds, so a
    /// change shows up after <see cref="PathProviderBehaviour.Rebuild"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "Path Event Setting", menuName = "Path Setting/New Path Event Setting", order = 1)]
    public sealed class PathEventSettingSO : ScriptableObject
    {
        [Serializable]
        public sealed class DelayedEventEntry
        {
            public float Delay = 0f;
            public PathEventSettingSO EventSetting = null;
        }

        [Header("Event Name")]
        public string EventName;

        [Header("Playback State")]
        [Tooltip("Pause stops the playback without changing values. Resume continues a paused playback.")]
        public EPathEventLifecycle Lifecycle;

        [Header("Speed Change (SpeedBased segments only)")]
        public bool UseSpeedChange;
        public float SpeedTarget = 1.0f;
        public float SpeedBlendTime;
        public AnimationCurve SpeedBlendCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Duration Change (TimeBased segments only)")]
        public bool UseDurationChange;
        public float DurationTarget = 5.0f;
        public float DurationBlendTime;
        public AnimationCurve DurationBlendCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Delayed Events (game time, canceled when a later event changes the playback)")]
        public List<DelayedEventEntry> DelayedEvents = new List<DelayedEventEntry>();
    }
}
