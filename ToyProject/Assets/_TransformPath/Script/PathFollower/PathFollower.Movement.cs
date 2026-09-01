using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    public partial class PathFollower
    {
        #region Movement Control API

        public void StartMove(Action onComplete = null)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            _pendingReplacePathStart = _replacePathStartWithFollowerPosition;
            StartSinglePath(onComplete);
        }

        public void StartMove(Action onComplete, Action<int> onPathChanged)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (_multiPathData == null)
                throw new InvalidOperationException("MultiPathData is required for a multi-path move.");

            if (!_multiPathData.IsInitialized || !_multiPathData.IsReady)
                throw new InvalidOperationException("MultiPathData must be initialized and ready before movement.");
            if (_multiPathData.PathCount == 0)
                throw new InvalidOperationException("MultiPathData contains no paths.");

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
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (pathData == null)
                throw new ArgumentNullException(nameof(pathData));
            if (!pathData.IsInitialized || !pathData.IsReady)
                throw new InvalidOperationException("PathData must be initialized and ready before movement.");

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
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (pathData == null)
                throw new ArgumentNullException(nameof(pathData));
            if (!pathData.IsInitialized || !pathData.IsReady)
                throw new InvalidOperationException("PathData must be initialized and ready before movement.");
            if (!Enum.IsDefined(typeof(EMoveType), moveType))
                throw new ArgumentOutOfRangeException(nameof(moveType));
            if (!IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (moveType == EMoveType.TimeBased && timeCurve == null)
                throw new ArgumentNullException(nameof(timeCurve));
            if (moveType == EMoveType.TimeBased && timeCurve.length == 0)
                throw new ArgumentException("TimeCurve must contain at least one key for time-based movement.", nameof(timeCurve));

            _useMultiPaths = false;
            _pathData = pathData;
            _moveType = moveType;
            _normalizedTime = 0f;
            _timeCurve = timeCurve;
            _pendingReplacePathStart = _replacePathStartWithFollowerPosition;

            if (moveType == EMoveType.TimeBased)
                _duration = value;
            else
                _speed = value;

            StartSinglePath(onComplete);
        }

        public void StartMove(MultiPathData multiPathData, Action onComplete = null, Action<int> onPathChanged = null)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (multiPathData == null)
                throw new ArgumentNullException(nameof(multiPathData));

            _multiPathData = multiPathData;
            StartMove(onComplete, onPathChanged);
        }

        public void StartMove(
            List<MultiPathData.PathDataConfig> pathDataConfigs,
            Action onComplete = null,
            Action<int> onPathChanged = null)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (pathDataConfigs == null)
                throw new ArgumentNullException(nameof(pathDataConfigs));
            if (pathDataConfigs.Count == 0)
                throw new ArgumentException("At least one PathDataConfig is required.", nameof(pathDataConfigs));

            if (_multiPathData == null)
                throw new InvalidOperationException("MultiPathData is required.");

            _multiPathData.ConfigureSegments(pathDataConfigs);
            if (!_multiPathData.IsInitialized || !_multiPathData.IsReady)
                throw new InvalidOperationException("MultiPathData must be initialized and ready before movement.");
            StartMove(onComplete, onPathChanged);
        }

        public void StartMove(
            List<PathData> pathDataList,
            Action onComplete = null,
            Action<int> onPathChanged = null)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (pathDataList == null)
                throw new ArgumentNullException(nameof(pathDataList));
            if (pathDataList.Count == 0)
                throw new ArgumentException("At least one PathData is required.", nameof(pathDataList));

            if (_multiPathData == null)
                throw new InvalidOperationException("MultiPathData must be assigned on PathFollower.");

            _multiPathData.ConfigureSegments(pathDataList);
            if (!_multiPathData.IsInitialized || !_multiPathData.IsReady)
                throw new InvalidOperationException("MultiPathData must be initialized and ready before movement.");
            StartMove(onComplete, onPathChanged);
        }

        public void StartMove(
            PathData[] pathDataArray,
            Action onComplete = null,
            Action<int> onPathChanged = null)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (pathDataArray == null)
                throw new ArgumentNullException(nameof(pathDataArray));
            if (pathDataArray.Length == 0)
                throw new ArgumentException("At least one PathData is required.", nameof(pathDataArray));

            if (_multiPathData == null)
                throw new InvalidOperationException("MultiPathData must be assigned on PathFollower.");

            _multiPathData.ConfigureSegments(pathDataArray);
            if (!_multiPathData.IsInitialized || !_multiPathData.IsReady)
                throw new InvalidOperationException("MultiPathData must be initialized and ready before movement.");
            StartMove(onComplete, onPathChanged);
        }

        public void StopMove()
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            StopMoveCoroutineIfRunning();
            StopRestoreSpeed();
            _moveRevision++;

            UnsubscribeFromActiveProvider();
            _activePathProvider = null;
            if (_runtimePathProvider.IsInitialized || _runtimePathProvider.IsFaulted)
                _runtimePathProvider.Release();
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
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
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
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsPathValid())
                throw new InvalidOperationException("Path provider is not initialized and ready.");
            if (_normalizedTime >= 1f && !_loop)
            {
                ForcePathCompletion();
            }
            else if (IsPathValid() && (_loop || _normalizedTime < 1f))
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
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsPathValid())
                throw new InvalidOperationException("Path provider is not initialized and ready.");
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
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!_useMultiPaths || _multiPathData == null || pathIndex < 0 || pathIndex >= _multiPathData.PathCount)
                throw new ArgumentOutOfRangeException(nameof(pathIndex));

            MultiPathData.PathDataConfig config = _multiPathData.PathDataConfigs[pathIndex];
            if (config == null || config.PathData == null)
                throw new InvalidOperationException("Selected path segment is invalid.");

            _currentPathIndex = pathIndex;
            ApplyPathConfig(config, _useContinuousSpeedOnPathChange);
            if (!_pathData.IsInitialized || !_pathData.IsReady)
                throw new InvalidOperationException("Selected PathData is not initialized and ready.");
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
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsFinite(globalNormalizedTime) || globalNormalizedTime < 0f || globalNormalizedTime > 1f)
                throw new ArgumentOutOfRangeException(nameof(globalNormalizedTime));
            if (!_useMultiPaths || _multiPathData == null)
            {
                SetNormalizedTime(globalNormalizedTime);
                return;
            }
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

            throw new InvalidOperationException("Global normalized time is outside the MultiPathData segment ranges.");
        }

        public void SetNormalizedTime(float normalizedTime)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            NormalizedTime = normalizedTime;
        }

        #endregion

        #region Movement Helpers

        private void StartMoveCoroutine(int moveRevision)
        {
            int coroutineId = ++_activeMoveCoroutineId;
            _moveCoroutine = StartCoroutine(Co_MoveWrapper(moveRevision, coroutineId));
        }

        private void UpdatePosition(float normalizedTime)
        {
            if (_activePathProvider != null)
            {
                transform.position = _activePathProvider.Sample(normalizedTime);
                return;
            }

            if (_pathData == null)
                throw new InvalidOperationException("PathFollower has no active path provider.");

            Vector3 targetPosition = _pathData.GetPointOnPath(normalizedTime);
            transform.position = targetPosition;
        }

        private void AbortMoveCoroutineAndBumpRevision()
        {
            StopMoveCoroutineIfRunning();
            _moveRevision++;
        }

        private bool ShouldAbortMove(int moveRevision) => moveRevision != _moveRevision;

        private void StopMoveCoroutineIfRunning()
        {
            if (_moveCoroutine == null)
                return;

            StopCoroutine(_moveCoroutine);
            _moveCoroutine = null;
        }

        private void UpdatePositionAndEvents(float normalizedTime)
        {
            UpdatePosition(normalizedTime);
            if (_hasPathEvents)
                InvokePendingPathEventsUpTo(normalizedTime);
        }

        #endregion


        #region Movement Coroutines

        private IEnumerator Co_MoveWrapper(int moveRevision, int coroutineId)
        {
            if (_moveType == EMoveType.TimeBased)
                yield return Co_MoveTimeBased(moveRevision);
            else
                yield return Co_MoveSpeedBased(moveRevision);

            if (coroutineId == _activeMoveCoroutineId)
                _moveCoroutine = null;
        }

        private IEnumerator Co_MoveTimeBased(int moveRevision)
        {
            if (_duration < TIME_BASED_MIN_DURATION)
            {
                throw new InvalidOperationException("Duration must be positive before starting movement.");
            }

            float elapsedTime = 0f;
            float baseNormalizedTime = _normalizedTime;
            _needsElapsedTimeReset = false;

            while (true)
            {
                if (ShouldAbortMove(moveRevision))
                    yield break;

                if (!_isMoving)
                {
                    yield return null;
                    continue;
                }

                if (!ConsumeProviderChange())
                    yield break;

                if (_needsElapsedTimeReset)
                {
                    _needsElapsedTimeReset = false;
                    baseNormalizedTime = _durationChangeBaseNormalizedTime;
                    elapsedTime = 0f;
                }

                elapsedTime += Time.deltaTime;
                _previousNormalizedTime = _normalizedTime;

                float remainingPath = 1f - baseNormalizedTime;
                float t = Mathf.Clamp01(elapsedTime / _duration);
                float curvedT = _timeCurve.Evaluate(t);
                _normalizedTime = baseNormalizedTime + curvedT * remainingPath;

                UpdatePositionAndEvents(_normalizedTime);

                if (ShouldAbortMove(moveRevision))
                    yield break;

                float timeTracking = elapsedTime;
                if (HandlePathCompletion(ref timeTracking))
                    yield break;
                elapsedTime = timeTracking;
                baseNormalizedTime = _loop && _normalizedTime == 0f ? 0f : baseNormalizedTime;

                yield return null;
            }
        }

        private IEnumerator Co_MoveSpeedBased(int moveRevision)
        {
            if (!IsPathValid())
            {
                throw new InvalidOperationException("Path provider is not ready.");
            }

            float pathLength = GetActivePathLength();

            if (pathLength <= 0f)
            {
                throw new InvalidOperationException("Path provider has no measurable length.");
            }

            float traveledDistance = _normalizedTime * pathLength;

            while (true)
            {
                if (ShouldAbortMove(moveRevision))
                    yield break;

                if (!_isMoving)
                {
                    yield return null;
                    continue;
                }

                if (!ConsumeProviderChange())
                    yield break;

                if (_needsTravelDistanceReset)
                {
                    _needsTravelDistanceReset = false;
                    traveledDistance = _normalizedTime * pathLength;
                }

                traveledDistance += _speed * Time.deltaTime;
                _previousNormalizedTime = _normalizedTime;
                _normalizedTime = Mathf.Clamp01(traveledDistance / pathLength);

                UpdatePositionAndEvents(_normalizedTime);

                if (ShouldAbortMove(moveRevision))
                    yield break;

                if (HandlePathCompletion(ref traveledDistance))
                    yield break;

                yield return null;
            }
        }

        #endregion

        #region Speed and Animator API

        /// <summary>
        /// 이동 속도를 기본값으로 즉시 복구합니다
        /// </summary>
        public void RestoreDefaultSpeed()
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            StopRestoreSpeed();

            if (_moveType == EMoveType.SpeedBased)
                Speed = _defaultSpeed;
            else
                Duration = _defaultDuration;

            ApplyAnimatorSpeed(_moveType == EMoveType.SpeedBased ? _speed : _duration, _moveType == EMoveType.SpeedBased, true);
        }

        /// <summary>
        /// 이동 속도를 기본값으로 부드럽게 복구합니다
        /// </summary>
        /// <param name="duration">복구에 걸리는 시간</param>
        /// <param name="curve">복구 시 사용할 비어 있지 않은 애니메이션 커브</param>
        public void RestoreDefaultSpeed(float duration, AnimationCurve curve = null)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsFinite(duration) || duration <= 0f)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (curve == null)
                throw new ArgumentNullException(nameof(curve));
            StopRestoreSpeed();

            _restoreSpeedCoroutine = StartCoroutine(Co_RestoreDefaultSpeed(duration, curve));
        }

        /// <summary>
        /// 이동 속도를 새 값으로 즉시 설정합니다
        /// </summary>
        /// <param name="speed">설정할 속도 값 (EMoveType에 따라 해석)</param>
        /// <param name="applyAnimator">Animator 속도도 함께 조정할지 여부</param>
        public void SetSpeed(float speed, bool applyAnimator = true)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsFinite(speed) || speed <= 0f)
                throw new ArgumentOutOfRangeException(nameof(speed));
            StopRestoreSpeed();

            if (_moveType == EMoveType.SpeedBased)
            {
                Speed = speed;
                ApplyAnimatorSpeed(_speed, true, applyAnimator);
                return;
            }

            if (!IsPathValid())
                throw new InvalidOperationException("A ready path provider is required for time-based speed control.");
            float activePathLength = GetActivePathLength();
            if (activePathLength <= 0f)
                throw new InvalidOperationException("A measurable path provider is required for time-based speed control.");
            float targetDuration = activePathLength / speed;

            Duration = targetDuration;
            ApplyAnimatorSpeed(_duration, false, applyAnimator);
        }

        /// <summary>
        /// 기본 이동 속도 대비 배수를 설정합니다
        /// </summary>
        /// <param name="multiplier">배속 값</param>
        /// <param name="applyAnimator">Animator 속도도 함께 조정할지 여부</param>
        public void SetSpeedMultiplier(float multiplier, bool applyAnimator = true)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsFinite(multiplier) || multiplier <= 0f)
                throw new ArgumentOutOfRangeException(nameof(multiplier));
            StopRestoreSpeed();

            if (_moveType == EMoveType.SpeedBased)
            {
                float targetSpeed = _defaultSpeed * multiplier;
                Speed = targetSpeed;
                ApplyAnimatorSpeed(_speed, true, applyAnimator);
                return;
            }

            float baseDuration = _defaultDuration;
            if (baseDuration <= 0f)
                throw new InvalidOperationException("Default movement duration is not initialized.");
            float targetDuration = baseDuration / multiplier;
            Duration = targetDuration;
            ApplyAnimatorSpeed(_duration, false, applyAnimator);
        }

        /// <summary>
        /// 진행 중인 속도 복구를 중지합니다
        /// </summary>
        public void StopRestoreSpeed()
        {
            if (_restoreSpeedCoroutine != null)
            {
                StopCoroutine(_restoreSpeedCoroutine);
                _restoreSpeedCoroutine = null;
            }
        }

        #endregion


        #region Animator Helpers

        private void ApplyAnimatorSpeed(float targetValue, bool isSpeedBased, bool applyAnimator)
        {
            if (!applyAnimator)
                return;

            if (_animator == null)
                return;

            _animator.speed = CalculateAnimatorSpeed(targetValue, isSpeedBased);
        }

        private float CalculateAnimatorSpeed(float targetValue, bool isSpeedBased)
        {
            if (_animator == null)
                throw new InvalidOperationException("CalculateAnimatorSpeed requires an Animator.");

            if (!IsFinite(targetValue) || targetValue <= 0f)
                throw new ArgumentOutOfRangeException(nameof(targetValue));

            if (isSpeedBased)
            {
                if (!IsFinite(_defaultSpeed) || _defaultSpeed <= 0f)
                    throw new InvalidOperationException("Default movement speed is not initialized.");

                return _defaultAnimatorSpeed * (targetValue / _defaultSpeed);
            }

            if (!IsFinite(_defaultDuration) || _defaultDuration <= 0f)
                throw new InvalidOperationException("Default movement duration is not initialized.");

            return _defaultAnimatorSpeed * (_defaultDuration / targetValue);
        }

        #endregion


        #region Speed Restore Coroutine

        private IEnumerator Co_RestoreDefaultSpeed(float duration, AnimationCurve curve)
        {
            bool isSpeedBased = _moveType == EMoveType.SpeedBased;
            float startValue = isSpeedBased ? _speed : _duration;
            float targetValue = isSpeedBased ? _defaultSpeed : _defaultDuration;
            float startAnimatorSpeed = _animator != null ? _animator.speed : _defaultAnimatorSpeed;
            float targetAnimatorSpeed = _animator != null ? CalculateAnimatorSpeed(targetValue, isSpeedBased) : _defaultAnimatorSpeed;

            float timer = 0f;

            while (timer < duration)
            {
                timer += Time.deltaTime;
                float t = Mathf.Clamp01(timer / duration);

                t = curve.Evaluate(t);

                float currentValue = Mathf.Lerp(startValue, targetValue, t);

                if (isSpeedBased)
                    Speed = currentValue;
                else
                    Duration = currentValue;

                if (_animator != null)
                    _animator.speed = Mathf.Lerp(startAnimatorSpeed, targetAnimatorSpeed, t);

                yield return null;
            }

            if (isSpeedBased)
                Speed = targetValue;
            else
                Duration = targetValue;

            if (_animator != null)
                _animator.speed = targetAnimatorSpeed;

            _restoreSpeedCoroutine = null;
        }

        #endregion
    }
}
