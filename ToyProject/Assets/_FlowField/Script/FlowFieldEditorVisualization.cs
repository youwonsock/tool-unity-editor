#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldGizmoRequest
    {
        internal FlowFieldGridSpace Grid { get; }
        internal FlowFieldSurfaceData Surface { get; }
        internal FlowFieldWorkspace Workspace { get; }
        internal float CellSize { get; }
        internal bool HasGoal { get; }
        internal Vector3 GoalWorld { get; }
        internal float GoalRadius { get; }

        internal FlowFieldGizmoRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            float cellSize,
            bool hasGoal,
            Vector3 goalWorld,
            float goalRadius)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            CellSize = cellSize;
            HasGoal = hasGoal;
            GoalWorld = goalWorld;
            GoalRadius = goalRadius;
        }
    }

    internal static class FlowFieldGizmoDrawer
    {
        private const float SURFACE_PADDING = 0.05f;
        private const float DIRECTION_EPSILON_SQR = 0.0001f;
        private const int LARGE_GRID_CELL_THRESHOLD = 2500;
        private static readonly Color VALID_BAKE_BOUNDS_COLOR = new Color(0.15f, 0.85f, 1f, 0.9f);
        private static readonly Color STALE_BAKE_BOUNDS_COLOR = new Color(1f, 0.55f, 0.1f, 0.9f);

        internal static void Draw(in FlowFieldGizmoRequest request)
        {
            DrawGridAnchor(request.Grid);
            DrawObstacles(request);
            DrawModifierInfluence(request);
            DrawGoal(request);
            DrawFlowVectors(request);
        }

        internal static void DrawBakeBounds(Bounds worldBounds, bool isValid)
        {
            Color color = isValid ? VALID_BAKE_BOUNDS_COLOR : STALE_BAKE_BOUNDS_COLOR;
            Gizmos.color = new Color(color.r, color.g, color.b, 0.05f);
            Gizmos.DrawCube(worldBounds.center, worldBounds.size);
            Gizmos.color = color;
            Gizmos.DrawWireCube(worldBounds.center, worldBounds.size);

            float padding = Mathf.Min(0.1f, worldBounds.size.y * 0.1f);
            Vector3 arrowStart = new Vector3(worldBounds.center.x, worldBounds.max.y - padding, worldBounds.center.z);
            float arrowLength = Mathf.Max(
                FlowFieldBakeBoundsUtility.MinBoundsHeight,
                worldBounds.size.y - padding * 2f);
            DrawBakeDirectionArrow(arrowStart, arrowLength);
        }

        private static void DrawGridAnchor(FlowFieldGridSpace grid)
        {
            Vector3 anchor = grid.Origin + Vector3.up * SURFACE_PADDING;
            const float arm = 0.35f;
            Gizmos.color = new Color(0.2f, 1f, 0.35f, 0.95f);
            Gizmos.DrawLine(anchor + Vector3.left * arm, anchor + Vector3.right * arm);
            Gizmos.DrawLine(anchor + Vector3.back * arm, anchor + Vector3.forward * arm);
        }

        private static void DrawObstacles(in FlowFieldGizmoRequest request)
        {
            if (request.Workspace.Capacity != request.Grid.CellCount)
                return;

            Gizmos.color = new Color(1f, 0.35f, 0.35f, 0.45f);
            for (int index = 0; index < request.Grid.CellCount; index++)
            {
                if (!request.Workspace.Blocked[index] || !request.Surface.IsSurfaceValid(index))
                    continue;
                Gizmos.DrawCube(CellCenter(request, index), Vector3.one * request.CellSize * 0.9f);
            }
        }

        private static void DrawGoal(in FlowFieldGizmoRequest request)
        {
            if (!request.HasGoal
                || !request.Grid.TryWorldToLocalClamped(request.GoalWorld, out int x, out int z))
                return;

            int index = FlowFieldGraphTraversal.FindNearestSurfaceAnchor(request.Grid, request.Surface, x, z);
            if (index < 0)
                return;

            Vector3 goal = CellCenter(request, index);
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(goal, request.CellSize * 0.25f);
            if (FlowFieldGridSpace.IsFinite(request.GoalRadius) && request.GoalRadius > 0f)
                Gizmos.DrawWireSphere(goal, request.GoalRadius);
        }

        private static void DrawModifierInfluence(in FlowFieldGizmoRequest request)
        {
            if (request.Workspace.Capacity != request.Grid.CellCount
                || request.Workspace.ModifierInfluence == null)
                return;

            Gizmos.color = new Color(0.1f, 0.9f, 1f, 0.22f);
            Vector3 size = new Vector3(request.CellSize * 0.9f, 0.015f, request.CellSize * 0.9f);
            for (int index = 0; index < request.Grid.CellCount; index++)
            {
                if (request.Workspace.ModifierInfluence[index])
                    Gizmos.DrawCube(CellCenter(request, index), size);
            }
        }

        private static void DrawFlowVectors(in FlowFieldGizmoRequest request)
        {
            if (request.Workspace.Capacity != request.Grid.CellCount)
                return;

            int stride = request.Grid.CellCount > LARGE_GRID_CELL_THRESHOLD ? 2 : 1;
            for (int z = 0; z < request.Grid.Depth; z += stride)
            {
                for (int x = 0; x < request.Grid.Width; x += stride)
                {
                    int index = request.Grid.ToFlatIndex(x, z);
                    bool hasSurface = request.Surface.IsSurfaceValid(index);
                    Vector3 position = hasSurface
                        ? CellCenter(request, index)
                        : request.Grid.LocalToWorldCenter(x, z) + Vector3.up * SURFACE_PADDING;
                    if (!hasSurface)
                    {
                        Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.65f);
                        DrawStop(position, request.CellSize * 0.14f);
                        continue;
                    }

                    Vector3 direction = request.Workspace.FinalDirections[index];
                    float speed = request.Workspace.FinalSpeedMultipliers[index];
                    Gizmos.color = request.Workspace.Blocked[index]
                        ? new Color(1f, 0.35f, 0.35f, 0.95f)
                        : (request.Workspace.GoalFlags[index] & FlowFieldGoalFlags.Directed) != 0
                            ? new Color(0.2f, 1f, 0.4f, 0.95f)
                            : Color.yellow;
                    if (direction.sqrMagnitude <= DIRECTION_EPSILON_SQR || speed <= 0f)
                    {
                        DrawStop(position, request.CellSize * 0.14f);
                        continue;
                    }

                    DrawArrow(position, direction, request.CellSize * 0.4f * Mathf.Clamp(speed, 0f, 2f));
                }
            }
        }

        private static Vector3 CellCenter(in FlowFieldGizmoRequest request, int index)
            => request.Surface.GetCellCenter(request.Grid, index) + Vector3.up * SURFACE_PADDING;

        private static void DrawArrow(Vector3 position, Vector3 direction, float length)
        {
            if (direction.sqrMagnitude <= DIRECTION_EPSILON_SQR)
                return;

            Vector3 body = direction.normalized * length;
            Vector3 side = Vector3.Cross(body.normalized, Vector3.up);
            if (side.sqrMagnitude <= DIRECTION_EPSILON_SQR)
                return;
            side.Normalize();
            Vector3 back = -body.normalized * length * 0.35f;
            Gizmos.DrawRay(position, body);
            Gizmos.DrawRay(position + body, back + side * length * 0.2f);
            Gizmos.DrawRay(position + body, back - side * length * 0.2f);
        }

        private static void DrawStop(Vector3 position, float radius)
        {
            Gizmos.DrawLine(position + new Vector3(-radius, 0f, -radius), position + new Vector3(radius, 0f, radius));
            Gizmos.DrawLine(position + new Vector3(-radius, 0f, radius), position + new Vector3(radius, 0f, -radius));
        }

        private static void DrawBakeDirectionArrow(Vector3 start, float length)
        {
            Vector3 end = start + Vector3.down * length;
            float headLength = Mathf.Clamp(length * 0.08f, 0.08f, 0.5f);
            float headWidth = headLength * 0.5f;
            Gizmos.DrawLine(start, end);
            Gizmos.DrawLine(end, end + Vector3.up * headLength + Vector3.left * headWidth);
            Gizmos.DrawLine(end, end + Vector3.up * headLength + Vector3.right * headWidth);
            Gizmos.DrawLine(end, end + Vector3.up * headLength + Vector3.back * headWidth);
            Gizmos.DrawLine(end, end + Vector3.up * headLength + Vector3.forward * headWidth);
        }
    }

}
#endif
