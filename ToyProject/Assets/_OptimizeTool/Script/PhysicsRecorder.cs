using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Common.OptimizeTool
{
    public enum PhysicsRecordingStopReason { DurationReached, Stopped, Disabled, Failed }

    /// <summary>The outcome of one take: its saved assets, or the error that prevented saving them.</summary>
    public sealed class PhysicsRecordingResult
    {
        public float Duration { get; }
        public PhysicsRecordingStopReason StopReason { get; }
        public AnimationClip Clip { get; }
        public GameObject Prefab { get; }
        public PhysicsRecordingReference Reference { get; }
        public Exception Error { get; }
        public bool Succeeded => Error == null;

        internal PhysicsRecordingResult(float duration, PhysicsRecordingStopReason reason, AnimationClip clip,
            GameObject prefab, PhysicsRecordingReference reference, Exception error = null)
        {
            Duration = duration;
            StopReason = reason;
            Clip = clip;
            Prefab = prefab;
            Reference = reference;
            Error = error;
        }
    }

    /// <summary>
    /// Records the local poses of a Rigidbody hierarchy after every physics step in editor Play Mode, then saves
    /// a physics-free Legacy Animation prefab, its clip, and the unreduced observations as a reference.
    /// </summary>
    public sealed class PhysicsRecorder : MonoBehaviour
    {
        // The settings stay serialized in Players too, so both builds share one layout; only the editor records.
#if !UNITY_EDITOR
#pragma warning disable 0414
#endif
        [Header("Target")]
        [SerializeField] private Transform _root;
        [Header("Output")]
        [SerializeField] private string _savePath;
        [SerializeField] private string _animationFileName;
        [SerializeField] private bool _overwriteExistingAssets;
        [Header("Key Reduction")]
        [Tooltip("Largest position error (m) linear playback may have at any recorded physics step. 0 keeps every step.")]
        [SerializeField, Min(0f)] private float _positionTolerance = 0.005f;
        [Tooltip("Largest rotation error (degrees) linear playback may have at any recorded physics step. 0 keeps every step.")]
        [SerializeField, Min(0f)] private float _rotationTolerance = 0.5f;
#if !UNITY_EDITOR
#pragma warning restore 0414
#endif

#if UNITY_EDITOR
        private readonly struct TrackSnapshot
        {
            private readonly Transform _parent;
            private readonly string _name;
            private readonly Vector3 _scale;

            public TrackSnapshot(Transform tf)
            {
                _parent = tf.parent;
                _name = tf.name;
                _scale = tf.localScale;
            }

            public bool Matches(Transform tf) => tf != null && tf.parent == _parent && tf.name == _name && tf.localScale == _scale;
        }

        private PhysicsTake _take;
        private Transform[] _tracks;
        private TrackSnapshot[] _snapshots;
        private PhysicsTakeSettings _settings;
        private Coroutine _routine;
        private double _startTime;
        private float _duration;

        public bool IsRecording => _take != null;
        /// <summary>The last finished take; null while recording, after a cancel, or before the first take.</summary>
        public PhysicsRecordingResult Result { get; private set; }

        /// <summary>Starts a take at the latest physics step. Throws for invalid calls or settings.</summary>
        public void StartRecording(float duration, string animName = null)
        {
            if (IsRecording)
                throw new InvalidOperationException("PhysicsRecorder is already recording.");
            if (!PhysicsPoseMath.IsFinite(duration) || duration <= 0f)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (!Application.isPlaying || !isActiveAndEnabled)
                throw new InvalidOperationException("PhysicsRecorder records only while it is active in Play Mode.");
            if (Physics.simulationMode != SimulationMode.FixedUpdate)
                throw new InvalidOperationException("PhysicsRecorder records after FixedUpdate physics steps; set Physics.simulationMode to FixedUpdate.");
            if (_root == null)
                throw new InvalidOperationException("PhysicsRecorder requires a Root reference.");
            if (_root.GetComponentInChildren<Rigidbody>(true) == null)
                throw new InvalidOperationException("PhysicsRecorder requires at least one Rigidbody under Root.");
            var settings = new PhysicsTakeSettings(_savePath, string.IsNullOrWhiteSpace(animName) ? _animationFileName : animName,
                _overwriteExistingAssets, _positionTolerance, _rotationTolerance);
            PhysicsRecordingAssetWriter.ValidateTargets(settings);

            // Include parents without a Rigidbody too, so their motion is preserved in local space.
            Transform[] tracks = _root.GetComponentsInChildren<Transform>(true);
            var paths = new string[tracks.Length];
            var snapshots = new TrackSnapshot[tracks.Length];
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tracks.Length; i++)
            {
                paths[i] = AnimationUtility.CalculateTransformPath(tracks[i], _root);
                if (tracks[i].name.Contains("/") || !unique.Add(paths[i]))
                    throw new InvalidOperationException($"Animation paths must be unique and names cannot contain '/': {paths[i]}");
                snapshots[i] = new TrackSnapshot(tracks[i]);
            }

            Result = null;
            _settings = settings;
            _duration = duration;
            _tracks = tracks;
            _snapshots = snapshots;
            _startTime = Time.fixedTimeAsDouble;
            _take = new PhysicsTake(paths, tracks);
            // Prefabs cannot be saved during a script reload, so hold reloads until the take ends.
            EditorApplication.LockReloadAssemblies();
            _routine = StartCoroutine(RecordAfterPhysics());
        }

        /// <summary>Ends the take at the last completed physics step and saves it. Save failures are in the result.</summary>
        public PhysicsRecordingResult StopRecording()
        {
            if (!IsRecording)
                throw new InvalidOperationException("PhysicsRecorder is not recording.");
            return Complete(PhysicsRecordingStopReason.Stopped);
        }

        /// <summary>Discards the take without writing any asset.</summary>
        public void CancelRecording()
        {
            if (!IsRecording)
                throw new InvalidOperationException("PhysicsRecorder is not recording.");
            End();
            Result = null;
        }

        private void OnDisable()
        {
            // Covers disabling, destroying and leaving Play Mode, which all end the take.
            if (IsRecording)
                Complete(PhysicsRecordingStopReason.Disabled);
        }

        private IEnumerator RecordAfterPhysics()
        {
            var wait = new WaitForFixedUpdate();
            while (IsRecording)
            {
                yield return wait;
                float elapsed = (float)(Time.fixedTimeAsDouble - _startTime);
                try { _take.Capture(elapsed, _tracks); }
                catch (Exception exception)
                {
                    // A tracked Transform was destroyed; saving would fail the same way.
                    float captured = _take.Duration;
                    End();
                    Result = new PhysicsRecordingResult(captured, PhysicsRecordingStopReason.Failed, null, null, null, exception);
                    Debug.LogException(exception, this);
                    yield break;
                }
                if (elapsed >= _duration)
                {
                    Complete(PhysicsRecordingStopReason.DurationReached);
                    yield break;
                }
            }
        }

        private PhysicsRecordingResult Complete(PhysicsRecordingStopReason reason)
        {
            PhysicsTake take = _take;
            Transform[] tracks = _tracks;
            TrackSnapshot[] snapshots = _snapshots;
            PhysicsTakeSettings settings = _settings;
            End();
            try
            {
                take.Finish(_duration, Time.fixedDeltaTime * 0.01f);
                ValidateHierarchy(tracks, snapshots);
                PhysicsRecordingAssetWriter.Assets assets = PhysicsRecordingAssetWriter.Write(_root, take, settings);
                Result = new PhysicsRecordingResult(take.Duration, reason, assets.Clip, assets.Prefab, assets.Reference);
            }
            catch (Exception exception)
            {
                Result = new PhysicsRecordingResult(take.Duration, reason, null, null, null, exception);
                Debug.LogException(exception, this);
            }
            return Result;
        }

        private void End()
        {
            if (_take != null) EditorApplication.UnlockReloadAssemblies();
            if (_routine != null) StopCoroutine(_routine);
            _routine = null;
            _take = null;
            _tracks = null;
            _snapshots = null;
        }

        /// <summary>The clip only animates local poses, so the hierarchy, names and scales must be unchanged.</summary>
        private void ValidateHierarchy(Transform[] tracks, TrackSnapshot[] snapshots)
        {
            if (_root == null)
                throw new InvalidOperationException("The recording Root was destroyed.");
            if (_root.GetComponentsInChildren<Transform>(true).Length != tracks.Length)
                throw new InvalidOperationException("Transforms were added or removed under Root during the take; record again.");
            for (int i = 0; i < tracks.Length; i++)
                if (!snapshots[i].Matches(tracks[i]))
                    throw new InvalidOperationException("A recorded Transform was destroyed, renamed, reparented or rescaled during the take; record again.");
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>The validated output and key reduction settings of one take, fixed when it starts.</summary>
    internal readonly struct PhysicsTakeSettings
    {
        public readonly string Folder, Name;
        public readonly bool Overwrite;
        public readonly float PositionTolerance, RotationTolerance;

        public PhysicsTakeSettings(string folder, string name, bool overwrite, float positionTolerance, float rotationTolerance)
        {
            Folder = AssetFileTransaction.ValidateFolder(folder);
            AssetFileTransaction.ValidateFileName(name);
            if (!PhysicsPoseMath.IsFinite(positionTolerance) || positionTolerance < 0f)
                throw new ArgumentOutOfRangeException(nameof(positionTolerance));
            if (!PhysicsPoseMath.IsFinite(rotationTolerance) || rotationTolerance < 0f)
                throw new ArgumentOutOfRangeException(nameof(rotationTolerance));
            Name = name;
            Overwrite = overwrite;
            PositionTolerance = positionTolerance;
            RotationTolerance = rotationTolerance;
        }

        public string ClipPath => $"{Folder}/{Name}.anim";
        public string PrefabPath => $"{Folder}/{Name}.prefab";
        public string ReferencePath => PhysicsRecordingReference.AssetPathForClip(ClipPath);
    }
#endif
}
