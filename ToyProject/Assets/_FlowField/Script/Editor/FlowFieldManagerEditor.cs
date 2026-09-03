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
            // Keep the mode immutable while a play-mode Init session is
            // active. The rest of the serialized settings retain Unity's
            // default inspector rendering.
            DrawPropertiesExcluding(
                serializedObject,
                "_bakeMode",
                "_staticBakeData");
            SerializedProperty mode = serializedObject.FindProperty("_bakeMode");
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                EditorGUILayout.PropertyField(mode);
            if (mode != null && mode.enumValueIndex == (int)FlowFieldBakeMode.StaticBaked)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_staticBakeData"));
            serializedObject.ApplyModifiedProperties();
            var manager = (FlowFieldManager)target;

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

                        using (new EditorGUI.DisabledScope(manager.StaticBakeData == null))
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
            if (manager == null || !manager.TryGetBakeLayout(out Bounds worldBounds, out _))
                return;

            Color color = manager.TryValidateSurfaceBake(out _)
                ? ValidBoundsColor
                : StaleBoundsColor;
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
                Bounds snapped = FlowFieldBakeBoundsUtility.SnapResizedKeepingOppositeFace(
                    manager.BakeBoundsLocal,
                    candidateLocal,
                    manager.CellSize);
                ApplyBoundsChange(manager, snapped, "Resize FlowField Bake Bounds");
            }

            Handles.color = previousHandlesColor;
        }

        private static bool DrawBakeLayout(FlowFieldManager manager)
        {
            if (!manager.TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
            {
                EditorGUILayout.HelpBox("Bake Bounds 또는 Cell Size가 유효하지 않습니다.", MessageType.Error);
                return false;
            }

            EditorGUILayout.LabelField("Computed Grid", $"{grid.Width} × {grid.Depth} ({grid.CellCount:N0} cells)");
            EditorGUILayout.LabelField("World Y Range", $"{worldBounds.min.y:0.###} → {worldBounds.max.y:0.###}");
            if (grid.CellCount <= FlowFieldBakeBoundsUtility.MaxCellCount)
                return false;

            EditorGUILayout.HelpBox(
                $"Cell Count가 상한({FlowFieldBakeBoundsUtility.MaxCellCount:N0})을 초과합니다. "
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
            if (manager.TryValidateSurfaceBake(out string reason))
            {
                if (manager.BakeMode == FlowFieldBakeMode.StaticBaked && manager.StaticBakeData != null)
                {
                    EditorGUILayout.HelpBox(
                        "Static Flow Bake is valid.",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Runtime Dynamic uses a fresh downward raycast Surface bake.",
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
