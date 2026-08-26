using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public class PathEventHandler : MonoBehaviour
    {
        #region Constants

        private const float BASE_FIXED_DELTA_TIME = 0.02f;

        #endregion


        #region Member Variables

        [SerializeField] private MonoBehaviour _eventSinkObject = null;

        private readonly List<Coroutine> _delayedEventCoroutines = new List<Coroutine>();
        private Coroutine _timeScaleCoroutine = null;
        private Coroutine _speedControlCoroutine = null;
        private Coroutine _durationControlCoroutine = null;
        private IPathEventSink _eventSink = null;
        private IPathEventReceiver _eventReceiver = null;

        private Action<string> _onEventDispatched = null;
        public event Action<string> OnEventDispatched
        {
            add => _onEventDispatched += value;
            remove => _onEventDispatched -= value;
        }

        #endregion


        #region Properties

        public bool IsControllingSpeed => _speedControlCoroutine != null || _durationControlCoroutine != null;

        #endregion


        #region Unity Events

        private void Awake()
        {
            CacheEventSinkReference();
        }

        private void OnValidate()
        {
            CacheEventSinkReference();
        }

        private void OnDisable()
        {
            CancelAllDelayedEvents();
            StopAllCoroutinesSafely();
        }

        #endregion


        #region Public Methods

        public virtual void HandleEvent(PathEventSettingSO eventSetting, PathFollower pathAnimator)
        {
            if (eventSetting == null)
                return;

            CancelAllDelayedEvents();
            ProcessEvent(eventSetting, pathAnimator);
        }

        public void CancelAllDelayedEvents()
        {
            for (int i = 0; i < _delayedEventCoroutines.Count; i++)
            {
                Coroutine routine = _delayedEventCoroutines[i];
                if (routine != null)
                    StopCoroutine(routine);
            }

            _delayedEventCoroutines.Clear();
        }

        #endregion


        #region Private Methods

        private void CacheEventSinkReference()
        {
            _eventSink = _eventSinkObject as IPathEventSink;
            _eventReceiver = _eventSinkObject as IPathEventReceiver;
        }

        private void ProcessEvent(PathEventSettingSO eventSetting, PathFollower pathFollower)
        {
            DispatchPathEvent(eventSetting.EventName, pathFollower);

            if (eventSetting.UseTimeScaleAdjust)
                ControlTimeScale(eventSetting);

            if (pathFollower != null && eventSetting.UseModifyPathMoveSpeed)
                ControlMoveSpeed(eventSetting, pathFollower);

            if (pathFollower != null && eventSetting.UseModifyPathMoveDuration)
                ControlMoveDuration(eventSetting, pathFollower);

            if (eventSetting.UseDelayedEvents)
                EnqueueDelayedEvents(eventSetting, pathFollower);
        }

        private void DispatchPathEvent(string eventName, PathFollower pathFollower)
        {
            if (string.IsNullOrEmpty(eventName))
                return;

            IPathEventReceiver receiver = _eventReceiver ?? PathEventBroker.Receiver;
            if (receiver != null)
            {
                receiver.ReceivePathEvent(eventName, pathFollower);
            }
            else
            {
                IPathEventSink sink = _eventSink ?? PathEventBroker.Sink;
                sink?.SendPathEvent(eventName, pathFollower);
            }

            _onEventDispatched?.Invoke(eventName);
        }

        private void ControlTimeScale(PathEventSettingSO setting)
        {
            StopCoroutineSafe(ref _timeScaleCoroutine);
            _timeScaleCoroutine = StartCoroutine(Co_ProcessTimeScale(setting));
        }

        private void ControlMoveSpeed(PathEventSettingSO setting, PathFollower pathFollower)
        {
            StopCoroutineSafe(ref _speedControlCoroutine);
            SuspendQueuedSpeedSmoothIfPresent(pathFollower);

            if (setting.MoveSpeedAdjustDuration <= 0f)
            {
                if (pathFollower.CurrentMoveType != PathFollower.EMoveType.SpeedBased)
                {
                    Debug.LogWarning("PathEventHandler: 이동 속도 제어는 SpeedBased 모드에서만 동작합니다.");
                    return;
                }

                // instant 변경은 Speed=만 사용 (SetSpeed/ApplyAnimatorSpeed 미호출 — pause 시 animator freeze 방지)
                pathFollower.Speed = setting.MoveSpeedTargetValue;
                return;
            }

            _speedControlCoroutine = StartCoroutine(Co_ProcessMoveSpeed(setting, pathFollower));
        }

        private void ControlMoveDuration(PathEventSettingSO setting, PathFollower pathFollower)
        {
            StopCoroutineSafe(ref _durationControlCoroutine);
            SuspendQueuedSpeedSmoothIfPresent(pathFollower);

            if (setting.MoveDurationAdjustDuration <= 0f)
            {
                if (pathFollower.CurrentMoveType != PathFollower.EMoveType.TimeBased)
                {
                    Debug.LogWarning("PathEventHandler: Duration 제어는 TimeBased 모드에서만 동작합니다.");
                    return;
                }

                pathFollower.Duration = setting.MoveDurationTargetValue;
                return;
            }

            _durationControlCoroutine = StartCoroutine(Co_ProcessMoveDuration(setting, pathFollower));
        }

        private void SuspendQueuedSpeedSmoothIfPresent(PathFollower pathFollower)
        {
            if (pathFollower != null && pathFollower.TryGetComponent(out QueuedPathFollower queuedFollower))
                queuedFollower.SuspendSpeedSmooth();
        }

        private void EnqueueDelayedEvents(PathEventSettingSO setting, PathFollower pathFollower)
        {
            if (setting.DelayedEvents == null || setting.DelayedEvents.Count == 0)
                return;

            foreach (PathEventSettingSO.DelayedEventEntry entry in setting.DelayedEvents)
            {
                if (entry == null || entry.EventSetting == null)
                    continue;

                Coroutine routine = StartCoroutine(Co_ProcessDelayedEvent(entry.EventSetting, pathFollower, entry.Delay));
                if (routine != null)
                    _delayedEventCoroutines.Add(routine);
            }
        }

        private void StopCoroutineSafe(ref Coroutine routine)
        {
            if (routine == null)
                return;

            StopCoroutine(routine);
            routine = null;
        }

        private void StopAllCoroutinesSafely()
        {
            StopCoroutineSafe(ref _timeScaleCoroutine);
            StopCoroutineSafe(ref _speedControlCoroutine);
            StopCoroutineSafe(ref _durationControlCoroutine);
        }

        private static float EvaluateAdjustCurveT(float timer, float duration, AnimationCurve curve)
        {
            float t = Mathf.Clamp01(timer / duration);
            if (curve != null && curve.length > 0)
                t = curve.Evaluate(t);
            return t;
        }

        private delegate void ApplyPathFollowerScalar(PathFollower pathFollower, float value);

        private IEnumerator Co_LerpPathFollowerScalar(
            PathFollower pathFollower,
            float startVal,
            float targetVal,
            float duration,
            AnimationCurve curve,
            ApplyPathFollowerScalar applyValue,
            Action onComplete)
        {
            float timer = 0f;

            while (timer < duration)
            {
                if (pathFollower == null)
                {
                    onComplete?.Invoke();
                    yield break;
                }

                timer += Time.deltaTime;
                applyValue(pathFollower, Mathf.Lerp(startVal, targetVal, EvaluateAdjustCurveT(timer, duration, curve)));
                yield return null;
            }

            if (pathFollower != null)
                applyValue(pathFollower, targetVal);

            onComplete?.Invoke();
        }

        #endregion


        #region IEnumerator

        private IEnumerator Co_ProcessDelayedEvent(PathEventSettingSO eventSetting, PathFollower pathFollower, float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            if (this == null || !isActiveAndEnabled)
                yield break;

            ProcessEvent(eventSetting, pathFollower);
        }

        private IEnumerator Co_ProcessTimeScale(PathEventSettingSO setting)
        {
            float startVal = Time.timeScale;
            float targetVal = setting.TimeScaleAdjustValue;
            float duration = setting.TimeScaleAdjustDuration;
            AnimationCurve curve = setting.TimeScaleAdjustCurve;

            float timer = 0f;

            while (timer < duration)
            {
                timer += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(timer / duration);
                if (curve != null && curve.length > 0)
                    t = curve.Evaluate(t);

                float currentVal = Mathf.Lerp(startVal, targetVal, t);
                Time.timeScale = currentVal;
                Time.fixedDeltaTime = BASE_FIXED_DELTA_TIME * currentVal;

                yield return null;
            }

            Time.timeScale = targetVal;
            Time.fixedDeltaTime = BASE_FIXED_DELTA_TIME * targetVal;
            _timeScaleCoroutine = null;
        }

        private IEnumerator Co_ProcessMoveSpeed(PathEventSettingSO setting, PathFollower pathFollower)
        {
            if (pathFollower.CurrentMoveType != PathFollower.EMoveType.SpeedBased)
            {
                Debug.LogWarning("PathEventHandler: 이동 속도 제어는 SpeedBased 모드에서만 동작합니다.");
                _speedControlCoroutine = null;
                yield break;
            }

            yield return Co_LerpPathFollowerScalar(
                pathFollower,
                pathFollower.Speed,
                setting.MoveSpeedTargetValue,
                setting.MoveSpeedAdjustDuration,
                setting.MoveSpeedAdjustCurve,
                static (follower, value) => follower.Speed = value,
                () => _speedControlCoroutine = null);
        }

        private IEnumerator Co_ProcessMoveDuration(PathEventSettingSO setting, PathFollower pathFollower)
        {
            if (pathFollower.CurrentMoveType != PathFollower.EMoveType.TimeBased)
            {
                Debug.LogWarning("PathEventHandler: Duration 제어는 TimeBased 모드에서만 동작합니다.");
                _durationControlCoroutine = null;
                yield break;
            }

            yield return Co_LerpPathFollowerScalar(
                pathFollower,
                pathFollower.Duration,
                setting.MoveDurationTargetValue,
                setting.MoveDurationAdjustDuration,
                setting.MoveDurationAdjustCurve,
                static (follower, value) => follower.Duration = value,
                () => _durationControlCoroutine = null);
        }

        #endregion
    }
}
