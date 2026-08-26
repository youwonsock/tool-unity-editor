using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower
    {
        #region Public Methods

        public void StartMove(Action onComplete = null)
        {
            _pendingReplacePathStart = _replacePathStartWithFollowerPosition;
            StartSinglePath(onComplete);
        }

        public void StartMove(Action onComplete, Action<int> onPathChanged)
        {
            if (_multiPathData == null)
            {
                Debug.LogWarning("PathFollower: MultiPathData가 설정되지 않았습니다!");
                return;
            }

            _multiPathData.Init();
            if (_multiPathData.PathCount == 0)
            {
                Debug.LogWarning("PathFollower: MultiPathData에 경로가 없습니다!");
                return;
            }

            StopMove();
            _useMultiPaths = true;
            _onMultiComplete = onComplete;
            _onPathChanged = onPathChanged;
            _currentPathIndex = 0;
            _pendingReplacePathStart = _replacePathStartWithFollowerPosition;
            StartCurrentPath();
        }

        public void StartMove(PathData pathData, Action onComplete = null)
        {
            if (pathData == null)
            {
                Debug.LogWarning("PathFollower: 제공된 PathData가 null입니다!");
                return;
            }

            _useMultiPaths = false;
            _pathData = pathData;
            _normalizedTime = 0f;
            _pendingReplacePathStart = _replacePathStartWithFollowerPosition;
            StartSinglePath(onComplete);
        }

        public void StartMove(
            PathData pathData,
            EMoveType moveType,
            float value,
            AnimationCurve timeCurve = null,
            Action onComplete = null)
        {
            if (pathData == null)
            {
                Debug.LogWarning("PathFollower: 제공된 PathData가 null입니다!");
                return;
            }

            _useMultiPaths = false;
            _pathData = pathData;
            _moveType = moveType;
            _normalizedTime = 0f;
            _timeCurve = timeCurve ?? AnimationCurve.Linear(0, 0, 1, 1);
            _pendingReplacePathStart = _replacePathStartWithFollowerPosition;

            if (moveType == EMoveType.TimeBased)
                _duration = Mathf.Max(0f, value);
            else
                _speed = Mathf.Max(0f, value);

            StartSinglePath(onComplete);
        }

        public void StartMove(MultiPathData multiPathData, Action onComplete = null, Action<int> onPathChanged = null)
        {
            if (multiPathData == null)
            {
                Debug.LogWarning("PathFollower: 제공된 MultiPathData가 null입니다!");
                return;
            }

            _multiPathData = multiPathData;
            StartMove(onComplete, onPathChanged);
        }

        public void StartMove(
            List<MultiPathData.PathDataConfig> pathDataConfigs,
            Action onComplete = null,
            Action<int> onPathChanged = null)
        {
            if (pathDataConfigs == null || pathDataConfigs.Count == 0)
            {
                Debug.LogWarning("PathFollower: 제공된 PathDataConfig 리스트가 비어있습니다!");
                return;
            }

            if (_multiPathData == null)
            {
                Debug.LogWarning("PathFollower: MultiPathData가 설정되지 않았습니다!");
                return;
            }

            _multiPathData.Init(pathDataConfigs);
            StartMove(onComplete, onPathChanged);
        }

        public void StartMove(
            List<PathData> pathDataList,
            Action onComplete = null,
            Action<int> onPathChanged = null)
        {
            if (pathDataList == null || pathDataList.Count == 0)
            {
                Debug.LogWarning("PathFollower: 제공된 PathData 리스트가 비어있습니다!");
                return;
            }

            if (_multiPathData == null && !TryGetComponent(out _multiPathData))
                _multiPathData = gameObject.AddComponent<MultiPathData>();

            _multiPathData.Init(pathDataList);
            StartMove(onComplete, onPathChanged);
        }

        public void StartMove(
            PathData[] pathDataArray,
            Action onComplete = null,
            Action<int> onPathChanged = null)
        {
            if (pathDataArray == null || pathDataArray.Length == 0)
            {
                Debug.LogWarning("PathFollower: 제공된 PathData 배열이 비어있습니다!");
                return;
            }

            if (_multiPathData == null && !TryGetComponent(out _multiPathData))
                _multiPathData = gameObject.AddComponent<MultiPathData>();

            _multiPathData.Init(pathDataArray);
            StartMove(onComplete, onPathChanged);
        }

        public void StopMove()
        {
            RestorePathDataCacheFromSerializedTransformsIfNeeded();
            StopMoveCoroutineIfRunning();
            StopRestoreSpeed();
            _moveRevision++;

            UnsubscribeFromActiveProvider();
            _activePathProvider = null;
            _providerChangePending = false;
            _isMoving = false;
            _useMultiPaths = false;
            _onComplete = null;
            _onMultiComplete = null;
            _onPathChanged = null;
            _pendingReplacePathStart = false;
            PublishState(EPathFollowerState.Stopped);
        }

        public void PauseMove(bool pauseAnimation = false)
        {
            if (!_isMoving)
                return;

            _isMoving = false;
            PublishState(EPathFollowerState.Paused);

            if (pauseAnimation && _animator != null)
            {
                _pausedAnimatorSpeed = _animator.speed;
                _animator.speed = 0f;
                _isAnimatorPaused = true;
            }
        }

        public void ResumeMove(bool resumeAnimation = false)
        {
            if (_normalizedTime >= 1f && !_loop)
            {
                TryForcePathCompletion();
            }
            else if (IsPathValid() && _normalizedTime < 1f && !_loop)
            {
                if (_moveCoroutine != null)
                {
                    _isMoving = true;
                }
                else
                {
                    AbortMoveCoroutineAndBumpRevision();
                    _isMoving = true;
                    PublishState(EPathFollowerState.Moving);
                    StartMoveCoroutine(_moveRevision);
                }
            }

            if (resumeAnimation && _animator != null)
            {
                if (_isAnimatorPaused)
                    _animator.speed = _pausedAnimatorSpeed;
                _isAnimatorPaused = false;
            }

            if (_isMoving)
                PublishState(EPathFollowerState.Moving);
        }

        public void ResetToStart()
        {
            ResetMoveProgressState(
                resetNormalizedTime: true,
                needsTravelDistanceReset: _moveType == EMoveType.SpeedBased,
                needsElapsedTimeReset: _moveType == EMoveType.TimeBased);
            UpdatePosition(_normalizedTime);

            _hasPathEvents = _pathData != null && _pathData.HasPathEvents;
            if (_hasPathEvents)
                InvokePendingPathEventsUpTo(0f);
        }

        public void SetPathIndex(int pathIndex, float normalizedTime = 0f)
        {
            if (!_useMultiPaths || _multiPathData == null || pathIndex < 0 || pathIndex >= _multiPathData.PathCount)
            {
                Debug.LogWarning($"PathFollower: 유효하지 않은 경로 인덱스입니다! Index={pathIndex}");
                return;
            }

            MultiPathData.PathDataConfig config = _multiPathData.PathDataConfigs[pathIndex];
            if (config == null || config.PathData == null)
            {
                Debug.LogWarning($"PathFollower: 경로 {pathIndex}의 PathData가 유효하지 않습니다!");
                return;
            }

            _currentPathIndex = pathIndex;
            ApplyPathConfig(config, _useContinuousSpeedOnPathChange);
            PathDataInitialization.Initialize(_pathData);
            NormalizedTime = normalizedTime;
            _previousNormalizedTime = _normalizedTime;
            _durationChangeBaseNormalizedTime = _normalizedTime;
            _needsElapsedTimeReset = _moveType == EMoveType.TimeBased;
            _needsTravelDistanceReset = _moveType == EMoveType.SpeedBased;
            _hasPathEvents = _pathData.HasPathEvents;

            if (_hasPathEvents)
            {
                _nextPathEventIndex = 0;
                InvokePendingPathEventsUpTo(_normalizedTime);
            }

            _onPathChanged?.Invoke(_currentPathIndex);
            PublishSegmentChanged();
        }

        public void SetGlobalNormalizedTime(float globalNormalizedTime)
        {
            if (!_useMultiPaths || _multiPathData == null || _multiPathData.PathCount == 0)
                return;

            globalNormalizedTime = Mathf.Clamp01(globalNormalizedTime);
            for (int i = 0; i < _multiPathData.PathCount; i++)
            {
                float pathStart = _multiPathData.GetPathStartNormalizedValue(i);
                float pathEnd = _multiPathData.GetPathEndNormalizedValue(i);

                if (globalNormalizedTime < pathStart || globalNormalizedTime > pathEnd)
                    continue;

                float pathRange = pathEnd - pathStart;
                float localNormalized = pathRange > 0f
                    ? (globalNormalizedTime - pathStart) / pathRange
                    : 0f;
                SetPathIndex(i, localNormalized);
                return;
            }

            SetPathIndex(_multiPathData.PathCount - 1, 1f);
        }

        public void SetNormalizedTime(float normalizedTime)
        {
            NormalizedTime = normalizedTime;
        }

        #endregion
    }
}
