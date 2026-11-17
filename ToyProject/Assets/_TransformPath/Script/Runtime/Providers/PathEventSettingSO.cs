using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Common.TransformPath
{
    /// <summary>
    /// Authoring data of one path event. A path copies it when it builds, so a
    /// change shows up after <see cref="PathProviderBehaviour.Rebuild"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "Path Event Setting", menuName = "Path Setting/New Path Event Setting", order = 1)]
    public class PathEventSettingSO : ScriptableObject
    {
        [Serializable]
        public class DelayedEventEntry
        {
            public float Delay = 0f;
            public PathEventSettingSO EventSetting = null;
        }

        [Header("이벤트 이름")]
        public string EventName;

        [Header("재생 상태")]
        [Tooltip("Pause는 재생을 멈추고 값을 바꾸지 않습니다. Resume은 일시정지한 재생을 재개합니다.")]
        public EPathEventLifecycle Lifecycle;

        [Header("속도 변경 (SpeedBased 구간에서만 적용)")]
        [FormerlySerializedAs("UseModifyPathMoveSpeed")] public bool UseSpeedChange;
        [FormerlySerializedAs("MoveSpeedTargetValue")] public float SpeedTarget = 1.0f;
        [FormerlySerializedAs("MoveSpeedAdjustDuration")] public float SpeedBlendTime;
        [FormerlySerializedAs("MoveSpeedAdjustCurve")] public AnimationCurve SpeedBlendCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Duration 변경 (TimeBased 구간에서만 적용)")]
        [FormerlySerializedAs("UseModifyPathMoveDuration")] public bool UseDurationChange;
        [FormerlySerializedAs("MoveDurationTargetValue")] public float DurationTarget = 5.0f;
        [FormerlySerializedAs("MoveDurationAdjustDuration")] public float DurationBlendTime;
        [FormerlySerializedAs("MoveDurationAdjustCurve")] public AnimationCurve DurationBlendCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("지연 이벤트 (게임 시간, 다음 경로 이벤트가 실행되면 취소)")]
        public List<DelayedEventEntry> DelayedEvents = new List<DelayedEventEntry>();
    }
}
