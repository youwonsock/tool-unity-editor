#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.Editor
{
    [CustomEditor(typeof(MeshCombiner))]
    public sealed class MeshCombinerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            foreach (string warning in ((MeshCombiner)target).LastBuildWarnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            EditorGUILayout.HelpBox("Bake in Edit Mode. Camera culling permanently removes geometry for this view. Scene output assigns materials and can disable source Renderers; colliders are retained.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Combine and Save Meshes"))
                {
                    try { ((MeshCombiner)target).CombineAndSaveMeshes(); }
                    catch (Exception exception) { Debug.LogException(exception, target); }
                }
        }
    }

    [CustomEditor(typeof(BackfaceCullMeshBaker))]
    public sealed class BackfaceCullMeshBakerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Bake in Edit Mode. All results are calculated before replacing MeshFilters. Undo restores mesh references and overwritten mesh data. New asset files are retained after Undo.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Bake and Save Backfaces"))
                {
                    try { ((BackfaceCullMeshBaker)target).BakeBackFaceCullingMesh(); }
                    catch (Exception exception) { Debug.LogException(exception, target); }
                }
        }
    }
}
#endif
