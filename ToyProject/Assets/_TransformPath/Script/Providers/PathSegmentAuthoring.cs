using System;
using UnityEngine;

namespace Common.TransformPath
{
    public enum EPathSegmentMovementSource
    {
        /// <summary>The segment moves with its PathData's own settings.</summary>
        Path = 0,
        /// <summary>The segment moves with the settings stored here.</summary>
        Override = 1,
    }

    /// <summary>One segment of a <see cref="MultiPathData"/>, as it is authored in the Inspector.</summary>
    [Serializable]
    public struct PathSegmentAuthoring
    {
        [SerializeField] private PathData _path;
        [SerializeField] private EPathSegmentMovementSource _movementSource;
        [SerializeField] private EPathMoveType _moveType;
        [SerializeField] private float _moveValue;
        [SerializeField] private AnimationCurve _timeCurve;
        [SerializeField] private bool _preservePreviousSpeed;

        public PathSegmentAuthoring(PathData path, bool preservePreviousSpeed = false)
        {
            _path = path;
            _movementSource = EPathSegmentMovementSource.Path;
            _moveType = EPathMoveType.TimeBased;
            _moveValue = 0f;
            _timeCurve = null;
            _preservePreviousSpeed = preservePreviousSpeed;
        }

        public PathSegmentAuthoring(PathData path, PathMovementSettings movement, bool preservePreviousSpeed = false)
        {
            _path = path;
            _movementSource = EPathSegmentMovementSource.Override;
            _moveType = movement.MoveType;
            _moveValue = movement.Value;
            _timeCurve = movement.CopyTimeCurve();
            _preservePreviousSpeed = preservePreviousSpeed;
        }

        public PathData Path => _path;
        public EPathSegmentMovementSource MovementSource => _movementSource;
        public bool PreservePreviousSpeed => _preservePreviousSpeed;

        internal bool TryResolveMovement(out PathMovementSettings settings, out string error)
        {
            PathData path = Path;
            if (path == null)
            {
                settings = default(PathMovementSettings);
                error = "A PathData is required.";
                return false;
            }
            settings = _movementSource == EPathSegmentMovementSource.Path
                ? path.MovementSettings
                : new PathMovementSettings(_moveType, _moveValue, _timeCurve);
            return settings.TryValidate(out error);
        }
    }
}
