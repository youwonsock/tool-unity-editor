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


        #region Inner Classes / Structs

        private delegate void ApplyPathFollowerScalar(
            PathFollower pathFollower,
            float value);

        #endregion


        #region Member Variables

        [SerializeField] private MonoBehaviour _receiverObject;

        private readonly List<Coroutine> _delayedEventCoroutines =
            new List<Coroutine>();
        private Coroutine _timeScaleCoroutine;
        private Coroutine _speedControlCoroutine;
        private Coroutine _durationControlCoroutine;
        private IPathEventReceiver _eventReceiver;
        private bool _isInitialized;
        private bool _timeStateCaptured;
        private float _originalTimeScale;
        private float _originalFixedDeltaTime;
        private Action<string> _onEventDispatched;

        #endregion


        #region Properties

        public bool IsControllingSpeed => _speedControlCoroutine != null
            || _durationControlCoroutine != null;
        public bool IsInitialized => _isInitialized;

        public event Action<string> OnEventDispatched
        {
            add => _onEventDispatched += value;
            remove => _onEventDispatched -= value;
        }

        #endregion


        #region Unity Events

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

        public void Release()
        {
            CancelAllDelayedEventsCore();
            StopAllEventCoroutines();
            RestoreTimeState();
            _isInitialized = false;
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
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
            StopAllEventCoroutines();
            RestoreTimeState();
        }

        private void OnDestroy()
        {
            Release();
        }

        #endregion


        #region Public Methods

        public virtual void HandleEvent(
            PathEventSettingSO eventSetting,
            PathFollower pathAnimator)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized)
                return;
            if (eventSetting == null)
                throw new ArgumentNullException(nameof(eventSetting));

            PathEventContext context = PathEventValidator.ValidateSetting(
                eventSetting,
                pathAnimator,
                new HashSet<PathEventSettingSO>(),
                false);
            CancelAllDelayedEvents();
            ProcessEvent(eventSetting, pathAnimator, context);
        }

        public void CancelAllDelayedEvents()
        {
            if (!_isInitialized)
                return;
            CancelAllDelayedEventsCore();
        }

        #endregion


        #region Private Methods

        private void CacheEventReceiver()
        {
            _eventReceiver = _receiverObject as IPathEventReceiver;
        }

        private void ProcessEvent(
            PathEventSettingSO eventSetting,
            PathFollower pathFollower,
            PathEventContext context)
        {
            DispatchPathEvent(eventSetting.EventName, pathFollower);
            ApplyMoveLifecycle(context.Intent, eventSetting, pathFollower);

            if (eventSetting.UseTimeScaleAdjust)
                ControlTimeScale(eventSetting);

            if (context.Intent == EMoveEventIntent.ChangeValue)
            {
                switch (context.Channel)
                {
                    case EMoveControlChannel.Speed:
                        ControlMoveSpeed(context, pathFollower);
                        break;
                    case EMoveControlChannel.Duration:
                        ControlMoveDuration(context, pathFollower);
                        break;
                }
            }

            if (eventSetting.UseDelayedEvents)
                EnqueueDelayedEvents(
                    eventSetting,
                    pathFollower,
                    context.Intent == EMoveEventIntent.Pause);
        }

        private void ApplyMoveLifecycle(
            EMoveEventIntent moveIntent,
            PathEventSettingSO eventSetting,
            PathFollower pathFollower)
        {
            if (moveIntent == EMoveEventIntent.None || pathFollower == null)
                return;

            pathFollower.TryGetComponent(out QueuedPathFollower queuedFollower);
            switch (moveIntent)
            {
                case EMoveEventIntent.Pause:
                    StopCoroutineIfActive(ref _speedControlCoroutine);
                    StopCoroutineIfActive(ref _durationControlCoroutine);
                    if (queuedFollower != null)
                        queuedFollower.PauseMove();
                    else
                        pathFollower.PauseMove();

                    Debug.Log(
                        $"[TransformPath] PauseFollower event: actor='{pathFollower.name}', "
                        + $"duration={GetPauseDuration(eventSetting):F2}s",
                        pathFollower);
                    break;
                case EMoveEventIntent.Resume:
                    if (pathFollower.State != EPathFollowerState.Paused)
                        return;
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
            return eventSetting.DelayedEvents.Count == 1
                && eventSetting.DelayedEvents[0] != null
                ? eventSetting.DelayedEvents[0].Delay
                : 0f;
        }

        private void DispatchPathEvent(
            string eventName,
            PathFollower pathFollower)
        {
            if (string.IsNullOrEmpty(eventName))
                return;

            CacheEventReceiver();
            if (_eventReceiver != null)
            {
                if (pathFollower == null)
                    throw new ArgumentNullException(nameof(pathFollower));
                _eventReceiver.ReceivePathEvent(eventName, pathFollower);
            }

            _onEventDispatched?.Invoke(eventName);
        }

        private void ControlTimeScale(PathEventSettingSO setting)
        {
            StopCoroutineIfActive(ref _timeScaleCoroutine);
            _timeScaleCoroutine = StartCoroutine(Co_ProcessTimeScale(setting));
        }

        private void ControlMoveSpeed(
            PathEventContext context,
            PathFollower pathFollower)
        {
            StopCoroutineIfActive(ref _speedControlCoroutine);
            if (context.AdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.SpeedBased)
                    throw new InvalidOperationException(
                        "Move speed control requires SpeedBased mode.");
                pathFollower.Speed = context.TargetValue;
                return;
            }

            _speedControlCoroutine = StartCoroutine(
                Co_ProcessMoveSpeed(pathFollower, context));
        }

        private void ControlMoveDuration(
            PathEventContext context,
            PathFollower pathFollower)
        {
            StopCoroutineIfActive(ref _durationControlCoroutine);
            if (context.AdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.TimeBased)
                    throw new InvalidOperationException(
                        "Move duration control requires TimeBased mode.");
                pathFollower.Duration = context.TargetValue;
                return;
            }

            _durationControlCoroutine = StartCoroutine(
                Co_ProcessMoveDuration(pathFollower, context));
        }

        private void EnqueueDelayedEvents(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            bool pauseEvent)
        {
            if (setting.DelayedEvents == null || setting.DelayedEvents.Count == 0)
                return;

            for (int i = 0; i < setting.DelayedEvents.Count; i++)
            {
                PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[i];
                if (entry == null || entry.EventSetting == null)
                    throw new ArgumentException(
                        "Delayed event entries require an EventSetting.",
                        nameof(setting));
                if (!PathValueUtility.IsNonNegativeFinite(entry.Delay))
                    throw new ArgumentOutOfRangeException(
                        nameof(setting),
                        "Delayed event delay must be finite and non-negative.");

                Coroutine routine = StartCoroutine(Co_ProcessDelayedEvent(
                    entry.EventSetting,
                    pathFollower,
                    entry.Delay,
                    pauseEvent && i == 0));
                if (routine != null)
                    _delayedEventCoroutines.Add(routine);
            }
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

        private void StopCoroutineIfActive(ref Coroutine routine)
        {
            if (routine == null)
                return;

            StopCoroutine(routine);
            routine = null;
        }

        private void StopAllEventCoroutines()
        {
            StopCoroutineIfActive(ref _timeScaleCoroutine);
            StopCoroutineIfActive(ref _speedControlCoroutine);
            StopCoroutineIfActive(ref _durationControlCoroutine);
        }

        private void RestoreTimeState()
        {
            if (!_timeStateCaptured)
                return;

            Time.timeScale = _originalTimeScale;
            Time.fixedDeltaTime = _originalFixedDeltaTime;
            _timeStateCaptured = false;
        }

        private static float EvaluateAdjustCurveT(
            float timer,
            float duration,
            AnimationCurve curve)
        {
            if (duration <= 0f || !PathValueUtility.IsFinite(duration))
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (curve == null || curve.length == 0)
                throw new ArgumentException(
                    "Curve must contain at least one key.",
                    nameof(curve));
            float t = Mathf.Clamp01(timer / duration);
            return curve.Evaluate(t);
        }

        private IEnumerator Co_LerpPathFollowerScalar(
            PathFollower pathFollower,
            float startValue,
            float targetValue,
            float duration,
            AnimationCurve curve,
            ApplyPathFollowerScalar applyValue,
            Action onComplete)
        {
            float timer = 0f;
            while (timer < duration)
            {
                if (pathFollower == null)
                    throw new InvalidOperationException(
                        "Path follower was destroyed during an active event.");

                timer += Time.deltaTime;
                applyValue(
                    pathFollower,
                    Mathf.Lerp(
                        startValue,
                        targetValue,
                        EvaluateAdjustCurveT(timer, duration, curve)));
                yield return null;
            }

            if (pathFollower == null)
                throw new InvalidOperationException(
                    "Path follower was destroyed during an active event.");
            applyValue(pathFollower, targetValue);
            onComplete?.Invoke();
        }

        #endregion


        #region IEnumerator

        private IEnumerator Co_ProcessDelayedEvent(
            PathEventSettingSO eventSetting,
            PathFollower pathFollower,
            float delay,
            bool resumeOnly)
        {
            if (eventSetting == null)
                throw new ArgumentNullException(nameof(eventSetting));
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            if (this == null || !isActiveAndEnabled)
                yield break;

            PathEventContext context = PathEventValidator.ValidateSetting(
                eventSetting,
                pathFollower,
                new HashSet<PathEventSettingSO>(),
                resumeOnly);
            ProcessEvent(eventSetting, pathFollower, context);
        }

        private IEnumerator Co_ProcessTimeScale(PathEventSettingSO setting)
        {
            float startValue = Time.timeScale;
            float targetValue = setting.TimeScaleAdjustValue;
            float duration = setting.TimeScaleAdjustDuration;
            AnimationCurve curve = setting.TimeScaleAdjustCurve;
            float timer = 0f;

            while (timer < duration)
            {
                timer += Time.unscaledDeltaTime;
                float t = curve.Evaluate(Mathf.Clamp01(timer / duration));
                float currentValue = Mathf.Lerp(startValue, targetValue, t);
                Time.timeScale = currentValue;
                Time.fixedDeltaTime = BASE_FIXED_DELTA_TIME * currentValue;
                yield return null;
            }

            Time.timeScale = targetValue;
            Time.fixedDeltaTime = BASE_FIXED_DELTA_TIME * targetValue;
            _timeScaleCoroutine = null;
        }

        private IEnumerator Co_ProcessMoveSpeed(
            PathFollower pathFollower,
            PathEventContext context)
        {
            if (pathFollower.MoveType != EPathMoveType.SpeedBased)
                throw new InvalidOperationException(
                    "Move speed control requires SpeedBased mode.");

            yield return Co_LerpPathFollowerScalar(
                pathFollower,
                pathFollower.Speed,
                context.TargetValue,
                context.AdjustDuration,
                context.AdjustCurve,
                static (follower, value) => follower.Speed = value,
                () => _speedControlCoroutine = null);
        }

        private IEnumerator Co_ProcessMoveDuration(
            PathFollower pathFollower,
            PathEventContext context)
        {
            if (pathFollower.MoveType != EPathMoveType.TimeBased)
                throw new InvalidOperationException(
                    "Move duration control requires TimeBased mode.");

            yield return Co_LerpPathFollowerScalar(
                pathFollower,
                pathFollower.Duration,
                context.TargetValue,
                context.AdjustDuration,
                context.AdjustCurve,
                static (follower, value) => follower.Duration = value,
                () => _durationControlCoroutine = null);
        }

        #endregion
    }
}
