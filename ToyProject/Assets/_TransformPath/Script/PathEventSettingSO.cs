using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    [CreateAssetMenu(fileName = "Path Event Setting", menuName = "Path Setting/New Path Event Setting", order = 1)]
    public class PathEventSettingSO : ScriptableObject
    {
        #region Inner Classes / Structs

        [Serializable]
        public class DelayedEventEntry
        {
            public float Delay = 0f;
            public PathEventSettingSO EventSetting = null;
        }

        #endregion


        #region Member Variables

        [Header("이벤트 이름")]
        public string EventName;

        [Header("Path 이동 속도 및 Follower lifecycle 제어 (SpeedBased 자동 선택)")]
        [Tooltip("현재 follower가 SpeedBased이면 이 설정을 사용합니다. 목표 속도 0은 Pause, 일시정지 중 양수 속도는 Resume 신호, 그 외 양수 속도는 Speed 변경으로 처리됩니다. TimeBased follower에서는 무시됩니다.")]
        public bool UseModifyPathMoveSpeed;
        public float MoveSpeedTargetValue = 1.0f;
        public float MoveSpeedAdjustDuration;
        public AnimationCurve MoveSpeedAdjustCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Path 이동 Duration 제어 (TimeBased 자동 선택)")]
        [Tooltip("현재 follower가 TimeBased이면 이 설정을 사용합니다. Duration 9999는 Pause, 일시정지 중 0 초과 9999 미만 값은 Resume 신호, 그 외 값은 Duration 변경으로 처리됩니다. SpeedBased follower에서는 무시됩니다.")]
        public bool UseModifyPathMoveDuration;
        public float MoveDurationTargetValue = 5.0f;
        public float MoveDurationAdjustDuration;
        public AnimationCurve MoveDurationAdjustCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("타임 스케일 조정 사용")]
        public bool UseTimeScaleAdjust;
        public float TimeScaleAdjustValue;
        public float TimeScaleAdjustDuration;
        public AnimationCurve TimeScaleAdjustCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("지연 이벤트 목록 (경로 상 다음 이벤트 트리거 시 취소됨)")]
        public bool UseDelayedEvents;
        public List<DelayedEventEntry> DelayedEvents = new List<DelayedEventEntry>();

        #endregion


        #region Unity Events

        private void OnValidate()
        {
            // Serialized values are validated at the event dispatch boundary.
        }

        #endregion
    }
}
