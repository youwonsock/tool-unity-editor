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
            // Keep the mode immutable while a play-mode Init session is
            // active. The rest of the serialized settings retain Unity's
            // default inspector rendering.
            SerializedProperty spaceMode = serializedObject.FindProperty("_spaceMode");
            bool isVolume = spaceMode != null
                && spaceMode.enumValueIndex == (int)FlowFieldSpaceMode.Volume3D;
            DrawPropertiesExcluding(
                serializedObject,
                isVolume
                    ? new[]
                    {
                        "_bakeMode",
                        "_spaceMode",
                        "_staticBakeData",
                        "_volumeStaticBakeData",
                        "_groundBakeLayer",
                        "_maxSurfaceSlope",
                        "_maxStepHeight",
                        "_obstacleCheckHeight",
                        "_obstacleCheckCenterOffset",
                        "_refreshRate",
                        "_showField",
                        "_volumeGizmoMode",
                        "_showVolumeCells",
                        "_showVolumeVectors",
                        "_volumeGizmoSliceAxis",
                        "_volumeGizmoSlice",
                    }
                    : new[]
                    {
                        "_bakeMode",
                        "_spaceMode",
                        "_staticBakeData",
                        "_volumeStaticBakeData",
                        "_showField",
                        "_volumeGizmoMode",
                        "_showVolumeCells",
                        "_showVolumeVectors",
                        "_volumeGizmoSliceAxis",
                        "_volumeGizmoSlice",
                    });
            using (new EditorGUI.DisabledScope(Application.isPlaying && manager.IsInitialized))
                EditorGUILayout.PropertyField(spaceMode);
            SerializedProperty mode = serializedObject.FindProperty("_bakeMode");
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                EditorGUILayout.PropertyField(mode);
            if (mode != null && mode.enumValueIndex == (int)FlowFieldBakeMode.StaticBaked)
            {
                if (spaceMode != null && spaceMode.enumValueIndex == (int)FlowFieldSpaceMode.Volume3D)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_volumeStaticBakeData"));
                else
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_staticBakeData"));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_showField"));
            if (isVolume)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_volumeGizmoMode"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_showVolumeCells"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_showVolumeVectors"));
                SerializedProperty gizmoMode = serializedObject.FindProperty("_volumeGizmoMode");
                if (gizmoMode != null
                    && gizmoMode.enumValueIndex == (int)FlowFieldVolumeGizmoMode.Slice)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_volumeGizmoSliceAxis"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_volumeGizmoSlice"));
                }
            }
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            bool exceedsCellLimit = DrawBakeLayout(manager);
            DrawBakeStatus(manager);
            bool disableBakeActions = FlowFieldSurfaceBakeEditor.IsBaking || exceedsCellLimit;
            if (manager.BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (FlowFieldSurfaceBakeEditor.IsBaking)
                {
                    EditorGUILayout.HelpBox(
                        string.IsNullOrEmpty(FlowFieldSurfaceBakeEditor.ProgressLabel)
                            ? "Static Flow Bake is running."
                            : FlowFieldSurfaceBakeEditor.ProgressLabel,
                        MessageType.Info);
                    if (GUILayout.Button("Cancel Static Bake"))
                        FlowFieldSurfaceBakeEditor.CancelBake();
                }
                using (new EditorGUI.DisabledScope(disableBakeActions))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Bake / ReBake Static Flow Field"))
                            FlowFieldSurfaceBakeEditor.ScheduleBake(manager);

                        bool hasBakeAsset = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                            ? manager.VolumeStaticBakeData != null
                            : manager.StaticBakeData != null;
                        using (new EditorGUI.DisabledScope(!hasBakeAsset))
                        {
                            if (GUILayout.Button("Clear Bake"))
                                FlowFieldSurfaceBakeEditor.ClearReference(manager);
                        }
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "RuntimeDynamic: Surface와 장애물은 실행 중 공통 Session이 계산합니다. Static Bake Asset은 사용하지 않습니다.",
                    MessageType.Info);
            }

            DrawTransformWarning(manager);
        }

        private void OnSceneGUI()
        {
            if (Application.isPlaying)
                return;

            var manager = (FlowFieldManager)target;
            if (manager == null)
                return;
            Bounds worldBounds;
            FlowFieldGridSpace unusedGrid;
            bool validLayout = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? manager.TryGetVolumeLayout(out worldBounds, out unusedGrid)
                : manager.TryGetBakeLayout(out worldBounds, out unusedGrid);
            if (!validLayout)
                return;

            bool validBounds = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldVolumeVisualizationEditor.TryGetBoundsValidity(manager, out bool volumeValid)
                    && volumeValid
                : manager.TryValidateSurfaceBake(out _);
            Color color = validBounds ? ValidBoundsColor : StaleBoundsColor;
            Color previousHandlesColor = Handles.color;
            Handles.color = color;
            EditorGUI.BeginChangeCheck();
            Vector3 movedCenter = Handles.PositionHandle(worldBounds.center, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Bounds moved = manager.BakeBoundsLocal;
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
                Bounds snapped = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                    ? FlowFieldBakeBoundsUtility.SnapVolumeResizedKeepingOppositeFace(
                        manager.BakeBoundsLocal,
                        candidateLocal,
                        manager.CellSize)
                    : FlowFieldBakeBoundsUtility.SnapResizedKeepingOppositeFace(
                        manager.BakeBoundsLocal,
                        candidateLocal,
                        manager.CellSize);
                ApplyBoundsChange(manager, snapped, "Resize FlowField Bake Bounds");
            }

            Handles.color = previousHandlesColor;
        }

        private static bool DrawBakeLayout(FlowFieldManager manager)
        {
            bool validLayout = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? manager.TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
                : manager.TryGetBakeLayout(out worldBounds, out grid);
            if (!validLayout)
            {
                EditorGUILayout.HelpBox("Bake Bounds 또는 Cell Size가 유효하지 않습니다.", MessageType.Error);
                return false;
            }

            string dimensions = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? $"{grid.Width} × {grid.Height} × {grid.Depth}"
                : $"{grid.Width} × {grid.Depth}";
            EditorGUILayout.LabelField("Computed Grid", $"{dimensions} ({grid.CellCount:N0} cells)");
            EditorGUILayout.LabelField("World Y Range", $"{worldBounds.min.y:0.###} → {worldBounds.max.y:0.###}");
            int maxCells = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldBakeBoundsUtility.MaxVolumeCellCount
                : FlowFieldBakeBoundsUtility.MaxSurfaceCellCount;
            if (grid.CellCount <= maxCells)
                return false;

            EditorGUILayout.HelpBox(
                $"Cell Count가 상한({maxCells:N0})을 초과합니다. "
                + "Bake Bounds 또는 Cell Size를 줄이세요.",
                MessageType.Error);
            return true;
        }

        private static void ApplyBoundsChange(
            FlowFieldManager manager,
            Bounds localBounds,
            string undoName)
        {
            Undo.RecordObject(manager, undoName);
            manager.SetBakeBoundsLocal(localBounds);
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
                if (manager.TryGetVolumeLayout(out _, out FlowFieldGridSpace displayGrid))
                {
                    string displayMode = manager.VolumeGizmoMode == FlowFieldVolumeGizmoMode.FullVolume
                        ? "FullVolume"
                        : $"Slice {manager.VolumeGizmoSliceAxis}={manager.VolumeGizmoSlice}";
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
                        int limit = manager.VolumeGizmoMode == FlowFieldVolumeGizmoMode.FullVolume
                            ? FlowFieldVolumeDisplaySelection.DefaultFullVolumeLimit
                            : FlowFieldVolumeDisplaySelection.DefaultSliceLimit;
                        if (volumeStatus.SelectedCellCount < displayGrid.CellCount)
                            EditorGUILayout.HelpBox(
                                $"표시 상한 {limit:N0}에 맞춰 격자 셀을 균등 추출합니다. "
                                + "실제 계산 해상도는 변경되지 않습니다.",
                                MessageType.None);
                    }
                }
                return;
            }
            if (manager.TryValidateSurfaceBake(out string reason))
            {
                bool hasStaticAsset = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                    ? manager.VolumeStaticBakeData != null
                    : manager.StaticBakeData != null;
                if (manager.BakeMode == FlowFieldBakeMode.StaticBaked && hasStaticAsset)
                {
                    EditorGUILayout.HelpBox(
                        "Static Flow Bake is valid.",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                            ? "Runtime Dynamic fills the configured Bounds with Volume3D cells."
                            : "Runtime Dynamic uses a fresh downward raycast Surface bake.",
                        MessageType.Info);
                }
                return;
            }

            EditorGUILayout.HelpBox(reason, MessageType.Error);
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
                "FlowField Grid는 회전 없는 월드 XZ 정렬과 Scale 1만 지원합니다. "
                + "Manager 또는 부모 Transform의 회전/스케일을 확인하세요.",
                MessageType.Warning);
        }
    }
}
