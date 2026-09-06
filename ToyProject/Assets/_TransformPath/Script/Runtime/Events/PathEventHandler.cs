using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Unity bridge for immutable path events. Authoring assets are converted
    /// before dispatch; the handler never reads an SO during a path tick.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class PathEventHandler : MonoBehaviour, IPathRuntimeTickable, IPathRuntimeFaultHandler
    {
        private struct ScalarAdjustment
        {
            public bool Active;
            public IPathFollower Follower;
            public float StartValue;
            public float TargetValue;
            public float Duration;
            public float Elapsed;
            public AnimationCurve Curve;
            public int StartFrame;
            public ulong PlaybackId;
            public EMoveControlChannel Channel;
        }

        private enum EMoveControlChannel
        {
            None,
            Speed,
            Duration,
        }

        private readonly PathEventScheduler _scheduler = new PathEventScheduler();
        private readonly PathEventRuntime _eventRuntime = new PathEventRuntime();
        private readonly List<PathRuntimeEvent> _preparedEvents = new List<PathRuntimeEvent>();
        private IPathEventReceiver _receiver;
        private IPathFollower _boundFollower;
        private bool _isInitialized;
        private ScalarAdjustment _scalarAdjustment;
        private ulong _eventBatchId;
        private Action<string> _onEventDispatched;
        private Delegate[] _onEventDispatchedInvocationList;
        private bool _runtimeDriverRegistered;
        private int _currentFrameId;

        [SerializeField] private MonoBehaviour _receiverObject;

        public bool IsInitialized => _isInitialized;
        public bool IsControllingSpeed => _scalarAdjustment.Active;
        public ulong PlaybackId => _boundFollower == null
            ? 0UL
            : _boundFollower.PlaybackId;

        public event Action<string> OnEventDispatched
        {
            add
            {
                _onEventDispatched += value;
                _onEventDispatchedInvocationList = _onEventDispatched?.GetInvocationList();
            }
            remove
            {
                _onEventDispatched -= value;
                _onEventDispatchedInvocationList = _onEventDispatched?.GetInvocationList();
            }
        }

        public void Init()
        {
            if (_isInitialized)
                return;
            _receiver = _receiverObject as IPathEventReceiver;
            _isInitialized = true;
        }

        public void Bind(IPathFollower follower)
        {
            _boundFollower = follower;
        }

        public void Release()
        {
            CancelPlaybackEffects(
                _boundFollower == null ? 0UL : _boundFollower.PlaybackId);
            _boundFollower = null;
            _isInitialized = false;
        }

        private void Awake()
        {
            if (Application.isPlaying)
            {
                Init();
                PathRuntimeDriverBehaviour.Instance.Register(this);
                _runtimeDriverRegistered = true;
            }
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            if (!_isInitialized)
                Init();
            if (!_runtimeDriverRegistered)
            {
                PathRuntimeDriverBehaviour.Instance.Register(this);
                _runtimeDriverRegistered = true;
            }
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                if (_runtimeDriverRegistered && PathRuntimeDriverBehaviour.HasInstance)
                {
                    PathRuntimeDriverBehaviour.Instance.Unregister(this);
                    _runtimeDriverRegistered = false;
                }
                Release();
            }
        }

        private void OnDestroy()
        {
            if (_runtimeDriverRegistered && PathRuntimeDriverBehaviour.HasInstance)
            {
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
                _runtimeDriverRegistered = false;
            }
            Release();
        }

        void IPathRuntimeTickable.Tick(
            float deltaTime,
            float unscaledDeltaTime,
            int frameId)
        {
            Tick(deltaTime, unscaledDeltaTime, frameId);
        }

        internal void Tick(
            float deltaTime,
            float unscaledDeltaTime,
            int frameId)
        {
            if (!_isInitialized)
                return;

            int frame = frameId;
            _currentFrameId = frame;
            UpdateTimeScale(unscaledDeltaTime, frame);
            UpdateScalar(deltaTime, frame);
            _scheduler.Advance(deltaTime, frame);

            while (_scheduler.TryDequeueDue(frame, out PathEventScheduler.ScheduledEvent item))
            {
                if (item.Follower == null
                    || item.Follower.PlaybackId != item.PlaybackId
                    || item.EventBatchId != _eventBatchId)
                    continue;

                ulong before = item.Follower.PlaybackId;
                ulong beforeStateRevision = item.Follower.StateRevision;
                ulong beforeBatch = _eventBatchId;
                ProcessEvent(
                    item.Definition,
                    item.Follower,
                    item.ResumeOnly,
                    before,
                    item.EventBatchId);
                if (item.Follower.PlaybackId != before
                    || item.Follower.StateRevision != beforeStateRevision
                    || _eventBatchId != beforeBatch)
                    break;
            }
        }

        void IPathRuntimeFaultHandler.HandleRuntimeFault(
            ulong playbackId,
            System.Exception exception)
        {
            if (_boundFollower == null || _boundFollower.PlaybackId != playbackId)
                return;

            CancelPlaybackEffects(playbackId);
            try
            {
                _boundFollower.StopMove();
            }
            catch
            {
                // The driver reports the original callback failure. Cleanup
                // continues even when a state listener also throws.
            }
        }

        public void PrepareForPlayback(
            IPathFollower follower,
            IPathEventSource source,
            EPathMoveType moveType)
        {
            if (!_isInitialized)
                Init();
            IPathFollower target = _boundFollower ?? follower;
            ValidateForPlayback(target, source, moveType);
            _boundFollower = target;
            _preparedEvents.Clear();
            if (source == null)
                return;

            for (int i = 0; i < source.EventCount; i++)
                _preparedEvents.Add(source.GetEvent(i));
            _scheduler.EnsureCapacity(_preparedEvents.Count);
        }

        internal void PrepareForSequence(
            IPathFollower follower,
            PathSequenceSnapshot snapshot)
        {
            if (!_isInitialized)
                Init();
            IPathFollower target = _boundFollower ?? follower;
            ValidateForSequence(target, snapshot);
            _boundFollower = target;
            _preparedEvents.Clear();
            if (snapshot == null)
                return;

            for (int i = 0; i < snapshot.Count; i++)
            {
                IPathEventSource source = snapshot.GetEventSource(i);
                if (source == null)
                    continue;
                for (int eventIndex = 0; eventIndex < source.EventCount; eventIndex++)
                    _preparedEvents.Add(source.GetEvent(eventIndex));
            }
            _scheduler.EnsureCapacity(_preparedEvents.Count);
        }

        internal void ValidateForPlayback(
            IPathFollower follower,
            IPathEventSource source,
            EPathMoveType moveType)
        {
            if (source == null)
                return;

            for (int i = 0; i < source.EventCount; i++)
            {
                PathRuntimeEvent pathEvent = source.GetEvent(i);
                ValidateDefinition(
                    pathEvent.Definition,
                    follower,
                    false,
                    new HashSet<PathEventDefinition>(),
                    moveType,
                    EPathFollowerState.Moving);
            }
        }

        internal void ValidateForSequence(
            IPathFollower follower,
            PathSequenceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            for (int i = 0; i < snapshot.Count; i++)
            {
                IPathEventSource source = snapshot.GetEventSource(i);
                if (source == null)
                    continue;
                EPathMoveType moveType = snapshot.GetDescriptor(i).MovementSettings.MoveType;
                for (int eventIndex = 0; eventIndex < source.EventCount; eventIndex++)
                {
                    PathRuntimeEvent pathEvent = source.GetEvent(eventIndex);
                    ValidateDefinition(
                        pathEvent.Definition,
                        follower,
                        false,
                        new HashSet<PathEventDefinition>(),
                        moveType,
                        EPathFollowerState.Moving);
                }
            }
        }

        public void CancelAllDelayedEvents()
        {
            _scheduler.Clear();
        }

        internal void CancelMovementAdjustments()
        {
            _scalarAdjustment = default(ScalarAdjustment);
        }

        internal void CancelPlaybackEffects(ulong playbackId)
        {
            _scheduler.Clear();
            _scalarAdjustment = default(ScalarAdjustment);
            PathTimeScaleCoordinator.Shared.Release(
                GetOwnerId(_boundFollower, playbackId));
        }

        public void HandleEvent(PathEventSettingSO setting)
        {
            if (setting == null)
                throw new ArgumentNullException(nameof(setting));
            HandleEvent(PathEventDefinitionFactory.Create(setting), _boundFollower);
        }

        internal void HandleEvent(
            PathEventDefinition definition,
            IPathFollower follower,
            bool resumeOnly = false)
        {
            if (!_isInitialized)
                Init();
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));

            _boundFollower = follower;
            ValidateDefinition(definition, follower, resumeOnly, new HashSet<PathEventDefinition>());
            _scheduler.Clear();
            ulong playbackId = follower.PlaybackId;
            try
            {
                ProcessEvent(
                    definition,
                    follower,
                    resumeOnly,
                    playbackId,
                    ++_eventBatchId);
            }
            catch
            {
                if (follower.PlaybackId == playbackId)
                {
                    CancelPlaybackEffects(playbackId);
                    try
                    {
                        follower.StopMove();
                    }
                    catch
                    {
                        // Preserve the original event exception.
                    }
                }
                throw;
            }
        }

        private void ProcessEvent(
            PathEventDefinition definition,
            IPathFollower follower,
            bool resumeOnly,
            ulong expectedPlaybackId,
            ulong eventBatchId)
        {
            if (follower == null || follower.PlaybackId != expectedPlaybackId)
                return;
            if (eventBatchId != _eventBatchId)
                return;

            EPathEventIntent intent = _eventRuntime.ResolveIntent(
                definition,
                follower,
                resumeOnly);
            ulong callbackStateRevision = follower.StateRevision;
            if (!DispatchPathEvent(
                    definition.EventName,
                    follower,
                    expectedPlaybackId,
                    callbackStateRevision,
                    eventBatchId))
                return;

            if (intent == EPathEventIntent.Pause)
                _scalarAdjustment = default(ScalarAdjustment);
            PathCommandReceipt lifecycleReceipt = _eventRuntime.ApplyLifecycle(
                intent,
                follower);
            if (lifecycleReceipt.PlaybackId != expectedPlaybackId
                || lifecycleReceipt.StateRevision != follower.StateRevision
                || follower.PlaybackId != expectedPlaybackId
                || eventBatchId != _eventBatchId)
                return;

            if (definition.UseTimeScaleAdjust)
                StartTimeScale(definition, follower);
            if (follower.PlaybackId != expectedPlaybackId
                || eventBatchId != _eventBatchId)
                return;

            if (intent == EPathEventIntent.ChangeValue)
            {
                if (definition.UseModifyPathMoveSpeed)
                    StartScalar(follower, EMoveControlChannel.Speed,
                        definition.MoveSpeedTargetValue,
                        definition.MoveSpeedAdjustDuration,
                        definition.MoveSpeedAdjustCurve);
                else if (definition.UseModifyPathMoveDuration)
                    StartScalar(follower, EMoveControlChannel.Duration,
                        definition.MoveDurationTargetValue,
                        definition.MoveDurationAdjustDuration,
                        definition.MoveDurationAdjustCurve);
            }

            if (follower.PlaybackId != expectedPlaybackId
                || eventBatchId != _eventBatchId)
                return;
            EnqueueDelayedEvents(
                definition,
                follower,
                intent == EPathEventIntent.Pause,
                expectedPlaybackId,
                eventBatchId);
        }

        private bool DispatchPathEvent(
            string eventName,
            IPathFollower follower,
            ulong expectedPlaybackId,
            ulong expectedStateRevision,
            ulong expectedEventBatchId)
        {
            if (string.IsNullOrEmpty(eventName))
                return true;

            _receiver?.ReceivePathEvent(eventName, follower);
            if (follower.PlaybackId != expectedPlaybackId
                || follower.StateRevision != expectedStateRevision
                || _eventBatchId != expectedEventBatchId)
                return false;

            Delegate[] listeners = _onEventDispatchedInvocationList;
            if (listeners == null)
                return true;

            for (int i = 0; i < listeners.Length; i++)
            {
                ((Action<string>)listeners[i])(eventName);
                if (follower.PlaybackId != expectedPlaybackId
                    || follower.StateRevision != expectedStateRevision
                    || _eventBatchId != expectedEventBatchId)
                    return false;
            }
            return true;
        }

        private void EnqueueDelayedEvents(
            PathEventDefinition definition,
            IPathFollower follower,
            bool pauseEvent,
            ulong playbackId,
            ulong eventBatchId)
        {
            IReadOnlyList<PathDelayedEventDefinition> delayed = definition.DelayedEvents;
            for (int i = 0; i < delayed.Count; i++)
            {
                PathDelayedEventDefinition entry = delayed[i];
                bool resumeOnly = pauseEvent && i == 0;
                if (entry.Delay <= 0f)
                {
                    ulong beforePlaybackId = follower.PlaybackId;
                    ulong beforeStateRevision = follower.StateRevision;
                    ulong beforeBatch = _eventBatchId;
                    ProcessEvent(
                        entry.Definition,
                        follower,
                        resumeOnly,
                        playbackId,
                        eventBatchId);
                    if (follower.PlaybackId != beforePlaybackId
                        || follower.StateRevision != beforeStateRevision
                        || _eventBatchId != beforeBatch)
                        return;
                    continue;
                }

                _scheduler.Schedule(
                    entry.Definition,
                    follower,
                    entry.Delay,
                    resumeOnly,
                    _currentFrameId > 0 ? _currentFrameId : Time.frameCount,
                    playbackId,
                    eventBatchId);
            }
        }

        private void StartScalar(
            IPathFollower follower,
            EMoveControlChannel channel,
            float target,
            float duration,
            AnimationCurve curve)
        {
            if (duration <= 0f)
            {
                if (channel == EMoveControlChannel.Speed)
                    follower.SetSpeed(target);
                else
                    follower.SetDuration(target);
                return;
            }

            _scalarAdjustment = new ScalarAdjustment
            {
                Active = true,
                Follower = follower,
                StartValue = channel == EMoveControlChannel.Speed ? follower.Speed : follower.Duration,
                TargetValue = target,
                Duration = duration,
                Curve = curve,
                StartFrame = _currentFrameId > 0 ? _currentFrameId : Time.frameCount,
                PlaybackId = follower.PlaybackId,
                Channel = channel,
            };
        }

        private void UpdateScalar(float deltaTime, int frame)
        {
            if (!_scalarAdjustment.Active
                || _scalarAdjustment.Follower == null
                || _scalarAdjustment.StartFrame >= frame
                || deltaTime <= 0f)
                return;
            if (_scalarAdjustment.Follower.PlaybackId != _scalarAdjustment.PlaybackId)
            {
                _scalarAdjustment = default(ScalarAdjustment);
                return;
            }

            _scalarAdjustment.Elapsed += deltaTime;
            float t = EvaluateAdjustment(
                _scalarAdjustment.Elapsed,
                _scalarAdjustment.Duration,
                _scalarAdjustment.Curve);
            float value = Mathf.Lerp(_scalarAdjustment.StartValue, _scalarAdjustment.TargetValue, t);
            PathCommandReceipt receipt = _scalarAdjustment.Channel == EMoveControlChannel.Speed
                ? _scalarAdjustment.Follower.SetSpeed(value)
                : _scalarAdjustment.Follower.SetDuration(value);
            if (receipt.PlaybackId != _scalarAdjustment.PlaybackId
                || _scalarAdjustment.Follower.PlaybackId != _scalarAdjustment.PlaybackId
                || receipt.StateRevision != _scalarAdjustment.Follower.StateRevision)
            {
                _scalarAdjustment = default(ScalarAdjustment);
                return;
            }

            if (_scalarAdjustment.Elapsed >= _scalarAdjustment.Duration)
            {
                if (_scalarAdjustment.Channel == EMoveControlChannel.Speed)
                    _scalarAdjustment.Follower.SetSpeed(_scalarAdjustment.TargetValue);
                else
                    _scalarAdjustment.Follower.SetDuration(_scalarAdjustment.TargetValue);
                _scalarAdjustment = default(ScalarAdjustment);
            }
        }

        private void StartTimeScale(
            PathEventDefinition definition,
            IPathFollower follower)
        {
            PathTimeScaleCoordinator.Shared.Request(
                GetOwnerId(follower, follower == null ? 0UL : follower.PlaybackId),
                definition.TimeScaleAdjustValue,
                definition.TimeScaleAdjustDuration,
                definition.TimeScaleAdjustCurve,
                _currentFrameId);
        }

        private void UpdateTimeScale(float unscaledDeltaTime, int frame)
        {
            PathTimeScaleCoordinator.Shared.Tick(unscaledDeltaTime, frame);
        }

        private static float EvaluateAdjustment(float elapsed, float duration, AnimationCurve curve)
        {
            if (duration <= 0f)
                return 1f;
            return Mathf.Clamp01(curve == null
                ? elapsed / duration
                : curve.Evaluate(Mathf.Clamp01(elapsed / duration)));
        }

        private static void ValidateDefinition(
            PathEventDefinition definition,
            IPathPlaybackState follower,
            bool resumeOnly,
            HashSet<PathEventDefinition> stack,
            EPathMoveType? moveTypeOverride = null,
            EPathFollowerState? stateOverride = null)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (!stack.Add(definition))
                throw new ArgumentException("Delayed path event settings cannot contain a cycle.");

            if (definition.UseTimeScaleAdjust)
            {
                if (!PathValueUtility.IsInRange(
                         definition.TimeScaleAdjustValue,
                         0f,
                         PathMovementSettingsUtility.MAX_VALUE)
                    || definition.TimeScaleAdjustDuration <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(definition));
                ValidateAdjustment(definition.TimeScaleAdjustDuration, definition.TimeScaleAdjustCurve);
            }

            PathEventRuntime runtime = new PathEventRuntime();
            EPathEventIntent intent = runtime.ResolveIntent(
                definition,
                moveTypeOverride ?? follower.MoveType,
                stateOverride ?? follower.State,
                resumeOnly);
            ValidateMovementIntent(
                definition,
                moveTypeOverride ?? follower.MoveType,
                intent);
            if (intent == EPathEventIntent.Pause)
            {
                if (definition.DelayedEvents.Count != 1
                    || definition.DelayedEvents[0].Delay <= 0f
                    || definition.DelayedEvents[0].Definition == null)
                    throw new ArgumentException("A pause requires exactly one delayed Resume event.");
                EPathEventIntent resumeIntent = runtime.ResolveIntent(
                    definition.DelayedEvents[0].Definition,
                    moveTypeOverride ?? follower.MoveType,
                    stateOverride ?? follower.State,
                    true);
                if (resumeIntent != EPathEventIntent.Resume
                    || definition.DelayedEvents[0].Definition.DelayedEvents.Count != 0)
                    throw new ArgumentException("A pause delayed event must be a terminal Resume event.");
            }
            else if (intent == EPathEventIntent.Resume
                && definition.DelayedEvents.Count != 0)
            {
                throw new ArgumentException("A Resume event cannot enqueue delayed events.");
            }

            for (int i = 0; i < definition.DelayedEvents.Count; i++)
            {
                PathDelayedEventDefinition delayed = definition.DelayedEvents[i];
                if (!PathValueUtility.IsNonNegativeFinite(delayed.Delay))
                    throw new ArgumentOutOfRangeException(nameof(delayed.Delay));
                ValidateDefinition(
                    delayed.Definition,
                    follower,
                    intent == EPathEventIntent.Pause && i == 0,
                    stack,
                    moveTypeOverride,
                    stateOverride);
            }

            stack.Remove(definition);
        }

        private static void ValidateMovementIntent(
            PathEventDefinition definition,
            EPathMoveType moveType,
            EPathEventIntent intent)
        {
            if (intent == EPathEventIntent.None)
                return;

            switch (moveType)
            {
                case EPathMoveType.SpeedBased:
                    if (!definition.UseModifyPathMoveSpeed)
                        return;
                    if (!PathValueUtility.IsInRange(
                            definition.MoveSpeedTargetValue,
                            0f,
                            PathMovementSettingsUtility.MAX_VALUE))
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveSpeedTargetValue));
                    ValidateNonNegativeDuration(
                        definition.MoveSpeedAdjustDuration,
                        nameof(definition.MoveSpeedAdjustDuration));
                    if (intent == EPathEventIntent.Pause
                        && (!Mathf.Approximately(definition.MoveSpeedTargetValue, 0f)
                            || definition.MoveSpeedAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A zero-speed pause must be applied immediately.");
                    if (intent == EPathEventIntent.Resume
                        && (definition.MoveSpeedTargetValue <= 0f
                            || definition.MoveSpeedAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A Resume event must use an immediate positive speed target.");
                    if (intent == EPathEventIntent.ChangeValue
                        && definition.MoveSpeedTargetValue <= 0f)
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveSpeedTargetValue));
                    if (intent == EPathEventIntent.ChangeValue)
                        ValidateAdjustment(
                            definition.MoveSpeedAdjustDuration,
                            definition.MoveSpeedAdjustCurve);
                    return;

                case EPathMoveType.TimeBased:
                    if (!definition.UseModifyPathMoveDuration)
                        return;
                    if (!PathValueUtility.IsInRange(
                            definition.MoveDurationTargetValue,
                            PathMovementSettingsUtility.MIN_VALUE,
                            PathMovementSettingsUtility.MAX_VALUE))
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveDurationTargetValue));
                    ValidateNonNegativeDuration(
                        definition.MoveDurationAdjustDuration,
                        nameof(definition.MoveDurationAdjustDuration));
                    if (intent == EPathEventIntent.Pause
                        && (!Mathf.Approximately(
                                definition.MoveDurationTargetValue,
                                PathMovementSettingsUtility.MAX_VALUE)
                            || definition.MoveDurationAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A Duration value of 9999 must be applied as an immediate pause.");
                    if (intent == EPathEventIntent.Resume
                        && (definition.MoveDurationTargetValue
                                >= PathMovementSettingsUtility.MAX_VALUE
                            || definition.MoveDurationAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A Resume event must use an immediate Duration target below 9999.");
                    if (intent == EPathEventIntent.ChangeValue
                        && definition.MoveDurationTargetValue
                            >= PathMovementSettingsUtility.MAX_VALUE)
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveDurationTargetValue));
                    if (intent == EPathEventIntent.ChangeValue)
                        ValidateAdjustment(
                            definition.MoveDurationAdjustDuration,
                            definition.MoveDurationAdjustCurve);
                    return;
            }
        }

        private static void ValidateNonNegativeDuration(
            float duration,
            string parameterName)
        {
            if (!PathValueUtility.IsNonNegativeFinite(duration))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidateAdjustment(float duration, AnimationCurve curve)
        {
            if (!PathValueUtility.IsNonNegativeFinite(duration))
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (duration <= 0f)
                return;
            if (curve == null || curve.length == 0)
                throw new ArgumentException("An adjustment requires a non-empty curve.");
        }

        private static ulong GetOwnerId(
            IPathFollower follower,
            ulong fallbackPlaybackId)
        {
            if (follower is IPathPlaybackIdentity identity
                && identity.PlaybackOwnerId != 0UL)
                return identity.PlaybackOwnerId;
            return fallbackPlaybackId;
        }
    }
}
