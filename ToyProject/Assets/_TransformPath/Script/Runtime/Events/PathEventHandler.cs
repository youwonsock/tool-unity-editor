using System;
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

        private struct ScalarAdjustmentState
        {
            public bool IsActive;
            public PathFollower PathFollower;
            public float StartValue;
            public float TargetValue;
            public float Duration;
            public float Elapsed;
            public AnimationCurve Curve;
            public int StartFrame;
        }

        private struct TimeScaleAdjustmentState
        {
            public bool IsActive;
            public float StartValue;
            public float TargetValue;
            public float Duration;
            public float Elapsed;
            public AnimationCurve Curve;
            public int StartFrame;
        }

        #endregion


        #region Member Variables

        [SerializeField] private MonoBehaviour _receiverObject;

        private readonly PathEventScheduler _delayedEventScheduler =
            new PathEventScheduler();
        private readonly HashSet<PathEventSettingSO> _validationStack =
            new HashSet<PathEventSettingSO>();
        private ScalarAdjustmentState _speedAdjustment;
        private ScalarAdjustmentState _durationAdjustment;
        private TimeScaleAdjustmentState _timeScaleAdjustment;
        private IPathEventReceiver _eventReceiver;
        private bool _isInitialized;
        private bool _timeStateCaptured;
        private float _originalTimeScale;
        private float _originalFixedDeltaTime;
        private Action<string> _onEventDispatched;

        #endregion


        #region Properties

        public bool IsControllingSpeed => _speedAdjustment.IsActive
            || _durationAdjustment.IsActive;
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
            _delayedEventScheduler.Clear();
            StopAllEventAdjustments();
            RestoreTimeState();
            _isInitialized = false;
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
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
            _delayedEventScheduler.Clear();
            StopAllEventAdjustments();
            RestoreTimeState();
        }

        private void OnDestroy()
        {
            Release();
        }

        private void Update()
        {
            if (!_isInitialized)
                return;

            float deltaTime = Time.deltaTime;
            float unscaledDeltaTime = Time.unscaledDeltaTime;
            int frame = Time.frameCount;
            UpdateTimeScaleAdjustment(unscaledDeltaTime, frame);
            UpdateScalarAdjustment(
                ref _speedAdjustment,
                deltaTime,
                frame,
                EMoveControlChannel.Speed);
            UpdateScalarAdjustment(
                ref _durationAdjustment,
                deltaTime,
                frame,
                EMoveControlChannel.Duration);

            if (_delayedEventScheduler.Count == 0)
                return;

            _delayedEventScheduler.Advance(deltaTime, frame);
            int schedulerRevision = _delayedEventScheduler.Revision;
            while (_delayedEventScheduler.TryDequeueDue(
                frame,
                out PathEventScheduler.ScheduledEvent scheduledEvent))
            {
                _validationStack.Clear();
                PathEventContext context = PathEventValidator.ValidateSetting(
                    scheduledEvent.EventSetting,
                    scheduledEvent.PathFollower,
                    _validationStack,
                    scheduledEvent.ResumeOnly);
                ProcessEvent(
                    scheduledEvent.EventSetting,
                    scheduledEvent.PathFollower,
                    context);
                if (_delayedEventScheduler.Revision != schedulerRevision)
                    return;
            }
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

            _validationStack.Clear();
            PathEventContext context = PathEventValidator.ValidateSetting(
                eventSetting,
                pathAnimator,
                _validationStack,
                false);
            CancelAllDelayedEvents();
            ProcessEvent(eventSetting, pathAnimator, context);
        }

        public void CancelAllDelayedEvents()
        {
            if (!_isInitialized)
                return;
            _delayedEventScheduler.Clear();
        }

        internal void PrepareForPlayback(
            PathFollower pathFollower,
            IPathEventSource source,
            EPathMoveType moveType)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized || pathFollower == null || source == null)
                return;

            int maximumDelayedEventCount = 0;
            for (int i = 0; i < source.EventCount; i++)
            {
                PathEventEntry entry = source.GetEvent(i);
                _validationStack.Clear();
                int delayedEventCount = PrepareEventTree(
                    entry.EventSetting,
                    pathFollower,
                    moveType);
                if (delayedEventCount > maximumDelayedEventCount)
                    maximumDelayedEventCount = delayedEventCount;
            }

            _delayedEventScheduler.EnsureCapacity(maximumDelayedEventCount);
        }

        internal void PrepareForSequence(
            PathFollower pathFollower,
            PathSequenceSnapshot snapshot)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized || pathFollower == null || snapshot == null)
                return;

            for (int i = 0; i < snapshot.Count; i++)
            {
                PathSegmentDescriptor descriptor = snapshot.GetDescriptor(i);
                PrepareForPlayback(
                    pathFollower,
                    snapshot.GetEventSource(i),
                    descriptor.MovementSettings.MoveType);
            }
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
            ApplyMoveLifecycle(context.Intent, pathFollower);

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
            PathFollower pathFollower)
        {
            if (moveIntent == EMoveEventIntent.None || pathFollower == null)
                return;

            pathFollower.TryGetComponent(out QueuedPathFollower queuedFollower);
            switch (moveIntent)
            {
                case EMoveEventIntent.Pause:
                    StopMovementAdjustments();
                    if (queuedFollower != null)
                        queuedFollower.PauseMove();
                    else
                        pathFollower.PauseMove();
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

        private int PrepareEventTree(
            PathEventSettingSO eventSetting,
            PathFollower pathFollower,
            EPathMoveType moveType)
        {
            if (eventSetting == null || !_validationStack.Add(eventSetting))
                return 0;

            int delayedEventCount = 0;
            if (eventSetting.UseDelayedEvents
                && eventSetting.DelayedEvents != null)
            {
                for (int i = 0; i < eventSetting.DelayedEvents.Count; i++)
                {
                    PathEventSettingSO.DelayedEventEntry entry =
                        eventSetting.DelayedEvents[i];
                    delayedEventCount++;
                    if (entry == null || entry.EventSetting == null)
                        continue;
                    delayedEventCount += PrepareEventTree(
                        entry.EventSetting,
                        pathFollower,
                        moveType);
                }
            }

            _validationStack.Remove(eventSetting);
            return delayedEventCount;
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
            _timeScaleAdjustment = new TimeScaleAdjustmentState
            {
                IsActive = true,
                StartValue = Time.timeScale,
                TargetValue = setting.TimeScaleAdjustValue,
                Duration = setting.TimeScaleAdjustDuration,
                Curve = setting.TimeScaleAdjustCurve,
                StartFrame = Time.frameCount,
            };
            UpdateTimeScaleAdjustment(
                Time.unscaledDeltaTime,
                Time.frameCount,
                true);
        }

        private void ControlMoveSpeed(
            PathEventContext context,
            PathFollower pathFollower)
        {
            _speedAdjustment = default(ScalarAdjustmentState);
            if (context.AdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.SpeedBased)
                    throw new InvalidOperationException(
                        "Move speed control requires SpeedBased mode.");
                pathFollower.Speed = context.TargetValue;
                return;
            }

            _speedAdjustment = new ScalarAdjustmentState
            {
                IsActive = true,
                PathFollower = pathFollower,
                StartValue = pathFollower.Speed,
                TargetValue = context.TargetValue,
                Duration = context.AdjustDuration,
                Curve = context.AdjustCurve,
                StartFrame = Time.frameCount,
            };
            UpdateScalarAdjustment(
                ref _speedAdjustment,
                Time.deltaTime,
                Time.frameCount,
                EMoveControlChannel.Speed,
                true);
        }

        private void ControlMoveDuration(
            PathEventContext context,
            PathFollower pathFollower)
        {
            _durationAdjustment = default(ScalarAdjustmentState);
            if (context.AdjustDuration <= 0f)
            {
                if (pathFollower.MoveType != EPathMoveType.TimeBased)
                    throw new InvalidOperationException(
                        "Move duration control requires TimeBased mode.");
                pathFollower.Duration = context.TargetValue;
                return;
            }

            _durationAdjustment = new ScalarAdjustmentState
            {
                IsActive = true,
                PathFollower = pathFollower,
                StartValue = pathFollower.Duration,
                TargetValue = context.TargetValue,
                Duration = context.AdjustDuration,
                Curve = context.AdjustCurve,
                StartFrame = Time.frameCount,
            };
            UpdateScalarAdjustment(
                ref _durationAdjustment,
                Time.deltaTime,
                Time.frameCount,
                EMoveControlChannel.Duration,
                true);
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

                bool resumeOnly = pauseEvent && i == 0;
                if (entry.Delay <= 0f)
                {
                    _validationStack.Clear();
                    PathEventContext context = PathEventValidator.ValidateSetting(
                        entry.EventSetting,
                        pathFollower,
                        _validationStack,
                        resumeOnly);
                    ProcessEvent(entry.EventSetting, pathFollower, context);
                    continue;
                }

                _delayedEventScheduler.Schedule(
                    entry.EventSetting,
                    pathFollower,
                    entry.Delay,
                    resumeOnly,
                    Time.frameCount);
            }
        }

        private void StopMovementAdjustments()
        {
            _speedAdjustment = default(ScalarAdjustmentState);
            _durationAdjustment = default(ScalarAdjustmentState);
        }

        private void StopAllEventAdjustments()
        {
            StopMovementAdjustments();
            _timeScaleAdjustment = default(TimeScaleAdjustmentState);
        }

        private void RestoreTimeState()
        {
            if (!_timeStateCaptured)
                return;

            Time.timeScale = _originalTimeScale;
            Time.fixedDeltaTime = _originalFixedDeltaTime;
            _timeStateCaptured = false;
        }

        private void UpdateScalarAdjustment(
            ref ScalarAdjustmentState adjustment,
            float deltaTime,
            int frame,
            EMoveControlChannel channel,
            bool allowStartFrame = false)
        {
            if (!adjustment.IsActive
                || (!allowStartFrame && adjustment.StartFrame >= frame)
                || deltaTime <= 0f)
                return;
            if (adjustment.PathFollower == null)
                throw new InvalidOperationException(
                    "Path follower was destroyed during an active event.");

            adjustment.Elapsed += deltaTime;
            float t = EvaluateAdjustCurveT(
                adjustment.Elapsed,
                adjustment.Duration,
                adjustment.Curve);
            float value = Mathf.Lerp(
                adjustment.StartValue,
                adjustment.TargetValue,
                t);
            ApplyScalarValue(channel, adjustment.PathFollower, value);
            if (adjustment.Elapsed < adjustment.Duration)
                return;

            ApplyScalarValue(
                channel,
                adjustment.PathFollower,
                adjustment.TargetValue);
            adjustment = default(ScalarAdjustmentState);
        }

        private void ApplyScalarValue(
            EMoveControlChannel channel,
            PathFollower pathFollower,
            float value)
        {
            switch (channel)
            {
                case EMoveControlChannel.Speed:
                    if (pathFollower.MoveType != EPathMoveType.SpeedBased)
                        throw new InvalidOperationException(
                            "Move speed control requires SpeedBased mode.");
                    pathFollower.Speed = value;
                    break;
                case EMoveControlChannel.Duration:
                    if (pathFollower.MoveType != EPathMoveType.TimeBased)
                        throw new InvalidOperationException(
                            "Move duration control requires TimeBased mode.");
                    pathFollower.Duration = value;
                    break;
            }
        }

        private void UpdateTimeScaleAdjustment(
            float deltaTime,
            int frame,
            bool allowStartFrame = false)
        {
            if (!_timeScaleAdjustment.IsActive
                || (!allowStartFrame && _timeScaleAdjustment.StartFrame >= frame)
                || deltaTime <= 0f)
                return;

            _timeScaleAdjustment.Elapsed += deltaTime;
            float t = EvaluateAdjustCurveT(
                _timeScaleAdjustment.Elapsed,
                _timeScaleAdjustment.Duration,
                _timeScaleAdjustment.Curve);
            float currentValue = Mathf.Lerp(
                _timeScaleAdjustment.StartValue,
                _timeScaleAdjustment.TargetValue,
                t);
            Time.timeScale = currentValue;
            Time.fixedDeltaTime = BASE_FIXED_DELTA_TIME * currentValue;
            if (_timeScaleAdjustment.Elapsed < _timeScaleAdjustment.Duration)
                return;

            Time.timeScale = _timeScaleAdjustment.TargetValue;
            Time.fixedDeltaTime = BASE_FIXED_DELTA_TIME
                * _timeScaleAdjustment.TargetValue;
            _timeScaleAdjustment = default(TimeScaleAdjustmentState);
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

        #endregion
    }
}
