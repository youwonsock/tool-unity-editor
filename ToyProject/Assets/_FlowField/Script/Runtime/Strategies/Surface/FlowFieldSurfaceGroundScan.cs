using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    internal delegate bool FlowFieldSurfaceGroundScanProgress(int row, int rowCount);

    internal readonly struct FlowFieldSurfaceGroundScanSettings
    {
        public FlowFieldGridSpace Grid { get; }
        public Bounds BakeBounds { get; }
        public LayerMask GroundLayer { get; }
        public float MaxSurfaceSlope { get; }
        public float MaxStepHeight { get; }

        public FlowFieldSurfaceGroundScanSettings(
            FlowFieldGridSpace grid,
            Bounds bakeBounds,
            LayerMask groundLayer,
            float maxSurfaceSlope,
            float maxStepHeight)
        {
            Grid = grid;
            BakeBounds = bakeBounds;
            GroundLayer = groundLayer;
            MaxSurfaceSlope = maxSurfaceSlope;
            MaxStepHeight = maxStepHeight;
        }

        public bool IsValid => Grid.IsValid
            && GroundLayer.value != 0
            && FlowFieldGridSpace.IsFinite(BakeBounds.center)
            && FlowFieldGridSpace.IsFinite(BakeBounds.size)
            && BakeBounds.size.x > 0f
            && BakeBounds.size.y >= FlowFieldLayoutUtility.MinBoundsHeight
            && BakeBounds.size.z > 0f
            && Mathf.Abs(BakeBounds.size.x - Grid.WorldSizeX) <= 0.0001f
            && Mathf.Abs(BakeBounds.size.z - Grid.WorldSizeZ) <= 0.0001f
            && Mathf.Abs(BakeBounds.min.x - Grid.Origin.x) <= 0.0001f
            && Mathf.Abs(BakeBounds.min.z - Grid.Origin.z) <= 0.0001f
            && Mathf.Abs(BakeBounds.center.y - Grid.Origin.y) <= 0.0001f
            && FlowFieldGridSpace.IsFinite(MaxSurfaceSlope)
            && MaxSurfaceSlope >= 0f
            && MaxSurfaceSlope < 90f
            && FlowFieldGridSpace.IsFinite(MaxStepHeight)
            && MaxStepHeight >= 0f;
    }

    internal sealed class FlowFieldSurfaceGroundScanResult
    {
        public float[] SurfaceHeights { get; }
        public Vector3[] SurfaceNormals { get; }
        public byte[] CellFlags { get; }
        public byte[] NeighborMasks { get; }
        public int ValidCellCount { get; internal set; }

        public FlowFieldSurfaceGroundScanResult(int cellCount)
        {
            if (cellCount <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(cellCount));
            SurfaceHeights = new float[cellCount];
            SurfaceNormals = new Vector3[cellCount];
            CellFlags = new byte[cellCount];
            NeighborMasks = new byte[cellCount];
        }

        public bool IsValidFor(int cellCount)
            => cellCount > 0
                && ValidCellCount > 0
                && SurfaceHeights.Length == cellCount
                && SurfaceNormals.Length == cellCount
                && CellFlags.Length == cellCount
                && NeighborMasks.Length == cellCount;

        internal void SetSurface(int index, float height, Vector3 normal)
        {
            SurfaceHeights[index] = height;
            SurfaceNormals[index] = normal;
            CellFlags[index] = 1;
            ValidCellCount++;
        }
    }

    internal enum FlowFieldSurfaceGroundScanStatus
    {
        Pending = 0,
        Complete = 1,
        Failed = 2,
        Cancelled = 3,
    }

    /// <summary>
    /// The Surface2D ground scan in resumable steps: one downward ray per
    /// cell, cast in RaycastCommand batches on worker threads, then the
    /// neighbor masks in Burst chunks. Runtime builds run it at once
    /// (<see cref="ScanAll"/>); the Editor bake advances it from its update,
    /// so the raycast rows do not hold one callback. Its settings, result,
    /// status and progress callback are declared beside it.
    /// </summary>
    internal sealed class FlowFieldSurfaceGroundScan
    {
        // Rays per batch and neighbor masks per chunk. Each is scheduled and
        // completed inside one Step; a batch shrinks to what the rest of the
        // step's budget allows at the last batch's cost per ray.
        private const int RaysPerBatch = 1024;
        private const int MinRaysPerBatch = 64;
        private const int RaysPerJob = 64;
        private const int MasksPerChunk = 16384;
        private const int MasksPerJob = 1024;
        private const float NormalEpsilonSqr = 0.000001f;
        private const float BoundsQueryEpsilon = 0.001f;

        /// <summary>The whole scan at once, as a runtime build runs it.</summary>
        public static FlowFieldSurfaceGroundScanResult ScanAll(
            in FlowFieldSurfaceGroundScanSettings settings,
            FlowFieldSurfaceGroundScanProgress progress = null)
        {
            var scan = new FlowFieldSurfaceGroundScan(settings, progress);
            while (!scan.IsComplete)
                scan.Step(double.MaxValue);
            if (scan.Status == FlowFieldSurfaceGroundScanStatus.Cancelled)
                throw new OperationCanceledException(scan.Error);
            if (scan.Status == FlowFieldSurfaceGroundScanStatus.Failed)
                throw new InvalidOperationException(scan.Error);
            return scan.Result;
        }

        /// <summary>
        /// The downward ray of a cell, from just above Bake Bounds to just
        /// below it. Its closest hit is the topmost ground under the cell.
        /// </summary>
        internal static RaycastCommand GroundRay(
            in FlowFieldSurfaceGroundScanSettings settings,
            Vector3 cellCenter,
            QueryParameters query)
        {
            Bounds bounds = settings.BakeBounds;
            Vector3 origin = new Vector3(
                cellCenter.x,
                bounds.max.y + BoundsQueryEpsilon,
                cellCenter.z);
            return new RaycastCommand(
                origin,
                Vector3.down,
                query,
                bounds.size.y + BoundsQueryEpsilon * 2f);
        }

        /// <summary>The ground rays' query: the Ground Layer, triggers ignored, back faces as Physics.Raycast.</summary>
        internal static QueryParameters GroundQuery(in FlowFieldSurfaceGroundScanSettings settings)
            => new QueryParameters(
                settings.GroundLayer,
                false,
                QueryTriggerInteraction.Ignore,
                Physics.queriesHitBackfaces);

        /// <summary>A cell's ground from the closest hit of its ray; no hit leaves it without ground.</summary>
        internal static void ApplyHit(
            in FlowFieldSurfaceGroundScanSettings settings,
            FlowFieldSurfaceGroundScanResult result,
            int index,
            in RaycastHit hit)
        {
            if (hit.colliderInstanceID == 0 || !FlowFieldGridSpace.IsFinite(hit.point))
                return;
            Bounds bounds = settings.BakeBounds;
            float height = hit.point.y;
            if (height < bounds.min.y || height > bounds.max.y)
                return;

            Vector3 normal = hit.normal;
            if (!FlowFieldGridSpace.IsFinite(normal)
                || normal.sqrMagnitude <= NormalEpsilonSqr)
                return;

            normal.Normalize();
            if (Vector3.Dot(normal, Vector3.up) <= 0f
                || Vector3.Angle(normal, Vector3.up) > settings.MaxSurfaceSlope)
                return;

            result.SetSurface(index, hit.point.y, normal);
        }

        private enum Phase
        {
            SurfaceCells,
            NeighborMasks,
            Complete,
        }

        private readonly FlowFieldSurfaceGroundScanSettings _settings;
        private readonly FlowFieldSurfaceGroundScanProgress _progress;
        private readonly FlowFieldSurfaceGroundScanResult _result;
        private readonly int _cellCount;
        private Phase _phase;
        private FlowFieldSurfaceGroundScanStatus _status;
        private string _error;
        private int _cursor;
        private bool _physicsSynchronized;
        private RaycastCommand[] _commands;
        private RaycastHit[] _hits;
        private double _millisecondsPerRay;

        internal FlowFieldSurfaceGroundScan(
            in FlowFieldSurfaceGroundScanSettings settings,
            FlowFieldSurfaceGroundScanProgress progress = null)
        {
            if (!settings.IsValid)
                throw new ArgumentException(
                    "Grid, Ground Layer or Bake Bounds settings are invalid.",
                    nameof(settings));
            if (!FlowFieldLayoutUtility.TryValidateCellCount(
                    settings.Grid.Width,
                    settings.Grid.Depth,
                    out _cellCount))
                throw new ArgumentOutOfRangeException(
                    nameof(settings),
                    $"The cell count exceeds the limit ({FlowFieldLayoutUtility.MaxSurfaceCellCount:N0}). "
                    + $"The grid is {settings.Grid.Width} × {settings.Grid.Depth} cells. "
                    + "Shrink Bake Bounds or increase Cell Size.");

            _settings = settings;
            _progress = progress;
            _result = new FlowFieldSurfaceGroundScanResult(_cellCount);
            _phase = Phase.SurfaceCells;
            _status = FlowFieldSurfaceGroundScanStatus.Pending;
            _error = string.Empty;
        }

        internal FlowFieldSurfaceGroundScanStatus Status => _status;
        internal bool IsComplete => _status == FlowFieldSurfaceGroundScanStatus.Complete
            || _status == FlowFieldSurfaceGroundScanStatus.Failed
            || _status == FlowFieldSurfaceGroundScanStatus.Cancelled;
        internal bool IsValid => _status == FlowFieldSurfaceGroundScanStatus.Complete
            && _result.IsValidFor(_cellCount);
        internal string Error => _error;
        internal FlowFieldSurfaceGroundScanResult Result => IsValid ? _result : null;
        internal float Progress
        {
            get
            {
                if (_status == FlowFieldSurfaceGroundScanStatus.Complete)
                    return 1f;
                if (IsComplete)
                    return 0f;
                if (_phase == Phase.SurfaceCells)
                    return 0.05f + 0.7f * _cursor / Math.Max(1, _cellCount);
                if (_phase == Phase.NeighborMasks)
                    return 0.75f + 0.25f * _cursor / Math.Max(1, _cellCount);
                return 0f;
            }
        }

        internal void Cancel()
        {
            if (IsComplete)
                return;
            _phase = Phase.Complete;
            _status = FlowFieldSurfaceGroundScanStatus.Cancelled;
            _error = "FlowField Surface Bake was cancelled.";
        }

        internal void Step(double budgetMilliseconds = 2.0)
        {
            if (IsComplete)
                return;
            if (budgetMilliseconds <= 0d)
                budgetMilliseconds = 2.0;

            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                if (!_physicsSynchronized)
                {
                    Physics.SyncTransforms();
                    _physicsSynchronized = true;
                }

                while (!IsComplete && timer.Elapsed.TotalMilliseconds < budgetMilliseconds)
                {
                    if (_phase == Phase.SurfaceCells)
                    {
                        if (_cursor >= _cellCount)
                        {
                            if (_result.ValidCellCount <= 0)
                            {
                                Fail("No walkable ground collider was found within Bake Bounds.");
                                return;
                            }
                            _cursor = 0;
                            _phase = Phase.NeighborMasks;
                            continue;
                        }

                        if (!ScanGroundBatch(budgetMilliseconds - timer.Elapsed.TotalMilliseconds))
                            return;
                        continue;
                    }

                    if (_phase == Phase.NeighborMasks)
                    {
                        if (_cursor >= _cellCount)
                        {
                            _phase = Phase.Complete;
                            _status = FlowFieldSurfaceGroundScanStatus.Complete;
                            _error = string.Empty;
                            return;
                        }

                        BuildNeighborMaskChunk();
                    }
                }
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        // One batch of ground rays, read back in cell order. Each ray keeps its
        // closest hit, the hit Physics.Raycast returns. The progress callback
        // still sees every row start; false when it cancels.
        private bool ScanGroundBatch(double remainingMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            FlowFieldGridSpace grid = _settings.Grid;
            int start = _cursor;
            int count = Math.Min(RaysPerBatch, _cellCount - start);
            if (_millisecondsPerRay > 0d && remainingMilliseconds < count * _millisecondsPerRay)
                count = Math.Max(Math.Min(MinRaysPerBatch, count), (int)(remainingMilliseconds / _millisecondsPerRay));
            _commands ??= new RaycastCommand[RaysPerBatch];
            _hits ??= new RaycastHit[RaysPerBatch];
            QueryParameters query = GroundQuery(_settings);
            for (int i = 0; i < count; i++)
            {
                grid.FromFlatIndex(start + i, out int x, out int z);
                _commands[i] = GroundRay(_settings, grid.LocalToWorldCenter(x, z), query);
            }
            // Whole-array copies in and out keep the per-ray work managed.
            var commands = new NativeArray<RaycastCommand>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var hits = new NativeArray<RaycastHit>(count, Allocator.TempJob);
            try
            {
                NativeArray<RaycastCommand>.Copy(_commands, commands, count);
                RaycastCommand.ScheduleBatch(commands, hits, RaysPerJob, 1).Complete();
                NativeArray<RaycastHit>.Copy(hits, _hits, count);
            }
            finally
            {
                commands.Dispose();
                hits.Dispose();
            }
            for (int i = 0; i < count; i++)
            {
                int index = start + i;
                grid.FromFlatIndex(index, out int x, out int z);
                if (x == 0 && _progress != null && !_progress(z, grid.Depth))
                {
                    Cancel();
                    return false;
                }
                ApplyHit(_settings, _result, index, in _hits[i]);
                _cursor = index + 1;
            }
            _millisecondsPerRay = timer.Elapsed.TotalMilliseconds / count;
            return true;
        }

        // One chunk of neighbor masks, in a Burst job over the scanned ground.
        private unsafe void BuildNeighborMaskChunk()
        {
            int start = _cursor;
            int count = Math.Min(MasksPerChunk, _cellCount - start);
            FlowFieldGridSpace grid = _settings.Grid;
            var pins = new FlowFieldPinnedArrays(FlowFieldSurfaceNeighborMaskJob.PinnedArrayCount);
            try
            {
                var job = new FlowFieldSurfaceNeighborMaskJob
                {
                    CellFlags = pins.Pin(_result.CellFlags),
                    Heights = pins.Pin(_result.SurfaceHeights),
                    NeighborMasks = pins.Pin(_result.NeighborMasks),
                    Width = grid.Width,
                    Depth = grid.Depth,
                    Start = start,
                    MaxStepHeight = _settings.MaxStepHeight,
                };
                for (int direction = 0; direction < FlowFieldNeighborUtility.SurfaceCount; direction++)
                {
                    job.DeltaX[direction] = FlowFieldNeighborUtility.DeltaX[direction];
                    job.DeltaZ[direction] = FlowFieldNeighborUtility.DeltaZ[direction];
                }
                job.Schedule(count, MasksPerJob).Complete();
            }
            finally
            {
                pins.Release();
            }
            _cursor = start + count;
        }

        private void Fail(string reason)
        {
            _phase = Phase.Complete;
            _status = FlowFieldSurfaceGroundScanStatus.Failed;
            _error = string.IsNullOrEmpty(reason)
                ? "FlowField Surface Bake failed."
                : reason;
        }
    }
}
