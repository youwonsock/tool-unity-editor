using System;
using Unity.Collections;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Managed and Native backing for FlowField rebuild.
    /// The owning runtime context explicitly calls Init after the grid is configured;
    /// Native arrays are released with the workspace and are never allocated by a job.
    /// </summary>
    internal sealed class FlowFieldWorkspace
    {
        public bool[] Blocked { get; private set; }
        public bool[] ObstacleScratch { get; private set; }
        public bool[] StaticBlocked { get; private set; }
        public bool[] DynamicBlocked { get; private set; }
        public Vector3[] EscapeDirections { get; private set; }
        public Vector3[] GoalDirections { get; private set; }
        public FlowFieldGoalFlags[] GoalFlags { get; private set; }
        public Vector3[] FinalDirections { get; private set; }
        public float[] FinalSpeedMultipliers { get; private set; }
        public bool[] ModifierInfluence { get; private set; }
        public ushort[] CellGeneration { get; private set; }

        internal bool[] InfluenceMask { get; private set; }
        internal int[] Costs { get; private set; }
        internal int[] Queue { get; private set; }
        internal int[] HeapCells { get; private set; }
        internal int[] HeapPositions { get; private set; }
        internal int HeapCount;

        public NativeArray<int> NativeCosts;
        public NativeArray<byte> NativeBlocked;
        public NativeArray<byte> NativeInfluence;
        public NativeArray<byte> NativeNeighborMasks;
        public NativeArray<byte> NativeSurfaceValid;
        public NativeArray<float> NativeCentersX;
        public NativeArray<float> NativeCentersZ;
        public NativeArray<Vector3> NativeSurfaceNormals;
        public NativeArray<int> NativeHeapCells;
        public NativeArray<int> NativeHeapPositions;
        public NativeArray<Vector3> NativeFinalDirections;
        public NativeArray<float> NativeFinalSpeeds;
        public NativeArray<Vector3> NativeEscape;
        public NativeArray<Vector3> NativeGoalDirections;
        public NativeArray<byte> NativeGoalFlags;

        private bool _nativeAllocated;

        public int Capacity => Blocked == null ? 0 : Blocked.Length;
        public bool HasNative => _nativeAllocated;
        public bool HasBlockedCells { get; private set; }

        public bool Resize(int cellCount)
        {
            if (cellCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "Workspace capacity must be positive.");

            if (Capacity == cellCount)
                return false;

            Release();
            Blocked = new bool[cellCount];
            ObstacleScratch = new bool[cellCount];
            StaticBlocked = new bool[cellCount];
            DynamicBlocked = new bool[cellCount];
            EscapeDirections = new Vector3[cellCount];
            GoalDirections = new Vector3[cellCount];
            GoalFlags = new FlowFieldGoalFlags[cellCount];
            FinalDirections = new Vector3[cellCount];
            FinalSpeedMultipliers = new float[cellCount];
            ModifierInfluence = new bool[cellCount];
            CellGeneration = new ushort[cellCount];
            InfluenceMask = new bool[cellCount];
            Costs = new int[cellCount];
            Queue = new int[cellCount];
            HeapCells = new int[cellCount];
            HeapPositions = new int[cellCount];
            HeapCount = 0;
            return true;
        }

        public void Init(int cellCount, Allocator allocator = Allocator.Persistent)
        {
            if (cellCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "Native workspace capacity must be positive.");

            if (_nativeAllocated)
            {
                if (!NativeCosts.IsCreated || NativeCosts.Length != cellCount)
                    throw new InvalidOperationException("Native workspace is initialized with an incompatible capacity.");
                throw new InvalidOperationException("Native workspace is already initialized; call Release before Init.");
            }

            Release();
            _nativeAllocated = true;
            try
            {
                NativeCosts = new NativeArray<int>(cellCount, allocator);
                NativeBlocked = new NativeArray<byte>(cellCount, allocator);
                NativeInfluence = new NativeArray<byte>(cellCount, allocator);
                NativeNeighborMasks = new NativeArray<byte>(cellCount, allocator);
                NativeSurfaceValid = new NativeArray<byte>(cellCount, allocator);
                NativeCentersX = new NativeArray<float>(cellCount, allocator);
                NativeCentersZ = new NativeArray<float>(cellCount, allocator);
                NativeSurfaceNormals = new NativeArray<Vector3>(cellCount, allocator);
                NativeHeapCells = new NativeArray<int>(cellCount, allocator);
                NativeHeapPositions = new NativeArray<int>(cellCount, allocator);
                NativeFinalDirections = new NativeArray<Vector3>(cellCount, allocator);
                NativeFinalSpeeds = new NativeArray<float>(cellCount, allocator);
                NativeEscape = new NativeArray<Vector3>(cellCount, allocator);
                NativeGoalDirections = new NativeArray<Vector3>(cellCount, allocator);
                NativeGoalFlags = new NativeArray<byte>(cellCount, allocator);
            }
            catch
            {
                Release();
                throw;
            }
        }

        public void Release()
        {
            if (!_nativeAllocated)
                return;

            if (NativeCosts.IsCreated) NativeCosts.Dispose();
            if (NativeBlocked.IsCreated) NativeBlocked.Dispose();
            if (NativeInfluence.IsCreated) NativeInfluence.Dispose();
            if (NativeNeighborMasks.IsCreated) NativeNeighborMasks.Dispose();
            if (NativeSurfaceValid.IsCreated) NativeSurfaceValid.Dispose();
            if (NativeCentersX.IsCreated) NativeCentersX.Dispose();
            if (NativeCentersZ.IsCreated) NativeCentersZ.Dispose();
            if (NativeSurfaceNormals.IsCreated) NativeSurfaceNormals.Dispose();
            if (NativeHeapCells.IsCreated) NativeHeapCells.Dispose();
            if (NativeHeapPositions.IsCreated) NativeHeapPositions.Dispose();
            if (NativeFinalDirections.IsCreated) NativeFinalDirections.Dispose();
            if (NativeFinalSpeeds.IsCreated) NativeFinalSpeeds.Dispose();
            if (NativeEscape.IsCreated) NativeEscape.Dispose();
            if (NativeGoalDirections.IsCreated) NativeGoalDirections.Dispose();
            if (NativeGoalFlags.IsCreated) NativeGoalFlags.Dispose();
            _nativeAllocated = false;
        }

        public void CommitObstacleScratch()
            => Array.Copy(ObstacleScratch, Blocked, Blocked.Length);

        public void RebuildCombinedBlocked()
        {
            int count = Capacity;
            HasBlockedCells = false;
            for (int i = 0; i < count; i++)
            {
                Blocked[i] = StaticBlocked[i] || DynamicBlocked[i];
                HasBlockedCells |= Blocked[i];
            }
        }

        public void ClearGoal()
        {
            if (GoalDirections == null)
                return;

            Array.Clear(GoalDirections, 0, GoalDirections.Length);
            Array.Clear(GoalFlags, 0, GoalFlags.Length);
            Array.Clear(InfluenceMask, 0, InfluenceMask.Length);
        }

        public void ClearAll()
        {
            if (Capacity <= 0)
                return;

            Array.Clear(Blocked, 0, Capacity);
            Array.Clear(ObstacleScratch, 0, Capacity);
            Array.Clear(StaticBlocked, 0, Capacity);
            Array.Clear(DynamicBlocked, 0, Capacity);
            HasBlockedCells = false;
            Array.Clear(EscapeDirections, 0, Capacity);
            ClearGoal();
            Array.Clear(FinalDirections, 0, Capacity);
            Array.Clear(FinalSpeedMultipliers, 0, Capacity);
            Array.Clear(ModifierInfluence, 0, Capacity);
            Array.Clear(CellGeneration, 0, Capacity);
        }

        public void BumpGeneration(FlowFieldGridSpace grid, FlowFieldCellRect rect)
        {
            if (CellGeneration == null || !grid.IsValid)
                return;

            if (!rect.IsValid)
                rect = FlowFieldCellRect.Full(grid);

            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            {
                for (int x = rect.MinX; x <= rect.MaxX; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    CellGeneration[index]++;
                }
            }
        }

        public void BumpGenerationAll()
        {
            if (CellGeneration == null)
                return;

            for (int i = 0; i < CellGeneration.Length; i++)
                CellGeneration[i]++;
        }
    }
}
