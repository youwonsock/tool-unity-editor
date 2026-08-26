using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower
    {
        #region Private Methods

        /// <summary>
        /// PathDataConfig를 적용하고 continuous speed 계산을 수행합니다.
        /// pause(speed&lt;=0) 중에는 <see cref="ShouldPreservePausedMoveSpeed"/>로 speed를 덮어쓰지 않고
        /// <see cref="_pendingDefaultSpeed"/>만 갱신합니다 (세그먼트 전환·end pause fix).
        /// </summary>
        /// <param name="config">적용할 PathDataConfig</param>
        /// <param name="useContinuousSpeed">이전 속도를 유지할지 여부</param>
        private void ApplyPathConfig(MultiPathData.PathDataConfig config, bool useContinuousSpeed)
        {
            float previousSpeed = 0f;
            bool hasPreviousSpeed = false;

            if (useContinuousSpeed)
            {
                if (_moveType == EMoveType.SpeedBased)
                {
                    if (_speed > SPEED_BASED_MIN_SPEED)
                    {
                        previousSpeed = _speed;
                        hasPreviousSpeed = true;
                    }
                }
                else
                {
                    float previousPathLength = _pathData != null ? _pathData.PathLength : 0f;
                    if (previousPathLength > 0f && _duration > TIME_BASED_MIN_DURATION)
                    {
                        previousSpeed = previousPathLength / _duration;
                        hasPreviousSpeed = previousSpeed > 0f;
                    }
                }
            }

            _pathData = config.PathData;
            _moveType = config.MoveType;
            _timeCurve = config.TimeCurve;

            if (useContinuousSpeed && hasPreviousSpeed)
            {
                if (_moveType == EMoveType.SpeedBased)
                {
                    _speed = Mathf.Clamp(previousSpeed, SPEED_BASED_MIN_SPEED, SPEED_BASED_MAX_SPEED);
                }
                else
                {
                    float targetDuration = config.PathData.PathLength > 0f && previousSpeed > 0f
                        ? config.PathData.PathLength / previousSpeed
                        : config.Value;
                    _duration = Mathf.Clamp(targetDuration, TIME_BASED_MIN_DURATION, TIME_BASED_MAX_DURATION);
                }
            }
            else
            {
                if (config.MoveType == EMoveType.TimeBased)
                    _duration = config.Value;
                else if (!ShouldPreservePausedMoveSpeed())
                {
                    _speed = config.Value;
                    _pendingDefaultSpeed = config.Value;
                }
                else
                    _pendingDefaultSpeed = config.Value;
            }
        }

        /// <summary>
        /// SpeedBased 이동 중 pause(speed&lt;=0) 상태면 다음 세그먼트 ApplyPathConfig에서 speed 갱신을 건너뜁니다.
        /// </summary>
        private bool ShouldPreservePausedMoveSpeed()
        {
            return _moveType == EMoveType.SpeedBased && _speed <= 0f;
        }

        private void StartCurrentPath()
        {
            if (!_useMultiPaths)
            {
                StartSinglePath(_onMultiComplete);
                return;
            }

            if (_multiPathData == null || _currentPathIndex < 0 || _currentPathIndex >= _multiPathData.PathCount)
            {
                CompleteAllPaths();
                return;
            }

            MultiPathData.PathDataConfig config = _multiPathData.PathDataConfigs[_currentPathIndex];

            if (config == null || config.PathData == null)
            {
                Debug.LogWarning($"PathFollower: 경로 {_currentPathIndex}의 PathData가 유효하지 않습니다!");
                MoveToNextPath();
                return;
            }

            bool useContinuous = _useContinuousSpeedOnPathChange && _currentPathIndex > 0;
            ApplyPathConfig(config, useContinuous);

            _loop = false;
            StartSinglePath(OnCurrentPathComplete);

            _onPathChanged?.Invoke(_currentPathIndex);
            PublishSegmentChanged();
        }

        private void OnCurrentPathComplete()
        {
            if (!_useMultiPaths)
            {
                _onComplete?.Invoke();
                return;
            }

            MoveToNextPath();
        }

        private void MoveToNextPath()
        {
            _currentPathIndex++;

            if (_multiPathData == null || _currentPathIndex >= _multiPathData.PathCount)
            {
                CompleteAllPaths();
                return;
            }

            StartCurrentPath();
        }

        private void CompleteAllPaths()
        {
            RestorePathDataCacheFromSerializedTransformsIfNeeded();

            _isMoving = false;
            _useMultiPaths = false;
            _moveCoroutine = null;
            StopRestoreSpeed();
            UnsubscribeFromActiveProvider();
            PublishState(EPathFollowerState.Stopped);
            PublishCompleted();
            _onMultiComplete?.Invoke();
        }

        private bool IsOnLastMultiPathSegment()
        {
            if (!_useMultiPaths || _multiPathData == null)
                return true;

            return _currentPathIndex >= _multiPathData.PathCount - 1;
        }

        #endregion
    }
}
