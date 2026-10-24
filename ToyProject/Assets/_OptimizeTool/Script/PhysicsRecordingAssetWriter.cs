#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Common.OptimizeTool
{
    /// <summary>Commits clip, prefab and original observations with synchronous save and rollback semantics.</summary>
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

        private static AnimationClip BuildAnimationClip(PhysicsRecordedTake take, PhysicsRecordingSettings settings)
        {
            var clip = new AnimationClip
            {
                name = settings.Name, frameRate = 1f / settings.Interval,
                legacy = true, wrapMode = WrapMode.ClampForever,
            };
            try
            {
                foreach (PhysicsRecordedTrack track in take.Tracks)
                {
                    SetCurve(clip, track, "localPosition.x", p => p.Position.x);
                    SetCurve(clip, track, "localPosition.y", p => p.Position.y);
                    SetCurve(clip, track, "localPosition.z", p => p.Position.z);
                    SetCurve(clip, track, "localRotation.x", p => p.Rotation.x);
                    SetCurve(clip, track, "localRotation.y", p => p.Rotation.y);
                    SetCurve(clip, track, "localRotation.z", p => p.Rotation.z);
                    SetCurve(clip, track, "localRotation.w", p => p.Rotation.w);
                }
                clip.EnsureQuaternionContinuity();
                // Unity's continuity pass recalculates smooth quaternion tangents.
                // Reapply broken linear tangents so idle rotation spans remain flat.
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                var curves = new AnimationCurve[bindings.Length];
                for (int i = 0; i < bindings.Length; i++)
                {
                    curves[i] = AnimationUtility.GetEditorCurve(clip, bindings[i]);
                    SetLinearTangents(curves[i]);
                }
                AnimationUtility.SetEditorCurves(clip, bindings, curves);
                return clip;
            }
            catch { UnityEngine.Object.DestroyImmediate(clip); throw; }
        }

        private static void SetCurve(AnimationClip clip, PhysicsRecordedTrack track, string property, Func<PhysicsPoseSample, float> value)
        {
            var keys = new Keyframe[track.Keys.Count];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = new Keyframe(track.Keys[i].Time, value(track.Keys[i]));
            var curve = new AnimationCurve(keys);
            SetLinearTangents(curve);
            clip.SetCurve(track.Path, typeof(Transform), property, curve);
        }

        private static void SetLinearTangents(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyBroken(curve, i, true);
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
        }

        internal static Assets Write(Transform root, PhysicsRecordedTake take, PhysicsRecordingSettings settings, PhysicsRecordingMeasurements measurements)
        {
            ValidateOutputAssetTargets(settings);
            EnsureSaveFolder(settings.Folder);
            var backups = new[] { new AssetFileSnapshot(settings.ClipPath),
                new AssetFileSnapshot(settings.PrefabPath), new AssetFileSnapshot(settings.ReferencePath) };
            foreach (AssetFileSnapshot backup in backups) measurements.BackupBytes += backup.ContentBytes;
            long started = Stopwatch.GetTimestamp();
            AnimationClip clip = BuildAnimationClip(take, settings);
            measurements.CurveTicks = Stopwatch.GetTimestamp() - started;
            Scene previewScene = default;
            PhysicsRecordingReference reference = null;
            bool writingAssets = false;
            try
            {
                started = Stopwatch.GetTimestamp();
                previewScene = EditorSceneManager.NewPreviewScene();
                var staging = new GameObject("PhysicsRecorder Staging") { hideFlags = HideFlags.HideAndDontSave };
                staging.SetActive(false);
                SceneManager.MoveGameObjectToScene(staging, previewScene);
                // The inactive parent prevents cloned gameplay Awake/OnEnable callbacks.
                GameObject playback = UnityEngine.Object.Instantiate(root.gameObject, staging.transform, false);
                playback.name = settings.Name;
                StripSimulationComponents(playback);
                clip.SampleAnimation(playback, 0f);
                measurements.PrefabTicks = Stopwatch.GetTimestamp() - started;
                started = Stopwatch.GetTimestamp();
                writingAssets = true;
                AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(settings.ClipPath);
                if (existing != null)
                {
                    EditorUtility.CopySerialized(clip, existing);
                    UnityEngine.Object.DestroyImmediate(clip);
                    clip = existing;
                }
                else
                    AssetDatabase.CreateAsset(clip, settings.ClipPath);
                AssetDatabase.SaveAssetIfDirty(clip);
                reference = ScriptableObject.CreateInstance<PhysicsRecordingReference>();
                reference.name = Path.GetFileNameWithoutExtension(settings.ReferencePath);
                reference.Initialize(clip, take);
                PhysicsRecordingReference existingReference = AssetDatabase.LoadAssetAtPath<PhysicsRecordingReference>(settings.ReferencePath);
                if (existingReference != null)
                {
                    EditorUtility.CopySerialized(reference, existingReference);
                    UnityEngine.Object.DestroyImmediate(reference);
                    reference = existingReference;
                }
                else AssetDatabase.CreateAsset(reference, settings.ReferencePath);
                AssetDatabase.SaveAssetIfDirty(reference);
                Animation animation = playback.AddComponent<Animation>();
                animation.AddClip(clip, settings.Name);
                animation.clip = clip;
                animation.playAutomatically = true;
                animation.wrapMode = WrapMode.ClampForever;
                playback.SetActive(true);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(playback, settings.PrefabPath, out bool success);
                if (!success || prefab == null || AssetDatabase.LoadAssetAtPath<AnimationClip>(settings.ClipPath) == null
                    || AssetDatabase.LoadAssetAtPath<GameObject>(settings.PrefabPath) == null
                    || prefab.GetComponent<Animation>()?.clip != clip)
                    throw new InvalidOperationException("Unable to save and link both recording assets.");
                if (AssetDatabase.LoadAssetAtPath<PhysicsRecordingReference>(settings.ReferencePath) != reference
                    || reference.Clip != clip)
                    throw new InvalidOperationException("Unable to save and link the original recording observations.");
                measurements.SaveTicks = Stopwatch.GetTimestamp() - started;
                return new Assets(clip, prefab, reference);
            }
            catch (Exception saveError)
            {
                if (writingAssets)
                {
                    var errors = new List<Exception> { saveError };
                    foreach (AssetFileSnapshot backup in backups)
                        AssetFileSnapshot.TryRestore(backup.Restore, errors);
                    if (errors.Count > 1)
                        throw new AggregateException("Recording save and asset rollback failed.", errors);
                }
                throw;
            }
            finally
            {
                if (previewScene.IsValid())
                    EditorSceneManager.ClosePreviewScene(previewScene);
                if (clip != null && !EditorUtility.IsPersistent(clip))
                    UnityEngine.Object.DestroyImmediate(clip);
                if (reference != null && !EditorUtility.IsPersistent(reference))
                    UnityEngine.Object.DestroyImmediate(reference);
            }
        }

        private static void StripSimulationComponents(GameObject root)
        {
            var scripts = new List<MonoBehaviour>(root.GetComponentsInChildren<MonoBehaviour>(true));
            while (scripts.Count > 0)
            {
                bool removed = false;
                for (int i = scripts.Count - 1; i >= 0; i--)
                {
                    MonoBehaviour script = scripts[i];
                    bool required = false;
                    if (script != null)
                        foreach (MonoBehaviour other in scripts)
                        {
                            if (other == null || other == script || other.gameObject != script.gameObject)
                                continue;
                            foreach (RequireComponent dep in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                                if (dep.m_Type0?.IsAssignableFrom(script.GetType()) == true
                                    || dep.m_Type1?.IsAssignableFrom(script.GetType()) == true
                                    || dep.m_Type2?.IsAssignableFrom(script.GetType()) == true)
                                    required = true;
                        }
                    if (required)
                        continue;
                    if (script != null)
                        UnityEngine.Object.DestroyImmediate(script);
                    scripts.RemoveAt(i);
                    removed = true;
                }
                if (!removed)
                    throw new InvalidOperationException("Cannot strip cyclic gameplay script dependencies from playback.");
            }
            foreach (Transform tf in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(tf.gameObject);
            RemoveComponents<Animation>(root);
            foreach (Type type in PhysicsPlaybackPolicy.SimulationTypes)
                foreach (Component component in root.GetComponentsInChildren(type, true))
                    UnityEngine.Object.DestroyImmediate(component);
            foreach (ParticleSystem particles in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var collision = particles.collision;
                collision.enabled = false;
                var trigger = particles.trigger;
                trigger.enabled = false;
            }
            PhysicsPlaybackPolicy.Validate(root);
        }

        private static void RemoveComponents<T>(GameObject root) where T : Component
        {
            foreach (T component in root.GetComponentsInChildren<T>(true))
                UnityEngine.Object.DestroyImmediate(component);
        }

        internal static void ValidateOutputAssetTargets(PhysicsRecordingSettings settings)
        {
            ValidateOutputAssetTarget(settings.ClipPath, typeof(AnimationClip), settings.Overwrite);
            ValidateOutputAssetTarget(settings.PrefabPath, typeof(GameObject), settings.Overwrite);
            ValidateOutputAssetTarget(settings.ReferencePath, typeof(PhysicsRecordingReference), settings.Overwrite);
        }

        private static void ValidateOutputAssetTarget(string path, Type type, bool overwrite)
        {
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing == null && !File.Exists(path) && !Directory.Exists(path))
                return;
            if (!overwrite)
                throw new InvalidOperationException($"Recording output already exists: {path}");
            if (existing == null || existing.GetType() != type || AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException($"Recording output path is occupied by an incompatible asset: {path}");
        }

        private static void EnsureSaveFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string[] parts = path.Split('/');
            string parent = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string child = $"{parent}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(child) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, parts[i])))
                    throw new IOException($"Unable to create recorder output folder: {child}");
                parent = child;
            }
        }

        internal static string ValidateAssetFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Save path is required.", nameof(path));
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Save path must be a normalized folder under Assets/.", nameof(path));
            foreach (string part in normalized.Split('/'))
                ValidateAssetFileName(part);
            return normalized;
        }

        internal static void ValidateAssetFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal)
                || name.Contains("..") || name.IndexOfAny("/\\<>:\"|?*".ToCharArray()) >= 0
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Animation and folder names must be simple valid file names.", nameof(name));
        }

    }
}
#endif
