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
        private readonly PathEventScheduler _scheduler = new PathEventScheduler();
        private IPathEventReceiver _receiver;
        private IPathFollower _boundFollower;
        private bool _isInitialized;
        private readonly PathScalarAdjustment _scalarAdjustment = new PathScalarAdjustment();
        private ulong _eventBatchId;
        private Action<string> _onEventDispatched;
        private Delegate[] _onEventDispatchedInvocationList;
        private bool _runtimeDriverRegistered;
        private int _currentFrameId;

        [SerializeField] private MonoBehaviour _receiverObject;

        public bool IsInitialized => _isInitialized;
        int IPathRuntimeTickable.RuntimeOrder => -200;
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
            _scalarAdjustment.Tick(deltaTime, frame);
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
            _scheduler.EnsureCapacity(source?.EventCount ?? 0);
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
            if (snapshot == null)
                return;

            int eventCount = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                IPathEventSource source = snapshot.GetEventSource(i);
                eventCount += source?.EventCount ?? 0;
            }
            _scheduler.EnsureCapacity(eventCount);
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
                PathEventDefinitionValidator.ValidateDefinition(
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
                    PathEventDefinitionValidator.ValidateDefinition(
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
            _scalarAdjustment.Cancel();
        }

        internal void CancelPlaybackEffects(ulong playbackId)
        {
            _scheduler.Clear();
            _scalarAdjustment.Cancel();
            PathTimeScaleCoordinator.Shared.Release(
                GetOwnerId(_boundFollower, playbackId));
        }

        public void HandleEvent(PathEventSettingSO setting)
        {
            if (setting == null)
                throw new ArgumentNullException(nameof(setting));
            HandleEvent(PathEventDefinition.FromAuthoring(setting), _boundFollower);
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
            PathEventDefinitionValidator.ValidateDefinition(definition, follower, resumeOnly, new HashSet<PathEventDefinition>());
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

            EPathEventIntent intent = PathEventRuntime.ResolveIntent(
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
                _scalarAdjustment.Cancel();
            PathCommandReceipt lifecycleReceipt = PathEventRuntime.ApplyLifecycle(
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

            if (intent == EPathEventIntent.ChangeValue
                || intent == EPathEventIntent.Resume)
            {
                if (definition.UseModifyPathMoveSpeed)
                    StartScalar(follower, EPathMoveControlChannel.Speed,
                        definition.MoveSpeedTargetValue,
                        definition.MoveSpeedAdjustDuration,
                        definition.MoveSpeedAdjustCurve);
                else if (definition.UseModifyPathMoveDuration)
                    StartScalar(follower, EPathMoveControlChannel.Duration,
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

        private void StartScalar(IPathFollower follower, EPathMoveControlChannel channel,
            float target, float duration, AnimationCurve curve)
        {
            _scalarAdjustment.Start(follower, channel, target, duration, curve,
                _currentFrameId > 0 ? _currentFrameId : Time.frameCount);
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
