using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower
    {
        #region Movement Control API

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
            _pathData?.Init();
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
                if (_activePathProvider.TrySample(normalizedTime, out Vector3 providerPosition))
                    transform.position = providerPosition;
                return;
            }

            if (_pathData == null)
                return;

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
                Debug.LogWarning("PathFollower: Duration이 너무 작습니다!");
                _isMoving = false;
                RestorePathDataCacheFromSerializedTransformsIfNeeded();
                yield break;
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
                float curvedT = _timeCurve != null ? _timeCurve.Evaluate(t) : t;
                _normalizedTime = baseNormalizedTime + curvedT * remainingPath;

                UpdatePositionAndEvents(_normalizedTime);

                if (ShouldAbortMove(moveRevision))
                    yield break;

                float timeTracking = elapsedTime;
                if (TryHandlePathCompletion(ref timeTracking))
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
                Debug.LogWarning("PathFollower: PathData가 유효하지 않습니다!");
                _isMoving = false;
                RestorePathDataCacheFromSerializedTransformsIfNeeded();
                yield break;
            }

            float pathLength = GetActivePathLength();

            if (pathLength <= 0f)
            {
                Debug.LogWarning("PathFollower: PathLength가 0 이하입니다!");
                _isMoving = false;
                RestorePathDataCacheFromSerializedTransformsIfNeeded();
                yield break;
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

                if (TryHandlePathCompletion(ref traveledDistance))
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
        /// <param name="curve">복구 시 사용할 애니메이션 커브 (null이면 선형 보간)</param>
        public void RestoreDefaultSpeed(float duration, AnimationCurve curve = null)
        {
            StopRestoreSpeed();

            if (duration <= 0f)
            {
                RestoreDefaultSpeed();
                return;
            }

            _restoreSpeedCoroutine = StartCoroutine(Co_RestoreDefaultSpeed(duration, curve));
        }

        /// <summary>
        /// 이동 속도를 새 값으로 즉시 설정합니다
        /// </summary>
        /// <param name="speed">설정할 속도 값 (EMoveType에 따라 해석)</param>
        /// <param name="applyAnimator">Animator 속도도 함께 조정할지 여부</param>
        public void SetSpeed(float speed, bool applyAnimator = true)
        {
            StopRestoreSpeed();

            if (_moveType == EMoveType.SpeedBased)
            {
                Speed = speed;
                ApplyAnimatorSpeed(_speed, true, applyAnimator);
                return;
            }

            float targetDuration = _duration;
            if (_pathData != null && _pathData.PathLength > 0f && speed > 0f)
                targetDuration = _pathData.PathLength / speed;

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
            StopRestoreSpeed();

            float clampedMultiplier = Mathf.Max(multiplier, MIN_SPEED_MULTIPLIER);

            if (_moveType == EMoveType.SpeedBased)
            {
                float targetSpeed = _defaultSpeed * clampedMultiplier;
                Speed = targetSpeed;
                ApplyAnimatorSpeed(_speed, true, applyAnimator);
                return;
            }

            float baseDuration = _defaultDuration > 0f ? _defaultDuration : _duration;
            float targetDuration = baseDuration / clampedMultiplier;
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
                return 1f;

            if (isSpeedBased)
            {
                if (_defaultSpeed <= 0f)
                    return _defaultAnimatorSpeed;

                return _defaultAnimatorSpeed * (targetValue / _defaultSpeed);
            }

            if (_defaultDuration <= 0f || targetValue <= 0f)
                return _defaultAnimatorSpeed;

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

                if (curve != null && curve.length > 0)
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
