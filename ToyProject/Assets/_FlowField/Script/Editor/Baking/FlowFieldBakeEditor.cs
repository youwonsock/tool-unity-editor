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
                Debug.Log("RuntimeDynamic은 Bake 에셋을 저장하지 않습니다. Surface는 런타임 세션이 다시 만듭니다.", manager);
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
                Debug.Log("FlowField Static Bake를 취소했습니다.");
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
                Debug.LogWarning($"{_name}: 입력이 바뀌어 기존 에셋을 그대로 두었습니다.", _manager);
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
                    Debug.Log($"{_name}: 취소했습니다.");
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
                        Debug.LogError($"[{nameof(FlowFieldManager)}] {_name} 실패: {exception?.Message}", _manager);
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
                Debug.Log($"[{nameof(FlowFieldManager)}] {target.Label} 완료: {assetPath}", manager);
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

    internal static class FlowFieldBakeAssetUtility
    {
        // AssetDatabase reports a failed write only in the Console. The Asset then
        // stays dirty, and a bake must not report success over the old file.
        internal static void Save(UnityEngine.Object asset)
        {
            AssetDatabase.SaveAssetIfDirty(asset);
            if (EditorUtility.IsDirty(asset))
                throw new IOException($"Could not save {AssetDatabase.GetAssetPath(asset)}.");
        }

        /// <summary>
        /// Where a bake writes: the assigned asset, or else this Manager's
        /// stable path next to its scene.
        /// </summary>
        internal static string ResolveAssetPath(FlowFieldManager manager, UnityEngine.Object assigned, string suffix)
        {
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new InvalidOperationException("Scene을 먼저 저장해야 합니다.");
            string assignedPath = assigned != null ? AssetDatabase.GetAssetPath(assigned) : null;
            return string.IsNullOrEmpty(assignedPath) ? ResolveStablePath(manager, suffix) : assignedPath;
        }

        private static string ResolveStablePath(FlowFieldManager manager, string suffix)
        {
            ValidateFileName(manager.name);
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string managerId = id.targetObjectId != 0
                ? id.targetObjectId.ToString()
                : manager.GetInstanceID().ToString();
            string scenePath = manager.gameObject.scene.path;
            string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(sceneGuid))
                throw new InvalidOperationException("씬 GUID를 확인할 수 없습니다. 씬을 저장한 뒤 다시 시도하세요.");
            // Next to the scene, in a folder named after it, as Unity keeps lightmaps.
            string sceneFolder = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            string bakeFolder = $"{sceneFolder}/{Path.GetFileNameWithoutExtension(scenePath)}";
            return $"{bakeFolder}/{sceneGuid}_{managerId}_{manager.name}_{suffix}.asset";
        }

        /// <summary>
        /// Creates the folders of a project asset path that do not exist yet.
        /// Returns the outermost folder it created, or null when all existed.
        /// </summary>
        internal static string CreateBakeFolder(string assetPath)
        {
            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder))
                throw new InvalidOperationException($"Bake target '{assetPath}' has no folder.");
            string[] parts = folder.Split('/');
            string current = parts[0];
            string created = null;
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    {
                        DeleteFolderIfEmpty(created);
                        throw new InvalidOperationException($"Unable to create bake folder '{next}'.");
                    }
                    created ??= next;
                }
                current = next;
            }
            return created;
        }

        // Project paths, not disk paths: a Packages/ path need not exist on disk under the project.
        internal static bool AssetExists(string assetPath)
            => !string.IsNullOrEmpty(assetPath)
                && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets));

        /// <summary>Deletes a folder a failed bake created, unless something else was put in it.</summary>
        internal static void DeleteFolderIfEmpty(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                return;
            // Every asset below it, subfolders included; only subfolders may remain.
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
            {
                if (!AssetDatabase.IsValidFolder(AssetDatabase.GUIDToAssetPath(guid)))
                    return;
            }
            AssetDatabase.DeleteAsset(folder);
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
