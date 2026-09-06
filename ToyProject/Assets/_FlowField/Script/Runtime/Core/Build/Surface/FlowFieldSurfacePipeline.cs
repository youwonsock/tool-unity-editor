using System;
using System.Diagnostics;
using UnityEngine;

namespace Common.FlowField
{
    internal delegate bool FlowFieldSurfaceBakeProgress(int row, int rowCount);

    internal static class FlowFieldSurfacePipeline
    {
        internal static bool TryValidate(in FlowFieldSurfaceBakeSettings settings, out string reason)
        {
            reason = settings.IsValid ? string.Empty : "Bake Bounds, Cell Size 또는 Ground 설정이 유효하지 않습니다.";
            return settings.IsValid;
        }
    }

    internal readonly struct FlowFieldSurfaceBakeSettings
    {
        public FlowFieldGridSpace Grid { get; }
        public Bounds BakeBounds { get; }
        public LayerMask GroundLayer { get; }
        public float MaxSurfaceSlope { get; }
        public float MaxStepHeight { get; }

        public FlowFieldSurfaceBakeSettings(
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
            && BakeBounds.size.y >= FlowFieldBakeBoundsUtility.MinBoundsHeight
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

    internal sealed class FlowFieldSurfaceBakeResult
    {
        public float[] SurfaceHeights { get; }
        public Vector3[] SurfaceNormals { get; }
        public byte[] CellFlags { get; }
        public byte[] NeighborMasks { get; }
        public int ValidCellCount { get; internal set; }

        public FlowFieldSurfaceBakeResult(int cellCount)
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

    internal static class FlowFieldSurfaceBaker
    {
        private const float NORMAL_EPSILON_SQR = 0.000001f;
        private const float BOUNDS_QUERY_EPSILON = 0.001f;

        public static FlowFieldSurfaceBakeResult Bake(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeProgress progress = null)
        {
            using (var job = new FlowFieldSurfaceBakeJob(settings, progress))
            {
                while (!job.IsComplete)
                    job.Step(double.MaxValue);
                if (job.Status == FlowFieldSurfaceBakeJobStatus.Cancelled)
                    throw new OperationCanceledException(job.Error);
                if (job.Status == FlowFieldSurfaceBakeJobStatus.Failed)
                    throw new InvalidOperationException(job.Error);
                return job.Result;
            }
        }

        internal static void BakeCell(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeResult result,
            int index)
        {
            FlowFieldGridSpace grid = settings.Grid;
            grid.FromFlatIndex(index, out int x, out int z);
            Vector3 origin = grid.LocalToWorldCenter(x, z);
            if (!TryFindTopmostHit(settings, origin, out RaycastHit hit))
                return;

            Vector3 normal = hit.normal;
            if (!FlowFieldGridSpace.IsFinite(hit.point)
                || !FlowFieldGridSpace.IsFinite(normal)
                || normal.sqrMagnitude <= NORMAL_EPSILON_SQR)
                return;

            normal.Normalize();
            if (Vector3.Dot(normal, Vector3.up) <= 0f
                || Vector3.Angle(normal, Vector3.up) > settings.MaxSurfaceSlope)
                return;

            result.SetSurface(index, hit.point.y, normal);
        }

        internal static bool TryFindTopmostHit(
            in FlowFieldSurfaceBakeSettings settings,
            Vector3 cellCenter,
            out RaycastHit topmostHit)
        {
            Bounds bounds = settings.BakeBounds;
            Vector3 origin = new Vector3(
                cellCenter.x,
                bounds.max.y + BOUNDS_QUERY_EPSILON,
                cellCenter.z);
            float distance = bounds.size.y + BOUNDS_QUERY_EPSILON * 2f;
            if (!Physics.Raycast(
                    origin,
                    Vector3.down,
                    out topmostHit,
                    distance,
                    settings.GroundLayer,
                    QueryTriggerInteraction.Ignore))
            {
                topmostHit = default;
                return false;
            }

            if (!FlowFieldGridSpace.IsFinite(topmostHit.point))
            {
                topmostHit = default;
                return false;
            }

            float height = topmostHit.point.y;
            if (height < bounds.min.y || height > bounds.max.y)
            {
                topmostHit = default;
                return false;
            }

            return true;
        }

        private static void BuildNeighborMasks(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeResult result)
        {
            FlowFieldGridSpace grid = settings.Grid;
            for (int z = 0; z < grid.Depth; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    BuildNeighborMask(settings, result, index, x, z);
                }
            }
        }

        internal static void BuildNeighborMask(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeResult result,
            int index,
            int x,
            int z)
        {
            FlowFieldGridSpace grid = settings.Grid;
            if (!IsValid(result, index))
                return;

            byte mask = 0;
            for (int directionIndex = 0; directionIndex < FlowFieldNeighborUtility.Count; directionIndex++)
            {
                int dx = FlowFieldNeighborUtility.DeltaX[directionIndex];
                int dz = FlowFieldNeighborUtility.DeltaZ[directionIndex];
                int nx = x + dx;
                int nz = z + dz;
                if (!grid.IsLocalInBounds(nx, nz))
                    continue;

                int neighbor = grid.ToFlatIndex(nx, nz);
                if (!CanConnect(result, index, neighbor, settings.MaxStepHeight))
                    continue;

                if (FlowFieldNeighborUtility.IsDiagonal(directionIndex)
                    && !CanConnectDiagonal(
                        grid,
                        result,
                        x,
                        z,
                        dx,
                        dz,
                        settings.MaxStepHeight))
                    continue;

                mask |= (byte)(1 << directionIndex);
            }

            result.NeighborMasks[index] = mask;
        }

        private static bool CanConnectDiagonal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeResult result,
            int x,
            int z,
            int dx,
            int dz,
            float maxStepHeight)
        {
            int a = grid.ToFlatIndex(x, z);
            int b = grid.ToFlatIndex(x + dx, z);
            int c = grid.ToFlatIndex(x, z + dz);
            int d = grid.ToFlatIndex(x + dx, z + dz);
            return CanConnect(result, a, b, maxStepHeight)
                && CanConnect(result, a, c, maxStepHeight)
                && CanConnect(result, b, d, maxStepHeight)
                && CanConnect(result, c, d, maxStepHeight);
        }

        private static bool CanConnect(
            FlowFieldSurfaceBakeResult result,
            int left,
            int right,
            float maxStepHeight)
            => IsValid(result, left)
                && IsValid(result, right)
                && Mathf.Abs(result.SurfaceHeights[left] - result.SurfaceHeights[right]) <= maxStepHeight;

        private static bool IsValid(FlowFieldSurfaceBakeResult result, int index)
            => (result.CellFlags[index] & 1) != 0;
    }

    internal enum FlowFieldSurfaceBakeJobStatus
    {
        Pending = 0,
        Complete = 1,
        Failed = 2,
        Cancelled = 3,
    }

    /// <summary>
    /// Resumable Surface2D authoring job. Runtime callers can still use the
    /// synchronous FlowFieldSurfaceBaker facade, while Editor callers advance
    /// this job from update so raycast rows do not monopolize one callback.
    /// </summary>
    internal sealed class FlowFieldSurfaceBakeJob : IDisposable
    {
        private enum Phase
        {
            SurfaceCells,
            NeighbourMasks,
            Complete,
        }

        private readonly FlowFieldSurfaceBakeSettings _settings;
        private readonly FlowFieldSurfaceBakeProgress _progress;
        private readonly FlowFieldSurfaceBakeResult _result;
        private readonly int _cellCount;
        private Phase _phase;
        private FlowFieldSurfaceBakeJobStatus _status;
        private string _error;
        private int _cursor;
        private bool _physicsSynchronized;
        private bool _disposed;

        internal FlowFieldSurfaceBakeJob(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeProgress progress = null)
        {
            if (!settings.IsValid)
                throw new ArgumentException(
                    "Grid, Ground Layer 또는 Bake Bounds 설정이 유효하지 않습니다.",
                    nameof(settings));
            if (!FlowFieldBakeBoundsUtility.TryValidateCellCount(
                    settings.Grid.Width,
                    settings.Grid.Depth,
                    out _cellCount))
                throw new ArgumentOutOfRangeException(
                    nameof(settings),
                    $"Cell Count가 상한({FlowFieldBakeBoundsUtility.MaxCellCount:N0})을 초과합니다. "
                    + $"현재 {settings.Grid.Width} × {settings.Grid.Depth} cells입니다. "
                    + "Bake Bounds 또는 Cell Size를 줄이세요.");

            _settings = settings;
            _progress = progress;
            _result = new FlowFieldSurfaceBakeResult(_cellCount);
            _phase = Phase.SurfaceCells;
            _status = FlowFieldSurfaceBakeJobStatus.Pending;
            _error = string.Empty;
        }

        internal FlowFieldSurfaceBakeJobStatus Status => _status;
        internal bool IsComplete => _status == FlowFieldSurfaceBakeJobStatus.Complete
            || _status == FlowFieldSurfaceBakeJobStatus.Failed
            || _status == FlowFieldSurfaceBakeJobStatus.Cancelled;
        internal bool IsValid => _status == FlowFieldSurfaceBakeJobStatus.Complete
            && _result.IsValidFor(_cellCount);
        internal string Error => _error;
        internal FlowFieldSurfaceBakeResult Result => IsValid ? _result : null;
        internal float Progress
        {
            get
            {
                if (_status == FlowFieldSurfaceBakeJobStatus.Complete)
                    return 1f;
                if (IsComplete)
                    return 0f;
                if (_phase == Phase.SurfaceCells)
                    return 0.05f + 0.7f * _cursor / Math.Max(1, _cellCount);
                if (_phase == Phase.NeighbourMasks)
                    return 0.75f + 0.25f * _cursor / Math.Max(1, _cellCount);
                return 0f;
            }
        }

        internal void Cancel()
        {
            if (IsComplete)
                return;
            _phase = Phase.Complete;
            _status = FlowFieldSurfaceBakeJobStatus.Cancelled;
            _error = "FlowField Surface Bake was cancelled.";
        }

        internal void Step(double budgetMilliseconds = 2.0)
        {
            if (_disposed || IsComplete)
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
                                Fail("Bake Bounds 범위에서 이동 가능한 Ground Collider를 찾지 못했습니다.");
                                return;
                            }
                            _cursor = 0;
                            _phase = Phase.NeighbourMasks;
                            continue;
                        }

                        int index = _cursor++;
                        FlowFieldGridSpace grid = _settings.Grid;
                        grid.FromFlatIndex(index, out _, out int z);
                        if (index % grid.Width == 0
                            && _progress != null
                            && !_progress(z, grid.Depth))
                        {
                            Cancel();
                            return;
                        }
                        FlowFieldSurfaceBaker.BakeCell(_settings, _result, index);
                        continue;
                    }

                    if (_phase == Phase.NeighbourMasks)
                    {
                        if (_cursor >= _cellCount)
                        {
                            _phase = Phase.Complete;
                            _status = FlowFieldSurfaceBakeJobStatus.Complete;
                            _error = string.Empty;
                            return;
                        }

                        int index = _cursor++;
                        _settings.Grid.FromFlatIndex(index, out int x, out int z);
                        FlowFieldSurfaceBaker.BuildNeighborMask(_settings, _result, index, x, z);
                    }
                }
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        private void Fail(string reason)
        {
            _phase = Phase.Complete;
            _status = FlowFieldSurfaceBakeJobStatus.Failed;
            _error = string.IsNullOrEmpty(reason)
                ? "FlowField Surface Bake failed."
                : reason;
        }
    }
}
