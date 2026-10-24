using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>How a follower advances through a path.</summary>
    public enum EPathMoveType
    {
        TimeBased = 0,
        SpeedBased = 1,
    }

    public enum EPathFollowerState
    {
        Uninitialized = 0,
        Ready = 1,
        Moving = 2,
        Paused = 3,
        Completed = 4,
    }

    /// <summary>
    /// <see cref="PathFollower.AlignRotationToPathDirection"/>에서 경로 방향을 반영할 euler 축.
    /// 선택되지 않은 축은 현재 회전값을 유지합니다.
    /// </summary>
    [Flags]
    public enum EPathAlignRotationAxes
    {
        None = 0,
        X = 1 << 0,
        Y = 1 << 1,
        Z = 1 << 2,
        All = X | Y | Z,
    }

    /// <summary>
    /// Runtime geometry build settings. Editor preview sampling is deliberately
    /// not part of the runtime path contract.
    /// </summary>
    [Serializable]
    public enum EPathCurveType
    {
        Linear = 0,
        SplineApproximating = 1,
        SplineInterpolating = 2,
    }

    [Serializable]
    public readonly struct PathBuildSettings
    {
        public EPathCurveType CurveType { get; }
        public int SegmentCount { get; }

        public PathBuildSettings(
            EPathCurveType curveType = EPathCurveType.Linear,
            int segmentCount = 500)
        {
            CurveType = curveType;
            SegmentCount = segmentCount;
        }
    }

    [Serializable]
    public readonly struct PathMovementSettings
    {
        public EPathMoveType MoveType { get; }
        public float Value { get; }
        public AnimationCurve TimeCurve { get; }

        public PathMovementSettings(
            EPathMoveType moveType = EPathMoveType.TimeBased,
            float value = 5f,
            AnimationCurve timeCurve = null)
        {
            MoveType = moveType;
            Value = value;
            TimeCurve = moveType == EPathMoveType.TimeBased
                ? timeCurve ?? AnimationCurve.Linear(0f, 0f, 1f, 1f)
                : timeCurve;
        }

        public static PathMovementSettings Time(float duration, AnimationCurve curve = null)
        {
            return new PathMovementSettings(EPathMoveType.TimeBased, duration, curve);
        }

        public static PathMovementSettings Speed(float speed)
        {
            return new PathMovementSettings(EPathMoveType.SpeedBased, speed, null);
        }
    }

    /// <summary>Runtime descriptor used by sequence providers.</summary>
    public readonly struct PathSegmentDescriptor
    {
        public IPathProvider Provider { get; }
        public PathMovementSettings MovementSettings { get; }
        public bool PreservePreviousSpeed { get; }

        public PathSegmentDescriptor(
            IPathProvider provider,
            PathMovementSettings movementSettings,
            bool preservePreviousSpeed)
        {
            Provider = provider;
            MovementSettings = movementSettings;
            PreservePreviousSpeed = preservePreviousSpeed;
        }
    }

    public enum EPathSegmentMovementSource
    {
        Provider = 0,
        Override = 1,
    }

    /// <summary>
    /// Inspector-facing sequence segment. The provider object is intentionally
    /// stored as a Unity component while runtime playback consumes the
    /// interface-only descriptor below.
    /// </summary>
    [Serializable]
    public struct PathSegmentAuthoring
    {
        [SerializeField]
        private MonoBehaviour _providerObject;
        [SerializeField] private EPathSegmentMovementSource _movementSource;
        [SerializeField] private EPathMoveType _moveType;
        [SerializeField] private float _moveValue;
        [SerializeField] private AnimationCurve _timeCurve;
        [SerializeField] private bool _preservePreviousSpeed;

        public MonoBehaviour ProviderObject => _providerObject;
        public IPathProvider Provider => _providerObject as IPathProvider;
        public EPathSegmentMovementSource MovementSource => _movementSource;
        public EPathMoveType MoveType => _moveType;
        public float MoveValue => _moveValue;
        public AnimationCurve TimeCurve => _timeCurve;
        public bool PreservePreviousSpeed => _preservePreviousSpeed;

        public bool TryResolveMovementSettings(
            out PathMovementSettings settings,
            out string error)
        {
            IPathProvider provider = Provider;
            if (provider == null)
            {
                settings = default(PathMovementSettings);
                error = "A provider component is required.";
                return false;
            }

            if (_movementSource == EPathSegmentMovementSource.Provider)
            {
                if (!(provider is IPathMovementProvider movementProvider))
                {
                    settings = default(PathMovementSettings);
                    error = "Provider movement mode requires IPathMovementProvider.";
                    return false;
                }

                settings = PathMovementSettingsUtility.Clone(
                    movementProvider.MovementSettings);
                error = null;
                return true;
            }

            settings = new PathMovementSettings(
                _moveType,
                _moveValue,
                _timeCurve);
            if (!PathMovementSettingsUtility.TryValidate(settings, out error))
                return false;
            settings = PathMovementSettingsUtility.Clone(settings);
            return true;
        }
    }

    public interface IPathProvider
    {
        bool IsInitialized { get; }
        bool IsReady { get; }
        int Revision { get; }
        float PathLength { get; }
        event Action PathChanged;

        Vector3 Sample(float normalizedTime);
        Vector3 SampleDistance(float distance);
    }

    public interface IPathMovementProvider : IPathProvider
    {
        PathMovementSettings MovementSettings { get; }
    }

    public interface IPathSequenceProvider : IPathProvider
    {
        int SegmentCount { get; }
        PathSegmentDescriptor GetSegment(int index);
        float GetSegmentStartDistance(int index);
        float GetSegmentLength(int index);
    }

}
