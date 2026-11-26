using System;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;

namespace Common.FlowField
{
    /// <summary>
    /// The Volume3D strategy of a <see cref="FlowFieldSession"/>: cubic cells
    /// filling the Bounds, 26-neighbor navigation on worker threads, and
    /// re-queries limited to the regions whose obstacles changed.
    /// </summary>
    internal sealed partial class FlowFieldVolumeBuilder : IFlowFieldSpaceBuilder
    {
        // Cells per obstacle or Modifier step, and array elements per copy step.
        private const int CellsPerStep = 64;
        private const int ArrayElementsPerStep = 4096;
        // Shared by the obstacle and Modifier steps: the cells of one step and
        // the Colliders one cell query found.
        private Collider[] _overlapBuffer;
        private readonly int[] _stepCells = new int[CellsPerStep];
        private readonly FlowFieldVolumeCellQuery _cellQuery = new FlowFieldVolumeCellQuery();
        // The uncertain regions, which the session owns and updates. The
        // builder only reads them to limit its obstacle queries.
        private IFlowFieldUncertainRegions _safety => _host.UncertainRegions;

        // What input owes the next build, and the published field with its spares.
        private readonly FlowFieldVolumeInputLedger _inputs = new FlowFieldVolumeInputLedger();
        private readonly FlowFieldVolumeFieldStore _fields = new FlowFieldVolumeFieldStore();
        private BuildContext _build;
        private FlowFieldBakeMode _bakeMode;

        // What this builder reads from the session that runs it. Lifecycle,
        // events and the build loop live there.
        private readonly IFlowFieldBuildHost _host;

