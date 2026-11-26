using System;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
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
        private readonly FlowFieldSpareArrays _spares = new FlowFieldSpareArrays();
        // The uncertain regions, which the session owns and updates. The
        // builder only reads them to limit its obstacle queries.
        private FlowFieldSafety _safety => _session.Safety;

        private VolumeFieldData _committed;
        private BuildContext _build;
        private readonly FlowFieldRequestInbox<FlowFieldVolumeRequest> _requests
            = new FlowFieldRequestInbox<FlowFieldVolumeRequest>();
        // Modifier changes reuse the published navigation unless a full build is pending.
        private bool _hasPendingModifierRecompose;
        // An explicit rebuild re-queries obstacles even when only the Goal changed.
        private bool _obstacleQueryRequired;
        // The sweep was turned on or off since the last build began.
        private bool _queryEveryCell;
        // BakedBase: every region an obstacle change reached since Initialize.
        // A reloaded bake queries it again, because the bake still holds what
        // was there before the changes.
        private Bounds? _bakedBaseTouched;
        private FlowFieldBakeMode _bakeMode;

        // The session that runs this builder. Lifecycle, events and the build
        // loop live there.
        private readonly FlowFieldSession _session;

        internal FlowFieldVolumeBuilder(FlowFieldSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Volume3D;
        public bool HasPendingRequest => _requests.Pending || _hasPendingModifierRecompose || _build != null;
        internal bool TryGetLatestRequestedInput(out FlowFieldVolumeRequest request)
        {
            request = _requests.Latest;
            return _requests.HasLatest;
        }

        internal float BuildProgress
        {
            get
            {
                if (_build == null)
                    return _requests.Pending || _hasPendingModifierRecompose ? 0f : 1f;
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
            _requests.Clear();
            _hasPendingModifierRecompose = false;
            _obstacleQueryRequired = false;
            _queryEveryCell = false;
            _bakedBaseTouched = null;
        }

        /// <summary>
        /// Accepts a request. Returns the Bounds when new obstacle settings
        /// make every published cell uncertain.
        /// </summary>
        internal Bounds? Submit(in FlowFieldVolumeRequest request, bool requeryObstacles = false)
        {
            ThrowIfDisposed();
            if (!_session.IsInitialized)
                throw new InvalidOperationException("FlowField volume session is not initialized.");
            ValidateRequest(request);
            if (requeryObstacles)
                _obstacleQueryRequired = true;
            // Turning the sweep on or off says nothing changed, so the field
            // stays readable; the next build still queries every cell from
            // Physics, BakedBase included, so it takes in unregistered changes
            // made while nothing swept them, as Surface2D does.
            if (_requests.HasLatest && _requests.Latest.SweepUnregistered != request.SweepUnregistered)
            {
                _obstacleQueryRequired = true;
                _queryEveryCell = true;
            }
            // A new grid or source drops the published field for good: readers
            // wait for a build on this grid, even if the grid later returns to
            // the old one. Obstacle changes that reached no cell of the new
            // grid were never applied to the old field (the session skips
            // them), so it can never be shown again.
            if (_committed != null && !_committed.Request.HasSameGrid(request))
                _committed = null;
            bool goalMovedWithinCell = IsGoalMoveWithinCell(request);
            // New obstacle settings make every cell uncertain from now. The
            // comparison is with the latest accepted request on the same grid:
            // a build that began with the previous settings, the first build
            // included, may still publish.
            bool obstacleSettingsChanged = _requests.HasLatest
                && _requests.Latest.HasSameGrid(request)
                && !_requests.Latest.HasSameObstacleInputs(request);
            Bounds? invalidated = obstacleSettingsChanged ? request.WorldBounds : (Bounds?)null;
            // Keep the last accepted request independently of the committed
            // field.  A dirty notification that arrives while another Goal is
            // building must never resurrect the older committed Goal.
            _requests.Latest = request;
            _requests.HasLatest = true;
            // A grid/signature change invalidates the private staging arrays
            // immediately, and a removed Goal must not publish once more.
            if (_build != null
                && (!_build.Request.HasSameGrid(request) || _build.Request.HasGoal && !request.HasGoal))
                DiscardBuild();
            if (!goalMovedWithinCell)
                _requests.Pending = true;
            return invalidated;
        }

        /// <summary>
        /// The running or published field already answers this request; only
        /// the requested position differs. Resubmitting the same position
        /// remains an explicit request for another build.
        /// </summary>
        private bool IsGoalMoveWithinCell(in FlowFieldVolumeRequest request)
        {
            if (_obstacleQueryRequired || _requests.Pending || request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            if (_build == null && _committed == null)
                return false;
            FlowFieldVolumeRequest current = _build != null ? _build.Request : _committed.Request;
            return !current.HasSameGoal(request) && current.HasSameResult(request);
        }

        // The session advanced its generation, so the discarded build cannot publish.
        public void CancelWork()
        {
            DiscardBuild();
            _requests.Pending = false;
            _hasPendingModifierRecompose = false;
        }

        // The world may have changed while nothing observed it, so the session
        // is not Ready again until a new build publishes. The latest request
        // builds again.
        public void DropComputedState()
        {
            _committed = null;
            _requests.Pending = _requests.HasLatest;
        }

        public void ReleaseResources()
        {
            _committed = null;
            _requests.Clear();
            _bakedBaseTouched = null;
            _overlapBuffer = null;
            _cellQuery.Clear();
            _spares.Clear();
        }

        private static Vector3 ObstacleHalfExtents(float cellSize, float clearance)
            => Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(cellSize, clearance);

        private void ThrowIfDisposed()
        {
            if (_session.Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldVolumeBuilder));
        }

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
