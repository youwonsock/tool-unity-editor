using System;
using System.Collections.Generic;
using System.IO;
using Common.OptimizeTool.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.PhysicsRecording.Editor
{
    /// <summary>
    /// Saves a take as its clip, playback prefab and, when enabled, reference. Either every output is saved
    /// and linked, or the previous files are restored.
    /// </summary>
    internal static class PhysicsRecordingAssetWriter
    {
        /// <summary>Rejects an output path that exists without overwrite, or that holds another kind of asset.</summary>
        internal static void ValidateTargets(in PhysicsTakeSettings settings)
        {
            AssetFileTransaction.ValidateOutputTarget(settings.ClipPath, typeof(AnimationClip), settings.Overwrite);
            AssetFileTransaction.ValidateOutputTarget(settings.PrefabPath, typeof(GameObject), settings.Overwrite);
            if (settings.SaveReference)
                AssetFileTransaction.ValidateOutputTarget(settings.ReferencePath, typeof(PhysicsRecordingReference), settings.Overwrite);
        }

        internal static PhysicsRecordingAssets Write(Transform root, PhysicsTake take, in PhysicsTakeSettings settings)
        {
            // Outputs may have changed since the take started.
            ValidateTargets(settings);
            AnimationClip clip = null;
            PhysicsRecordingReference reference = null;
            Scene previewScene = default;
            try
            {
                // Build everything in memory first; a failure here leaves the disk untouched.
                clip = PhysicsClipBuilder.Build(take, settings.ClipName, settings.PositionTolerance, settings.RotationTolerance);
                previewScene = EditorSceneManager.NewPreviewScene();
                GameObject playback = CreatePlaybackCopy(root, previewScene, settings.ClipName);
                clip.SampleAnimation(playback, 0f);
                if (settings.SaveReference)
                {
                    reference = ScriptableObject.CreateInstance<PhysicsRecordingReference>();
                    reference.name = Path.GetFileNameWithoutExtension(settings.ReferencePath);
                }

                GameObject prefab = null;
                string name = settings.ClipName;
                string clipPath = settings.ClipPath, prefabPath = settings.PrefabPath, referencePath = settings.ReferencePath;
                bool deleteStaleReference = reference == null && HasStaleReference(settings);
                var outputs = new List<string> { clipPath, prefabPath };
                if (reference != null || deleteStaleReference) outputs.Add(referencePath);
                AssetFileTransaction.EnsureFolder(settings.Folder);
                AssetFileTransaction.Execute(outputs, () =>
                {
                    if (deleteStaleReference && !AssetDatabase.DeleteAsset(referencePath))
                        throw new IOException($"Unable to delete the earlier recording reference: {referencePath}");
                    clip = AssetFileTransaction.CreateOrReplace(clip, clipPath);
                    if (reference != null)
                    {
                        take.WriteTo(reference, clip);
                        reference = AssetFileTransaction.CreateOrReplace(reference, referencePath);
                    }
                    AttachAnimation(playback, clip, name);
                    PhysicsPlaybackPolicy.Validate(playback);
                    prefab = PrefabUtility.SaveAsPrefabAsset(playback, prefabPath, out bool saved);
                    if (!saved || prefab == null)
                        throw new InvalidOperationException($"Unable to save the recording prefab: {prefabPath}");
                    Animation savedAnimation = prefab.GetComponent<Animation>();
                    if (savedAnimation == null || savedAnimation.clip != clip)
                        throw new InvalidOperationException($"The saved prefab does not play the saved clip: {prefabPath}");
                    if (reference != null && reference.Clip != clip)
                        throw new InvalidOperationException($"The saved reference does not belong to the saved clip: {referencePath}");
                });
                return new PhysicsRecordingAssets(clip, prefab, reference);
            }
            finally
            {
                if (previewScene.IsValid())
                    EditorSceneManager.ClosePreviewScene(previewScene);
                if (clip != null && !EditorUtility.IsPersistent(clip))
                    Object.DestroyImmediate(clip);
                if (reference != null && !EditorUtility.IsPersistent(reference))
                    Object.DestroyImmediate(reference);
            }
        }

        /// <summary>
        /// A reference an earlier take saved for the clip this overwrite replaces. The clip keeps its GUID, so the
        /// old reference would still match it and audit the new clip against the old take; it is deleted with the save.
        /// </summary>
        private static bool HasStaleReference(in PhysicsTakeSettings settings)
        {
            if (settings.SaveReference || !settings.Overwrite)
                return false;
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(settings.ClipPath);
            var reference = AssetDatabase.LoadAssetAtPath<PhysicsRecordingReference>(settings.ReferencePath);
            return clip != null && reference != null && reference.Clip == clip;
        }

        private static GameObject CreatePlaybackCopy(Transform root, Scene previewScene, string name)
        {
            var staging = new GameObject("PhysicsRecorder Staging") { hideFlags = HideFlags.HideAndDontSave };
            staging.SetActive(false);
            SceneManager.MoveGameObjectToScene(staging, previewScene);
            // The inactive parent prevents cloned gameplay Awake/OnEnable callbacks.
            GameObject playback = Object.Instantiate(root.gameObject, staging.transform, false);
            playback.name = name;
            PhysicsPlaybackStripper.Strip(playback);
            return playback;
        }

        private static void AttachAnimation(GameObject playback, AnimationClip clip, string name)
        {
            Animation animation = playback.AddComponent<Animation>();
            animation.AddClip(clip, name);
            animation.clip = clip;
            animation.playAutomatically = true;
            animation.wrapMode = WrapMode.ClampForever;
            // Still under the inactive staging parent, so no callbacks run.
            playback.SetActive(true);
        }
    }
}
