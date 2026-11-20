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
            EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldDisplaySettings.PropertyNames.ShowField));
            if (manager != null && manager.CaptureConfig().SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                SerializedProperty gizmoMode = serializedObject.FindProperty(FlowFieldDisplaySettings.PropertyNames.VolumeGizmoMode);
                EditorGUILayout.PropertyField(gizmoMode);
                EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldDisplaySettings.PropertyNames.ShowVolumeCells));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldDisplaySettings.PropertyNames.ShowVolumeVectors));
                if (gizmoMode.enumValueIndex == (int)FlowFieldVolumeGizmoMode.Slice)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldDisplaySettings.PropertyNames.VolumeGizmoSliceAxis));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(FlowFieldDisplaySettings.PropertyNames.VolumeGizmoSlice));
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
