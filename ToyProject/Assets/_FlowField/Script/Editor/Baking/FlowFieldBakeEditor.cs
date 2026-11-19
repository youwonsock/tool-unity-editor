using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Editor adapter for the shared build session. It owns only queueing,
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
        /// callback cannot write an Asset after the user cancels.
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
                    && FlowFieldBakeModes.UsesBakeAsset(manager.BakeMode))
                    _queue.Enqueue(manager.GetInstanceID());
            }
            _isBaking = true;
            EditorApplication.delayCall += ProcessQueue;
        }

        public static void ScheduleBake(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (!FlowFieldBakeModes.UsesBakeAsset(manager.BakeMode))
            {
                Debug.Log("RuntimeDynamic saves no bake asset. The runtime session builds the Surface again.", manager);
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
                Debug.Log("FlowField Static Bake was cancelled.");
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
            if (!FlowFieldBakeModes.UsesBakeAsset(manager.BakeMode))
                return;

            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                BakeVolumeAndAssign(manager);
                return;
            }

            FlowFieldSurfaceBakeSettings settings = manager.CreateSurfaceBakeSettings();
            int inputGeneration = FlowFieldBakeService.CaptureInputGeneration(manager);
            FlowFieldSurfaceBakeJob bakeJob = new FlowFieldSurfaceBakeJob(
                settings,
                ReportSurfaceProgress);
            var run = new BakeRun(
                manager,
                SurfaceTarget.Label,
                () => IsCurrentInput(manager, inputGeneration),
                session => session.Pump(),
                session => SaveSurfaceBake(manager, settings, session),
                bakeJob.Dispose);
            // The ground scan runs first; the session starts on its result.
            run.Prepare = () => StepSurfaceGroundScan(run, manager, settings, bakeJob);
            run.Start("Baking Surface");
        }

        // One editor frame of the ground scan. True once the session has started.
        private static bool StepSurfaceGroundScan(
            BakeRun run,
            FlowFieldManager manager,
            FlowFieldSurfaceBakeSettings settings,
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
            // The inputs are those the bake started with: a change stops it first.
            FlowFieldManagerConfig config = manager.CaptureConfig();
            session.Surface.Submit(FlowFieldSurfaceBuilder.CreateRequest(
                in config,
                FlowFieldBakeMode.RuntimeDynamic,
                in settings,
                FlowFieldSurfaceDirtyFlags.All,
                FlowFieldCellRect.Full(settings.Grid),
                FlowFieldCellRect.Full(settings.Grid)));
            session.Pump();
            return true;
        }

        private static void BakeVolumeAndAssign(FlowFieldManager manager)
        {
            FlowFieldSession session = FlowFieldBakeService.CreateVolumeBakeSession(manager);
            int inputGeneration = FlowFieldBakeService.CaptureInputGeneration(manager);
            var run = new BakeRun(
                manager,
                VolumeTarget.Label,
                () => IsCurrentInput(manager, inputGeneration),
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
                Debug.LogWarning($"{_name}: the inputs changed, so the existing asset was kept.", _manager);
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
                    Debug.Log($"{_name}: cancelled.");
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

        // What differs between the two modes' bake assets when one is saved or cleared.
        private sealed class BakeTarget
        {
            internal readonly string Snapshot;
            internal readonly string ModeName;
            internal readonly string Label;
            // The stable file name ends with it: <...>_<FileSuffix>.asset.
            internal readonly string FileSuffix;
            internal readonly string PropertyPath;
            internal readonly Func<FlowFieldManagerConfig, UnityEngine.Object> Assigned;

            internal BakeTarget(
                string snapshot,
                string modeName,
                string label,
                string fileSuffix,
                string propertyPath,
                Func<FlowFieldManagerConfig, UnityEngine.Object> assigned)
            {
                Snapshot = snapshot;
                ModeName = modeName;
                Label = label;
                FileSuffix = fileSuffix;
                PropertyPath = propertyPath;
                Assigned = assigned;
            }

            internal static BakeTarget For(FlowFieldSpaceMode mode)
                => mode == FlowFieldSpaceMode.Volume3D ? VolumeTarget : SurfaceTarget;
        }

        private static readonly BakeTarget SurfaceTarget = new BakeTarget(
            "Surface",
            "Surface2D",
            "Static Flow Bake",
            "SurfaceBake",
            FlowFieldManager.PropertyNames.SurfaceBakeData,
            config => config.BakeAssets.Surface);

        private static readonly BakeTarget VolumeTarget = new BakeTarget(
            "Volume",
            "Volume3D",
            "Volume3D Static Flow Bake",
            "VolumeBake",
            FlowFieldManager.PropertyNames.VolumeBakeData,
            config => config.BakeAssets.Volume);

        private static void SaveSurfaceBake(
            FlowFieldManager manager,
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldSession session)
            => SaveBakeAsset<FlowFieldSurfaceBakeData>(
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
                });

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
                });

        /// <summary>
        /// Writes a bake into this Manager's asset as one Undo step and assigns it.
        /// A failure reverts that step and removes an asset it created.
        /// </summary>
        private static void SaveBakeAsset<TAsset>(
            FlowFieldManager manager,
            BakeTarget target,
            Func<Action<TAsset>> export)
            where TAsset : ScriptableObject
        {
            int undoGroup = -1;
            string assetPath = null;
            string createdFolder = null;
            bool createdAsset = false;
            try
            {
                // Read the published field before any asset changes.
                Action<TAsset> apply = export();
                assetPath = FlowFieldBakeAssetUtility.ResolveAssetPath(
                    manager,
                    target.Assigned(manager.CaptureConfig()),
                    target.FileSuffix);
                createdFolder = FlowFieldBakeAssetUtility.CreateBakeFolder(assetPath);
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
                manager.AssignBakeAsset(target.PropertyPath, asset);
                FlowFieldEditorPreview.Invalidate(manager);
                EditorUtility.SetDirty(manager);
                if (manager.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                FlowFieldBakeAssetUtility.Save(asset);
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log($"[{nameof(FlowFieldManager)}] {target.Label} finished: {assetPath}", manager);
            }
            catch (Exception exception)
            {
                if (undoGroup >= 0)
                {
                    try { Undo.RevertAllDownToGroup(undoGroup); }
                    catch (Exception undoException) { Debug.LogException(undoException, manager); }
                }
                // Cleanup must not hide the bake's own error.
                try
                {
                    // The Undo revert above destroys the created object but
                    // leaves its asset, so ask the AssetDatabase, not the object.
                    if (createdAsset && FlowFieldBakeAssetUtility.AssetExists(assetPath))
                        AssetDatabase.DeleteAsset(assetPath);
                    FlowFieldBakeAssetUtility.DeleteFolderIfEmpty(createdFolder);
                }
                catch (Exception cleanupException) { Debug.LogException(cleanupException, manager); }
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

        // The bake goes on only with the inputs it started with. The generation
        // advances whenever they differ from the previous call, and the bake
        // asks every frame.
        private static bool IsCurrentInput(FlowFieldManager manager, int inputGeneration)
            => manager != null && FlowFieldBakeService.CaptureInputGeneration(manager) == inputGeneration;

        internal static bool HasBakeAsset(FlowFieldManager manager)
            => manager != null && BakeTarget.For(manager.SpaceMode).Assigned(manager.CaptureConfig()) != null;

        public static void ClearReference(FlowFieldManager manager)
        {
            if (!HasBakeAsset(manager))
                return;
            BakeTarget target = BakeTarget.For(manager.SpaceMode);
            Undo.RecordObject(manager, $"Clear FlowField {target.Snapshot} Bake Reference");
            manager.AssignBakeAsset(target.PropertyPath, null);
            FlowFieldEditorPreview.Invalidate(manager);
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        }
    }
}
