using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldJobRunner
    {
        public static void RunBaseComposeJob(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            Vector3 defaultDirection)
        {
            if (!grid.IsValid)
                throw new System.ArgumentException("Base compose requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new System.ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
                throw new System.ArgumentException("Base compose requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new System.ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new System.ArgumentException("Base compose workspace capacity must match the grid.", nameof(workspace));
            if (!FlowFieldGridSpace.IsFinite(defaultDirection)
                || defaultDirection.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                throw new System.ArgumentOutOfRangeException(nameof(defaultDirection));

            if (!workspace.HasNative
                || !workspace.NativeCosts.IsCreated
                || workspace.NativeCosts.Length != grid.CellCount
                || !workspace.NativeBlocked.IsCreated
                || workspace.NativeBlocked.Length != grid.CellCount
                || !workspace.NativeSurfaceValid.IsCreated
                || workspace.NativeSurfaceValid.Length != grid.CellCount
                || !workspace.NativeSurfaceNormals.IsCreated
                || workspace.NativeSurfaceNormals.Length != grid.CellCount
                || !workspace.NativeEscape.IsCreated
                || workspace.NativeEscape.Length != grid.CellCount
                || !workspace.NativeGoalDirections.IsCreated
                || workspace.NativeGoalDirections.Length != grid.CellCount
                || !workspace.NativeGoalFlags.IsCreated
                || workspace.NativeGoalFlags.Length != grid.CellCount
                || !workspace.NativeFinalDirections.IsCreated
                || workspace.NativeFinalDirections.Length != grid.CellCount
                || !workspace.NativeFinalSpeeds.IsCreated
                || workspace.NativeFinalSpeeds.Length != grid.CellCount)
                throw new System.InvalidOperationException(
                    "Native FlowField workspace must be initialized with the configured grid before running a job.");
            int count = grid.CellCount;
            for (int i = 0; i < count; i++)
            {
                workspace.NativeBlocked[i] = workspace.Blocked[i] ? (byte)1 : (byte)0;
                bool surfaceValid = surface.IsSurfaceValid(i);
                workspace.NativeSurfaceValid[i] = surfaceValid ? (byte)1 : (byte)0;
                workspace.NativeSurfaceNormals[i] = surfaceValid
                    ? FlowFieldVectorUtility.ValidateSurfaceNormal(surface.GetSurfaceNormal(i))
                    : Vector3.zero;
                workspace.NativeEscape[i] = workspace.EscapeDirections[i];
                workspace.NativeGoalDirections[i] = workspace.GoalDirections[i];
                workspace.NativeGoalFlags[i] = (byte)workspace.GoalFlags[i];
            }

            var job = new FlowFieldBaseComposeJob
            {
                CellCount = count,
                DefaultDirection = defaultDirection,
                Blocked = workspace.NativeBlocked,
                SurfaceValid = workspace.NativeSurfaceValid,
                SurfaceNormals = workspace.NativeSurfaceNormals,
                Escape = workspace.NativeEscape,
                GoalDirections = workspace.NativeGoalDirections,
                GoalFlags = workspace.NativeGoalFlags,
                FinalDirections = workspace.NativeFinalDirections,
                FinalSpeeds = workspace.NativeFinalSpeeds,
            };
            job.Schedule().Complete();

            for (int i = 0; i < count; i++)
            {
                workspace.FinalDirections[i] = workspace.NativeFinalDirections[i];
                workspace.FinalSpeedMultipliers[i] = workspace.NativeFinalSpeeds[i];
            }
        }
    }

    [BurstCompile]
    internal struct FlowFieldBaseComposeJob : IJob
    {
        public int CellCount;
        public Vector3 DefaultDirection;
        [ReadOnly] public NativeArray<byte> Blocked;
        [ReadOnly] public NativeArray<byte> SurfaceValid;
        [ReadOnly] public NativeArray<Vector3> SurfaceNormals;
        [ReadOnly] public NativeArray<Vector3> Escape;
        [ReadOnly] public NativeArray<Vector3> GoalDirections;
        [ReadOnly] public NativeArray<byte> GoalFlags;
        public NativeArray<Vector3> FinalDirections;
        public NativeArray<float> FinalSpeeds;

        public void Execute()
        {
            float3 defaultDirection = math.normalize((float3)(Vector3)DefaultDirection);

            for (int i = 0; i < CellCount; i++)
            {
                FinalSpeeds[i] = 1f;
                if (SurfaceValid[i] == 0)
                {
                    FinalDirections[i] = Vector3.zero;
                    continue;
                }

                float3 direction;
                bool isDefaultDirection = false;
                if (Blocked[i] != 0)
                {
                    direction = FlowFieldVectorUtility.ProjectOnSurface(
                        (float3)Escape[i],
                        (float3)SurfaceNormals[i]);
                }
                else if ((GoalFlags[i] & (byte)FlowFieldGoalFlags.Directed) != 0)
                {
                    direction = FlowFieldVectorUtility.ProjectOnSurface(
                        (float3)GoalDirections[i],
                        (float3)SurfaceNormals[i]);
                }
                else
                {
                    direction = defaultDirection;
                    isDefaultDirection = true;
                }

                if (isDefaultDirection)
                {
                    direction = FlowFieldVectorUtility.ProjectDefaultOnSurface(
                        direction,
                        (float3)SurfaceNormals[i]);
                }

                FinalDirections[i] = math.lengthsq(direction) > 1e-8f
                    ? (Vector3)math.normalize(direction)
                    : Vector3.zero;
            }
        }
    }
}
