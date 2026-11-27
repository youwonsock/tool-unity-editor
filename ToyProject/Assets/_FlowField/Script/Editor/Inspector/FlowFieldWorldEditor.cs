using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// The world asset's settings, then its bake: what the last bake holds
    /// and the buttons that bake the Bake Goals across the open scenes and
    /// clear the bake. Both run in Edit Mode only.
    /// </summary>
    [CustomEditor(typeof(FlowFieldWorld))]
    internal sealed class FlowFieldWorldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var world = (FlowFieldWorld)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("World Bake", EditorStyles.boldLabel);
            string status = FlowFieldWorldBaker.Describe(world, out MessageType type);
            EditorGUILayout.HelpBox(status, type);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || targets.Length != 1))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake"))
                {
                    if (FlowFieldWorldBaker.TryBake(world, out string message))
                        Debug.Log($"[FlowField] {world.name}: {message}", world);
                    else
                        Debug.LogWarning($"[FlowField] {world.name}: {message}", world);
                }
                using (new EditorGUI.DisabledScope(world.Bake == null || world.Bake.GoalCount == 0))
                {
                    if (GUILayout.Button("Clear Bake"))
                        FlowFieldWorldBaker.Clear(world);
                }
            }
        }
    }
}
