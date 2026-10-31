namespace Common.FlowField
{
    /// <summary>
    /// Decides whether a running Surface2D solve still matches the accepted
    /// inputs. RequestVersion advances for every observable input change;
    /// BaseVersion advances only when the navigation graph itself changes.
    /// </summary>
    internal sealed class FlowFieldSurfaceRebuildTracker
    {
        internal const FlowFieldDirtyFlags BaseDirtyMask = FlowFieldDirtyFlags.Grid
            | FlowFieldDirtyFlags.StaticObstacles
            | FlowFieldDirtyFlags.DynamicObstacles
            | FlowFieldDirtyFlags.Escape
            | FlowFieldDirtyFlags.Goal;

        private const FlowFieldDirtyFlags ObstacleFlags = FlowFieldDirtyFlags.StaticObstacles
            | FlowFieldDirtyFlags.DynamicObstacles
            | FlowFieldDirtyFlags.Escape;

        private int _baseVersion;
        private int _activeBaseVersion = -1;
        private int _activeRequestVersion = -1;
        // Obstacle registration/movement is first treated as a non-versioning
        // request.  The effective mask is not known until the physics probe
        // runs; only then is the pending input promoted to a base version.
        private bool _deferredObstacle;
        private bool _deferredSurface;
        private bool _deferredGoal;
        // MarkDirty is also used by the Unity-facing façade before it submits
        // a request. Keep track of those flags so Submit can detect changes
        // from test/editor adapters without incrementing the version twice.
        private FlowFieldDirtyFlags _versionedFlags;

        internal int RequestVersion { get; private set; }
        internal bool IsBaseStale => _activeBaseVersion != _baseVersion;
        internal bool IsRequestStale => _activeRequestVersion != RequestVersion;
        internal bool HasVersionedGrid => (_versionedFlags & FlowFieldDirtyFlags.Grid) != 0;

        internal static bool IsBaseDirty(FlowFieldDirtyFlags flags)
            => (flags & BaseDirtyMask) != 0;

        internal void BeginSolve()
        {
            _activeBaseVersion = _baseVersion;
            _activeRequestVersion = RequestVersion;
        }

        internal void RecordMarkDirty(
            FlowFieldDirtyFlags flags,
            bool deferObstacle,
            bool deferSurface,
            bool deferGoal)
        {
            unchecked
            {
                if (deferObstacle)
                    _deferredObstacle = true;
                if (deferSurface)
                    _deferredSurface = true;
                // Goal authoring changes are accepted immediately, but the
                // resolved cell/influence result is not known until the
                // shared Goal stage runs. RequestVersion therefore advances
                // now while BaseVersion waits for that stage to prove that
                // the base graph actually changed.
                if (deferGoal)
                    _deferredGoal = true;
                // Obstacle-only requests intentionally wait for the physics
                // probe before becoming observable. Surface and Goal edits
                // are authoring changes, so they advance RequestVersion as
                // soon as they are accepted. If several deferred categories
                // are marked together, still advance it only once.
                if (!deferObstacle || deferSurface || deferGoal)
                    RequestVersion++;
                FlowFieldDirtyFlags deferredBaseFlags = FlowFieldDirtyFlags.None;
                if (deferObstacle)
                    deferredBaseFlags |= ObstacleFlags;
                if (deferSurface)
                    deferredBaseFlags |= FlowFieldDirtyFlags.Grid;
                if (deferGoal)
                    deferredBaseFlags |= FlowFieldDirtyFlags.Goal;
                // Only navigation inputs advance BaseVersion; final-only
                // changes do not invalidate the BFS.
                if ((flags & BaseDirtyMask & ~deferredBaseFlags) != FlowFieldDirtyFlags.None)
                    _baseVersion++;
            }
            _versionedFlags |= flags;
        }

        /// <summary>
        /// A request can be submitted by a test/editor adapter without a
        /// preceding MarkDirty call. Subtracting the flags the façade already
        /// versioned gives one increment for one logical input change
        /// regardless of which entry point marked it first.
        /// </summary>
        internal void RecordSubmit(FlowFieldDirtyFlags combinedFlags)
        {
            FlowFieldDirtyFlags unversionedFlags = combinedFlags & ~_versionedFlags;
            if (unversionedFlags != FlowFieldDirtyFlags.None)
            {
                FlowFieldDirtyFlags deferredObstacleFlags = unversionedFlags & ObstacleFlags;
                if (deferredObstacleFlags != FlowFieldDirtyFlags.None)
                    _deferredObstacle = true;
                FlowFieldDirtyFlags deferredGoalFlags = unversionedFlags & FlowFieldDirtyFlags.Goal;
                if (deferredGoalFlags != FlowFieldDirtyFlags.None)
                    _deferredGoal = true;
                FlowFieldDirtyFlags deferredSurfaceFlags = unversionedFlags & FlowFieldDirtyFlags.Grid;
                if (deferredSurfaceFlags != FlowFieldDirtyFlags.None)
                    _deferredSurface = true;
                FlowFieldDirtyFlags immediatelyVersioned = unversionedFlags
                    & ~(deferredObstacleFlags | deferredSurfaceFlags | deferredGoalFlags);
                // FinalRegion is a derived flag for an obstacle-only request.
                // Do not turn that derived bit into a visible RequestVersion;
                // the probe decides whether the base input really changed. If
                // another source (Goal, DefaultDirection or a Modifier) is
                // present, keep it and version normally.
                if ((deferredObstacleFlags != FlowFieldDirtyFlags.None
                        || deferredSurfaceFlags != FlowFieldDirtyFlags.None
                        || deferredGoalFlags != FlowFieldDirtyFlags.None)
                    && (unversionedFlags
                        & ~(deferredObstacleFlags
                            | deferredSurfaceFlags
                            | deferredGoalFlags
                            | FlowFieldDirtyFlags.FinalRegion)) == 0)
                {
                    immediatelyVersioned &= ~FlowFieldDirtyFlags.FinalRegion;
                }
                unchecked
                {
                    if (deferredSurfaceFlags != FlowFieldDirtyFlags.None
                        || deferredGoalFlags != FlowFieldDirtyFlags.None
                        || immediatelyVersioned != FlowFieldDirtyFlags.None)
                    {
                        RequestVersion++;
                        if (IsBaseDirty(immediatelyVersioned))
                            _baseVersion++;
                    }
                }
            }
            _versionedFlags = FlowFieldDirtyFlags.None;
        }

        internal void PromoteObstacle()
        {
            if (!_deferredObstacle)
                return;
            unchecked
            {
                RequestVersion++;
                _baseVersion++;
            }
            _deferredObstacle = false;
        }

        internal void PromoteSurface()
        {
            if (!_deferredSurface)
                return;
            unchecked { _baseVersion++; }
            _deferredSurface = false;
        }

        internal void PromoteGoal()
        {
            if (!_deferredGoal)
                return;
            unchecked { _baseVersion++; }
            _deferredGoal = false;
        }

        internal void DropDeferredObstacle() => _deferredObstacle = false;
        internal void DropDeferredSurface() => _deferredSurface = false;
        internal void DropDeferredGoal() => _deferredGoal = false;

        internal void ClearVersioned(FlowFieldDirtyFlags flags) => _versionedFlags &= ~flags;
    }
}
