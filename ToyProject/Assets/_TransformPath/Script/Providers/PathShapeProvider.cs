using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// The base of a path drawn by code instead of control points, such as a
    /// circle. A subclass computes its shape in <see cref="TryPrepare"/> and
    /// answers <see cref="Evaluate"/> by arc length; play it with
    /// <see cref="PathPlaybackRequest.Shape"/> or give it to a queue. It builds
    /// in Awake in Play Mode. After changing what the shape reads, call
    /// <see cref="PathProviderBehaviour.Rebuild"/>; setting
    /// <see cref="MovementSettings"/> rebuilds when the shape was built before.
    /// <para>
    /// Contract: <see cref="Evaluate"/> reads only what <see cref="TryPrepare"/>
    /// kept, so each build is published whole. The distance is arc length,
    /// which the base cannot check: a shape that is not uniform moves at an
    /// uneven speed. An exception from <see cref="Evaluate"/> during a frame
    /// stops the follower, which goes back to Ready; there is no fault state.
    /// A subclass that declares Awake or OnDestroy overrides the base method
    /// and calls it.
    /// </para>
    /// </summary>
    public abstract class PathShapeProvider : PathProviderBehaviour
    {
        [SerializeField] private EPathMoveType _moveType = EPathMoveType.SpeedBased;
        [SerializeField, Min(PathConstants.MIN_MOVE_VALUE)] private float _moveValue = 5f;
        [SerializeField] private AnimationCurve _timeCurve = null;

        [NonSerialized] private float _length;
        [NonSerialized] private PathMovementSettings _movement;
        [NonSerialized] private bool _built;
        [NonSerialized] private bool _failed;
        // A failed build may already have changed what TryPrepare keeps, so
        // the next good build publishes the geometry whatever TryPrepare says.
        [NonSerialized] private bool _geometryUnpublished;

        protected PathShapeProvider() { }

        public override bool IsReady => _built && !_failed;

        public override float PathLength
        {
            get
            {
                ThrowIfNotReady();
                return _length;
            }
        }

        /// <summary>
        /// The published movement once built, the authored one before. Setting
        /// it validates the value and rebuilds when the shape was built before.
        /// </summary>
        public PathMovementSettings MovementSettings
        {
            get => _built ? _movement : GetAuthoringMovement();
            set
            {
                value.Validate(nameof(value));
                _moveType = value.MoveType;
                _moveValue = value.Value;
                _timeCurve = value.CopyTimeCurve();
                RebuildIfRequested();
            }
        }

        /// <summary>Builds the shape in Play Mode. An override calls this base method.</summary>
        protected virtual void Awake()
        {
            if (Application.isPlaying)
                Rebuild();
        }

        public override Vector3 Sample(float progress)
        {
            ThrowIfNotReady();
            PathValueUtility.ThrowIfNotFinite(progress, nameof(progress));
            return Evaluate(Mathf.Clamp01(progress) * _length);
        }

        /// <summary>
        /// Computes the shape from the current settings and keeps what
        /// <see cref="Evaluate"/> needs. Returns false with a message when it
        /// cannot build.
        /// </summary>
        /// <param name="length">The arc length, finite and positive.</param>
        /// <param name="shapeChanged">Whether the shape differs from the last successful build.</param>
        /// <param name="error">Why the shape cannot build, when it returns false.</param>
        protected abstract bool TryPrepare(out float length, out bool shapeChanged, out string error);

        /// <summary>The point at an arc length of 0..length, from what <see cref="TryPrepare"/> kept.</summary>
        protected abstract Vector3 Evaluate(float distance);

        private protected sealed override bool TryBuild(out bool geometryChanged, out bool definitionChanged, out string error)
        {
            geometryChanged = false;
            definitionChanged = false;
            float length;
            bool shapeChanged;
            try
            {
                if (!TryPrepare(out length, out shapeChanged, out error))
                {
                    if (string.IsNullOrEmpty(error))
                        error = "TryPrepare failed without a message.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = $"TryPrepare threw {exception.GetType().Name}: {exception.Message}";
                return false;
            }
            if (!PathValueUtility.IsFinite(length) || length <= 0f)
            {
                error = $"TryPrepare returned a length of {length}. It must be finite and positive.";
                return false;
            }
            // Play, Seek and rotation sample outside a frame, where a throwing
            // Evaluate would break a command halfway. Fail the build instead.
            if (!TryEvaluateEnd(0f, out error) || !TryEvaluateEnd(length, out error))
                return false;
            PathMovementSettings movement = GetAuthoringMovement();
            if (!movement.TryValidate(out error))
                return false;

            bool firstBuild = !_built;
            geometryChanged = firstBuild
                || shapeChanged
                || _geometryUnpublished
                || !Mathf.Approximately(length, _length);
            definitionChanged = firstBuild || !movement.IsSameAs(_movement);
            _length = length;
            _movement = movement;
            _built = true;
            _failed = false;
            _geometryUnpublished = false;
            return true;
        }

        private protected sealed override void MarkFailed()
        {
            _failed = true;
            _geometryUnpublished = true;
        }

        // A shape has no events of its own.
        private protected sealed override PathRuntimeEvent[] WholeRouteEvents => Array.Empty<PathRuntimeEvent>();

        private protected sealed override PathRoute CreateRoute()
        {
            return PathRoute.FromProvider(
                this,
                new[] { new PathRoute.Segment(this, 0f, _length, _movement, false, null) },
                false);
        }

        private bool TryEvaluateEnd(float distance, out string error)
        {
            Vector3 point;
            try
            {
                point = Evaluate(distance);
            }
            catch (Exception exception)
            {
                error = $"Evaluate({distance}) threw {exception.GetType().Name}: {exception.Message}";
                return false;
            }
            if (!PathValueUtility.IsFinite(point))
            {
                error = $"Evaluate({distance}) returned a point that is not finite.";
                return false;
            }
            error = null;
            return true;
        }

        private PathMovementSettings GetAuthoringMovement()
        {
            return new PathMovementSettings(_moveType, _moveValue, _timeCurve);
        }
    }
}
