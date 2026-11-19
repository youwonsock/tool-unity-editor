using System;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    [CustomEditor(typeof(MeshCombiner))]
    public sealed class MeshCombinerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var combiner = (MeshCombiner)target;
            foreach (string warning in combiner.LastBuildWarnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            EditorGUILayout.HelpBox("Bake in Edit Mode. Camera culling permanently removes geometry for this view. Scene output assigns materials and can disable source Renderers; colliders are retained. Restore Sources removes the scene output and re-enables the Renderers this component disabled; saved mesh assets are kept.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Combine and Save Meshes")) Run(combiner.CombineAndSaveMeshes);
                using (new EditorGUI.DisabledScope(combiner.GeneratedRoot == null && combiner.SourceRendererStates.Count == 0))
                    if (GUILayout.Button("Restore Sources")) Run(combiner.RestoreSources);
            }
        }

        private void Run(Action command)
        {
            try { command(); }
            catch (Exception exception) { Debug.LogException(exception, target); }
        }

        private void Run(Func<Mesh[]> command) => Run(() => { command(); });
    }
}
