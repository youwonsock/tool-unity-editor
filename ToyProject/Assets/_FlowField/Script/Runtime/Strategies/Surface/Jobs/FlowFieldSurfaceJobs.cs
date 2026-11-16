using System.Threading;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
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
    internal unsafe struct FlowFieldSurfaceFieldCommitJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] internal bool* Blocked;
        [NativeDisableUnsafePtrRestriction] internal bool* BlockedSource;
        // The published field keeps topology as uint, the workspace as byte.
        [NativeDisableUnsafePtrRestriction] internal uint* Topology;
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
