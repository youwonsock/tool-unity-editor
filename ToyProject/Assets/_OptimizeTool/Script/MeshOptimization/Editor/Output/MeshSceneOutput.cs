using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>
    /// The scene side of a combine: the generated result objects and the source Renderers the combiner disabled.
    /// Every change goes through a <see cref="MeshSceneEdit"/>, so Edit Mode records it with Undo and as a Prefab
    /// instance override, and Play Mode can put it back without Undo.
    /// </summary>
    internal static class MeshSceneOutput
    {
        /// <summary>
        /// Replaces the previous result objects with renderers for <paramref name="meshes"/> and disables the sources
        /// when the combiner asks for it. Sources the previous bake disabled and that are still inputs stay disabled
        /// and keep their recorded original state; dropped sources get their original state back.
        /// </summary>
        internal static void Apply(MeshCombiner combiner, Mesh[] meshes, MeshBuildResult result, MeshSceneEdit edit)
        {
            RecordCombiner(combiner, edit, "Assign combined result");
            // Undo can serialize the owner while Destroy/RegisterCreatedObjectUndo runs. Build the new
            // ledger on the side and assign it only after every scene operation has completed.
            var states = new List<MeshSourceRendererState>();
            bool disableSources = combiner.DisableSourceRenderers;
            var nextSources = disableSources ? new HashSet<Renderer>(result.SourceRenderers) : null;
            var previousStates = new Dictionary<Renderer, MeshSourceRendererState>();
            // Sources retained by this output remain disabled. Preserve their original state
            // without a temporary enable/disable pair or redundant renderer Undo records.
            foreach (MeshSourceRendererState state in combiner.SourceRendererStates)
                if (state.Renderer != null)
                {
                    previousStates[state.Renderer] = state;
                    if (nextSources == null || !nextSources.Contains(state.Renderer)) SetEnabled(state.Renderer, state.Enabled, edit);
                }
            // A Temporary edit only hides the previous result until the save succeeds, so a failure can bring it back.
            if (combiner.GeneratedRoot != null) edit.Remove(combiner.GeneratedRoot);
            GameObject root = CreateResults(combiner.transform, combiner.MeshName, meshes, result, edit);
            if (disableSources)
                foreach (Renderer renderer in result.SourceRenderers)
                {
                    if (previousStates.TryGetValue(renderer, out MeshSourceRendererState state)) states.Add(state);
                    else states.Add(new MeshSourceRendererState { Renderer = renderer, Enabled = renderer.enabled });
                    SetEnabled(renderer, false, edit);
                }
            // Recorded again after the result objects exist, so Redo restores the reference to them.
            edit.Record(combiner, "Assign combined result", () => { });
            combiner.GeneratedRoot = root;
            combiner.SourceRendererStates = states;
            edit.Changed(combiner);
        }

        /// <summary>
        /// Removes the result objects and gives every recorded source its original enabled state. Saved mesh assets
        /// are kept.
        /// </summary>
        internal static void Restore(MeshCombiner combiner, MeshSceneEdit edit)
        {
            RequireReplaceable(combiner.GeneratedRoot);
            RecordCombiner(combiner, edit, "Restore combined sources");
            foreach (MeshSourceRendererState state in combiner.SourceRendererStates)
                if (state.Renderer != null) SetEnabled(state.Renderer, state.Enabled, edit);
            if (combiner.GeneratedRoot != null) edit.Remove(combiner.GeneratedRoot);
            combiner.GeneratedRoot = null;
            combiner.SourceRendererStates = new List<MeshSourceRendererState>();
            edit.Changed(combiner);
        }

        /// <summary>Both operations replace the root and the ledger as a whole, so keeping the old ones reverts them.</summary>
        private static void RecordCombiner(MeshCombiner combiner, MeshSceneEdit edit, string undoName)
        {
            GameObject root = combiner.GeneratedRoot;
            List<MeshSourceRendererState> states = combiner.SourceRendererStates;
            edit.Record(combiner, undoName, () =>
            {
                combiner.GeneratedRoot = root;
                combiner.SourceRendererStates = states;
            });
        }

        /// <summary>
        /// Fails before any change when the result objects belong to a Prefab instance, where they cannot be destroyed.
        /// Result objects added to an instance as an override can be.
        /// </summary>
        internal static void RequireReplaceable(GameObject root)
        {
            if (root != null && PrefabUtility.IsPartOfPrefabInstance(root) && !PrefabUtility.IsAddedGameObjectOverride(root))
                throw new InvalidOperationException($"The combined output '{root.name}' is part of a Prefab instance and cannot be replaced here. Open the Prefab or unpack the instance first.");
        }

        /// <summary>
        /// A disabled Renderer the combiner did not disable itself. A source an earlier bake disabled is still an input.
        /// </summary>
        internal static bool IsDisabledByUser(MeshCombiner combiner, MeshRenderer renderer)
        {
            if (renderer == null || renderer.enabled) return false;
            foreach (MeshSourceRendererState state in combiner.SourceRendererStates)
                if (state.Renderer == renderer) return !state.Enabled;
            return true;
        }

        private static GameObject CreateResults(Transform parent, string name, Mesh[] meshes, MeshBuildResult result,
            MeshSceneEdit edit)
        {
            var root = new GameObject(name + " Results");
            edit.Created(root, "Create combined result");
            root.transform.SetParent(parent, false);
            for (int i = 0; i < meshes.Length; i++)
            {
                var output = new GameObject(meshes[i].name, typeof(MeshFilter), typeof(MeshRenderer));
                edit.Created(output, "Create combined renderer");
                output.transform.SetParent(root.transform, false);
                output.GetComponent<MeshFilter>().sharedMesh = meshes[i];
                result.RendererSettings.ApplyTo(output.GetComponent<MeshRenderer>(), result.Parts[i].Materials);
            }
            return root;
        }

        private static void SetEnabled(Renderer renderer, bool enabled, MeshSceneEdit edit)
        {
            if (renderer.enabled == enabled) return;
            bool previous = renderer.enabled;
            edit.Record(renderer, "Restore source renderer state", () =>
            {
                if (renderer != null) renderer.enabled = previous;
            });
            renderer.enabled = enabled;
            edit.Changed(renderer);
        }
    }
}
