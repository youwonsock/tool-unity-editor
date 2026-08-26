using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower
    {
        #region Properties

        public AnimationCurve TimeCurve
        {
            get => _timeCurve;
            set => _timeCurve = value;
        }

        public PathData PathData
        {
            get => _pathData;
            set
            {
                if (_pathData == value)
                    return;

                _pathData = value;
                _normalizedTime = 0f;
            }
        }

        public float NormalizedTime
        {
            get => _normalizedTime;
            set
            {
                _normalizedTime = Mathf.Clamp01(value);
                UpdatePosition(_normalizedTime);
            }
        }

        public bool IsMoving => _isMoving;
        public bool HasActiveMoveCoroutine => _moveCoroutine != null;

        public bool ReplacePathStartWithFollowerPosition
        {
            get => _replacePathStartWithFollowerPosition;
            set => _replacePathStartWithFollowerPosition = value;
        }

        public EMoveType CurrentMoveType
        {
            get => _moveType;
            set => _moveType = value;
        }

        public float Duration
        {
            get => _duration;
            set
            {
                float clampedValue = Mathf.Clamp(value, TIME_BASED_MIN_DURATION, TIME_BASED_MAX_DURATION);
                if (_isMoving
                    && _moveType == EMoveType.TimeBased
                    && !Mathf.Approximately(_duration, clampedValue))
                {
                    _durationChangeBaseNormalizedTime = _normalizedTime;
                    _needsElapsedTimeReset = true;
                }

                _duration = clampedValue;
            }
        }

        public float Speed
        {
            get => _speed;
            set => _speed = Mathf.Clamp(value, SPEED_BASED_MIN_SPEED, SPEED_BASED_MAX_SPEED);
        }

        public bool Loop
        {
            get => _loop;
            set => _loop = value;
        }

        public float DefaultSpeed => _defaultSpeed;
        public float DefaultDuration => _defaultDuration;

        public Animator Animator
        {
            get => _animator;
            set => _animator = value;
        }

        public MultiPathData MultiPathData
        {
            get => _multiPathData;
            set => _multiPathData = value;
        }

        public int CurrentPathIndex => _currentPathIndex;

        public float GlobalNormalizedTime
        {
            get
            {
                if (_useMultiPaths && _multiPathData != null && _multiPathData.PathCount > 0)
                {
                    float pathStartNormalized = _multiPathData.GetPathStartNormalizedValue(_currentPathIndex);
                    float pathEndNormalized = _multiPathData.GetPathEndNormalizedValue(_currentPathIndex);
                    float pathRange = pathEndNormalized - pathStartNormalized;
                    return pathStartNormalized + pathRange * _normalizedTime;
                }

                return _normalizedTime;
            }
        }

        #endregion
    }
}
