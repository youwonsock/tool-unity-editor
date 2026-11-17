#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool
{
    /// <summary>
    /// Saves a take as its clip, playback prefab and reference. Either all three are saved and linked,
    /// or the previous files are restored.
    /// </summary>
    internal static class PhysicsRecordingAssetWriter
    {
        internal readonly struct Assets
        {
            public AnimationClip Clip { get; }
            public GameObject Prefab { get; }
            public PhysicsRecordingReference Reference { get; }
            public Assets(AnimationClip clip, GameObject prefab, PhysicsRecordingReference reference)
            { Clip = clip; Prefab = prefab; Reference = reference; }
        }

        /// <summary>Rejects an output path that exists without overwrite, or that holds another kind of asset.</summary>
        internal static void ValidateTargets(in PhysicsTakeSettings settings)
        {
            ValidateTarget(settings.ClipPath, typeof(AnimationClip), settings.Overwrite);
            ValidateTarget(settings.PrefabPath, typeof(GameObject), settings.Overwrite);
            ValidateTarget(settings.ReferencePath, typeof(PhysicsRecordingReference), settings.Overwrite);
        }

        internal static Assets Write(Transform root, PhysicsTake take, in PhysicsTakeSettings settings)
        {
            // Outputs may have changed since the take started.
            ValidateTargets(settings);
            AnimationClip clip = null;
            PhysicsRecordingReference reference = null;
            Scene previewScene = default;
            try
            {
                // Build everything in memory first; a failure here leaves the disk untouched.
                clip = PhysicsClipBuilder.Build(take, settings.Name, settings.PositionTolerance, settings.RotationTolerance);
                previewScene = EditorSceneManager.NewPreviewScene();
                GameObject playback = CreatePlaybackCopy(root, previewScene, settings.Name);
                clip.SampleAnimation(playback, 0f);
                reference = ScriptableObject.CreateInstance<PhysicsRecordingReference>();
                reference.name = Path.GetFileNameWithoutExtension(settings.ReferencePath);

                GameObject prefab = null;
                string folder = settings.Folder, name = settings.Name;
                string clipPath = settings.ClipPath, prefabPath = settings.PrefabPath, referencePath = settings.ReferencePath;
                AssetFileTransaction.EnsureFolder(folder);
                AssetFileTransaction.Execute(new[] { clipPath, prefabPath, referencePath }, () =>
                {
                    clip = AssetFileTransaction.CreateOrReplace(clip, clipPath);
                    take.WriteTo(reference, clip);
                    reference = AssetFileTransaction.CreateOrReplace(reference, referencePath);
                    AttachAnimation(playback, clip, name);
                    PhysicsPlaybackPolicy.Validate(playback);
                    prefab = PrefabUtility.SaveAsPrefabAsset(playback, prefabPath, out bool saved);
                    if (!saved || prefab == null)
                        throw new InvalidOperationException($"Unable to save the recording prefab: {prefabPath}");
                    Animation savedAnimation = prefab.GetComponent<Animation>();
                    if (savedAnimation == null || savedAnimation.clip != clip)
                        throw new InvalidOperationException($"The saved prefab does not play the saved clip: {prefabPath}");
                    if (reference.Clip != clip)
                        throw new InvalidOperationException($"The saved reference does not belong to the saved clip: {referencePath}");
                });
                return new Assets(clip, prefab, reference);
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

        private static GameObject CreatePlaybackCopy(Transform root, Scene previewScene, string name)
        {
            var staging = new GameObject("PhysicsRecorder Staging") { hideFlags = HideFlags.HideAndDontSave };
            staging.SetActive(false);
            SceneManager.MoveGameObjectToScene(staging, previewScene);
            // The inactive parent prevents cloned gameplay Awake/OnEnable callbacks.
            GameObject playback = Object.Instantiate(root.gameObject, staging.transform, false);
            playback.name = name;
            PhysicsPlaybackPolicy.Strip(playback);
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

        private static void ValidateTarget(string path, Type type, bool overwrite)
        {
            Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing == null && !File.Exists(path) && !Directory.Exists(path))
                return;
            if (!overwrite)
                throw new InvalidOperationException($"Recording output already exists: {path}");
            if (existing == null || existing.GetType() != type || AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException($"Recording output path is occupied by an incompatible asset: {path}");
        }
    }
}
#endif
