using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Editor
{
    [CustomEditor(typeof(FlowFieldManager))]
    internal sealed class FlowFieldManagerEditor : UnityEditor.Editor
    {
        private static readonly Color ValidBoundsColor = new Color(0.15f, 0.85f, 1f, 0.9f);
        private static readonly Color StaleBoundsColor = new Color(1f, 0.55f, 0.1f, 0.9f);
        private readonly BoxBoundsHandle _boundsHandle = new BoxBoundsHandle();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var manager = (FlowFieldManager)target;
            // The mode stays fixed while a play-mode Init session is active.
            // Settings only the other mode reads are hidden.
            SerializedProperty spaceMode = serializedObject.FindProperty(FlowFieldManager.PropertyNames.SpaceMode);
            bool isVolume = spaceMode != null
                && spaceMode.enumValueIndex == (int)FlowFieldSpaceMode.Volume3D;
            DrawSettings(FlowFieldManager.PropertyNames.Grid, "Grid");
            if (!isVolume)
                DrawSettings(FlowFieldManager.PropertyNames.Surface, "Surface");
            DrawSettings(FlowFieldManager.PropertyNames.Obstacles, "Obstacles");
            DrawSettings(FlowFieldManager.PropertyNames.Flow, "Flow");
            EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldManager.PropertyNames.GoalTransform));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(Application.isPlaying && manager.IsInitialized))
                EditorGUILayout.PropertyField(spaceMode);
            SerializedProperty mode = serializedObject.FindProperty(FlowFieldManager.PropertyNames.BakeMode);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                EditorGUILayout.PropertyField(mode);
            if (mode != null && FlowFieldBakeModes.UsesBakeAsset((FlowFieldBakeMode)mode.enumValueIndex))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(isVolume
                    ? FlowFieldManager.PropertyNames.VolumeBakeData
                    : FlowFieldManager.PropertyNames.SurfaceBakeData));
            }
            serializedObject.ApplyModifiedProperties();
            DrawDisplaySettingsNotice(manager);
            DrawDuplicatePriorities(manager);

            EditorGUILayout.Space();
            bool exceedsCellLimit = DrawBakeLayout(manager);
            DrawBakeStatus(manager);
            bool disableBakeActions = FlowFieldBakeEditor.IsBaking || exceedsCellLimit;
            if (FlowFieldBakeModes.UsesBakeAsset(manager.BakeMode))
            {
                if (manager.BakeMode == FlowFieldBakeMode.BakedBase)
                    EditorGUILayout.HelpBox(
                        "BakedBase: the bake supplies the Surface and obstacles, and the session solves the Goal "
                        + "and the obstacle changes it is told about at runtime. A scene obstacle moved while no one "
                        + "observes it keeps its baked cells blocked, so bake again after moving it in the Editor.",
                        MessageType.Info);
                if (FlowFieldBakeEditor.IsBaking)
                {
                    EditorGUILayout.HelpBox(
                        string.IsNullOrEmpty(FlowFieldBakeEditor.ProgressLabel)
                            ? "Static Flow Bake is running."
                            : FlowFieldBakeEditor.ProgressLabel,
                        MessageType.Info);
                    if (GUILayout.Button("Cancel Static Bake"))
                        FlowFieldBakeEditor.CancelBake();
                }
                using (new EditorGUI.DisabledScope(disableBakeActions))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Bake / ReBake Static Flow Field"))
                            FlowFieldBakeEditor.ScheduleBake(manager);

                        using (new EditorGUI.DisabledScope(!FlowFieldBakeEditor.HasBakeAsset(manager)))
                        {
                            if (GUILayout.Button("Clear Bake"))
                                FlowFieldBakeEditor.ClearReference(manager);
                        }
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "RuntimeDynamic: the session computes the Surface and obstacles at runtime. No Static Bake Asset is used.",
                    MessageType.Info);
            }

            DrawTransformWarning(manager);
        }

        // A settings bundle drawn as a header and its fields, without a foldout.
        private void DrawSettings(string path, string header)
        {
            SerializedProperty bundle = serializedObject.FindProperty(path);
            if (bundle == null)
                return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
            SerializedProperty end = bundle.GetEndProperty();
            SerializedProperty child = bundle.Copy();
            for (bool enter = true; child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end); enter = false)
                EditorGUILayout.PropertyField(child, true);
        }

        private void OnSceneGUI()
        {
            if (Application.isPlaying)
                return;

            var manager = (FlowFieldManager)target;
            if (manager == null)
                return;
            if (!manager.TryGetLayout(out Bounds worldBounds, out _))
                return;

            bool validBounds = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldVolumeVisualizationEditor.TryGetBoundsValidity(manager, out bool volumeValid)
                    && volumeValid
                : manager.TryValidateConfig(out _);
            Color color = validBounds ? ValidBoundsColor : StaleBoundsColor;
            Color previousHandlesColor = Handles.color;
            Handles.color = color;
            EditorGUI.BeginChangeCheck();
            Vector3 movedCenter = Handles.PositionHandle(worldBounds.center, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Bounds moved = manager.CaptureConfig().Grid.BakeBoundsLocal;
                moved.center = movedCenter - manager.transform.position;
                ApplyBoundsChange(manager, moved, "Move FlowField Bake Bounds");
                Handles.color = previousHandlesColor;
                return;
            }

            _boundsHandle.center = worldBounds.center;
            _boundsHandle.size = worldBounds.size;
            _boundsHandle.handleColor = color;
            _boundsHandle.wireframeColor = color;
            EditorGUI.BeginChangeCheck();
            _boundsHandle.DrawHandle();
            if (EditorGUI.EndChangeCheck())
            {
                var candidateWorld = new Bounds(_boundsHandle.center, _boundsHandle.size);
                var candidateLocal = new Bounds(
                    candidateWorld.center - manager.transform.position,
                    candidateWorld.size);
                FlowFieldManagerConfig config = manager.CaptureConfig();
                Bounds snapped = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                    ? FlowFieldLayoutUtility.SnapVolumeResizedKeepingOppositeFace(
                        config.Grid.BakeBoundsLocal,
                        candidateLocal,
                        config.Grid.CellSize)
                    : FlowFieldLayoutUtility.SnapResizedKeepingOppositeFace(
                        config.Grid.BakeBoundsLocal,
                        candidateLocal,
                        config.Grid.CellSize);
                ApplyBoundsChange(manager, snapped, "Resize FlowField Bake Bounds");
            }

            Handles.color = previousHandlesColor;
        }

        private static bool DrawBakeLayout(FlowFieldManager manager)
        {
            bool validLayout = manager.TryGetLayout(out Bounds worldBounds, out FlowFieldGridSpace grid);
            int maxCells = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldLayoutUtility.MaxVolumeCellCount
                : FlowFieldLayoutUtility.MaxSurfaceCellCount;
            if (!validLayout)
            {
                // The layout also fails above the cell limit.
                EditorGUILayout.HelpBox(
                    $"Bake Bounds or Cell Size is invalid, or the cell count exceeds the limit ({maxCells:N0}).",
                    MessageType.Error);
                return true;
            }

            string dimensions = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? $"{grid.Width} × {grid.Height} × {grid.Depth}"
                : $"{grid.Width} × {grid.Depth}";
            EditorGUILayout.LabelField("Computed Grid", $"{dimensions} ({grid.CellCount:N0} cells)");
            EditorGUILayout.LabelField("World Y Range", $"{worldBounds.min.y:0.###} → {worldBounds.max.y:0.###}");
            return false;
        }

        private static void ApplyBoundsChange(
            FlowFieldManager manager,
            Bounds localBounds,
            string undoName)
        {
            manager.SetBakeBoundsLocal(localBounds, undoName);
            FlowFieldEditorPreview.Invalidate(manager);
            FlowFieldVolumeVisualizationEditor.RequestRefresh(manager);
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            SceneView.RepaintAll();
        }

        private static void DrawBakeStatus(FlowFieldManager manager)
        {
            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (!FlowFieldVolumeVisualizationEditor.TryGetStatus(
                        manager,
                        out FlowFieldVolumeVisualizationStatus volumeStatus))
                    return;
                MessageType type = volumeStatus.Code == FlowFieldVolumeVisualizationStatusCode.Invalid
                    ? MessageType.Error
                    : volumeStatus.Code == FlowFieldVolumeVisualizationStatusCode.Valid
                        || volumeStatus.Code == FlowFieldVolumeVisualizationStatusCode.Rebuilding
                        ? MessageType.Info
                        : MessageType.Warning;
                EditorGUILayout.HelpBox(volumeStatus.Message, type);
                if (manager.TryGetLayout(out _, out FlowFieldGridSpace displayGrid))
                {
                    FlowFieldDisplay display = FlowFieldDisplay.Of(manager);
                    string displayMode = display.VolumeGizmoMode == FlowFieldVolumeGizmoMode.FullVolume
                        ? "FullVolume"
                        : $"Slice {display.VolumeGizmoSliceAxis}={display.VolumeGizmoSlice}";
                    EditorGUILayout.LabelField(
                        "Display Grid",
                        $"{displayGrid.Width} × {displayGrid.Height} × {displayGrid.Depth} "
                        + $"({displayGrid.CellCount:N0} cells, CellSize {displayGrid.CellSize:0.###})");
                    EditorGUILayout.LabelField(
                        "Volume Display",
                        $"{displayMode}: {volumeStatus.SelectedCellCount:N0} selected, "
                        + $"{volumeStatus.MovingCellCount:N0} moving, "
                        + $"{volumeStatus.StoppedCellCount:N0} stopped");
                    if (volumeStatus.IsSampled)
                    {
                        int limit = display.VolumeGizmoMode == FlowFieldVolumeGizmoMode.FullVolume
                            ? FlowFieldVolumeDisplaySelection.DefaultFullVolumeLimit
                            : FlowFieldVolumeDisplaySelection.DefaultSliceLimit;
                        if (volumeStatus.SelectedCellCount < displayGrid.CellCount)
                            EditorGUILayout.HelpBox(
                                $"The display samples grid cells evenly to stay within {limit:N0}. "
                                + "The computed resolution does not change.",
                                MessageType.None);
                    }
                }
                return;
            }
            // A Manager that loads a bake passes only with a matching asset.
            if (!manager.TryValidateConfig(out string reason))
                EditorGUILayout.HelpBox(reason, MessageType.Error);
            else if (FlowFieldBakeModes.UsesBakeAsset(manager.BakeMode))
                EditorGUILayout.HelpBox("Static Flow Bake is valid.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Runtime Dynamic uses a fresh downward raycast Surface bake.", MessageType.Info);
        }

        // Registration rejects a second Modifier with the same Priority.
        private static void DrawDuplicatePriorities(FlowFieldManager manager)
        {
            foreach (var group in FlowFieldModifierPriorities.FindDuplicatePriorities(manager))
                EditorGUILayout.HelpBox(FlowFieldModifierPriorities.Describe(group.Key, group.Value), MessageType.Warning);
        }

        // Display options live on their own component, which the Scene View reads.
        private static void DrawDisplaySettingsNotice(FlowFieldManager manager)
        {
            if (manager.GetComponent<FlowFieldDisplaySettings>() != null)
                return;
            EditorGUILayout.HelpBox(
                "Without Display Settings the Scene View draws no field.",
                MessageType.None);
            if (GUILayout.Button("Add Display Settings"))
                Undo.AddComponent<FlowFieldDisplaySettings>(manager.gameObject);
        }

        private static void DrawTransformWarning(FlowFieldManager manager)
        {
            Transform managerTransform = manager.transform;
            bool hasWorldRotation = Quaternion.Angle(managerTransform.rotation, Quaternion.identity) > 0.01f;
            bool hasUnsupportedScale = (managerTransform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f;
            if (!hasWorldRotation && !hasUnsupportedScale)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "The FlowField grid supports only an unrotated, world-aligned Manager with scale 1. "
                + "Check the rotation and scale of the Manager and its parents.",
                MessageType.Warning);
        }
    }
}
