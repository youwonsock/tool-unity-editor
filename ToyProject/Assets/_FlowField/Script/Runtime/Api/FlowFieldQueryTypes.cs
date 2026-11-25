using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>A Goal for <see cref="IFlowFieldController.SetGoal"/>, or none to clear it.</summary>
    public readonly struct FlowFieldGoalRequest
    {
        /// <summary>False for <see cref="None"/>, which clears the Goal.</summary>
        public bool HasGoal { get; }
        /// <summary>The requested Goal in world space; the build resolves it to a cell.</summary>
        public Vector3 WorldPosition { get; }
        /// <summary>
        /// In world units. 0 lets every cell follow the Goal; a positive value limits it to cells
        /// within that distance (XYZ) of the resolved Goal, and the rest use the Default Direction.
        /// </summary>
        public float InfluenceRadius { get; }

        private FlowFieldGoalRequest(bool hasGoal, Vector3 worldPosition, float influenceRadius)
        {
            HasGoal = hasGoal;
            WorldPosition = worldPosition;
            InfluenceRadius = influenceRadius;
        }

        /// <summary>No Goal: SetGoal with it clears the Goal.</summary>
        public static FlowFieldGoalRequest None
            => new FlowFieldGoalRequest(false, default, 0f);

        /// <summary>A Goal at a world position.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="worldPosition"/> is not finite, or <paramref name="influenceRadius"/> is
        /// not finite or is negative.
        /// </exception>
        public static FlowFieldGoalRequest Position(Vector3 worldPosition, float influenceRadius = 0f)
        {
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            if (!FlowFieldGridSpace.IsFinite(influenceRadius) || influenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(influenceRadius));
            return new FlowFieldGoalRequest(true, worldPosition, influenceRadius);
        }

        /// <summary>
        /// A Goal at the Transform's position now. It does not follow later moves; set it again,
        /// or use <see cref="FlowFieldGoalTarget"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null or destroyed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">As <see cref="Position"/> throws.</exception>
        public static FlowFieldGoalRequest Target(Transform target, float influenceRadius = 0f)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            return Position(target.position, influenceRadius);
        }
    }

    /// <summary>The result of <see cref="IFlowFieldProvider.ClampPositionToGrid"/>.</summary>
    public readonly struct FlowFieldClampResult
    {
        /// <summary>The clamped world position.</summary>
        public Vector3 Position { get; }
        /// <summary>Whether X moved.</summary>
        public bool ClampedX { get; }
        /// <summary>Whether Y moved; always false on a Surface2D grid, which keeps Y.</summary>
        public bool ClampedY { get; }
        /// <summary>Whether Z moved.</summary>
        public bool ClampedZ { get; }

        /// <summary>Creates a result from its parts.</summary>
        public FlowFieldClampResult(Vector3 position, bool clampedX, bool clampedY, bool clampedZ)
        {
            Position = position;
            ClampedX = clampedX;
            ClampedY = clampedY;
            ClampedZ = clampedZ;
        }
    }

    /// <summary>What the field says at one position: where to move and how fast.</summary>
    public readonly struct FlowFieldSample
    {
        /// <summary>
        /// The world-space direction to move, unit length or zero. On Surface2D it lies in the
        /// cell's surface.
        /// </summary>
        public Vector3 Direction { get; }
        /// <summary>The factor for the mover's speed; 0 stops it, and Modifiers can raise it above 1.</summary>
        public float SpeedMultiplier { get; }
        /// <summary>The ground normal of a Surface2D cell; zero in Volume3D and without a surface.</summary>
        public Vector3 SurfaceNormal { get; }
        /// <summary>Whether the cell lies on ground the Surface2D scan found; false in Volume3D.</summary>
        public bool HasSurface { get; }
        /// <summary>
        /// Whether the position fell on a usable cell. A Surface2D cell without ground samples
        /// successfully but stopped, with this false.
        /// </summary>
        public bool HasCell { get; }

        /// <summary>Creates a sample from its parts.</summary>
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

        /// <summary>No direction, no speed, no surface and no cell: what a failed read returns.</summary>
        public static FlowFieldSample Stopped
            => new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, false);
    }
}
