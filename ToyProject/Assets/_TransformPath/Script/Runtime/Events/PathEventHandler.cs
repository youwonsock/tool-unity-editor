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
        private readonly PathListeners _onEventDispatched = new PathListeners();
        private readonly HashSet<PathEventDefinition> _validationSet = new HashSet<PathEventDefinition>();
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
            add => _onEventDispatched.Add(value);
            remove => _onEventDispatched.Remove(value);
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
            }
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            if (!_isInitialized)
                Init();
            PathRuntimeDriverBehaviour.Instance.Register(this);
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                if (PathRuntimeDriverBehaviour.HasInstance)
                    PathRuntimeDriverBehaviour.Instance.Unregister(this);
                Release();
            }
        }

        private void OnDestroy()
        {
            if (PathRuntimeDriverBehaviour.HasInstance)
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
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

            using var operation = PathExecutionBoundary.Enter();
            _currentFrameId = frameId;
            UpdateTimeScale(unscaledDeltaTime, frameId);
            _scalarAdjustment.Tick(deltaTime, frameId);
            _scheduler.Advance(deltaTime, frameId);

            while (_scheduler.TryDequeueDue(frameId, out PathEventScheduler.ScheduledEvent item))
            {
                if (item.Follower == null || item.Follower.PlaybackId != item.PlaybackId)
                    continue;

                ulong stateRevision = item.Follower.StateRevision;
                ProcessEvent(item.Definition, item.Follower, item.ResumeOnly);
                if (item.Follower.StateRevision != stateRevision)
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
                // The driver reports the original failure.
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
                    NextValidationSet(),
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
                        NextValidationSet(),
                        moveType,
                        EPathFollowerState.Moving);
                }
            }
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

            using var operation = PathExecutionBoundary.Enter();
            _boundFollower = follower;
            PathEventDefinitionValidator.ValidateDefinition(definition, follower, resumeOnly, NextValidationSet());
            _scheduler.Clear();
            ulong playbackId = follower.PlaybackId;
            try
            {
                ProcessEvent(definition, follower, resumeOnly);
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

        // A failed validation leaves its definitions behind.
        private HashSet<PathEventDefinition> NextValidationSet()
        {
            _validationSet.Clear();
            return _validationSet;
        }

        private void ProcessEvent(
            PathEventDefinition definition,
            IPathFollower follower,
            bool resumeOnly)
        {
            EPathEventIntent intent = PathEventRuntime.ResolveIntent(
                definition,
                follower,
                resumeOnly);
            if (!string.IsNullOrEmpty(definition.EventName))
            {
                PathExecutionBoundary.PublishEventReceiver(_receiver, definition.EventName, follower);
                PathExecutionBoundary.PublishEventName(_onEventDispatched, definition.EventName);
            }

            if (intent == EPathEventIntent.Pause)
                _scalarAdjustment.Cancel();
            PathEventRuntime.ApplyLifecycle(intent, follower);

            if (definition.UseTimeScaleAdjust)
                StartTimeScale(definition, follower);

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

            EnqueueDelayedEvents(definition, follower, intent == EPathEventIntent.Pause);
        }

        private void EnqueueDelayedEvents(
            PathEventDefinition definition,
            IPathFollower follower,
            bool pauseEvent)
        {
            IReadOnlyList<PathDelayedEventDefinition> delayed = definition.DelayedEvents;
            for (int i = 0; i < delayed.Count; i++)
            {
                PathDelayedEventDefinition entry = delayed[i];
                bool resumeOnly = pauseEvent && i == 0;
                if (entry.Delay <= 0f)
                {
                    ulong stateRevision = follower.StateRevision;
                    ProcessEvent(entry.Definition, follower, resumeOnly);
                    if (follower.StateRevision != stateRevision)
                        return;
                    continue;
                }

                _scheduler.Schedule(
                    entry.Definition,
                    follower,
                    entry.Delay,
                    resumeOnly,
                    _currentFrameId > 0 ? _currentFrameId : Time.frameCount,
                    follower.PlaybackId);
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
