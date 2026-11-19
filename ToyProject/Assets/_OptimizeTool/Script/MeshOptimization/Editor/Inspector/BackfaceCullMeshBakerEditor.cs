using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    [CustomEditor(typeof(BackfaceCullMeshBaker))]
    public sealed class BackfaceCullMeshBakerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var baker = (BackfaceCullMeshBaker)target;
            EditorGUILayout.HelpBox("Bake in Edit Mode. Each MeshFilter gets a saved copy of its mesh with only the triangles facing the view, which permanently removes geometry for that view. Baking again starts from the recorded original. Restore Original Meshes puts the originals back; saved mesh assets are kept.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Bake and Save Backfaces")) InspectorCommand.Run(baker, () => baker.BakeBackfaces());
                using (new EditorGUI.DisabledScope(baker.OriginalMeshes.Count == 0))
                    if (GUILayout.Button("Restore Original Meshes")) InspectorCommand.Run(baker, baker.RestoreOriginalMeshes);
            }
        }
    }
}
