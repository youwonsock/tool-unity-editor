#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool
{
    [Serializable]
    internal sealed class MeshSourceRendererState
    {
        public Renderer Renderer;
        public bool Enabled;
    }

    /// <summary>Undo-aware scene application, independent from geometry construction and asset paths.</summary>
    internal static class MeshSceneOutput
    {
        internal static GameObject Replace(Transform parent, string name, GameObject previous,
            Mesh[] meshes, MeshBuildResult result, bool disableSources, List<MeshSourceRendererState> sourceStates)
        {
            var previousStates = new Dictionary<Renderer, MeshSourceRendererState>();
            var nextSources = disableSources ? new HashSet<Renderer>(result.SourceRenderers) : null;
            // Sources retained by this output remain disabled. Preserve their original state
            // without a temporary enable/disable pair or redundant renderer Undo records.
            foreach (MeshSourceRendererState state in sourceStates)
                if (state.Renderer != null)
                {
                    previousStates[state.Renderer] = state;
                    if (nextSources == null || !nextSources.Contains(state.Renderer)) SetEnabled(state.Renderer, state.Enabled);
                }
            sourceStates.Clear();
            if (previous != null) Undo.DestroyObjectImmediate(previous);
            var root = new GameObject(name + " Results");
            Undo.RegisterCreatedObjectUndo(root, "Create combined result");
            root.transform.SetParent(parent, false);
            for (int i = 0; i < meshes.Length; i++)
            {
                var output = new GameObject(meshes[i].name, typeof(MeshFilter), typeof(MeshRenderer));
                Undo.RegisterCreatedObjectUndo(output, "Create combined renderer");
                output.transform.SetParent(root.transform, false);
                output.GetComponent<MeshFilter>().sharedMesh = meshes[i];
                MeshRenderer renderer = output.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = result.Parts[i].Materials;
                result.RendererSettings.ApplyTo(renderer);
            }
            if (disableSources)
                foreach (Renderer renderer in result.SourceRenderers)
                {
                    if (previousStates.TryGetValue(renderer, out MeshSourceRendererState state)) sourceStates.Add(state);
                    else sourceStates.Add(new MeshSourceRendererState { Renderer = renderer, Enabled = renderer.enabled });
                    SetEnabled(renderer, false);
                }
            return root;
        }

        private static void SetEnabled(Renderer renderer, bool enabled)
        {
            if (renderer.enabled == enabled) return;
            Undo.RegisterCompleteObjectUndo(renderer, "Restore source renderer state");
            renderer.enabled = enabled;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }
    }
}
#endif