        internal FlowFieldVolumeBuilder(IFlowFieldBuildHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Volume3D;
        public bool HasPendingRequest => _inputs.IsOwed || _build != null;
        internal bool TryGetLatestRequestedInput(out FlowFieldVolumeRequest request)
        {
            request = _inputs.Requests.Latest;
            return _inputs.Requests.HasLatest;
        }

        internal float BuildProgress
        {
            get
            {
                if (_build == null)
                    return _inputs.IsOwed ? 0f : 1f;
                if (_build.Phase == BuildPhase.Complete)
                    return 1f;

                float phaseCount = (float)BuildPhase.Complete;
                float local = _build.Count <= 0 ? 0f : (float)_build.Cursor / _build.Count;
                return Mathf.Clamp01(((int)_build.Phase + Mathf.Clamp01(local)) / phaseCount);
            }
        }

        // The session cleared the uncertain regions. Running with no request
        // yet; the first Submit starts a build.
        public void Initialize(FlowFieldBakeMode bakeMode)
        {
            _bakeMode = bakeMode;
            _inputs.Reset();
        }

        /// <summary>
        /// Accepts a request. Returns the Bounds when new obstacle settings
        /// make every published cell uncertain.
        /// </summary>
        internal Bounds? Submit(in FlowFieldVolumeRequest request, bool requeryObstacles = false)
        {
            ValidateRequest(request);
            if (requeryObstacles)
                _inputs.ObstacleQueryRequired = true;
            // Turning the sweep on or off says nothing changed, so the field
            // stays readable; the next build still queries every cell from
            // Physics, BakedBase included, so it takes in unregistered changes
            // made while nothing swept them, as Surface2D does.
            if (_inputs.Requests.HasLatest && _inputs.Requests.Latest.SweepUnregistered != request.SweepUnregistered)
            {
                _inputs.ObstacleQueryRequired = true;
                _inputs.QueryEveryCell = true;
            }
            // A new grid or source drops the published field for good: readers
            // wait for a build on this grid, even if the grid later returns to
            // the old one. Obstacle changes that reached no cell of the new
            // grid were never applied to the old field (the session skips
            // them), so it can never be shown again.
            if (_fields.Committed != null && !_fields.Committed.Request.HasSameGrid(request))
                _fields.Drop();
            bool goalMovedWithinCell = IsGoalMoveWithinCell(request);
            // New obstacle settings make every cell uncertain from now. The
            // comparison is with the latest accepted request on the same grid:
            // a build that began with the previous settings, the first build
            // included, may still publish.
            bool obstacleSettingsChanged = _inputs.Requests.HasLatest
                && _inputs.Requests.Latest.HasSameGrid(request)
                && !_inputs.Requests.Latest.HasSameObstacleInputs(request);
            Bounds? invalidated = obstacleSettingsChanged ? request.WorldBounds : (Bounds?)null;
            // Keep the last accepted request independently of the committed
            // field.  A dirty notification that arrives while another Goal is
            // building must never resurrect the older committed Goal.
            _inputs.Requests.Latest = request;
            _inputs.Requests.HasLatest = true;
            // A grid/signature change invalidates the private staging arrays
            // immediately, and a removed Goal must not publish once more.
            if (_build != null
                && (!_build.Request.HasSameGrid(request) || _build.Request.HasGoal && !request.HasGoal))
                DiscardBuild();
            if (!goalMovedWithinCell)
                _inputs.Requests.Pending = true;
            return invalidated;
        }

        /// <summary>
        /// The running or published field already answers this request; only
        /// the requested position differs. Resubmitting the same position
        /// remains an explicit request for another build.
        /// </summary>
        private bool IsGoalMoveWithinCell(in FlowFieldVolumeRequest request)
        {
            if (_inputs.ObstacleQueryRequired || _inputs.Requests.Pending || request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            if (_build == null && _fields.Committed == null)
                return false;
            FlowFieldVolumeRequest current = _build != null ? _build.Request : _fields.Committed.Request;
            return !current.HasSameGoal(request) && current.HasSameResult(request);
        }

        // The session advanced its generation, so the discarded build cannot publish.
        public void CancelWork()
        {
            DiscardBuild();
            _inputs.ClearOwed();
        }

        // The world may have changed while nothing observed it, so the session
        // is not Ready again until a new build publishes. The latest request
        // builds again.
        public void DropComputedState()
        {
            _fields.Drop();
            _inputs.Requests.Pending = _inputs.Requests.HasLatest;
        }

        public void ReleaseResources()
        {
            _fields.Release();
            _inputs.Reset();
            _overlapBuffer = null;
            _cellQuery.Clear();
        }

        private static Vector3 ObstacleHalfExtents(float cellSize, float clearance)
            => Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(cellSize, clearance);

        private void ValidateRequest(in FlowFieldVolumeRequest request)
        {
            if (!request.Grid.IsValid || request.Grid.SpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new ArgumentException("Volume request requires a valid Volume3D grid.", nameof(request));
            if (request.ObstacleLayer.value == 0)
                throw new ArgumentException("Volume obstacle layer must contain at least one layer.", nameof(request));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleClearance) || request.ObstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleClearance));
            if (!FlowFieldVectorUtility.TryNormalizeDefaultDirection(request.DefaultDirection, out _))
                throw new ArgumentOutOfRangeException(nameof(request.DefaultDirection));
            if (request.HasGoal && !FlowFieldGridSpace.IsFinite(request.GoalWorld))
                throw new ArgumentOutOfRangeException(nameof(request.GoalWorld));
            if (!FlowFieldGridSpace.IsFinite(request.GoalInfluenceRadius) || request.GoalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.GoalInfluenceRadius));

            bool usesBake = FlowFieldBakeModes.UsesBakeAsset(request.BakeMode);
            if (usesBake && request.BakeData == null)
                throw new InvalidOperationException(
                    $"{request.BakeMode} Volume3D requests require a FlowFieldVolumeBakeData asset.");
            if (!usesBake && request.BakeData != null)
                throw new InvalidOperationException(
                    $"{request.BakeMode} Volume3D requests cannot carry a bake asset.");

            if (request.BakeData != null
                && !request.BakeData.Matches(
                    request.Grid,
                    request.WorldBounds,
                    request.ObstacleLayer,
                    request.ObstacleClearance,
                    out string staticReason))
                throw new InvalidOperationException(staticReason);
        }
    }

}
