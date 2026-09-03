using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    [DefaultExecutionOrder(-200)]
    public class PathEventHandler : MonoBehaviour
    {
        #region Constants

        private const float BASE_FIXED_DELTA_TIME = 0.02f;

        #endregion


        #region Member Variables

        [SerializeField] private MonoBehaviour _receiverObject = null;

        private readonly List<Coroutine> _delayedEventCoroutines = new List<Coroutine>();
        private Coroutine _timeScaleCoroutine = null;
        private Coroutine _speedControlCoroutine = null;
        private Coroutine _durationControlCoroutine = null;
        private IPathEventReceiver _eventReceiver = null;
        private bool _isInitialized;
        private bool _timeStateCaptured;
        private float _originalTimeScale;
        private float _originalFixedDeltaTime;

        private Action<string> _onEventDispatched = null;
        public event Action<string> OnEventDispatched
        {
            add => _onEventDispatched += value;
            remove => _onEventDispatched -= value;
        }

        #endregion


        #region Properties

        public bool IsControllingSpeed => _speedControlCoroutine != null || _durationControlCoroutine != null;
        public bool IsInitialized => _isInitialized;

        #endregion


        #region Unity Events

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                return;

            CacheEventReceiver();
            _originalTimeScale = Time.timeScale;
            _originalFixedDeltaTime = Time.fixedDeltaTime;
            _timeStateCaptured = true;
            _isInitialized = true;
        }

        private void OnValidate()
        {
            // Serialized references are validated at Init/HandleEvent.
        }

        private void OnEnable()
        {
            if (!_isInitialized)
                return;

            _originalTimeScale = Time.timeScale;
            _originalFixedDeltaTime = Time.fixedDeltaTime;
            _timeStateCaptured = true;
        }

        private void OnDisable()
        {
            CancelAllDelayedEventsCore();
            StopAllCoroutinesSafely();
            if (_timeStateCaptured)
            {
                Time.timeScale = _originalTimeScale;
                Time.fixedDeltaTime = _originalFixedDeltaTime;
            }
            _timeStateCaptured = false;
        }

        private void OnDestroy()
        {
            Release();
        }

        public void Release()
        {
            CancelAllDelayedEventsCore();
            StopAllCoroutinesSafely();
            if (_timeStateCaptured)
            {
                Time.timeScale = _originalTimeScale;
                Time.fixedDeltaTime = _originalFixedDeltaTime;
            }
            _timeStateCaptured = false;
            _isInitialized = false;
        }

        #endregion


        #region Public Methods

        public virtual void HandleEvent(PathEventSettingSO eventSetting, PathFollower pathAnimator)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized)
                return;
            if (eventSetting == null)
                throw new ArgumentNullException(nameof(eventSetting));

            ValidateSetting(eventSetting, pathAnimator, new HashSet<PathEventSettingSO>());
            CancelAllDelayedEvents();
            ProcessEvent(eventSetting, pathAnimator);
        }

        public void CancelAllDelayedEvents()
        {
            if (!_isInitialized)
                return;
            CancelAllDelayedEventsCore();
        }

        private void CancelAllDelayedEventsCore()
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

        private void CacheEventReceiver()
        {
            _eventReceiver = _receiverObject as IPathEventReceiver;
        }

        private void ProcessEvent(PathEventSettingSO eventSetting, PathFollower pathFollower)
        {
            // Validate every operation that can throw before dispatching a message or
            // starting a coroutine. A malformed movement event must not partially
            // mutate the receiver or the follower.
            if (eventSetting.UseModifyPathMoveSpeed && pathFollower == null)
                throw new ArgumentNullException(nameof(pathFollower));
            if (eventSetting.UseModifyPathMoveDuration && pathFollower == null)
                throw new ArgumentNullException(nameof(pathFollower));
            if (eventSetting.UseModifyPathMoveSpeed
                && pathFollower.MoveType != EPathMoveType.SpeedBased)
                throw new InvalidOperationException("Move speed control requires SpeedBased mode.");
            if (eventSetting.UseModifyPathMoveDuration
                && pathFollower.MoveType != EPathMoveType.TimeBased)
                throw new InvalidOperationException("Move duration control requires TimeBased mode.");

            DispatchPathEvent(eventSetting.EventName, pathFollower);

            ApplyFollowerAction(eventSetting, pathFollower);

            if (eventSetting.UseTimeScaleAdjust)
                ControlTimeScale(eventSetting);

            if (eventSetting.UseModifyPathMoveSpeed)
                ControlMoveSpeed(eventSetting, pathFollower);

            if (eventSetting.UseModifyPathMoveDuration)
                ControlMoveDuration(eventSetting, pathFollower);

            if (eventSetting.UseDelayedEvents)
                EnqueueDelayedEvents(eventSetting, pathFollower);
        }

        private void ApplyFollowerAction(PathEventSettingSO eventSetting, PathFollower pathFollower)
        {
            if (eventSetting == null || pathFollower == null)
                return;

            QueuedPathFollower queuedFollower = pathFollower.GetComponent<QueuedPathFollower>();
            switch (eventSetting.FollowerAction)
            {
                case EFollowerEventAction.Pause:
                    if (queuedFollower != null)
                        queuedFollower.PauseMove();
                    else
                        pathFollower.PauseMove();

                    Debug.Log(
                        $"[TransformPath] PauseFollower event: actor='{pathFollower.name}', duration={GetPauseDuration(eventSetting):F2}s",
                        pathFollower);
                    break;
                case EFollowerEventAction.Resume:
                    if (queuedFollower != null)
                        queuedFollower.ResumeMove();
                    else
                        pathFollower.ResumeMove();
                    break;
            }
        }

        private static float GetPauseDuration(PathEventSettingSO eventSetting)
        {
            if (eventSetting == null || eventSetting.DelayedEvents == null)
                return 0f;
            for (int i = 0; i < eventSetting.DelayedEvents.Count; i++)
            {
                PathEventSettingSO.DelayedEventEntry entry = eventSetting.DelayedEvents[i];
                if (entry != null && entry.EventSetting != null
                    && entry.EventSetting.FollowerAction == EFollowerEventAction.Resume)
                    return entry.Delay;
            }
            return 0f;
        }

        private void DispatchPathEvent(string eventName, PathFollower pathFollower)
        {
            if (string.IsNullOrEmpty(eventName))
                return;

            CacheEventReceiver();

            if (_eventReceiver != null)
            {
                if (pathFollower == null)
                    throw new ArgumentNullException(nameof(pathFollower));
                try
                {
                    _eventReceiver.ReceivePathEvent(eventName, pathFollower);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
            // A receiver is optional. Movement effects and OnEventDispatched
            // remain useful on a handler that has no message receiver.

            Delegate[] listeners = _onEventDispatched?.GetInvocationList();
            if (listeners == null)
                return;
            for (int i = 0; i < listeners.Length; i++)
            {
                try { ((Action<string>)listeners[i])(eventName); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }

        private void ControlTimeScale(PathEventSettingSO setting)
        {
            StopCoroutineSafe(ref _timeScaleCoroutine);
            _timeScaleCoroutine = StartCoroutine(Co_ProcessTimeScale(setting));
        }

        private void ControlMoveSpeed(PathEventSettingSO setting, PathFollower pathFollower)
        {
            StopCoroutineSafe(ref _speedControlCoroutine);

            if (setting.MoveSpeedAdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.SpeedBased)
                {
                    throw new InvalidOperationException("Move speed control requires SpeedBased mode.");
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

            if (setting.MoveDurationAdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.TimeBased)
                {
                    throw new InvalidOperationException("Move duration control requires TimeBased mode.");
                }

                pathFollower.Duration = setting.MoveDurationTargetValue;
                return;
            }

            _durationControlCoroutine = StartCoroutine(Co_ProcessMoveDuration(setting, pathFollower));
        }

        private void EnqueueDelayedEvents(PathEventSettingSO setting, PathFollower pathFollower)
        {
            if (setting.DelayedEvents == null || setting.DelayedEvents.Count == 0)
                return;

            foreach (PathEventSettingSO.DelayedEventEntry entry in setting.DelayedEvents)
            {
                if (entry == null || entry.EventSetting == null)
                    throw new ArgumentException("Delayed event entries require an EventSetting.", nameof(setting));
                if (float.IsNaN(entry.Delay) || float.IsInfinity(entry.Delay) || entry.Delay < 0f)
                    throw new ArgumentOutOfRangeException(nameof(setting), "Delayed event delay must be finite and non-negative.");

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
            if (duration <= 0f || float.IsNaN(duration) || float.IsInfinity(duration))
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (curve == null)
                throw new ArgumentNullException(nameof(curve));
            if (curve.length == 0)
                throw new ArgumentException("Curve must contain at least one key.", nameof(curve));
            float t = Mathf.Clamp01(timer / duration);
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
                    throw new InvalidOperationException("Path follower was destroyed during an active event.");

                timer += Time.deltaTime;
                applyValue(pathFollower, Mathf.Lerp(startVal, targetVal, EvaluateAdjustCurveT(timer, duration, curve)));
                yield return null;
            }

            if (pathFollower == null)
                throw new InvalidOperationException("Path follower was destroyed during an active event.");
            applyValue(pathFollower, targetVal);

            onComplete?.Invoke();
        }

        #endregion


        #region IEnumerator

        private IEnumerator Co_ProcessDelayedEvent(PathEventSettingSO eventSetting, PathFollower pathFollower, float delay)
        {
            if (eventSetting == null)
                throw new ArgumentNullException(nameof(eventSetting));
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
                float t = curve.Evaluate(Mathf.Clamp01(timer / duration));

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
            if (pathFollower.MoveType != EPathMoveType.SpeedBased)
            {
                throw new InvalidOperationException("Move speed control requires SpeedBased mode.");
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
            if (pathFollower.MoveType != EPathMoveType.TimeBased)
            {
                throw new InvalidOperationException("Move duration control requires TimeBased mode.");
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

        private void ValidateSetting(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            HashSet<PathEventSettingSO> validationStack)
        {
            if (setting == null)
                throw new ArgumentNullException(nameof(setting));
            if (validationStack == null)
                throw new ArgumentNullException(nameof(validationStack));
            if (!validationStack.Add(setting))
                throw new ArgumentException("Delayed path event settings cannot contain a cycle.", nameof(setting));

            try
            {
                if (!Enum.IsDefined(typeof(EFollowerEventAction), setting.FollowerAction))
                    throw new ArgumentOutOfRangeException(nameof(setting.FollowerAction));
                if (setting.FollowerAction != EFollowerEventAction.None && pathFollower == null)
                    throw new ArgumentNullException(nameof(pathFollower));
                if (setting.FollowerAction == EFollowerEventAction.Pause)
                    ValidatePauseAction(setting);
                if (setting.FollowerAction == EFollowerEventAction.Resume
                    && setting.UseDelayedEvents
                    && setting.DelayedEvents != null
                    && setting.DelayedEvents.Count > 0)
                    throw new ArgumentException("Resume follower events cannot enqueue delayed events.", nameof(setting));

                ValidateNonNegativeFinite(setting.MoveSpeedAdjustDuration, nameof(setting.MoveSpeedAdjustDuration));
                ValidateNonNegativeFinite(setting.MoveDurationAdjustDuration, nameof(setting.MoveDurationAdjustDuration));
                ValidateNonNegativeFinite(setting.TimeScaleAdjustDuration, nameof(setting.TimeScaleAdjustDuration));
                if (setting.UseDelayedEvents && setting.DelayedEvents == null)
                    throw new ArgumentException("Delayed event list is required when delayed events are enabled.", nameof(setting));
                if (setting.UseModifyPathMoveSpeed)
                {
                    if (pathFollower == null)
                        throw new ArgumentNullException(nameof(pathFollower));
                    if (pathFollower.MoveType != EPathMoveType.SpeedBased)
                        throw new InvalidOperationException("Move speed control requires SpeedBased mode.");
                    if (setting.MoveSpeedAdjustDuration > 0f && setting.MoveSpeedAdjustCurve == null)
                        throw new ArgumentNullException(nameof(setting.MoveSpeedAdjustCurve));
                    if (setting.MoveSpeedAdjustDuration > 0f && setting.MoveSpeedAdjustCurve.length == 0)
                        throw new ArgumentException("Move speed adjustment requires a non-empty curve.", nameof(setting.MoveSpeedAdjustCurve));
                    if (setting.MoveSpeedTargetValue <= 0f || float.IsNaN(setting.MoveSpeedTargetValue) || float.IsInfinity(setting.MoveSpeedTargetValue))
                        throw new ArgumentOutOfRangeException(nameof(setting));
                }
                if (setting.UseModifyPathMoveDuration)
                {
                    if (pathFollower == null)
                        throw new ArgumentNullException(nameof(pathFollower));
                    if (pathFollower.MoveType != EPathMoveType.TimeBased)
                        throw new InvalidOperationException("Move duration control requires TimeBased mode.");
                    if (setting.MoveDurationAdjustDuration > 0f && setting.MoveDurationAdjustCurve == null)
                        throw new ArgumentNullException(nameof(setting.MoveDurationAdjustCurve));
                    if (setting.MoveDurationAdjustDuration > 0f && setting.MoveDurationAdjustCurve.length == 0)
                        throw new ArgumentException("Move duration adjustment requires a non-empty curve.", nameof(setting.MoveDurationAdjustCurve));
                    if (setting.MoveDurationTargetValue <= 0f || float.IsNaN(setting.MoveDurationTargetValue) || float.IsInfinity(setting.MoveDurationTargetValue))
                        throw new ArgumentOutOfRangeException(nameof(setting));
                }
                if (setting.UseTimeScaleAdjust)
                {
                    if (setting.TimeScaleAdjustDuration <= 0f || setting.TimeScaleAdjustValue <= 0f
                        || float.IsNaN(setting.TimeScaleAdjustValue) || float.IsInfinity(setting.TimeScaleAdjustValue))
                        throw new ArgumentOutOfRangeException(nameof(setting));
                    if (setting.TimeScaleAdjustCurve == null)
                        throw new ArgumentNullException(nameof(setting.TimeScaleAdjustCurve));
                    if (setting.TimeScaleAdjustCurve.length == 0)
                        throw new ArgumentException("Time scale adjustment requires a non-empty curve.", nameof(setting.TimeScaleAdjustCurve));
                }
                if (setting.UseDelayedEvents)
                {
                    if (setting.DelayedEvents == null)
                        throw new ArgumentException("Delayed event list is required when delayed events are enabled.", nameof(setting));
                    for (int i = 0; i < setting.DelayedEvents.Count; i++)
                    {
                        PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[i];
                        if (entry == null || entry.EventSetting == null)
                            throw new ArgumentException("Delayed event entries require an EventSetting.", nameof(setting));
                        if (float.IsNaN(entry.Delay) || float.IsInfinity(entry.Delay) || entry.Delay < 0f)
                            throw new ArgumentOutOfRangeException(nameof(setting), "Delayed event delay must be finite and non-negative.");
                        ValidateSetting(entry.EventSetting, pathFollower, validationStack);
                    }
                }
            }
            finally
            {
                validationStack.Remove(setting);
            }
        }

        private static void ValidatePauseAction(PathEventSettingSO setting)
        {
            if (!setting.UseDelayedEvents || setting.DelayedEvents == null || setting.DelayedEvents.Count != 1)
                throw new ArgumentException("Pause follower events require exactly one delayed Resume event.", nameof(setting));

            PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[0];
            if (entry == null || entry.EventSetting == null)
                throw new ArgumentException("Pause follower events require a delayed Resume event.", nameof(setting));
            if (entry.EventSetting.FollowerAction != EFollowerEventAction.Resume)
                throw new ArgumentException("Pause follower events must delay a Resume follower event.", nameof(setting));
            ValidateNonNegativeFinite(entry.Delay, nameof(entry.Delay));
            if (entry.Delay <= 0f)
                throw new ArgumentOutOfRangeException(nameof(entry.Delay), "Pause duration must be greater than zero.");
        }

        private static void ValidateNonNegativeFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
