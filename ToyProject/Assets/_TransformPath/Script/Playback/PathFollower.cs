using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Moves its Transform along a path. Playback, events and their effects run
    /// in <see cref="PathPlayback"/>; this component applies the result and
    /// turns the CharacterController off while it moves.
    /// Callbacks run after the command or frame that raised them has finished.
    /// </summary>
    public sealed class PathFollower : PathRuntimeBehaviour
    {
        [Header("Events")]
        [Tooltip("An IPathEventReceiver that hears the name of every event this follower reaches.")]
        [SerializeField] private MonoBehaviour _eventReceiver;

        [Header("Rotation")]
        [Tooltip("Turns to the direction of travel while moving. Turn it off to keep the rotation, as on an escalator.")]
        [SerializeField] private bool _rotateToPathDirection = true;

        [Header("Movement Collision")]
        [SerializeField] private CharacterController _characterController;
        [SerializeField] private bool _suppressCharacterControllerDuringPlayback = true;

        private readonly PathPlaybackListeners _listeners = new PathPlaybackListeners();
        private PathPlayback _playback;
        private bool _characterControllerDisabledByFollower;

        private PathPlayback Playback => _playback ??= CreatePlayback();

        public EPathFollowerState State => Playback.State;
        public bool IsMoving => Playback.IsMoving;
        /// <summary>Identifies the current playback. Each Play issues a new one; 0 when none runs.</summary>
        public ulong PlaybackId => Playback.PlaybackId;
        public PathProviderBehaviour CurrentProvider => Playback.Route?.Provider as PathProviderBehaviour;
        /// <summary>Progress along the whole route, 0..1.</summary>
        public float Progress => Playback.Progress;
        /// <summary>Progress within the current segment, 0..1. Equals Progress outside a sequence.</summary>
        public float SegmentProgress => Playback.SegmentProgress;
        public int SegmentIndex => Playback.SegmentIndex;
        public EPathMoveType MoveType => Playback.MoveType;
        public float Speed => Playback.Speed;
        public float Duration => Playback.Duration;

        public bool RotateToPathDirection
        {
            get => _rotateToPathDirection;
            set => _rotateToPathDirection = value;
        }

        internal override ulong TickPlaybackId => Playback.PlaybackId;
        internal int MotionEpoch => Playback.MotionEpoch;
        /// <summary>The definition revision of the route this follower plays, or -1 without one.</summary>
        internal int RouteDefinitionRevision => Playback.Route?.DefinitionRevision ?? -1;

        public event Action<EPathFollowerState> StateChanged
        {
            add => _listeners.StateChanged.Add(value);
            remove => _listeners.StateChanged.Remove(value);
        }

        /// <summary>A sequence entered a segment by moving. Seek does not raise it.</summary>
        public event Action<int> SegmentChanged
        {
            add => _listeners.SegmentChanged.Add(value);
            remove => _listeners.SegmentChanged.Remove(value);
        }

        public event Action Completed
        {
            add => _listeners.Completed.Add(value);
            remove => _listeners.Completed.Remove(value);
        }

        /// <summary>The name of each event reached, after the receiver heard it.</summary>
        public event Action<string> EventReached
        {
            add => _listeners.EventReached.Add(value);
            remove => _listeners.EventReached.Remove(value);
        }

        #region Commands

        /// <summary>Starts a playback from the start of the route. Throws before anything changes when the request is invalid.</summary>
        public bool Play(PathPlaybackRequest request)
        {
            using var operation = PathExecutionBoundary.Enter();
            bool changed = Playback.Play(request);
            TrySuppressCharacterController();
            ApplyPositionIfChanged();
            return changed;
        }

        public bool Stop()
        {
            using var operation = PathExecutionBoundary.Enter();
            bool changed = Playback.Stop();
            AfterPlaybackWork();
            return changed;
        }

        public bool Pause()
        {
            using var operation = PathExecutionBoundary.Enter();
            bool changed = Playback.Pause();
            AfterPlaybackWork();
            return changed;
        }

        public bool Resume()
        {
            using var operation = PathExecutionBoundary.Enter();
            bool changed = Playback.Resume();
            AfterPlaybackWork();
            return changed;
        }

        /// <summary>Moves to a progress of 0..1. Events at and before it are skipped.</summary>
        public bool Seek(float progress)
        {
            using var operation = PathExecutionBoundary.Enter();
            bool changed = Playback.Seek(progress);
            ApplyPositionIfChanged();
            return changed;
        }

        /// <summary>Moves to a progress within one segment of a sequence.</summary>
        public bool SeekSegment(int segmentIndex, float localProgress)
        {
            using var operation = PathExecutionBoundary.Enter();
            bool changed = Playback.SeekSegment(segmentIndex, localProgress);
            ApplyPositionIfChanged();
            return changed;
        }

        public bool SetSpeed(float speed)
        {
            using var operation = PathExecutionBoundary.Enter();
            return Playback.SetSpeed(speed);
        }

        public bool SetDuration(float duration)
        {
            using var operation = PathExecutionBoundary.Enter();
            return Playback.SetDuration(duration);
        }

        /// <summary>Runs an event asset now with its current values, as if the follower reached it.</summary>
        public void TriggerEvent(PathEventSettingSO setting)
        {
            PathEventDefinition definition = PathEventDefinition.FromAuthoring(setting);
            using var operation = PathExecutionBoundary.Enter();
            Playback.TriggerEvent(definition);
            AfterPlaybackWork();
        }

        /// <summary>
        /// Turns once to the direction of travel at the current position. Only the
        /// chosen euler axes take the path direction; the others keep their value.
        /// </summary>
        public bool AlignRotationToPathDirection(EPathAlignRotationAxes axes = EPathAlignRotationAxes.All)
        {
            if (axes == EPathAlignRotationAxes.None || !Playback.TryEvaluateDirection(out Vector3 direction))
                return false;

            Quaternion pathRotation = Quaternion.LookRotation(direction, Vector3.up);
            if (axes == EPathAlignRotationAxes.All)
            {
                transform.rotation = pathRotation;
                return true;
            }

            Vector3 pathEuler = pathRotation.eulerAngles;
            Vector3 currentEuler = transform.rotation.eulerAngles;
            if ((axes & EPathAlignRotationAxes.X) == 0) pathEuler.x = currentEuler.x;
            if ((axes & EPathAlignRotationAxes.Y) == 0) pathEuler.y = currentEuler.y;
            if ((axes & EPathAlignRotationAxes.Z) == 0) pathEuler.z = currentEuler.z;
            transform.rotation = Quaternion.Euler(pathEuler);
            return true;
        }

        #endregion

        #region Runtime

        internal override void Initialize()
        {
            if (_characterController == null)
                TryGetComponent(out _characterController);
            if (_eventReceiver is IPathEventReceiver receiver)
                _listeners.Receiver.Add(receiver.OnPathEvent);
            else if (_eventReceiver != null)
                Debug.LogError($"PathFollower '{name}' event receiver does not implement IPathEventReceiver.", this);
            using var operation = PathExecutionBoundary.Enter();
            Playback.Initialize();
        }

        internal override void Release()
        {
            using var operation = PathExecutionBoundary.Enter();
            Playback.Release();
            TryRestoreCharacterController();
        }

        protected override void OnDisabled()
        {
            Stop();
            TryRestoreCharacterController();
        }

        internal override EPathTickPhases TickPhases => EPathTickPhases.Effects | EPathTickPhases.Movement;

        internal override void Tick(EPathTickPhase phase, float deltaTime, int frameId)
        {
            // Most frames have no blend or delayed event: only the frame is recorded.
            if (phase == EPathTickPhase.Effects && !Playback.HasPendingEffects)
            {
                Playback.TickEffects(deltaTime, frameId);
                return;
            }
            if (phase == EPathTickPhase.Movement && !Playback.IsMoving)
                return;
            using var operation = PathExecutionBoundary.Enter();
            if (phase == EPathTickPhase.Effects)
                Playback.TickEffects(deltaTime, frameId);
            else
                Playback.TickMovement(deltaTime);
            AfterPlaybackWork();
        }

        internal override void OnTickFault(Exception exception)
        {
            using var operation = PathExecutionBoundary.Enter();
            Playback.Stop();
            TryRestoreCharacterController();
        }

        /// <summary>The queue writes the limit for the next movement here.</summary>
        internal void SetMotionLimit(PathMotionLimit limit) => Playback.Limit = limit;

        /// <summary>
        /// Takes the provider's new geometry now, paused or not, when only the
        /// geometry changed. The queue calls it before it reads the progress.
        /// </summary>
        internal void RefreshRouteGeometry()
        {
            PathRoute route = Playback.Route;
            if (Playback.PlaybackId == 0 || route == null || route.Revision == route.Provider.Revision)
                return;
            using var operation = PathExecutionBoundary.Enter();
            if (Playback.TryRefreshRoute())
                ApplyPositionIfChanged();
        }

        #endregion

        #region Private Methods

        private PathPlayback CreatePlayback()
        {
            _listeners.Owner = this;
            return new PathPlayback(_listeners);
        }

        private void AfterPlaybackWork()
        {
            ApplyPositionIfChanged();
            TryRestoreCharacterController();
        }

        private void ApplyPositionIfChanged()
        {
            if (!Playback.ConsumePositionDirty() || !Playback.CanSample)
                return;
            transform.position = Playback.SamplePosition();
            if (_rotateToPathDirection && Playback.TryEvaluateDirection(out Vector3 direction))
                transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        private void TrySuppressCharacterController()
        {
            if (!Playback.IsMoving
                || !_suppressCharacterControllerDuringPlayback
                || _characterController == null
                || !_characterController.enabled)
                return;
            _characterController.enabled = false;
            _characterControllerDisabledByFollower = true;
        }

        private void TryRestoreCharacterController()
        {
            if (!_characterControllerDisabledByFollower || _characterController == null)
                return;
            // A paused playback still owns the suppression.
            if (isActiveAndEnabled && Playback.PlaybackId != 0
                && (Playback.IsMoving || Playback.State == EPathFollowerState.Paused))
                return;
            _characterController.enabled = true;
            _characterControllerDisabledByFollower = false;
        }

        #endregion
    }
}
