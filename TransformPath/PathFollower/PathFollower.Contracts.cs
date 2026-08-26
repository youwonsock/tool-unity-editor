using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower : IPathFollower
    {
        #region Member Variables

        private IPathProvider _activePathProvider = null;
        private bool _providerChangePending = false;
        private EPathFollowerState _pathState = EPathFollowerState.Stopped;
        private int _stateRevision = 0;

        private event Action _stateChanged;
        private event Action _segmentChanged;
        private event Action _completed;

        #endregion


        #region Properties

        IPathProvider IPathFollower.CurrentProvider
        {
            get
            {
                if (_activePathProvider != null)
                    return _activePathProvider;

                if (_useMultiPaths)
                    return _multiPathData;

                return _pathData;
            }
        }
        EPathFollowerState IPathFollower.State => _pathState;
        int IPathFollower.StateRevision => _stateRevision;
        int IPathFollower.CurrentSegmentIndex => _currentPathIndex;
        EPathMoveType IPathFollower.CurrentMoveType => PathTypeConversion.ToPublic(_moveType);

        event Action IPathFollower.StateChanged
        {
            add => _stateChanged += value;
            remove => _stateChanged -= value;
        }

        event Action IPathFollower.SegmentChanged
        {
            add => _segmentChanged += value;
            remove => _segmentChanged -= value;
        }

        event Action IPathFollower.Completed
        {
            add => _completed += value;
            remove => _completed -= value;
        }

        #endregion


        #region Public Methods

        public bool TryStartMove(IPathProvider provider, PathMoveSettings settings)
        {
            if (!IsAlive(provider))
                return false;

            if (!provider.IsReady && provider is IPathController controller)
                controller.TryRebuild();

            if (!provider.IsReady)
                return false;

            if (_replacePathStartWithFollowerPosition && !(provider is PathData))
            {
                Debug.LogWarning("PathFollower: ReplacePathStartWithFollowerPosition은 PathData Provider에서만 지원됩니다.");
                return false;
            }

            if (provider is PathData pathData)
            {
                StartMove(
                    pathData,
                    PathTypeConversion.ToLegacy(settings.MoveType),
                    settings.Value,
                    settings.TimeCurve,
                    null);
                Loop = settings.Loop;
                return IsMoving;
            }

            StopMove();
            _useMultiPaths = false;
            _activePathProvider = provider;
            _moveType = PathTypeConversion.ToLegacy(settings.MoveType);
            _timeCurve = settings.TimeCurve ?? AnimationCurve.Linear(0, 0, 1, 1);
            _loop = settings.Loop;

            if (_moveType == EMoveType.TimeBased)
                Duration = settings.Value;
            else
                Speed = settings.Value;

            _normalizedTime = 0f;
            _onComplete = null;
            _onMultiComplete = null;
            _hasPathEvents = provider is IPathEventSource eventSource && eventSource.PathEvents.Count > 0;
            _nextPathEventIndex = 0;
            _providerChangePending = false;
            SubscribeToActiveProvider();
            AbortMoveCoroutineAndBumpRevision();

            _defaultSpeed = _speed;
            _defaultDuration = _duration;
            _defaultAnimatorSpeed = _animator != null ? _animator.speed : 1f;
            _isMoving = true;
            PublishState(EPathFollowerState.Moving);

            if (_hasPathEvents)
            {
                InvokePendingPathEventsUpTo(0f);
                if (!_isMoving)
                    return false;
            }

            StartMoveCoroutine(_moveRevision);
            return true;
        }

        public bool TrySeek(float normalizedTime)
        {
            if (!IsFinite(normalizedTime) || !IsPathValid())
                return false;

            SetNormalizedTime(normalizedTime);
            PublishState(_isMoving ? EPathFollowerState.Moving : EPathFollowerState.Paused);
            return true;
        }

        #endregion


        #region Private Methods

        private void SubscribeToActiveProvider()
        {
            UnsubscribeFromActiveProvider();

            if (_activePathProvider != null)
                _activePathProvider.PathChanged += HandleActiveProviderChanged;
        }

        private void UnsubscribeFromActiveProvider()
        {
            if (_activePathProvider != null)
                _activePathProvider.PathChanged -= HandleActiveProviderChanged;
        }

        private void HandleActiveProviderChanged()
        {
            _providerChangePending = true;
        }

        private bool ConsumeProviderChange()
        {
            if (!_providerChangePending)
                return true;

            _providerChangePending = false;

            if (!IsAlive(_activePathProvider) || !_activePathProvider.IsReady)
            {
                _isMoving = false;
                PublishState(EPathFollowerState.Stopped);
                return false;
            }

            UpdatePosition(_normalizedTime);
            return true;
        }

        private void PublishState(EPathFollowerState state)
        {
            if (_pathState == state)
                return;

            _pathState = state;
            _stateRevision++;
            _stateChanged?.Invoke();
        }

        private void PublishSegmentChanged()
        {
            _stateRevision++;
            _segmentChanged?.Invoke();
        }

        private void PublishCompleted()
        {
            _completed?.Invoke();
        }

        private float GetActivePathLength()
            => _activePathProvider != null ? _activePathProvider.PathLength : (_pathData != null ? _pathData.PathLength : 0f);

        private IReadOnlyList<PathEventEntry> GetActivePathEvents()
        {
            if (_activePathProvider is IPathEventSource providerEventSource)
                return providerEventSource.PathEvents;

            return _pathData != null ? _pathData.PathEvents : null;
        }

        private static bool IsAlive(IPathProvider provider)
        {
            if (provider == null)
                return false;

            if (provider is UnityEngine.Object unityObject && unityObject == null)
                return false;

            return true;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        #endregion
    }
}
