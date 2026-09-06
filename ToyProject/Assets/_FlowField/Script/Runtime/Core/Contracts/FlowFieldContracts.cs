using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    public enum FlowFieldSpaceMode
    {
        Surface2D = 0,
        Volume3D = 1,
    }

    public enum FlowFieldVolumeSliceAxis
    {
        X = 0,
        Y = 1,
        Z = 2,
    }

    /// <summary>
    /// Selects how a Volume3D field is sampled for editor visualization.
    /// This is a display-only setting and does not change the computed grid.
    /// </summary>
    public enum FlowFieldVolumeGizmoMode
    {
        FullVolume = 0,
        Slice = 1,
    }

    /// <summary>
    /// Selects where the immutable navigation/base field comes from.
    /// RuntimeDynamic is deliberately zero so managers serialized before the
    /// mode was introduced keep their existing runtime behaviour.
    /// </summary>
    public enum FlowFieldBakeMode
    {
        RuntimeDynamic = 0,
        StaticBaked = 1,
    }

    /// <summary>
    /// Observable lifecycle of a flow-field provider.  Values are explicit so
    /// serialized diagnostics and external integrations remain stable.
    /// </summary>
    public enum FlowFieldRuntimeState
    {
        Uninitialized = 0,
        Building = 1,
        Ready = 2,
        Suspended = 3,
        Faulted = 4,
        Released = 5,
    }

    /// <summary>
    /// 외부 이동 시스템이 현재 FlowField를 읽는 계약입니다.
    /// 구현체는 샘플을 Push하지 않으며 호출자가 자신의 FixedUpdate에서 Pull합니다.
    /// </summary>
    public interface IFlowFieldProvider
    {
        bool IsInitialized { get; }
        FlowFieldRuntimeState State { get; }
        bool IsReady { get; }
        bool IsRebuilding { get; }
        bool IsFaulted { get; }
        string LastError { get; }
        int Revision { get; }
        FlowFieldSpaceMode SpaceMode { get; }
        FlowFieldBakeMode BakeMode { get; }
        event Action FieldChanged;
        event Action<FlowFieldRuntimeState> StateChanged;

        bool TrySample(Vector3 worldPosition, out FlowFieldSample sample);
        FlowFieldSample Sample(Vector3 worldPosition);

        FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
        bool TryGetFieldInfo(out FlowFieldFieldInfo info);
    }

    /// <summary>
    /// 외부 시스템이 FlowField 계산 입력을 변경하는 계약입니다.
    /// </summary>
    public interface IFlowFieldController : IFlowFieldProvider
    {
        void Init();
        void RequestRebuild();
        void Release();
        void SetSpaceMode(FlowFieldSpaceMode mode);
        void SetGoal(in FlowFieldGoalRequest request);
        void NotifyCellsDirty();

        bool RegisterDynamicObstacle(Collider collider);
        bool UnregisterDynamicObstacle(Collider collider);
        void NotifyObstacleRegionDirty(Bounds worldBounds);

        bool RegisterVectorModifier(IFlowFieldVectorModifier modifier);
        bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier);
        void NotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change);
    }

    [Flags]
    public enum FlowFieldModifierChange
    {
        None = 0,
        Value = 1 << 0,
        Area = 1 << 1,
        Priority = 1 << 2,
    }

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

    public interface IFlowFieldVectorModifier
    {
        Collider InfluenceCollider { get; }
        int Priority { get; }
        int Revision { get; }

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
