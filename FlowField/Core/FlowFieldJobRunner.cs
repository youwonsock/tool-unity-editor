using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal static class FlowFieldJobRunner
    {
        public static void RunBaseComposeJob(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            Vector3 defaultDirection)
        {
            workspace.EnsureNative(grid.CellCount);
            int count = grid.CellCount;
            for (int i = 0; i < count; i++)
            {
                workspace.NativeBlocked[i] = workspace.Blocked[i] ? (byte)1 : (byte)0;
                workspace.NativeSurfaceValid[i] = surface != null && surface.IsSurfaceValid(i) ? (byte)1 : (byte)0;
                workspace.NativeSurfaceNormals[i] = surface != null
                    ? FlowFieldVectorUtility.SanitizeSurfaceNormal(surface.GetSurfaceNormal(i))
                    : Vector3.up;
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
            float3 fallback = math.lengthsq((float3)(Vector3)DefaultDirection) > 1e-8f
                ? math.normalize((float3)(Vector3)DefaultDirection)
                : new float3(0f, 0f, 1f);

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
                    direction = fallback;
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
