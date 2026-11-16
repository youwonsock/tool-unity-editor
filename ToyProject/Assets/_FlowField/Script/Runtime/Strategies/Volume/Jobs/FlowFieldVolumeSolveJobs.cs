using System.Threading;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What the jobs of one Volume3D solve share: the pinned arrays, the grid
    /// and the request. Everything between the obstacle queries and the
    /// Modifiers runs as one chain of jobs: topology, Goal resolution, BFS,
    /// directions and escape directions. Stages that write only their own cell
    /// run in parallel chunks. The searches stay sequential, so the result is
    /// identical to a single-threaded solve.
    /// </summary>
    internal unsafe struct FlowFieldVolumeSolveData
    {
        internal const int NeighborCount = FlowFieldNeighborUtility.VolumeCount;
        internal const int MaxCorners = 96;
        // Cells per parallel work item. Every stage also checks for a cancel
        // request this often.
        internal const int ChunkSize = 4096;
        internal const int Unreachable = int.MaxValue;
        internal const int PinnedArrayCount = 10;

        [NativeDisableUnsafePtrRestriction] internal bool* Blocked;
        [NativeDisableUnsafePtrRestriction] internal uint* Topology;
        [NativeDisableUnsafePtrRestriction] internal FlowFieldGoalFlags* GoalFlags;
        [NativeDisableUnsafePtrRestriction] internal int* Next;
        [NativeDisableUnsafePtrRestriction] internal Vector3* Directions;
        [NativeDisableUnsafePtrRestriction] internal float* Speeds;
        [NativeDisableUnsafePtrRestriction] internal Vector3* EscapeDirections;
        [NativeDisableUnsafePtrRestriction] internal int* Costs;
        [NativeDisableUnsafePtrRestriction] internal int* Queue;
        [NativeDisableUnsafePtrRestriction] internal bool* Influence;
        // One-element arrays shared with the owner: its cancel request and the Goal the chain resolved.
        [NativeDisableUnsafePtrRestriction] internal int* Cancel;
        [NativeDisableUnsafePtrRestriction] internal int* ResolvedGoal;

        internal int Width;
        internal int Height;
        internal int Depth;
        internal float CellSize;
        internal bool HasGoal;
        internal int RequestedGoal;
        internal float GoalInfluenceRadius;
        internal Vector3 DefaultDirection;
        internal fixed int DeltaX[NeighborCount];
        internal fixed int DeltaY[NeighborCount];
        internal fixed int DeltaZ[NeighborCount];
        internal fixed int FaceDirections[6];

        internal int Count => Width * Height * Depth;

        internal int ChunkCount => (Count + ChunkSize - 1) / ChunkSize;

        internal bool Cancelled => Volatile.Read(ref *Cancel) != 0;

        internal void ChunkRange(int chunk, out int start, out int end)
        {
            start = chunk * ChunkSize;
            end = System.Math.Min(start + ChunkSize, Count);
        }

        internal int Flat(int x, int y, int z) => x + Width * (z + Depth * y);

        internal void Unflatten(int index, out int x, out int y, out int z)
        {
            y = index / (Width * Depth);
            int layer = index - y * Width * Depth;
            z = layer / Width;
            x = layer - z * Width;
        }

        internal bool InBounds(int x, int y, int z)
            => x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth;

        internal void BuildInteriorOffsets(int* neighbor, int* cornerStart, int* corner)
        {
            int count = 0;
            for (int direction = 0; direction < NeighborCount; direction++)
            {
                int dx = DeltaX[direction];
                int dy = DeltaY[direction];
                int dz = DeltaZ[direction];
                neighbor[direction] = Flat(dx, dy, dz);
                cornerStart[direction] = count;
                int maxX = dx == 0 ? 0 : 1;
                int maxY = dy == 0 ? 0 : 1;
                int maxZ = dz == 0 ? 0 : 1;
                for (int sx = 0; sx <= maxX; sx++)
                for (int sy = 0; sy <= maxY; sy++)
                for (int sz = 0; sz <= maxZ; sz++)
                {
                    if (sx == 0 && sy == 0 && sz == 0 || sx == maxX && sy == maxY && sz == maxZ)
                        continue;
                    corner[count++] = Flat(sx == 1 ? dx : 0, sy == 1 ? dy : 0, sz == 1 ? dz : 0);
                }
            }
            cornerStart[NeighborCount] = count;
        }

        internal bool CanTraverse(int x, int y, int z, int direction)
        {
            int dx = DeltaX[direction];
            int dy = DeltaY[direction];
            int dz = DeltaZ[direction];
            if (!InBounds(x + dx, y + dy, z + dz) || Blocked[Flat(x + dx, y + dy, z + dz)])
                return false;
            int maxX = dx == 0 ? 0 : 1;
            int maxY = dy == 0 ? 0 : 1;
            int maxZ = dz == 0 ? 0 : 1;
            for (int sx = 0; sx <= maxX; sx++)
            for (int sy = 0; sy <= maxY; sy++)
            for (int sz = 0; sz <= maxZ; sz++)
            {
                if (sx == 0 && sy == 0 && sz == 0 || sx == maxX && sy == maxY && sz == maxZ)
                    continue;
                int ix = x + (sx == 1 ? dx : 0);
                int iy = y + (sy == 1 ? dy : 0);
                int iz = z + (sz == 1 ? dz : 0);
                if (!InBounds(ix, iy, iz) || Blocked[Flat(ix, iy, iz)])
                    return false;
            }
            return true;
        }

        internal Vector3 StepDirection(int direction)
            => Normalized(DeltaX[direction], DeltaY[direction], DeltaZ[direction]);

        // Same arithmetic as Vector3.normalized, which keeps published values unchanged.
        internal static Vector3 Normalized(int x, int y, int z)
        {
            float length = (float)System.Math.Sqrt((float)x * x + (float)y * y + (float)z * z);
            return new Vector3(x / length, y / length, z / length);
        }

        /// <summary>
        /// Schedules the whole solve. With reused obstacle queries, Topology and
        /// EscapeDirections are the published arrays, so their stages are left out.
        /// </summary>
        internal JobHandle ScheduleChain(bool buildObstacleResults)
        {
            int chunks = ChunkCount;
            JobHandle handle = new FlowFieldVolumeResetJob { Data = this }.Schedule(chunks, 1);
            if (buildObstacleResults)
                handle = new FlowFieldVolumeTopologyJob { Data = this }.Schedule(chunks, 1, handle);
            handle = new FlowFieldVolumeGoalJob { Data = this }.Schedule(handle);
            handle = new FlowFieldVolumeInfluenceJob { Data = this }.Schedule(chunks, 1, handle);
            handle = new FlowFieldVolumeBfsJob { Data = this }.Schedule(handle);
            handle = new FlowFieldVolumeDirectionsJob { Data = this }.Schedule(chunks, 1, handle);
            if (buildObstacleResults)
                handle = new FlowFieldVolumeEscapeJob { Data = this }.Schedule(handle);
            return handle;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeResetJob : IJobParallelFor
    {
        internal FlowFieldVolumeSolveData Data;

        public void Execute(int chunk)
        {
            Data.ChunkRange(chunk, out int start, out int end);
            for (int index = start; index < end; index++)
            {
                Data.Next[index] = -1;
                Data.Costs[index] = FlowFieldVolumeSolveData.Unreachable;
                Data.Influence[index] = false;
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeTopologyJob : IJobParallelFor
    {
        private const int NeighborCount = FlowFieldVolumeSolveData.NeighborCount;

        internal FlowFieldVolumeSolveData Data;

        public void Execute(int chunk)
        {
            if (Data.Cancelled)
                return;
            int* neighbor = stackalloc int[NeighborCount];
            int* cornerStart = stackalloc int[NeighborCount + 1];
            int* corner = stackalloc int[FlowFieldVolumeSolveData.MaxCorners];
            Data.BuildInteriorOffsets(neighbor, cornerStart, corner);

            int width = Data.Width;
            int height = Data.Height;
            int depth = Data.Depth;
            Data.ChunkRange(chunk, out int start, out int end);
            for (int index = start; index < end; index++)
            {
                if (Data.Blocked[index])
                {
                    // A blocked cell keeps its previous Topology value, as before.
                    Data.Next[index] = -2;
                    Data.GoalFlags[index] = FlowFieldGoalFlags.None;
                    Data.Directions[index] = Vector3.zero;
                    Data.Speeds[index] = 0f;
                    continue;
                }

                Data.Unflatten(index, out int x, out int y, out int z);
                uint mask = 0u;
                if (x > 0 && x < width - 1 && y > 0 && y < height - 1 && z > 0 && z < depth - 1)
                {
                    for (int direction = 0; direction < NeighborCount; direction++)
                    {
                        if (Data.Blocked[index + neighbor[direction]])
                            continue;
                        bool open = true;
                        for (int i = cornerStart[direction]; i < cornerStart[direction + 1]; i++)
                        {
                            if (Data.Blocked[index + corner[i]])
                            {
                                open = false;
                                break;
                            }
                        }
                        if (open)
                            mask |= 1u << direction;
                    }
                }
                else
                {
                    for (int direction = 0; direction < NeighborCount; direction++)
                    {
                        if (Data.CanTraverse(x, y, z, direction))
                            mask |= 1u << direction;
                    }
                }
                Data.Topology[index] = mask;
            }
        }
    }

    /// <summary>Resolves the Goal cell. A blocked Goal moves to the nearest open cell, lowest index on a tie.</summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeGoalJob : IJob
    {
        internal FlowFieldVolumeSolveData Data;

        public void Execute()
        {
            if (Data.Cancelled)
                return;
            *Data.ResolvedGoal = Resolve();
        }

        private int Resolve()
        {
            if (!Data.HasGoal)
                return -1;
            int goal = Data.RequestedGoal;
            if (!Data.Blocked[goal])
                return goal;

            goal = -1;
            long nearest = long.MaxValue;
            Data.Unflatten(Data.RequestedGoal, out int rx, out int ry, out int rz);
            int count = Data.Count;
            for (int index = 0; index < count; index++)
            {
                if (Data.Blocked[index])
                    continue;
                Data.Unflatten(index, out int x, out int y, out int z);
                long dx = x - rx;
                long dy = y - ry;
                long dz = z - rz;
                long distance = dx * dx + dy * dy + dz * dz;
                // Indices rise, so the lowest index already wins a tie.
                if (distance < nearest)
                {
                    nearest = distance;
                    goal = index;
                }
            }
            return goal;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeInfluenceJob : IJobParallelFor
    {
        internal FlowFieldVolumeSolveData Data;

        public void Execute(int chunk)
        {
            if (Data.Cancelled)
                return;
            int goal = *Data.ResolvedGoal;
            if (goal < 0)
                return;

            Data.Unflatten(goal, out int gx, out int gy, out int gz);
            float radius = Data.GoalInfluenceRadius;
            float cellSize = Data.CellSize;
            double radiusSqr = (double)radius * radius;
            Data.ChunkRange(chunk, out int start, out int end);
            for (int index = start; index < end; index++)
            {
                if (Data.Blocked[index])
                    continue;
                Data.Unflatten(index, out int x, out int y, out int z);
                long dx = x - gx;
                long dy = y - gy;
                long dz = z - gz;
                double distanceSqr = (double)(dx * dx + dy * dy + dz * dz) * cellSize * cellSize;
                Data.Influence[index] = radius <= 0f || distanceSqr <= radiusSqr;
            }
            if (goal >= start && goal < end)
                Data.Influence[goal] = true;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeBfsJob : IJob
    {
        private const int NeighborCount = FlowFieldVolumeSolveData.NeighborCount;

        internal FlowFieldVolumeSolveData Data;

        public void Execute()
        {
            if (Data.Cancelled)
                return;
            int goal = *Data.ResolvedGoal;
            if (goal < 0)
                return;

            int head = 0;
            int tail = 0;
            Data.Costs[goal] = 0;
            Data.Queue[tail++] = goal;
            while (head < tail)
            {
                if ((head & (FlowFieldVolumeSolveData.ChunkSize - 1)) == 0 && Data.Cancelled)
                    return;
                int current = Data.Queue[head++];
                Data.Unflatten(current, out int x, out int y, out int z);
                int candidate = Data.Costs[current] + 1;
                uint mask = Data.Topology[current];
                for (int direction = 0; direction < NeighborCount; direction++)
                {
                    if ((mask & (1u << direction)) == 0u)
                        continue;
                    int nx = x + Data.DeltaX[direction];
                    int ny = y + Data.DeltaY[direction];
                    int nz = z + Data.DeltaZ[direction];
                    if (!Data.InBounds(nx, ny, nz))
                        continue;
                    int neighbor = Data.Flat(nx, ny, nz);
                    if (!Data.Influence[neighbor] || candidate >= Data.Costs[neighbor])
                        continue;
                    Data.Costs[neighbor] = candidate;
                    Data.Queue[tail++] = neighbor;
                }
            }
        }
    }

    /// <summary>Picks each cell's next step from the finished BFS. A cell writes only itself.</summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeDirectionsJob : IJobParallelFor
    {
        private const int NeighborCount = FlowFieldVolumeSolveData.NeighborCount;

        internal FlowFieldVolumeSolveData Data;

        public void Execute(int chunk)
        {
            if (Data.Cancelled)
                return;
            int goal = *Data.ResolvedGoal;
            int gx = 0, gy = 0, gz = 0;
            if (goal >= 0)
                Data.Unflatten(goal, out gx, out gy, out gz);

            Data.ChunkRange(chunk, out int start, out int end);
            for (int index = start; index < end; index++)
            {
                if (Data.Blocked[index])
                {
                    Data.Next[index] = -2;
                    Data.GoalFlags[index] = FlowFieldGoalFlags.None;
                    Data.Directions[index] = Vector3.zero;
                    Data.Speeds[index] = 0f;
                    continue;
                }
                if (goal == index)
                {
                    Data.Next[index] = index;
                    Data.GoalFlags[index] = FlowFieldGoalFlags.Directed | FlowFieldGoalFlags.Anchor;
                    Data.Directions[index] = Vector3.zero;
                    Data.Speeds[index] = 0f;
                    continue;
                }

                Vector3 direction = Data.DefaultDirection;
                int next = -1;
                bool influenced = goal >= 0 && Data.Influence[index];
                if (influenced)
                {
                    if (Data.Costs[index] == FlowFieldVolumeSolveData.Unreachable)
                    {
                        next = -3;
                    }
                    else
                    {
                        Data.Unflatten(index, out int x, out int y, out int z);
                        int bestScore = int.MinValue;
                        int bestDirection = -1;
                        int previousWave = Data.Costs[index] - 1;
                        uint mask = Data.Topology[index];
                        for (int candidate = 0; candidate < NeighborCount; candidate++)
                        {
                            if ((mask & (1u << candidate)) == 0u)
                                continue;
                            int sx = Data.DeltaX[candidate];
                            int sy = Data.DeltaY[candidate];
                            int sz = Data.DeltaZ[candidate];
                            if (!Data.InBounds(x + sx, y + sy, z + sz))
                                continue;
                            int neighbor = Data.Flat(x + sx, y + sy, z + sz);
                            if (Data.Costs[neighbor] != previousWave)
                                continue;
                            int score = 2 * (sx * (gx - x) + sy * (gy - y) + sz * (gz - z))
                                - (sx * sx + sy * sy + sz * sz);
                            if (next < 0 || score > bestScore || score == bestScore && neighbor < next)
                            {
                                next = neighbor;
                                bestScore = score;
                                bestDirection = candidate;
                            }
                        }
                        if (next >= 0)
                            direction = Data.StepDirection(bestDirection);
                        else
                            next = -3;
                    }
                    if (next == -3)
                        direction = Vector3.zero;
                }

                Data.Next[index] = next;
                Data.GoalFlags[index] = next >= 0
                    ? FlowFieldGoalFlags.Directed
                    : influenced
                        ? FlowFieldGoalFlags.Unreachable
                        : FlowFieldGoalFlags.None;
                Data.Directions[index] = direction;
                Data.Speeds[index] = direction.x * direction.x + direction.y * direction.y + direction.z * direction.z
                    > 0.00000001f
                        ? 1f
                        : 0f;
            }
        }
    }

    /// <summary>
    /// Points each blocked cell back toward open space, a multi-source BFS over
    /// the six faces. It reuses the Goal BFS arrays, so it runs last.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeEscapeJob : IJob
    {
        internal FlowFieldVolumeSolveData Data;

        public void Execute()
        {
            if (Data.Cancelled)
                return;
            int count = Data.Count;
            int head = 0;
            int tail = 0;
            for (int index = 0; index < count; index++)
            {
                bool blocked = Data.Blocked[index];
                Data.Costs[index] = blocked ? FlowFieldVolumeSolveData.Unreachable : 0;
                Data.EscapeDirections[index] = Vector3.zero;
                if (!blocked)
                    Data.Queue[tail++] = index;
            }

            while (head < tail)
            {
                if ((head & (FlowFieldVolumeSolveData.ChunkSize - 1)) == 0 && Data.Cancelled)
                    return;
                int current = Data.Queue[head++];
                Data.Unflatten(current, out int x, out int y, out int z);
                int nextCost = Data.Costs[current] + 1;
                for (int face = 0; face < 6; face++)
                {
                    int direction = Data.FaceDirections[face];
                    int nx = x + Data.DeltaX[direction];
                    int ny = y + Data.DeltaY[direction];
                    int nz = z + Data.DeltaZ[direction];
                    if (!Data.InBounds(nx, ny, nz))
                        continue;
                    int neighbor = Data.Flat(nx, ny, nz);
                    if (!Data.Blocked[neighbor] || Data.Costs[neighbor] != FlowFieldVolumeSolveData.Unreachable)
                        continue;
                    Data.Costs[neighbor] = nextCost;
                    // From the blocked neighbor back to the walkable cell.
                    Data.EscapeDirections[neighbor] = FlowFieldVolumeSolveData.Normalized(
                        -Data.DeltaX[direction],
                        -Data.DeltaY[direction],
                        -Data.DeltaZ[direction]);
                    Data.Queue[tail++] = neighbor;
                }
            }
        }
    }

}
