using UnityEditor;

namespace Common.FlowField.Editor
{
    [CustomEditor(typeof(FlowFieldDisplaySettings))]
    internal sealed class FlowFieldDisplaySettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var settings = (FlowFieldDisplaySettings)target;
            var manager = settings.GetComponent<FlowFieldManager>();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_showField"));
            if (manager != null && manager.CaptureConfig().SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                SerializedProperty gizmoMode = serializedObject.FindProperty("_volumeGizmoMode");
                EditorGUILayout.PropertyField(gizmoMode);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_showVolumeCells"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_showVolumeVectors"));
                if (gizmoMode.enumValueIndex == (int)FlowFieldVolumeGizmoMode.Slice)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_volumeGizmoSliceAxis"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_volumeGizmoSlice"));
                }
            }
            if (serializedObject.ApplyModifiedProperties() && manager != null)
            {
                FlowFieldVolumeVisualizationEditor.RequestRefresh(manager);
                SceneView.RepaintAll();
            }
        }
    }
}
