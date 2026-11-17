using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Editor adapter for the shared build session.  It owns only queueing,
    /// Undo and AssetDatabase side effects; surface, obstacle and BFS logic is
    /// the same code used by runtime sessions.
    /// </summary>
    internal static class FlowFieldBakeEditor
    {
        private static bool _isBaking;
        private static bool _asyncPending;
        private static bool _processingQueue;
        private static bool _shuttingDown;
        private static int _callbackGeneration;
        private static readonly Queue<int> _queue = new Queue<int>();
        private static Action _cancel;
        private static string _progressLabel = string.Empty;

        static FlowFieldBakeEditor()
        {
            AssemblyReloadEvents.beforeAssemblyReload += InvalidateCallbacks;
            EditorApplication.quitting += InvalidateCallbacks;
        }

        public static bool IsBaking => _isBaking;
        public static string ProgressLabel => _progressLabel;

        /// <summary>
        /// Cancels the active bake at the next safe callback/readback
        /// boundary. The generation is invalidated before cleanup so a late
        /// late callback cannot write an Asset after the user cancels.
        /// </summary>
        public static void CancelBake()
        {
            if (!_isBaking && !_asyncPending)
                return;

            _shuttingDown = true;
            unchecked { _callbackGeneration++; }
            _cancel?.Invoke();
            _cancel = null;
            _queue.Clear();
            _asyncPending = false;
            _isBaking = false;
            _progressLabel = string.Empty;
            EditorUtility.ClearProgressBar();
            _shuttingDown = false;
        }

        private static void InvalidateCallbacks()
        {
            _shuttingDown = true;
            unchecked { _callbackGeneration++; }
            _cancel?.Invoke();
            _cancel = null;
            _queue.Clear();
            _asyncPending = false;
            _isBaking = false;
            _processingQueue = false;
            _progressLabel = string.Empty;
            EditorUtility.ClearProgressBar();
            _shuttingDown = false;
        }

        [MenuItem("Tools/FlowField/Bake All Managers In Open Scenes")]
        private static void BakeAllManagersInOpenScenes()
        {
            if (_isBaking)
                throw new InvalidOperationException("A FlowField bake is already in progress.");
            FlowFieldManager[] managers = Resources.FindObjectsOfTypeAll<FlowFieldManager>();
            for (int i = 0; i < managers.Length; i++)
            {
                FlowFieldManager manager = managers[i];
                if (manager != null && !EditorUtility.IsPersistent(manager)
                    && manager.gameObject.scene.IsValid()
                    && !string.IsNullOrEmpty(manager.gameObject.scene.path)
                    && manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                    _queue.Enqueue(manager.GetInstanceID());
            }
            _isBaking = true;
            EditorApplication.delayCall += ProcessQueue;
        }

        public static void ScheduleBake(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (manager.BakeMode != FlowFieldBakeMode.StaticBaked)
            {
                Debug.Log("RuntimeDynamic does not persist a bake asset; Surface is rebuilt by the runtime session.", manager);
                return;
            }
            if (_isBaking)
            {
                // latest-wins for a repeated request of the same Manager
                int instanceId = manager.GetInstanceID();
                if (!_queue.Contains(instanceId))
                    _queue.Enqueue(instanceId);
                return;
            }
            _queue.Enqueue(manager.GetInstanceID());
            _isBaking = true;
            EditorApplication.delayCall += ProcessQueue;
        }

        private static void ProcessQueue()
        {
            if (_processingQueue || _asyncPending)
                return;
            _processingQueue = true;
            if (_queue.Count == 0)
            {
                _isBaking = false;
                _processingQueue = false;
                return;
            }

            int id = _queue.Dequeue();
            FlowFieldManager manager = EditorUtility.InstanceIDToObject(id) as FlowFieldManager;
            _progressLabel = manager == null
                ? "Resolving FlowField manager"
                : $"Baking {manager.name}";
            EditorUtility.DisplayProgressBar("FlowField Static Bake", _progressLabel, 0f);
            try
            {
                if (manager == null)
                    throw new InvalidOperationException("The scheduled FlowField manager no longer exists.");
                BakeAndAssign(manager);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("FlowField Static Bake cancelled.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, manager);
            }
            _processingQueue = false;
            if (!_asyncPending)
            {
                ScheduleNextOrStop();
            }
        }

        private static void ScheduleNextOrStop()
        {
            if (_shuttingDown)
                return;
            if (_queue.Count > 0)
            {
                _isBaking = true;
                EditorApplication.delayCall += ProcessQueue;
            }
            else
            {
                _isBaking = false;
                _progressLabel = string.Empty;
                EditorUtility.ClearProgressBar();
            }
        }

        public static void BakeAndAssign(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (Application.isPlaying)
                throw new InvalidOperationException("Static Flow Bake is unavailable while the Editor is in Play mode.");
            if (manager.BakeMode != FlowFieldBakeMode.StaticBaked)
                return;

            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                BakeVolumeAndAssign(manager);
                return;
            }

            FlowFieldSurfaceBakeSettings settings = manager.CreateSurfaceBakeSettings();
            SurfaceBakeInput input = new SurfaceBakeInput(manager, settings);
            FlowFieldSurfaceBakeJob bakeJob = new FlowFieldSurfaceBakeJob(
                settings,
                ReportSurfaceProgress);
            var run = new BakeRun(
                manager,
                "Static Flow Bake",
                () => input.IsCurrent(manager),
                session => session.Pump(),
                session => SaveSurfaceBake(manager, settings, session),
                bakeJob.Dispose);
            // The ground scan runs first; the session starts on its result.
            run.Prepare = () => StepSurfaceGroundScan(run, manager, settings, input, bakeJob);
            run.Start("Baking Surface");
        }

        // One editor frame of the ground scan. True once the session has started.
        private static bool StepSurfaceGroundScan(
            BakeRun run,
            FlowFieldManager manager,
            FlowFieldSurfaceBakeSettings settings,
            in SurfaceBakeInput input,
            FlowFieldSurfaceBakeJob bakeJob)
        {
            bakeJob.Step(2.0);
            if (_shuttingDown)
            {
                run.Stop();
                return false;
            }
            _progressLabel = $"Baking Surface {bakeJob.Progress:P0}";
            if (EditorUtility.DisplayCancelableProgressBar(
                    "FlowField Static Bake",
                    _progressLabel,
                    bakeJob.Progress))
            {
                bakeJob.Cancel();
            }
            if (!bakeJob.IsComplete)
                return false;
            if (bakeJob.Status == FlowFieldSurfaceBakeJobStatus.Cancelled)
                throw new OperationCanceledException();
            if (!bakeJob.IsValid)
                throw new InvalidOperationException(bakeJob.Error);

            FlowFieldSurfaceData surface = FlowFieldSurfaceData.FromRuntime(settings, bakeJob.Result, 1);
            FlowFieldSession session = FlowFieldSession.CreateSurface(surfaceSource: new FlowFieldFixedSurfaceSource(surface));
            run.Attach(session);
            session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldSurfaceGoalResolution goalSnapshot = manager.ResolveConfiguredGoal(settings.Grid);
            bool accepted = session.Surface.Submit(FlowFieldSurfaceRequest.ForSceneBuild(
                settings,
                input.ObstacleLayer,
                input.CheckHeight,
                input.CheckCenterOffset,
                input.Clearance,
                goalSnapshot,
                manager.CaptureConfig().Flow.DefaultDirection,
                FlowFieldSurfaceDirtyFlags.All,
                FlowFieldCellRect.Full(settings.Grid),
                FlowFieldCellRect.Full(settings.Grid)));
            if (!accepted && !session.IsFaulted)
                throw new InvalidOperationException("Static Flow Bake session could not be started.");
            if (session.IsFaulted)
                throw session.Fault ?? new InvalidOperationException("Static Flow Bake failed.");
            session.Pump();
            return true;
        }

        private static void BakeVolumeAndAssign(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));

            FlowFieldSession session = FlowFieldBakeService.CreateVolumeBakeSession(
                manager,
                out FlowFieldVolumeRequest request);
            int bakeInputGeneration = FlowFieldBakeService.CaptureInputGeneration(manager);
            var run = new BakeRun(
                manager,
                "Volume3D Static Flow Bake",
                () => IsCurrentVolumeInput(manager, request, bakeInputGeneration),
                current =>
                {
                    if (!ReportVolumeProgress(current.Volume.BuildProgress))
                        throw new OperationCanceledException();
                    current.Pump(2.0);
                },
                current => SaveVolumeBake(manager, current),
                null);
            run.Attach(session);
            run.Start(null);
        }

        /// <summary>
        /// One asynchronous bake of one Manager, the same in both modes: it pumps
        /// from the editor update loop, drops the bake when a newer bake or the
        /// user cancels it or its input changes, and saves on the published field.
        /// </summary>
        private sealed class BakeRun
        {
            private readonly FlowFieldManager _manager;
            private readonly string _name;
            private readonly int _generation = _callbackGeneration;
            private readonly Func<bool> _inputIsCurrent;
            private readonly Action<FlowFieldSession> _pump;
            private readonly Action<FlowFieldSession> _save;
            private readonly Action _disposeExtra;
            private FlowFieldSession _session;
            private bool _cleaned;

            // Runs each frame before the session exists; true once it started it.
            internal Func<bool> Prepare;

            internal BakeRun(
                FlowFieldManager manager,
                string name,
                Func<bool> inputIsCurrent,
                Action<FlowFieldSession> pump,
                Action<FlowFieldSession> save,
                Action disposeExtra)
            {
                _manager = manager;
                _name = name;
                _inputIsCurrent = inputIsCurrent;
                _pump = pump;
                _save = save;
                _disposeExtra = disposeExtra;
            }

            /// <summary>Takes the session before it starts, so a build that completes at once is seen.</summary>
            internal void Attach(FlowFieldSession session)
            {
                _session = session;
                session.FieldCommitted += Complete;
                session.Failed += Failed;
            }

            internal void Start(string initialLabel)
            {
                _cancel = Stop;
                _asyncPending = true;
                EditorApplication.update += Pump;
                if (initialLabel != null)
                    EditorUtility.DisplayProgressBar("FlowField Static Bake", initialLabel, 0f);
            }

            internal void Stop()
            {
                if (_cleaned)
                    return;
                _cleaned = true;
                EditorApplication.update -= Pump;
                _cancel = null;
                _asyncPending = false;
                if (_session != null)
                {
                    _session.FieldCommitted -= Complete;
                    _session.Failed -= Failed;
                    _session.Dispose();
                }
                _disposeExtra?.Invoke();
                EditorUtility.ClearProgressBar();
                _progressLabel = string.Empty;
                if (!_processingQueue)
                    ScheduleNextOrStop();
            }

            private bool IsStale()
            {
                if (_generation != _callbackGeneration)
                    return true;
                if (_inputIsCurrent())
                    return false;
                Debug.LogWarning($"{_name} input changed; existing asset was preserved.", _manager);
                return true;
            }

            private void Pump()
            {
                if (_cleaned)
                    return;
                try
                {
                    if (IsStale())
                    {
                        Stop();
                        return;
                    }
                    if (_session == null)
                    {
                        Prepare();
                        return;
                    }
                    _pump(_session);
                    if (!_cleaned && _session.IsFaulted)
                        Failed(_session.Fault ?? new InvalidOperationException($"{_name} failed."));
                }
                catch (OperationCanceledException)
                {
                    Debug.Log($"{_name} cancelled.");
                    Stop();
                }
                catch (Exception exception)
                {
                    Failed(exception);
                }
            }

            private void Complete()
            {
                try
                {
                    if (!IsStale())
                        _save(_session);
                }
                finally
                {
                    Stop();
                }
            }

            private void Failed(Exception exception)
            {
                try
                {
                    if (_generation == _callbackGeneration && !_shuttingDown)
                        Debug.LogError($"[{nameof(FlowFieldManager)}] {_name} failed: {exception?.Message}", _manager);
                }
                finally
                {
                    Stop();
                }
            }
        }

        // What differs between the two modes' bake assets when one is saved.
        private readonly struct BakeTarget
        {
            internal readonly string Snapshot;
            internal readonly string ModeName;
            internal readonly string CompleteLabel;
            internal readonly Func<FlowFieldManager, string> ResolvePath;
            internal readonly Func<FlowFieldManagerConfig, UnityEngine.Object> Assigned;

            internal BakeTarget(
                string snapshot,
                string modeName,
                string completeLabel,
                Func<FlowFieldManager, string> resolvePath,
                Func<FlowFieldManagerConfig, UnityEngine.Object> assigned)
            {
                Snapshot = snapshot;
                ModeName = modeName;
                CompleteLabel = completeLabel;
                ResolvePath = resolvePath;
                Assigned = assigned;
            }
        }

        private static readonly BakeTarget SurfaceTarget = new BakeTarget(
            "Static",
            "Surface2D",
            "Static Flow Bake",
            FlowFieldBakeAssetUtility.ResolveStaticAssetPath,
            config => config.BakeAssets.Surface);

        private static readonly BakeTarget VolumeTarget = new BakeTarget(
            "Volume",
            "Volume3D",
            "Volume3D Static Bake",
            FlowFieldBakeAssetUtility.ResolveVolumeAssetPath,
            config => config.BakeAssets.Volume);

        private static void SaveSurfaceBake(
            FlowFieldManager manager,
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldSession session)
            => SaveBakeAsset<FlowFieldStaticBakeData>(
                manager,
                SurfaceTarget,
                () =>
                {
                    if (!session.Surface.TryExportBakeData(out FlowFieldSurfaceData committedSurface, out FlowFieldSurfaceWorkspace workspace))
                        throw new InvalidOperationException("Static Flow Bake produced no committed field.");
                    if (manager.CaptureConfig().HasGoal && (!workspace.HasActiveGoal || workspace.ResolvedGoalIndex < 0))
                        throw new InvalidOperationException(
                            "Cannot bake a Surface2D Goal without a walkable resolved Goal cell.");
                    return asset =>
                    {
                        bool hasGoal = workspace.HasActiveGoal && workspace.ResolvedGoalIndex >= 0;
                        FlowFieldSurfaceGoalResolution goal = manager.ResolveConfiguredGoal(settings.Grid);
                        FlowFieldManagerConfig config = manager.CaptureConfig();
                        asset.Apply(settings, committedSurface, config.Obstacles.Layer,
                            config.Surface.ObstacleCheckHeight, config.Surface.ObstacleCheckCenterOffset,
                            config.Obstacles.Clearance, hasGoal, goal.RequestedWorld,
                            goal.InfluenceRadius, hasGoal ? workspace.ResolvedGoalIndex : -1, workspace);
                    };
                },
                (target, asset) => target.AssignStaticBakeData(asset));

        private static void SaveVolumeBake(FlowFieldManager manager, FlowFieldSession session)
            => SaveBakeAsset<FlowFieldVolumeBakeData>(
                manager,
                VolumeTarget,
                () =>
                {
                    if (!session.Volume.TryExportBakeData(
                            out FlowFieldVolumeRequest committedRequest,
                            out bool[] blocked,
                            out uint[] topology,
                            out FlowFieldGoalFlags[] goalFlags,
                            out int[] next,
                            out Vector3[] directions,
                            out float[] speeds,
                            out Vector3[] escapeDirections,
                            out int resolvedGoalIndex))
                        throw new InvalidOperationException("Volume3D bake produced no committed field.");
                    return asset =>
                    {
                        FlowFieldManagerConfig config = manager.CaptureConfig();
                        asset.Apply(
                            committedRequest.Grid,
                            committedRequest.WorldBounds,
                            config.Obstacles.Layer,
                            config.Obstacles.Clearance,
                            committedRequest.HasGoal,
                            committedRequest.GoalWorld,
                            committedRequest.GoalInfluenceRadius,
                            resolvedGoalIndex,
                            blocked,
                            topology,
                            goalFlags,
                            next,
                            directions,
                            speeds,
                            escapeDirections);
                    };
                },
                (target, asset) => target.AssignVolumeBakeData(asset));

        /// <summary>
        /// Writes a bake into this Manager's asset as one Undo step and assigns it.
        /// A failure reverts that step and removes an asset it created.
        /// </summary>
        private static void SaveBakeAsset<TAsset>(
            FlowFieldManager manager,
            in BakeTarget target,
            Func<Action<TAsset>> export,
            Action<FlowFieldManager, TAsset> assign)
            where TAsset : ScriptableObject
        {
            int undoGroup = -1;
            string assetPath = null;
            bool createdAsset = false;
            try
            {
                // Read the published field before any asset changes.
                Action<TAsset> apply = export();
                assetPath = target.ResolvePath(manager);
                FlowFieldBakeAssetUtility.CreateBakeFolder();
                // A failed bake reverts down to this group, so it must hold nothing older.
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName($"Bake FlowField {target.Snapshot} Snapshot");
                TAsset asset = AssetDatabase.LoadAssetAtPath<TAsset>(assetPath);
                FlowFieldBakeAssetUtility.ValidateTargetOwnership(
                    target.Assigned(manager.CaptureConfig()),
                    asset,
                    AssetDatabase.LoadMainAssetAtPath(assetPath),
                    assetPath,
                    target.ModeName);
                if (asset == null)
                {
                    asset = ScriptableObject.CreateInstance<TAsset>();
                    createdAsset = true;
                    asset.name = Path.GetFileNameWithoutExtension(assetPath);
                    AssetDatabase.CreateAsset(asset, assetPath);
                    Undo.RegisterCreatedObjectUndo(asset, $"Create FlowField {target.Snapshot} Snapshot");
                }
                Undo.RecordObject(asset, $"Bake FlowField {target.Snapshot} Snapshot");
                apply(asset);
                EditorUtility.SetDirty(asset);
                Undo.RecordObject(manager, $"Assign FlowField {target.Snapshot} Snapshot");
                assign(manager, asset);
                FlowFieldEditorPreview.Invalidate(manager);
                EditorUtility.SetDirty(manager);
                if (manager.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                FlowFieldBakeAssetUtility.Save(asset);
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log($"[{nameof(FlowFieldManager)}] {target.CompleteLabel} complete: {assetPath}", manager);
            }
            catch (Exception exception)
            {
                if (undoGroup >= 0)
                {
                    try { Undo.RevertAllDownToGroup(undoGroup); }
                    catch (Exception undoException) { Debug.LogException(undoException, manager); }
                }
                if (createdAsset && !string.IsNullOrEmpty(assetPath)
                    && AssetDatabase.LoadAssetAtPath<TAsset>(assetPath) != null)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
                Debug.LogException(exception, manager);
            }
        }

        private static bool ReportSurfaceProgress(int row, int rowCount)
        {
            if (_shuttingDown)
                return false;
            _progressLabel = $"Baking Surface row {row + 1}/{rowCount}";
            float progress = rowCount <= 0 ? 0f : (float)row / rowCount;
            return !EditorUtility.DisplayCancelableProgressBar(
                "FlowField Static Bake",
                _progressLabel,
                progress);
        }

        private static bool ReportVolumeProgress(float progress)
        {
            if (_shuttingDown)
                return false;
            progress = Mathf.Clamp01(progress);
            _progressLabel = $"Baking Volume3D {progress:P0}";
            return !EditorUtility.DisplayCancelableProgressBar(
                "FlowField Static Bake",
                _progressLabel,
                progress);
        }

        private static bool IsCurrentVolumeInput(
            FlowFieldManager manager,
            in FlowFieldVolumeRequest request,
            int bakeInputGeneration)
        {
            if (manager == null
                || manager.BakeMode != FlowFieldBakeMode.StaticBaked
                || manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                return false;
            try
            {
                // Capture changes observed by the editor even when a
                // serialized property was changed and then changed back
                // between two OnValidate callbacks. The generation is
                // monotonic, so A -> B -> A is still rejected.
                if (FlowFieldBakeService.CaptureInputGeneration(manager) != bakeInputGeneration
                    || request.BakeInputGeneration != bakeInputGeneration)
                    return false;
                if (!manager.TryGetVolumeLayout(out Bounds bounds, out FlowFieldGridSpace grid)
                    || !grid.MatchesBounds(request.Grid)
                    || !FlowFieldBakeBoundsUtility.Approximately(bounds, request.WorldBounds))
                    return false;
                FlowFieldManagerConfig config = manager.CaptureConfig();
                return config.Obstacles.Layer.value == request.ObstacleLayer.value
                    && Mathf.Abs(config.Obstacles.Clearance - request.ObstacleClearance) <= 0.0001f
                    && FlowFieldGridSpace.Approximately(
                        config.Flow.DefaultDirection,
                        request.DefaultDirection,
                        0.00000001d)
                    && config.HasGoal == request.HasGoal
                    && (!request.HasGoal
                        || FlowFieldGridSpace.Approximately(
                            config.GoalWorld,
                            request.GoalWorld,
                            0.00000001d)
                            && Mathf.Abs(config.Flow.GoalInfluenceRadius - request.GoalInfluenceRadius) <= 0.0001f);
            }
            catch
            {
                return false;
            }
        }

        private readonly struct SurfaceBakeInput
        {
            private readonly FlowFieldSurfaceBakeSettings _settings;
            private readonly int _generation;
            internal readonly LayerMask ObstacleLayer;
            internal readonly float CheckHeight;
            internal readonly float CheckCenterOffset;
            internal readonly float Clearance;
            private readonly bool _hasGoal;
            private readonly Vector3 _goalWorld;
            private readonly float _goalRadius;

            internal SurfaceBakeInput(FlowFieldManager manager, FlowFieldSurfaceBakeSettings settings)
            {
                _settings = settings;
                _generation = FlowFieldBakeService.CaptureInputGeneration(manager);
                FlowFieldManagerConfig config = manager.CaptureConfig();
                ObstacleLayer = config.Obstacles.Layer;
                CheckHeight = config.Surface.ObstacleCheckHeight;
                CheckCenterOffset = config.Surface.ObstacleCheckCenterOffset;
                Clearance = config.Obstacles.Clearance;
                _hasGoal = config.HasGoal;
                _goalWorld = config.GoalWorld;
                _goalRadius = config.Flow.GoalInfluenceRadius;
            }

            internal bool IsCurrent(FlowFieldManager manager)
            {
                if (manager == null || manager.BakeMode != FlowFieldBakeMode.StaticBaked)
                    return false;
                try
                {
                    if (FlowFieldBakeService.CaptureInputGeneration(manager) != _generation)
                        return false;
                    FlowFieldManagerConfig config = manager.CaptureConfig();
                    FlowFieldSurfaceBakeSettings current = manager.CreateSurfaceBakeSettings();
                    return current.IsValid && current.Grid.MatchesBounds(_settings.Grid)
                        && FlowFieldBakeBoundsUtility.Approximately(current.BakeBounds, _settings.BakeBounds)
                        && current.GroundLayer.value == _settings.GroundLayer.value
                        && Mathf.Abs(current.MaxSurfaceSlope - _settings.MaxSurfaceSlope) <= 0.0001f
                        && Mathf.Abs(current.MaxStepHeight - _settings.MaxStepHeight) <= 0.0001f
                        && config.Obstacles.Layer.value == ObstacleLayer.value
                        && Mathf.Abs(config.Surface.ObstacleCheckHeight - CheckHeight) <= 0.0001f
                        && Mathf.Abs(config.Surface.ObstacleCheckCenterOffset - CheckCenterOffset) <= 0.0001f
                        && Mathf.Abs(config.Obstacles.Clearance - Clearance) <= 0.0001f
                        && config.HasGoal == _hasGoal
                        && (!_hasGoal
                            || (config.GoalWorld - _goalWorld).sqrMagnitude <= 0.00000001f
                                && Mathf.Abs(config.Flow.GoalInfluenceRadius - _goalRadius) <= 0.0001f);
                }
                catch { return false; }
            }
        }

        public static void ClearReference(FlowFieldManager manager)
        {
            if (manager != null && manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (manager.CaptureConfig().BakeAssets.Volume == null)
                    return;
                Undo.RecordObject(manager, "Clear FlowField Volume Bake Reference");
                manager.AssignVolumeBakeData(null);
                FlowFieldEditorPreview.Invalidate(manager);
                EditorUtility.SetDirty(manager);
                if (manager.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                return;
            }
            if (manager == null || manager.CaptureConfig().BakeAssets.Surface == null)
                return;
            Undo.RecordObject(manager, "Clear FlowField Static Bake Reference");
            manager.AssignStaticBakeData(null);
            FlowFieldEditorPreview.Invalidate(manager);
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        }
    }

    internal static class FlowFieldBakeAssetUtility
    {
        // Replaces the save in a test, which cannot make a real disk write fail.
        internal static Action<UnityEngine.Object> SaveOverride;

        // AssetDatabase reports a failed write only in the Console. The Asset then
        // stays dirty, and a bake must not report success over the old file.
        internal static void Save(UnityEngine.Object asset)
        {
            if (SaveOverride != null)
                SaveOverride(asset);
            else
                AssetDatabase.SaveAssetIfDirty(asset);
            if (EditorUtility.IsDirty(asset))
                throw new IOException($"Could not save {AssetDatabase.GetAssetPath(asset)}.");
        }

        private const string BAKE_DIRECTORY = "Assets/_FlowField/Settings";

        internal static string ResolveStaticAssetPath(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new InvalidOperationException("Scene을 먼저 저장해야 합니다.");
            if (manager.CaptureConfig().BakeAssets.Surface != null)
            {
                string assignedPath = AssetDatabase.GetAssetPath(manager.CaptureConfig().BakeAssets.Surface);
                if (!string.IsNullOrEmpty(assignedPath))
                    return assignedPath;
            }
            return ResolveStablePath(manager, "StaticBake");
        }

        internal static string ResolveVolumeAssetPath(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new InvalidOperationException("Scene을 먼저 저장해야 합니다.");
            if (manager.CaptureConfig().BakeAssets.Volume != null)
            {
                string assignedPath = AssetDatabase.GetAssetPath(manager.CaptureConfig().BakeAssets.Volume);
                if (!string.IsNullOrEmpty(assignedPath))
                    return assignedPath;
            }
            return ResolveStablePath(manager, "VolumeBake");
        }

        private static string ResolveStablePath(FlowFieldManager manager, string suffix)
        {
            ValidateFileName(manager.name);
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string managerId = id.targetObjectId != 0
                ? id.targetObjectId.ToString()
                : manager.GetInstanceID().ToString();
            string sceneGuid = AssetDatabase.AssetPathToGUID(manager.gameObject.scene.path);
            if (string.IsNullOrEmpty(sceneGuid))
                throw new InvalidOperationException("씬 GUID를 확인할 수 없습니다. 씬을 저장한 뒤 다시 시도하세요.");
            return $"{BAKE_DIRECTORY}/{sceneGuid}_{managerId}_{manager.name}_{suffix}.asset";
        }

        internal static void CreateBakeFolder()
        {
            string[] parts = BAKE_DIRECTORY.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    throw new InvalidOperationException($"Unable to create bake folder '{next}'.");
                current = next;
            }
        }

        internal static void ValidateTargetOwnership(
            UnityEngine.Object assignedAsset,
            UnityEngine.Object typedAsset,
            UnityEngine.Object existingAsset,
            string assetPath,
            string modeName)
        {
            if (existingAsset != null && typedAsset == null)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' already exists and is not assigned to this Manager. "
                    + "Choose a new asset or explicitly assign the existing asset before baking.");
            // With no reference, the path is this Manager's own, as after Clear Bake.
            if (typedAsset != null && assignedAsset != null && assignedAsset != typedAsset)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' is shared by another reference. "
                    + "Create a separate Bake Asset before overwriting it.");
        }

        private static void ValidateFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".."
                || value.IndexOfAny(new[] { '/', '\\' }) >= 0
                || value.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("FlowField manager name must be a simple asset file name.", nameof(value));
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                if (value.IndexOf(invalid[i]) >= 0)
                    throw new ArgumentException("FlowField manager name contains an invalid character.", nameof(value));
        }
    }
}
