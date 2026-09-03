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
        private const float MAX_MOVE_VALUE = 9999f;
        private const float TIME_BASED_PAUSE_VALUE = 9999f;

        private enum EMoveControlChannel
        {
            None,
            Speed,
            Duration,
        }

        private enum EMoveEventIntent
        {
            None,
            Pause,
            Resume,
            ChangeValue,
        }

        private readonly struct MoveEventContext
        {
            public readonly EMoveControlChannel Channel;
            public readonly EMoveEventIntent Intent;
            public readonly float TargetValue;
            public readonly float AdjustDuration;
            public readonly AnimationCurve AdjustCurve;

            public MoveEventContext(
                EMoveControlChannel channel,
                EMoveEventIntent intent,
                float targetValue,
                float adjustDuration,
                AnimationCurve adjustCurve)
            {
                Channel = channel;
                Intent = intent;
                TargetValue = targetValue;
                AdjustDuration = adjustDuration;
                AdjustCurve = adjustCurve;
            }
        }

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

            MoveEventContext context = ValidateSetting(
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

        private void ProcessEvent(
            PathEventSettingSO eventSetting,
            PathFollower pathFollower,
            MoveEventContext context)
        {
            // Validate every operation that can throw before dispatching a message or
            // starting a coroutine. A malformed movement event must not partially
            // mutate the receiver or the follower.
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
                EnqueueDelayedEvents(eventSetting, pathFollower, context.Intent == EMoveEventIntent.Pause);
        }

        private static MoveEventContext ResolveMoveEventContext(
            PathEventSettingSO eventSetting,
            PathFollower pathFollower,
            bool resumeOnly)
        {
            if (eventSetting == null)
                throw new ArgumentNullException(nameof(eventSetting));

            bool hasMovementControl = eventSetting.UseModifyPathMoveSpeed
                || eventSetting.UseModifyPathMoveDuration;
            if (pathFollower == null)
            {
                if (hasMovementControl)
                    throw new ArgumentNullException(nameof(pathFollower));
                return default;
            }

            switch (pathFollower.MoveType)
            {
                case EPathMoveType.SpeedBased:
                    if (!eventSetting.UseModifyPathMoveSpeed)
                        return default;
                    return new MoveEventContext(
                        EMoveControlChannel.Speed,
                        ResolveSpeedEventIntent(eventSetting.MoveSpeedTargetValue, pathFollower, resumeOnly),
                        eventSetting.MoveSpeedTargetValue,
                        eventSetting.MoveSpeedAdjustDuration,
                        eventSetting.MoveSpeedAdjustCurve);
                case EPathMoveType.TimeBased:
                    if (!eventSetting.UseModifyPathMoveDuration)
                        return default;
                    return new MoveEventContext(
                        EMoveControlChannel.Duration,
                        ResolveDurationEventIntent(eventSetting.MoveDurationTargetValue, pathFollower, resumeOnly),
                        eventSetting.MoveDurationTargetValue,
                        eventSetting.MoveDurationAdjustDuration,
                        eventSetting.MoveDurationAdjustCurve);
                default:
                    throw new InvalidOperationException("Path follower has an unsupported move mode.");
            }
        }

        private static EMoveEventIntent ResolveSpeedEventIntent(
            float targetValue,
            PathFollower pathFollower,
            bool resumeOnly)
        {
            if (targetValue == 0f)
                return EMoveEventIntent.Pause;
            if (resumeOnly || pathFollower.State == EPathFollowerState.Paused)
                return EMoveEventIntent.Resume;
            return EMoveEventIntent.ChangeValue;
        }

        private static EMoveEventIntent ResolveDurationEventIntent(
            float targetValue,
            PathFollower pathFollower,
            bool resumeOnly)
        {
            if (targetValue == TIME_BASED_PAUSE_VALUE)
                return EMoveEventIntent.Pause;
            if (resumeOnly || pathFollower.State == EPathFollowerState.Paused)
                return EMoveEventIntent.Resume;
            return EMoveEventIntent.ChangeValue;
        }

        private void ApplyMoveLifecycle(
            EMoveEventIntent moveIntent,
            PathEventSettingSO eventSetting,
            PathFollower pathFollower)
        {
            if (moveIntent == EMoveEventIntent.None || pathFollower == null)
                return;

            QueuedPathFollower queuedFollower = pathFollower.GetComponent<QueuedPathFollower>();
            switch (moveIntent)
            {
                case EMoveEventIntent.Pause:
                    StopCoroutineSafe(ref _speedControlCoroutine);
                    StopCoroutineSafe(ref _durationControlCoroutine);
                    if (queuedFollower != null)
                        queuedFollower.PauseMove();
                    else
                        pathFollower.PauseMove();

                    Debug.Log(
                        $"[TransformPath] PauseFollower event: actor='{pathFollower.name}', duration={GetPauseDuration(eventSetting):F2}s",
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
            return eventSetting.DelayedEvents.Count == 1 && eventSetting.DelayedEvents[0] != null
                ? eventSetting.DelayedEvents[0].Delay
                : 0f;
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

        private void ControlMoveSpeed(MoveEventContext context, PathFollower pathFollower)
        {
            StopCoroutineSafe(ref _speedControlCoroutine);

            if (context.AdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.SpeedBased)
                {
                    throw new InvalidOperationException("Move speed control requires SpeedBased mode.");
                }

                // instant 변경은 Speed=만 사용 (SetSpeed/ApplyAnimatorSpeed 미호출 — pause 시 animator freeze 방지)
                pathFollower.Speed = context.TargetValue;
                return;
            }

            _speedControlCoroutine = StartCoroutine(Co_ProcessMoveSpeed(pathFollower, context));
        }

        private void ControlMoveDuration(MoveEventContext context, PathFollower pathFollower)
        {
            StopCoroutineSafe(ref _durationControlCoroutine);

            if (context.AdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.TimeBased)
                {
                    throw new InvalidOperationException("Move duration control requires TimeBased mode.");
                }

                pathFollower.Duration = context.TargetValue;
                return;
            }

            _durationControlCoroutine = StartCoroutine(Co_ProcessMoveDuration(pathFollower, context));
        }

        private void EnqueueDelayedEvents(PathEventSettingSO setting, PathFollower pathFollower, bool pauseEvent)
        {
            if (setting.DelayedEvents == null || setting.DelayedEvents.Count == 0)
                return;

            for (int i = 0; i < setting.DelayedEvents.Count; i++)
            {
                PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[i];
                if (entry == null || entry.EventSetting == null)
                    throw new ArgumentException("Delayed event entries require an EventSetting.", nameof(setting));
                if (float.IsNaN(entry.Delay) || float.IsInfinity(entry.Delay) || entry.Delay < 0f)
                    throw new ArgumentOutOfRangeException(nameof(setting), "Delayed event delay must be finite and non-negative.");

                Coroutine routine = StartCoroutine(Co_ProcessDelayedEvent(
                    entry.EventSetting,
                    pathFollower,
                    entry.Delay,
                    pauseEvent && i == 0));
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

            MoveEventContext context = ValidateSetting(
                eventSetting,
                pathFollower,
                new HashSet<PathEventSettingSO>(),
                resumeOnly);
            ProcessEvent(eventSetting, pathFollower, context);
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

        private IEnumerator Co_ProcessMoveSpeed(PathFollower pathFollower, MoveEventContext context)
        {
            if (pathFollower.MoveType != EPathMoveType.SpeedBased)
            {
                throw new InvalidOperationException("Move speed control requires SpeedBased mode.");
            }

            yield return Co_LerpPathFollowerScalar(
                pathFollower,
                pathFollower.Speed,
                context.TargetValue,
                context.AdjustDuration,
                context.AdjustCurve,
                static (follower, value) => follower.Speed = value,
                () => _speedControlCoroutine = null);
        }

        private IEnumerator Co_ProcessMoveDuration(PathFollower pathFollower, MoveEventContext context)
        {
            if (pathFollower.MoveType != EPathMoveType.TimeBased)
            {
                throw new InvalidOperationException("Move duration control requires TimeBased mode.");
            }

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

        private MoveEventContext ValidateSetting(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            HashSet<PathEventSettingSO> validationStack,
            bool resumeOnly)
        {
            if (setting == null)
                throw new ArgumentNullException(nameof(setting));
            if (validationStack == null)
                throw new ArgumentNullException(nameof(validationStack));
            if (!validationStack.Add(setting))
                throw new ArgumentException("Delayed path event settings cannot contain a cycle.", nameof(setting));

            try
            {
                ValidateNonNegativeFinite(setting.TimeScaleAdjustDuration, nameof(setting.TimeScaleAdjustDuration));
                if (setting.UseDelayedEvents && setting.DelayedEvents == null)
                    throw new ArgumentException("Delayed event list is required when delayed events are enabled.", nameof(setting));

                MoveEventContext context = ResolveMoveEventContext(setting, pathFollower, resumeOnly);
                ValidateMoveEventContext(setting, pathFollower, context);

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
                        ValidateSetting(
                            entry.EventSetting,
                            pathFollower,
                            validationStack,
                            context.Intent == EMoveEventIntent.Pause);
                    }
                }

                return context;
            }
            finally
            {
                validationStack.Remove(setting);
            }
        }

        private static void ValidateMoveEventContext(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            MoveEventContext context)
        {
            if (context.Channel == EMoveControlChannel.None)
                return;
            if (pathFollower == null)
                throw new ArgumentNullException(nameof(pathFollower));

            ValidateNonNegativeFinite(context.AdjustDuration, nameof(context.AdjustDuration));

            switch (context.Channel)
            {
                case EMoveControlChannel.Speed:
                    if (!IsFinite(context.TargetValue)
                        || context.TargetValue < 0f
                        || context.TargetValue > MAX_MOVE_VALUE)
                        throw new ArgumentOutOfRangeException(nameof(setting.MoveSpeedTargetValue));

                    switch (context.Intent)
                    {
                        case EMoveEventIntent.Pause:
                            if (context.TargetValue != 0f || context.AdjustDuration > 0f)
                                throw new ArgumentException("A zero-speed pause must be applied immediately.", nameof(setting));
                            ValidatePauseEvent(setting, pathFollower, EPathMoveType.SpeedBased);
                            break;
                        case EMoveEventIntent.Resume:
                            if (context.TargetValue <= 0f || context.AdjustDuration > 0f)
                                throw new ArgumentException("A Resume event must use an immediate positive speed target.", nameof(setting));
                            break;
                        case EMoveEventIntent.ChangeValue:
                            if (context.TargetValue <= 0f)
                                throw new ArgumentOutOfRangeException(nameof(setting.MoveSpeedTargetValue));
                            ValidateMoveSpeedAdjustment(setting);
                            break;
                    }
                    break;

                case EMoveControlChannel.Duration:
                    if (!IsFinite(context.TargetValue)
                        || context.TargetValue <= 0f
                        || context.TargetValue > MAX_MOVE_VALUE)
                        throw new ArgumentOutOfRangeException(nameof(setting.MoveDurationTargetValue));

                    switch (context.Intent)
                    {
                        case EMoveEventIntent.Pause:
                            if (context.TargetValue != TIME_BASED_PAUSE_VALUE || context.AdjustDuration > 0f)
                                throw new ArgumentException("A Duration value of 9999 must be applied as an immediate pause.", nameof(setting));
                            ValidatePauseEvent(setting, pathFollower, EPathMoveType.TimeBased);
                            break;
                        case EMoveEventIntent.Resume:
                            if (context.TargetValue >= TIME_BASED_PAUSE_VALUE || context.AdjustDuration > 0f)
                                throw new ArgumentException("A Resume event must use an immediate Duration target below 9999.", nameof(setting));
                            break;
                        case EMoveEventIntent.ChangeValue:
                            if (context.TargetValue >= TIME_BASED_PAUSE_VALUE)
                                throw new ArgumentOutOfRangeException(nameof(setting.MoveDurationTargetValue));
                            ValidateMoveDurationAdjustment(setting);
                            break;
                    }
                    break;
            }
        }

        private static void ValidateMoveSpeedAdjustment(PathEventSettingSO setting)
        {
            if (setting.MoveSpeedAdjustDuration <= 0f)
                return;
            if (setting.MoveSpeedAdjustCurve == null)
                throw new ArgumentNullException(nameof(setting.MoveSpeedAdjustCurve));
            if (setting.MoveSpeedAdjustCurve.length == 0)
                throw new ArgumentException("Move speed adjustment requires a non-empty curve.", nameof(setting.MoveSpeedAdjustCurve));
        }

        private static void ValidateMoveDurationAdjustment(PathEventSettingSO setting)
        {
            if (setting.MoveDurationAdjustDuration <= 0f)
                return;
            if (setting.MoveDurationAdjustCurve == null)
                throw new ArgumentNullException(nameof(setting.MoveDurationAdjustCurve));
            if (setting.MoveDurationAdjustCurve.length == 0)
                throw new ArgumentException("Move duration adjustment requires a non-empty curve.", nameof(setting.MoveDurationAdjustCurve));
        }

        private static void ValidatePauseEvent(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            EPathMoveType pausedMoveType)
        {
            if (!setting.UseDelayedEvents || setting.DelayedEvents == null || setting.DelayedEvents.Count != 1)
                throw new ArgumentException("A pause requires exactly one delayed Resume event.", nameof(setting));

            PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[0];
            if (entry == null || entry.EventSetting == null)
                throw new ArgumentException("A pause requires a delayed Resume event.", nameof(setting));

            MoveEventContext resumeContext = ResolveMoveEventContext(
                entry.EventSetting,
                pathFollower,
                true);
            EMoveControlChannel expectedChannel = pausedMoveType == EPathMoveType.SpeedBased
                ? EMoveControlChannel.Speed
                : EMoveControlChannel.Duration;
            if (resumeContext.Channel != expectedChannel
                || resumeContext.Intent != EMoveEventIntent.Resume)
                throw new ArgumentException("A pause delayed event must provide a positive Resume value for the current follower mode.", nameof(setting));

            ValidateMoveEventContext(entry.EventSetting, pathFollower, resumeContext);
            if (entry.EventSetting.DelayedEvents != null
                && entry.EventSetting.DelayedEvents.Count > 0)
                throw new ArgumentException("A delayed Resume event cannot enqueue another delayed event.", nameof(setting));
            ValidateNonNegativeFinite(entry.Delay, nameof(entry.Delay));
            if (entry.Delay <= 0f)
                throw new ArgumentOutOfRangeException(nameof(entry.Delay), "Pause duration must be greater than zero.");
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void ValidateNonNegativeFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
