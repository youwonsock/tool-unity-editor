using System;
using UnityEngine;

namespace Common.FlowField
{
    [Flags]
    public enum FlowFieldModifierChange
    {
        None = 0,
        Value = 1 << 0,
        Area = 1 << 1,
        Priority = 1 << 2,
    }

    public readonly struct FlowFieldVectorState
    {
        public Vector3 Direction { get; }
        public float SpeedMultiplier { get; }

        public FlowFieldVectorState(Vector3 direction, float speedMultiplier)
        {
            Direction = direction;
            SpeedMultiplier = speedMultiplier;
        }

        public static FlowFieldVectorState Stopped => new FlowFieldVectorState(Vector3.zero, 0f);
    }

    public readonly struct FlowFieldVectorModifierContext
    {
        public int CellIndex { get; }
        public int CellX { get; }
        public int CellY { get; }
        public int CellZ { get; }
        public Vector3 CellCenter { get; }
        public Vector3 SurfaceNormal { get; }
        public FlowFieldGridSpace GridSpace { get; }
        public bool IsGoalDirected { get; }

        internal FlowFieldVectorModifierContext(
            int cellIndex,
            int cellX,
            int cellY,
            int cellZ,
            Vector3 cellCenter,
            Vector3 surfaceNormal,
            FlowFieldGridSpace gridSpace,
            bool isGoalDirected)
        {
            CellIndex = cellIndex;
            CellX = cellX;
            CellY = cellY;
            CellZ = cellZ;
            CellCenter = cellCenter;
            SurfaceNormal = surfaceNormal;
            GridSpace = gridSpace;
            IsGoalDirected = isGoalDirected;
        }

    }

    /// <summary>What a Modifier reports each time the session polls it.</summary>
    public readonly struct FlowFieldModifierObservation
    {
        public Collider InfluenceCollider { get; }
        public int Priority { get; }
        public int Revision { get; }

        public FlowFieldModifierObservation(Collider influenceCollider, int priority, int revision)
        {
            InfluenceCollider = influenceCollider;
            Priority = priority;
            Revision = revision;
        }
    }

    /// <summary>
    /// <see cref="Observe"/>, <see cref="CaptureSnapshot"/> and
    /// <see cref="IFlowFieldModifierSnapshot.Modify"/> run inside a FlowField
    /// operation. Calling RegisterVectorModifier, UnregisterVectorModifier,
    /// NotifyModifierChanged, RegisterDynamicObstacle or UnregisterDynamicObstacle
    /// from them takes effect after that operation. Other Manager commands
    /// throw InvalidOperationException. Sampling is allowed.
    /// </summary>
    public interface IFlowFieldVectorModifier
    {
        /// <summary>Called often while polling, so it should not allocate.</summary>
        FlowFieldModifierObservation Observe();

        /// <summary>
        /// Captures the value calculation for one build.  The returned object
        /// must not read mutable component state after this call.
        /// </summary>
        IFlowFieldModifierSnapshot CaptureSnapshot();
    }

    public interface IFlowFieldModifierSnapshot
    {
        FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context);
    }
}
