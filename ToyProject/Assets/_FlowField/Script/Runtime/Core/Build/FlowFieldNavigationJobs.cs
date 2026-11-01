using System.Threading;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Pins managed arrays so a Burst job can read and write them. The published
    /// field keeps its managed arrays, so sampling and baking never see a
    /// native type. A job must have finished before <see cref="Release"/>.
    /// </summary>
    internal sealed class FlowFieldPinnedArrays
    {
        private readonly ulong[] _handles;
        private int _count;

        internal FlowFieldPinnedArrays(int capacity)
        {
            _handles = new ulong[capacity];
        }

        internal unsafe T* Pin<T>(T[] array) where T : unmanaged
        {
            void* pointer = UnsafeUtility.PinGCArrayAndGetDataAddress(array, out _handles[_count]);
            _count++;
            return (T*)pointer;
        }

        internal void Release()
        {
            for (int i = 0; i < _count; i++)
                UnsafeUtility.ReleaseGCObject(_handles[i]);
            _count = 0;
        }
    }

    /// <summary>
    /// Everything between the obstacle queries and the Modifiers of one Volume3D
    /// build: topology, Goal resolution, BFS, directions and escape directions.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldVolumeSolveJob : IJob
    {
        private const int NeighborCount = FlowFieldNeighborUtility.VolumeCount;
        private const int MaxCorners = 96;
        private const int CancelCheckInterval = 4096;

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
        // One-element arrays shared with the owner: its cancel request and the Goal this job resolved.
        [NativeDisableUnsafePtrRestriction] internal int* Cancel;
        [NativeDisableUnsafePtrRestriction] internal int* ResolvedGoal;

        internal int Width;
        internal int Height;
        internal int Depth;
        internal float CellSize;
        internal bool BuildTopology;
        internal bool BuildEscape;
        internal bool HasGoal;
        internal int RequestedGoal;
        internal float GoalInfluenceRadius;
        internal Vector3 DefaultDirection;
        internal fixed int DeltaX[NeighborCount];
        internal fixed int DeltaY[NeighborCount];
        internal fixed int DeltaZ[NeighborCount];
        internal fixed int FaceDirections[6];

        private int Count => Width * Height * Depth;

        public void Execute()
        {
            for (int index = 0; index < Count; index++)
            {
                Next[index] = -1;
                Costs[index] = Unreachable;
                Influence[index] = false;
            }

            if (BuildTopology)
                RunTopology();
            if (Cancelled)
                return;
            int goal = ResolveGoal();
            *ResolvedGoal = goal;
            if (goal >= 0)
                RunBfs(goal);
            if (Cancelled)
                return;
            RunDirections(goal);
            if (!BuildEscape || Cancelled)
                return;
            RunEscape();
        }

        private bool Cancelled => Volatile.Read(ref *Cancel) != 0;

        private int Flat(int x, int y, int z) => x + Width * (z + Depth * y);

        private void Unflatten(int index, out int x, out int y, out int z)
        {
            y = index / (Width * Depth);
            int layer = index - y * Width * Depth;
            z = layer / Width;
            x = layer - z * Width;
        }

        private bool InBounds(int x, int y, int z)
            => x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth;

        private void RunTopology()
        {
            int* neighbor = stackalloc int[NeighborCount];
            int* cornerStart = stackalloc int[NeighborCount + 1];
            int* corner = stackalloc int[MaxCorners];
            BuildInteriorOffsets(neighbor, cornerStart, corner);

            for (int index = 0; index < Count; index++)
            {
                if ((index & (CancelCheckInterval - 1)) == 0 && Cancelled)
                    return;
                if (Blocked[index])
                {
                    Next[index] = -2;
                    GoalFlags[index] = FlowFieldGoalFlags.None;
                    Directions[index] = Vector3.zero;
                    Speeds[index] = 0f;
                    continue;
                }

                Unflatten(index, out int x, out int y, out int z);
                uint mask = 0u;
                if (x > 0 && x < Width - 1 && y > 0 && y < Height - 1 && z > 0 && z < Depth - 1)
                {
                    for (int direction = 0; direction < NeighborCount; direction++)
                    {
                        if (Blocked[index + neighbor[direction]])
                            continue;
                        bool open = true;
                        for (int i = cornerStart[direction]; i < cornerStart[direction + 1]; i++)
                        {
                            if (Blocked[index + corner[i]])
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
                        if (CanTraverse(x, y, z, direction))
                            mask |= 1u << direction;
                    }
                }
                Topology[index] = mask;
            }
        }

        private void BuildInteriorOffsets(int* neighbor, int* cornerStart, int* corner)
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

        private bool CanTraverse(int x, int y, int z, int direction)
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

        private int ResolveGoal()
        {
            if (!HasGoal)
                return -1;
            int goal = RequestedGoal;
            if (Blocked[goal])
            {
                goal = -1;
                long nearest = long.MaxValue;
                Unflatten(RequestedGoal, out int rx, out int ry, out int rz);
                for (int index = 0; index < Count; index++)
                {
                    if (Blocked[index])
                        continue;
                    Unflatten(index, out int x, out int y, out int z);
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
                if (goal < 0)
                    return -1;
            }

            Unflatten(goal, out int gx, out int gy, out int gz);
            double radiusSqr = (double)GoalInfluenceRadius * GoalInfluenceRadius;
            for (int index = 0; index < Count; index++)
            {
                if (Blocked[index])
                    continue;
                Unflatten(index, out int x, out int y, out int z);
                long dx = x - gx;
                long dy = y - gy;
                long dz = z - gz;
                double distanceSqr = (double)(dx * dx + dy * dy + dz * dz) * CellSize * CellSize;
                Influence[index] = GoalInfluenceRadius <= 0f || distanceSqr <= radiusSqr;
            }
            Influence[goal] = true;
            return goal;
        }

        private void RunBfs(int goal)
        {
            int head = 0;
            int tail = 0;
            Costs[goal] = 0;
            Queue[tail++] = goal;
            while (head < tail)
            {
                if ((head & (CancelCheckInterval - 1)) == 0 && Cancelled)
                    return;
                int current = Queue[head++];
                Unflatten(current, out int x, out int y, out int z);
                int candidate = Costs[current] + 1;
                uint mask = Topology[current];
                for (int direction = 0; direction < NeighborCount; direction++)
                {
                    if ((mask & (1u << direction)) == 0u)
                        continue;
                    int nx = x + DeltaX[direction];
                    int ny = y + DeltaY[direction];
                    int nz = z + DeltaZ[direction];
                    if (!InBounds(nx, ny, nz))
                        continue;
                    int neighbor = Flat(nx, ny, nz);
                    if (!Influence[neighbor] || candidate >= Costs[neighbor])
                        continue;
                    Costs[neighbor] = candidate;
                    Queue[tail++] = neighbor;
                }
            }
        }

        private void RunDirections(int goal)
        {
            int gx = 0, gy = 0, gz = 0;
            if (goal >= 0)
                Unflatten(goal, out gx, out gy, out gz);
            for (int index = 0; index < Count; index++)
            {
                if ((index & (CancelCheckInterval - 1)) == 0 && Cancelled)
                    return;
                if (Blocked[index])
                {
                    Next[index] = -2;
                    GoalFlags[index] = FlowFieldGoalFlags.None;
                    Directions[index] = Vector3.zero;
                    Speeds[index] = 0f;
                    continue;
                }
                if (goal == index)
                {
                    Next[index] = index;
                    GoalFlags[index] = FlowFieldGoalFlags.Directed | FlowFieldGoalFlags.Anchor;
                    Directions[index] = Vector3.zero;
                    Speeds[index] = 0f;
                    continue;
                }

                Vector3 direction = DefaultDirection;
                int next = -1;
                bool influenced = goal >= 0 && Influence[index];
                if (influenced)
                {
                    if (Costs[index] == Unreachable)
                    {
                        next = -3;
                    }
                    else
                    {
                        Unflatten(index, out int x, out int y, out int z);
                        int bestScore = int.MinValue;
                        int bestDirection = -1;
                        int previousWave = Costs[index] - 1;
                        uint mask = Topology[index];
                        for (int candidate = 0; candidate < NeighborCount; candidate++)
                        {
                            if ((mask & (1u << candidate)) == 0u)
                                continue;
                            int sx = DeltaX[candidate];
                            int sy = DeltaY[candidate];
                            int sz = DeltaZ[candidate];
                            if (!InBounds(x + sx, y + sy, z + sz))
                                continue;
                            int neighbor = Flat(x + sx, y + sy, z + sz);
                            if (Costs[neighbor] != previousWave)
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
                            direction = StepDirection(bestDirection);
                        else
                            next = -3;
                    }
                    if (next == -3)
                        direction = Vector3.zero;
                }

                Next[index] = next;
                GoalFlags[index] = next >= 0
                    ? FlowFieldGoalFlags.Directed
                    : influenced
                        ? FlowFieldGoalFlags.Unreachable
                        : FlowFieldGoalFlags.None;
                Directions[index] = direction;
                Speeds[index] = direction.x * direction.x + direction.y * direction.y + direction.z * direction.z
                    > 0.00000001f
                        ? 1f
                        : 0f;
            }
        }

        private void RunEscape()
        {
            int head = 0;
            int tail = 0;
            for (int index = 0; index < Count; index++)
            {
                bool blocked = Blocked[index];
                Costs[index] = blocked ? Unreachable : 0;
                EscapeDirections[index] = Vector3.zero;
                if (!blocked)
                    Queue[tail++] = index;
            }

            while (head < tail)
            {
                if ((head & (CancelCheckInterval - 1)) == 0 && Cancelled)
                    return;
                int current = Queue[head++];
                Unflatten(current, out int x, out int y, out int z);
                int nextCost = Costs[current] + 1;
                for (int face = 0; face < 6; face++)
                {
                    int direction = FaceDirections[face];
                    int nx = x + DeltaX[direction];
                    int ny = y + DeltaY[direction];
                    int nz = z + DeltaZ[direction];
                    if (!InBounds(nx, ny, nz))
                        continue;
                    int neighbor = Flat(nx, ny, nz);
                    if (!Blocked[neighbor] || Costs[neighbor] != Unreachable)
                        continue;
                    Costs[neighbor] = nextCost;
                    // From the blocked neighbor back to the walkable cell.
                    EscapeDirections[neighbor] = Normalized(-DeltaX[direction], -DeltaY[direction], -DeltaZ[direction]);
                    Queue[tail++] = neighbor;
                }
            }
        }

        private Vector3 StepDirection(int direction)
            => Normalized(DeltaX[direction], DeltaY[direction], DeltaZ[direction]);

        // Same arithmetic as Vector3.normalized, which keeps published values unchanged.
        private static Vector3 Normalized(int x, int y, int z)
        {
            float length = (float)System.Math.Sqrt((float)x * x + (float)y * y + (float)z * z);
            return new Vector3(x / length, y / length, z / length);
        }
    }

    /// <summary>
    /// BFS and Goal directions of one Surface2D build, from the influence and
    /// topology that the Goal stage prepared.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldSurfaceSolveJob : IJob
    {
        private const int NeighborCount = FlowFieldNeighborUtility.SurfaceCount;
        private const int CancelCheckInterval = 4096;
        private const int Unreachable = int.MaxValue;
        private const byte ValidSurface = 1;
        // Vector3.kEpsilon, the threshold of Vector3.normalized.
        private const float NormalizeEpsilon = 0.00001f;

        internal const int PinnedArrayCount = 12;

        [NativeDisableUnsafePtrRestriction] internal int* Costs;
        [NativeDisableUnsafePtrRestriction] internal int* Queue;
        [NativeDisableUnsafePtrRestriction] internal byte* Topology;
        [NativeDisableUnsafePtrRestriction] internal int* NextCells;
        [NativeDisableUnsafePtrRestriction] internal FlowFieldGoalFlags* GoalFlags;
        [NativeDisableUnsafePtrRestriction] internal Vector3* GoalDirections;
        [NativeDisableUnsafePtrRestriction] internal bool* Blocked;
        [NativeDisableUnsafePtrRestriction] internal bool* Influence;
        [NativeDisableUnsafePtrRestriction] internal byte* SurfaceFlags;
        [NativeDisableUnsafePtrRestriction] internal float* Heights;
        [NativeDisableUnsafePtrRestriction] internal Vector3* Normals;
        [NativeDisableUnsafePtrRestriction] internal int* Cancel;

        internal int Width;
        internal int Depth;
        internal float CellSize;
        internal Vector3 Origin;
        internal int Goal;
        internal float DirectionEpsilonSqr;
        internal float ProjectionEpsilon;
        internal fixed int DeltaX[NeighborCount];
        internal fixed int DeltaZ[NeighborCount];

        public void Execute()
        {
            int* offsets = stackalloc int[NeighborCount];
            for (int direction = 0; direction < NeighborCount; direction++)
                offsets[direction] = DeltaX[direction] + Width * DeltaZ[direction];
            RunBfs(offsets);
            if (!Cancelled)
                RunDirections(offsets);
        }

        private bool Cancelled => Volatile.Read(ref *Cancel) != 0;

        private int Count => Width * Depth;

        private bool Traversable(int index)
            => (SurfaceFlags[index] & ValidSurface) != 0 && Influence[index] && !Blocked[index];

        private void RunBfs(int* offsets)
        {
            for (int index = 0; index < Count; index++)
                Costs[index] = Unreachable;

            int head = 0;
            int tail = 0;
            Costs[Goal] = 0;
            Queue[tail++] = Goal;
            while (head < tail)
            {
                if ((head & (CancelCheckInterval - 1)) == 0 && Cancelled)
                    return;
                int current = Queue[head++];
                int candidate = Costs[current] + 1;
                int mask = Topology[current];
                for (int direction = 0; direction < NeighborCount; direction++)
                {
                    // A topology bit proves the move stays in bounds and traversable.
                    if ((mask & (1 << direction)) == 0)
                        continue;
                    int neighbor = current + offsets[direction];
                    if (candidate >= Costs[neighbor])
                        continue;
                    Costs[neighbor] = candidate;
                    Queue[tail++] = neighbor;
                }
            }
        }

        private void RunDirections(int* offsets)
        {
            int goalZ = Goal / Width;
            int goalX = Goal - goalZ * Width;
            for (int index = 0; index < Count; index++)
            {
                if ((index & (CancelCheckInterval - 1)) == 0 && Cancelled)
                    return;
                bool valid = (SurfaceFlags[index] & ValidSurface) != 0;
                NextCells[index] = !valid || Blocked[index]
                    ? -2
                    : !Influence[index]
                        ? -1
                        : -3;
                bool traversable = Traversable(index);
                if (!traversable || Costs[index] == Unreachable)
                {
                    if (traversable)
                    {
                        GoalFlags[index] = FlowFieldGoalFlags.Unreachable;
                        NextCells[index] = -3;
                    }
                    continue;
                }

                GoalFlags[index] = FlowFieldGoalFlags.Directed;
                if (index == Goal)
                {
                    GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
                    GoalDirections[index] = Vector3.zero;
                    NextCells[index] = index;
                    continue;
                }

                int z = index / Width;
                int x = index - z * Width;
                int best = -1;
                int bestScore = 0;
                int previousWave = Costs[index] - 1;
                int mask = Topology[index];
                for (int direction = 0; direction < NeighborCount; direction++)
                {
                    if ((mask & (1 << direction)) == 0)
                        continue;
                    // Every edge costs one, so the previous wave rules out
                    // zero-cost cycles. The flat index is the final tie-breaker.
                    int neighbor = index + offsets[direction];
                    if (Costs[neighbor] != previousWave)
                        continue;
                    int sx = DeltaX[direction];
                    int sz = DeltaZ[direction];
                    int score = 2 * (sx * (goalX - x) + sz * (goalZ - z)) - (sx * sx + sz * sz);
                    if (best < 0 || score > bestScore || score == bestScore && neighbor < best)
                    {
                        bestScore = score;
                        best = neighbor;
                    }
                }

                if (best < 0)
                {
                    GoalFlags[index] = FlowFieldGoalFlags.Unreachable;
                    NextCells[index] = -3;
                    continue;
                }

                GoalDirections[index] = DirectionTo(index, x, z, best);
                NextCells[index] = best;
            }
        }

        private Vector3 DirectionTo(int index, int x, int z, int next)
        {
            int nextZ = next / Width;
            int nextX = next - nextZ * Width;
            Vector3 current = CellCenter(x, z, Heights[index]);
            Vector3 target = CellCenter(nextX, nextZ, Heights[next]);
            Vector3 step = new Vector3(target.x - current.x, target.y - current.y, target.z - current.z);

            Vector3 normal = Normalize(Normals[index]);
            float normalSqr = Dot(normal, normal);
            if (normalSqr >= ProjectionEpsilon)
            {
                float dot = Dot(step, normal);
                step = new Vector3(
                    step.x - normal.x * dot / normalSqr,
                    step.y - normal.y * dot / normalSqr,
                    step.z - normal.z * dot / normalSqr);
            }
            return Dot(step, step) > DirectionEpsilonSqr ? Normalize(step) : Vector3.zero;
        }

        private Vector3 CellCenter(int x, int z, float height)
            => new Vector3(
                Origin.x + (x * CellSize + CellSize * 0.5f),
                height,
                Origin.z + (z * CellSize + CellSize * 0.5f));

        private static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        private static Vector3 Normalize(Vector3 value)
        {
            float length = (float)System.Math.Sqrt(Dot(value, value));
            return length > NormalizeEpsilon
                ? new Vector3(value.x / length, value.y / length, value.z / length)
                : Vector3.zero;
        }
    }

    /// <summary>The topology pass of the Surface2D Goal stage.</summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldSurfaceTopologyJob : IJob
    {
        private const int NeighborCount = FlowFieldNeighborUtility.SurfaceCount;
        private const byte ValidSurface = 1;

        [NativeDisableUnsafePtrRestriction] internal byte* Topology;
        [NativeDisableUnsafePtrRestriction] internal bool* Blocked;
        [NativeDisableUnsafePtrRestriction] internal bool* Influence;
        [NativeDisableUnsafePtrRestriction] internal byte* SurfaceFlags;
        [NativeDisableUnsafePtrRestriction] internal byte* NeighborMasks;

        internal int Width;
        internal int Depth;
        internal fixed int DeltaX[NeighborCount];
        internal fixed int DeltaZ[NeighborCount];

        public void Execute()
        {
            int count = Width * Depth;
            for (int index = 0; index < count; index++)
            {
                Topology[index] = 0;
                if (!Traversable(index))
                    continue;
                int z = index / Width;
                int x = index - z * Width;
                int connections = NeighborMasks[index];
                int mask = 0;
                for (int direction = 0; direction < NeighborCount; direction++)
                {
                    if ((connections & (1 << direction)) != 0 && CanTraverse(x, z, direction, connections))
                        mask |= 1 << direction;
                }
                Topology[index] = (byte)mask;
            }
        }

        private bool Traversable(int index)
            => (SurfaceFlags[index] & ValidSurface) != 0 && Influence[index] && !Blocked[index];

        private bool CanTraverse(int x, int z, int direction, int connections)
        {
            int dx = DeltaX[direction];
            int dz = DeltaZ[direction];
            int nx = x + dx;
            int nz = z + dz;
            if (nx < 0 || nx >= Width || nz < 0 || nz >= Depth || !Traversable(nx + Width * nz))
                return false;
            if (dx == 0 || dz == 0)
                return true;
            int firstDirection = dx > 0 ? 0 : 1;
            int secondDirection = dz > 0 ? 2 : 3;
            return (connections & (1 << firstDirection)) != 0
                && (connections & (1 << secondDirection)) != 0
                && Traversable(x + dx + Width * z)
                && Traversable(x + Width * (z + dz));
        }
    }

    /// <summary>The whole-grid influence mask of the Surface2D Goal stage.</summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldSurfaceInfluenceJob : IJob
    {
        private const byte ValidSurface = 1;

        [NativeDisableUnsafePtrRestriction] internal bool* Influence;
        [NativeDisableUnsafePtrRestriction] internal byte* SurfaceFlags;
        [NativeDisableUnsafePtrRestriction] internal float* Heights;

        internal int Width;
        internal int Depth;
        internal float CellSize;
        internal Vector3 Origin;
        internal Vector3 Anchor;
        internal float Radius;

        public void Execute()
        {
            float radiusSqr = Radius * Radius;
            for (int z = 0; z < Depth; z++)
            for (int x = 0; x < Width; x++)
            {
                int index = x + z * Width;
                bool inside = (SurfaceFlags[index] & ValidSurface) != 0;
                if (inside && Radius > 0f)
                {
                    float dx = Origin.x + (x * CellSize + CellSize * 0.5f) - Anchor.x;
                    float dy = Heights[index] - Anchor.y;
                    float dz = Origin.z + (z * CellSize + CellSize * 0.5f) - Anchor.z;
                    inside = dx * dx + dy * dy + dz * dz <= radiusSqr;
                }
                Influence[index] = inside;
            }
        }
    }

    /// <summary>
    /// Copies a composed region into the published field and reports whether
    /// anything changed. The comparisons match Vector3 != and Mathf.Approximately.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    internal unsafe struct FlowFieldFieldCommitJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] internal bool* Blocked;
        [NativeDisableUnsafePtrRestriction] internal bool* BlockedSource;
        [NativeDisableUnsafePtrRestriction] internal byte* Topology;
        [NativeDisableUnsafePtrRestriction] internal byte* TopologySource;
        [NativeDisableUnsafePtrRestriction] internal FlowFieldGoalFlags* GoalFlags;
        [NativeDisableUnsafePtrRestriction] internal FlowFieldGoalFlags* GoalFlagsSource;
        [NativeDisableUnsafePtrRestriction] internal int* Next;
        [NativeDisableUnsafePtrRestriction] internal int* NextSource;
        [NativeDisableUnsafePtrRestriction] internal Vector3* BaseDirections;
        [NativeDisableUnsafePtrRestriction] internal Vector3* BaseDirectionsSource;
        [NativeDisableUnsafePtrRestriction] internal float* BaseSpeeds;
        [NativeDisableUnsafePtrRestriction] internal float* BaseSpeedsSource;
        [NativeDisableUnsafePtrRestriction] internal Vector3* FinalDirections;
        [NativeDisableUnsafePtrRestriction] internal Vector3* FinalDirectionsSource;
        [NativeDisableUnsafePtrRestriction] internal float* FinalSpeeds;
        [NativeDisableUnsafePtrRestriction] internal float* FinalSpeedsSource;
        [NativeDisableUnsafePtrRestriction] internal bool* Influence;
        [NativeDisableUnsafePtrRestriction] internal bool* InfluenceSource;
        [NativeDisableUnsafePtrRestriction] internal bool* Changed;

        internal const int PinnedArrayCount = 19;

        internal int Width;
        internal int MinX;
        internal int MaxX;
        internal int MinZ;
        internal int MaxZ;
        internal bool IncludeBase;
        internal float Epsilon;

        public void Execute()
        {
            bool any = false;
            for (int z = MinZ; z <= MaxZ; z++)
            for (int x = MinX; x <= MaxX; x++)
            {
                int i = x + z * Width;
                if (IncludeBase)
                {
                    any |= Blocked[i] != BlockedSource[i]
                        || Topology[i] != TopologySource[i]
                        || GoalFlags[i] != GoalFlagsSource[i]
                        || Next[i] != NextSource[i]
                        || Differs(BaseDirections[i], BaseDirectionsSource[i])
                        || !Approximately(BaseSpeeds[i], BaseSpeedsSource[i]);
                    Blocked[i] = BlockedSource[i];
                    Topology[i] = TopologySource[i];
                    GoalFlags[i] = GoalFlagsSource[i];
                    Next[i] = NextSource[i];
                    BaseDirections[i] = BaseDirectionsSource[i];
                    BaseSpeeds[i] = BaseSpeedsSource[i];
                }
                any |= Differs(FinalDirections[i], FinalDirectionsSource[i])
                    || !Approximately(FinalSpeeds[i], FinalSpeedsSource[i])
                    || Influence[i] != InfluenceSource[i];
                FinalDirections[i] = FinalDirectionsSource[i];
                FinalSpeeds[i] = FinalSpeedsSource[i];
                Influence[i] = InfluenceSource[i];
            }
            *Changed = any;
        }

        private static bool Differs(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float y = a.y - b.y;
            float z = a.z - b.z;
            return !(x * x + y * y + z * z < 9.99999944E-11f);
        }

        private bool Approximately(float a, float b)
        {
            float largest = System.Math.Max(System.Math.Abs(a), System.Math.Abs(b));
            return System.Math.Abs(b - a) < System.Math.Max(0.000001f * largest, Epsilon * 8f);
        }
    }
}
