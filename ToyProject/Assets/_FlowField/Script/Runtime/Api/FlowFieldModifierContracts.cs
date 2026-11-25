using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What changed in a registered Modifier, for <see cref="IFlowFieldController.NotifyModifierChanged"/>.
    /// Flags combine.
    /// </summary>
    [Flags]
    public enum FlowFieldModifierChange
    {
        /// <summary>Nothing; NotifyModifierChanged returns at once.</summary>
        None = 0,
        /// <summary>The values its snapshot applies changed; its cells are composed again.</summary>
        Value = 1 << 0,
        /// <summary>Its Influence Collider moved, changed shape, or was replaced or destroyed.</summary>
        Area = 1 << 1,
        /// <summary>Its Priority changed, and with it the composition order.</summary>
        Priority = 1 << 2,
    }

    /// <summary>The direction and speed of one cell, as a Modifier reads and returns it.</summary>
    public readonly struct FlowFieldVectorState
    {
        /// <summary>The world-space direction, unit length or zero.</summary>
        public Vector3 Direction { get; }
        /// <summary>The speed factor, 0 or more.</summary>
        public float SpeedMultiplier { get; }

        /// <summary>Creates a state from its parts; the build checks a Modifier's result.</summary>
        public FlowFieldVectorState(Vector3 direction, float speedMultiplier)
        {
            Direction = direction;
            SpeedMultiplier = speedMultiplier;
        }

        /// <summary>Zero direction and zero speed.</summary>
        public static FlowFieldVectorState Stopped => new FlowFieldVectorState(Vector3.zero, 0f);
    }

    /// <summary>The cell a Modifier snapshot is applied to.</summary>
    public readonly struct FlowFieldVectorModifierContext
    {
        /// <summary>The cell's flat index in <see cref="GridSpace"/> (see its ToFlatIndex).</summary>
        public int CellIndex { get; }
        /// <summary>The cell's local X coordinate.</summary>
        public int CellX { get; }
        /// <summary>The cell's local Y coordinate; 0 on Surface2D.</summary>
        public int CellY { get; }
        /// <summary>The cell's local Z coordinate.</summary>
        public int CellZ { get; }
        /// <summary>The cell center in world space; on Surface2D at the ground height of the cell.</summary>
        public Vector3 CellCenter { get; }
        /// <summary>
        /// The ground normal on Surface2D, where a returned direction must lie in the surface;
        /// zero on Volume3D.
        /// </summary>
        public Vector3 SurfaceNormal { get; }
        /// <summary>The grid of the build.</summary>
        public FlowFieldGridSpace GridSpace { get; }
        /// <summary>
        /// Whether the cell's base direction leads to the Goal rather than the Default Direction.
        /// </summary>
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

    /// <summary>
    /// What a Modifier reports when the session reads it: at registration, at
    /// each NotifyModifierChanged, when a build captures snapshots and when a
    /// restart reads every Modifier again. FlowField never polls a Modifier.
    /// </summary>
    public readonly struct FlowFieldModifierObservation
    {
        /// <summary>
        /// The trigger Collider whose cells the Modifier changes. It must be a trigger, and a
        /// MeshCollider must be convex.
        /// </summary>
        public Collider InfluenceCollider { get; }
        /// <summary>
        /// The composition order, lowest first; no two Modifiers of one Manager may share it.
        /// </summary>
        public int Priority { get; }
        /// <summary>
        /// Changes when the Modifier's values change; a different Revision at a notification counts
        /// as a <see cref="FlowFieldModifierChange.Value"/> change.
        /// </summary>
        public int Revision { get; }

        /// <summary>Creates an observation from its parts.</summary>
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
        /// <summary>
        /// Called at registration, at each notification, at every build start
        /// and at a restart, so it should not allocate.
        /// </summary>
        FlowFieldModifierObservation Observe();

        /// <summary>
        /// Captures the value calculation for one build.  The returned object
        /// must not read mutable component state after this call.
        /// </summary>
        IFlowFieldModifierSnapshot CaptureSnapshot();
    }

    /// <summary>
    /// The values of one Modifier for one build, from <see cref="IFlowFieldVectorModifier.CaptureSnapshot"/>.
    /// </summary>
    public interface IFlowFieldModifierSnapshot
    {
        /// <summary>
        /// Returns the state of one cell inside the Influence Collider. It runs only on open cells
        /// that move (not blocked, the Goal cell or unreachable), in ascending Priority, with
        /// <paramref name="current"/> holding the result so far.
        /// </summary>
        /// <returns>
        /// A finite state with a speed of 0 or more and a unit-length or zero direction, which on
        /// Surface2D lies in the surface; anything else faults the session.
        /// </returns>
        FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context);
    }
}
