using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
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
    /// Optional capability exposed by a FlowFieldManager that supports the
    /// static/dynamic bake selection.  Keeping this separate from the legacy
    /// controller contract avoids breaking external controller implementations.
    /// </summary>
    public interface IFlowFieldBakeController
    {
        FlowFieldBakeMode BakeMode { get; }

        /// <summary>
        /// Requests a full runtime Surface bake.  Static-baked managers accept
        /// the call but intentionally leave their committed field untouched.
        /// </summary>
        void NotifySurfaceDirty();
    }

    /// <summary>
    /// 외부 이동 시스템이 현재 FlowField를 읽는 계약입니다.
    /// 구현체는 샘플을 Push하지 않으며 호출자가 자신의 FixedUpdate에서 Pull합니다.
    /// </summary>
    public interface IFlowFieldProvider
    {
        bool IsInitialized { get; }
        bool IsReady { get; }
        int Revision { get; }
        event Action FieldChanged;

        FlowFieldSample Sample(Vector3 worldPosition);

        FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
    }

    /// <summary>
    /// 외부 시스템이 FlowField 계산 입력을 변경하는 계약입니다.
    /// </summary>
    public interface IFlowFieldController
    {
        bool IsInitialized { get; }
        void Init();
        void Rebuild();
        void Release();

        void SetGoalPosition(Vector3 worldPosition);
        void SetGoalPosition(Vector3 worldPosition, float influenceRadius);
        void SetGoalTarget(Transform target);
        void SetGoalTarget(Transform target, float influenceRadius);
        void ClearGoal();

        void RegisterDynamicObstacle(Collider collider);
        void UnregisterDynamicObstacle(Collider collider);
        void NotifyObstacleRegionDirty(Bounds worldBounds);

        void RegisterVectorModifier(IFlowFieldVectorModifier modifier);
        void UnregisterVectorModifier(IFlowFieldVectorModifier modifier);
        void MarkVectorModifierDirty(IFlowFieldVectorModifier modifier);
        void MarkVectorModifierAreaDirty(IFlowFieldVectorModifier modifier);
    }

    public readonly struct FlowFieldClampResult
    {
        public Vector3 Position { get; }
        public bool ClampedX { get; }
        public bool ClampedZ { get; }

        public FlowFieldClampResult(Vector3 position, bool clampedX, bool clampedZ)
        {
            Position = position;
            ClampedX = clampedX;
            ClampedZ = clampedZ;
        }
    }

    public readonly struct FlowFieldSample
    {
        public Vector3 Direction { get; }
        public float SpeedMultiplier { get; }
        public Vector3 SurfaceNormal { get; }
        public bool HasSurface { get; }

        public FlowFieldSample(
            Vector3 direction,
            float speedMultiplier,
            Vector3 surfaceNormal,
            bool hasSurface)
        {
            Direction = direction;
            SpeedMultiplier = speedMultiplier;
            SurfaceNormal = surfaceNormal;
            HasSurface = hasSurface;
        }

        internal static FlowFieldSample Stopped
            => new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false);
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

        internal static FlowFieldVectorState Stopped => new FlowFieldVectorState(Vector3.zero, 1f);
    }

    public readonly struct FlowFieldVectorModifierContext
    {
        public int CellIndex { get; }
        public int CellX { get; }
        public int CellZ { get; }
        public Vector3 CellCenter { get; }
        public Vector3 SurfaceNormal { get; }
        public FlowFieldGridSpace GridSpace { get; }
        public bool IsGoalDirected { get; }

        internal FlowFieldVectorModifierContext(
            int cellIndex,
            int cellX,
            int cellZ,
            Vector3 cellCenter,
            Vector3 surfaceNormal,
            FlowFieldGridSpace gridSpace,
            bool isGoalDirected)
        {
            CellIndex = cellIndex;
            CellX = cellX;
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

        FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context);
    }
}
