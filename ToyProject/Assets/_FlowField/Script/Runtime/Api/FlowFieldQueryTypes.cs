using System;
using UnityEngine;

namespace Common.FlowField
{
    public readonly struct FlowFieldGoalRequest
    {
        public bool HasGoal { get; }
        public Vector3 WorldPosition { get; }
        public float InfluenceRadius { get; }

        private FlowFieldGoalRequest(bool hasGoal, Vector3 worldPosition, float influenceRadius)
        {
            HasGoal = hasGoal;
            WorldPosition = worldPosition;
            InfluenceRadius = influenceRadius;
        }

        public static FlowFieldGoalRequest None
            => new FlowFieldGoalRequest(false, default, 0f);

        public static FlowFieldGoalRequest Position(Vector3 worldPosition, float influenceRadius = 0f)
        {
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            if (!FlowFieldGridSpace.IsFinite(influenceRadius) || influenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(influenceRadius));
            return new FlowFieldGoalRequest(true, worldPosition, influenceRadius);
        }

        public static FlowFieldGoalRequest Target(Transform target, float influenceRadius = 0f)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            return Position(target.position, influenceRadius);
        }
    }

    public readonly struct FlowFieldClampResult
    {
        public Vector3 Position { get; }
        public bool ClampedX { get; }
        public bool ClampedY { get; }
        public bool ClampedZ { get; }

        public FlowFieldClampResult(Vector3 position, bool clampedX, bool clampedY, bool clampedZ)
        {
            Position = position;
            ClampedX = clampedX;
            ClampedY = clampedY;
            ClampedZ = clampedZ;
        }
    }

    public readonly struct FlowFieldSample
    {
        public Vector3 Direction { get; }
        public float SpeedMultiplier { get; }
        public Vector3 SurfaceNormal { get; }
        public bool HasSurface { get; }
        public bool HasCell { get; }

        public FlowFieldSample(
            Vector3 direction,
            float speedMultiplier,
            Vector3 surfaceNormal,
            bool hasSurface,
            bool hasCell)
        {
            Direction = direction;
            SpeedMultiplier = speedMultiplier;
            SurfaceNormal = surfaceNormal;
            HasSurface = hasSurface;
            HasCell = hasCell;
        }

        public static FlowFieldSample Stopped
            => new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, false);
    }
}
