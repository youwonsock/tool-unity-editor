using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Editor
{
    [CustomEditor(typeof(FlowFieldManager))]
    internal sealed class FlowFieldManagerEditor : UnityEditor.Editor
    {
        // The open Manager Inspectors. The Volume3D display repaints them when
        // the status they show changes, and leaves the other windows alone.
        private static readonly List<FlowFieldManagerEditor> Open = new List<FlowFieldManagerEditor>();

        private readonly BoxBoundsHandle _boundsHandle = new BoxBoundsHandle();
        // This Manager's bake owner id, read once: GetGlobalObjectIdSlow is slow.
        // Null until the scene is saved.
        private string _ownerId;

        private void OnEnable()
        {
            Open.Add(this);
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(target);
            _ownerId = id.targetObjectId != 0 ? id.ToString() : null;
        }

        private void OnDisable() => Open.Remove(this);

        internal static void RepaintOpen()
        {
            foreach (FlowFieldManagerEditor editor in Open)
                editor.Repaint();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var manager = (FlowFieldManager)target;
            // The mode stays fixed while a play-mode Init session is active,
            // so the settings shown are the ones that mode reads, even while
            // a Space Mode edit waits for Release. Settings only the other
            // mode reads are hidden.
            SerializedProperty spaceMode = serializedObject.FindProperty(FlowFieldManager.PropertyNames.SpaceMode);
            bool isVolume = manager.SpaceMode == FlowFieldSpaceMode.Volume3D;
            DrawSettings(FlowFieldManager.PropertyNames.Grid, "Grid");
            if (!isVolume)
                DrawSettings(FlowFieldManager.PropertyNames.Surface, "Surface");
            DrawSettings(FlowFieldManager.PropertyNames.Obstacles, "Obstacles");
            DrawAgentTypes(manager, isVolume);
            DrawSettings(FlowFieldManager.PropertyNames.Flow, "Flow");
            EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldManager.PropertyNames.GoalTransform));
            DrawWorld(manager);

            // The Mode header comes from the field's [Header] attribute.
            // SetSpaceMode refuses the same states: Release first.
            bool sessionHoldsMode = Application.isPlaying && (manager.IsInitialized || manager.IsFaulted);
            using (new EditorGUI.DisabledScope(sessionHoldsMode))
                EditorGUILayout.PropertyField(spaceMode);
            if (sessionHoldsMode && spaceMode != null && spaceMode.enumValueIndex != (int)manager.SpaceMode)
                EditorGUILayout.HelpBox(
                    manager.IsFaulted
                        ? $"The session keeps {manager.SpaceMode} until Release, and a retry refuses the edited Space Mode; Release, then Init to apply it."
                        : $"The session keeps {manager.SpaceMode} until Release; the edited Space Mode applies at the next Init.",
                    MessageType.Info);
            SerializedProperty mode = serializedObject.FindProperty(FlowFieldManager.PropertyNames.BakeMode);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                EditorGUILayout.PropertyField(mode);
            if (mode != null && FlowFieldBakeModes.UsesBakeAsset((FlowFieldBakeMode)mode.enumValueIndex))
            {
                SerializedProperty bakeAsset = serializedObject.FindProperty(isVolume
                    ? FlowFieldManager.PropertyNames.VolumeBakeData
                    : FlowFieldManager.PropertyNames.SurfaceBakeData);
                EditorGUILayout.PropertyField(bakeAsset);
                if (_ownerId != null
                    && bakeAsset.objectReferenceValue is FlowFieldBakeAsset assigned
                    && assigned.OwnerId != _ownerId)
                    EditorGUILayout.HelpBox(
                        "This Bake Asset was not baked by this Manager, for example after duplicating a Manager. "
                        + "Bake writes this Manager's own asset and assigns it; the assigned one stays as it is.",
                        MessageType.Info);
            }
            serializedObject.ApplyModifiedProperties();
            DrawDisplaySettingsNotice(manager);
            DrawDuplicatePriorities(manager);

            EditorGUILayout.Space();
            bool exceedsCellLimit = DrawBakeLayout(manager);
            DrawBakeStatus(manager);
            // Bakes change Assets and the scene, so Play Mode keeps them off.
            bool disableBakeActions = FlowFieldBakeEditor.IsBaking || exceedsCellLimit || Application.isPlaying;
            // The status above already shows why these settings cannot bake.
            bool disableBake = disableBakeActions || !manager.TryValidateBakeSettings(out _);
            if (FlowFieldBakeModes.UsesBakeAsset(manager.BakeMode))
            {
                if (Application.isPlaying)
                    EditorGUILayout.HelpBox("Bake and Clear Bake run in Edit Mode.", MessageType.None);
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
                            ? "FlowField Bake is running."
                            : FlowFieldBakeEditor.ProgressLabel,
                        MessageType.Info);
                    if (GUILayout.Button("Cancel Bake"))
                        FlowFieldBakeEditor.CancelBake();
                }
                using (new EditorGUI.DisabledScope(disableBakeActions))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(disableBake))
                        {
                            if (GUILayout.Button("Bake / ReBake FlowField"))
                                FlowFieldBakeEditor.ScheduleBake(manager);
                        }

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
                    "RuntimeDynamic computes the field at runtime and uses no Bake Asset.",
                    MessageType.Info);
            }
        }

        // A settings bundle drawn as a header and its fields, without a foldout.
        // The world the Manager joins at Init; it stays until Release.
        private void DrawWorld(FlowFieldManager manager)
        {
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(Application.isPlaying && (manager.IsInitialized || manager.IsFaulted)))
                EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldManager.PropertyNames.World));
            if (!manager.TryValidateWorldLattice(out string reason))
                EditorGUILayout.HelpBox(reason, MessageType.Warning);
        }

        // The settings above are the Default agent type; each element adds one.
        // A new type starts from the Manager's own values. A session holds the
        // number and names from Init to Release, so they are locked then.
        private void DrawAgentTypes(FlowFieldManager manager, bool isVolume)
        {
            SerializedProperty types = serializedObject.FindProperty(FlowFieldManager.PropertyNames.AgentTypes);
            if (types == null)
                return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Additional Agent Types", EditorStyles.boldLabel);
            bool locked = Application.isPlaying && (manager.IsInitialized || manager.IsFaulted);
            for (int i = 0; i < types.arraySize; i++)
            {
                SerializedProperty element = types.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(locked))
                    {
                        EditorGUILayout.PropertyField(element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.Name)));
                        if (GUILayout.Button("Remove", GUILayout.Width(64)))
                        {
                            types.DeleteArrayElementAtIndex(i);
                            break;
                        }
                    }
                }
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.Clearance)));
                if (!isVolume)
                {
                    EditorGUILayout.PropertyField(element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.MaxSlope)));
                    EditorGUILayout.PropertyField(element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.MaxStepHeight)));
                }
                EditorGUI.indentLevel--;
            }
            using (new EditorGUI.DisabledScope(locked || types.arraySize >= FlowFieldManager.MaxAdditionalAgentTypes))
            {
                if (GUILayout.Button("Add Agent Type"))
                    AddAgentType(types);
            }
            if (locked)
                EditorGUILayout.HelpBox("The number and names of agent types change after Release.", MessageType.None);
            if (types.arraySize > 0 && manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                EditorGUILayout.HelpBox(
                    "StaticBaked loads the Default agent type only; Init refuses Additional Agent Types.",
                    MessageType.Warning);
            else if (types.arraySize > 0 && manager.BakeMode == FlowFieldBakeMode.BakedBase)
                EditorGUILayout.HelpBox(
                    "The bake holds the Default agent type; each Additional Agent Type scans the scene at runtime.",
                    MessageType.Info);
        }

        private void AddAgentType(SerializedProperty types)
        {
            int index = types.arraySize;
            types.arraySize = index + 1;
            SerializedProperty element = types.GetArrayElementAtIndex(index);
            element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.Name)).stringValue = "Agent " + (index + 1);
            element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.Clearance)).floatValue = serializedObject
                .FindProperty(FlowFieldManager.PropertyNames.Obstacles + "." + nameof(FlowFieldObstacleSettings.Clearance)).floatValue;
            element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.MaxSlope)).floatValue = serializedObject
                .FindProperty(FlowFieldManager.PropertyNames.Surface + "." + nameof(FlowFieldSurfaceSettings.MaxSlope)).floatValue;
            element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.MaxStepHeight)).floatValue = serializedObject
                .FindProperty(FlowFieldManager.PropertyNames.Surface + "." + nameof(FlowFieldSurfaceSettings.MaxStepHeight)).floatValue;
        }

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
            Color color = validBounds ? FlowFieldGizmoDrawer.ValidBakeBoundsColor : FlowFieldGizmoDrawer.StaleBakeBoundsColor;
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
                Bounds snapped = FlowFieldSpaceStrategy.For(manager.SpaceMode).SnapResized(
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
            int maxCells = FlowFieldSpaceStrategy.For(manager.SpaceMode).MaxCellCount;
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

        // A handle nudge smaller than a cell snaps back to the same bounds and
        // leaves the Manager and its scene as they were.
        private static void ApplyBoundsChange(
            FlowFieldManager manager,
            Bounds localBounds,
            string undoName)
        {
            if (!manager.SetBakeBoundsLocal(localBounds, undoName))
                return;
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
                if (volumeStatus.Code == FlowFieldVolumeVisualizationStatusCode.Validating)
                {
                    Rect bar = GUILayoutUtility.GetRect(18f, 18f, "TextField");
                    EditorGUI.ProgressBar(bar, volumeStatus.ValidationProgress, $"Validating the bake {volumeStatus.ValidationProgress:P0}");
                }
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
                    if (volumeStatus.HasSelection)
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
                EditorGUILayout.HelpBox("The Bake Asset matches the Manager settings.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("RuntimeDynamic computes the field at runtime and uses no Bake Asset.", MessageType.Info);
            if (FlowFieldEditorPreview.TryGetFault(manager, out string previewFault))
                EditorGUILayout.HelpBox($"The Edit Mode preview failed: {previewFault}", MessageType.Warning);
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

    }
}
