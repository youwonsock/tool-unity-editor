using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// Records the local poses of a Rigidbody hierarchy after every physics step in editor Play Mode, then saves
    /// a physics-free Legacy Animation prefab, its clip and, optionally, the unreduced observations as a reference.
    /// </summary>
    public sealed class PhysicsRecorder : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Top of the recorded hierarchy. Every Transform below it is recorded; at least one needs a Rigidbody.")]
        [SerializeField] private Transform _root;
        [Header("Output")]
        [Tooltip("Folder under Assets/ for the clip, the playback prefab and the reference.")]
        [SerializeField] private string _savePath;
        [Tooltip("Name of the saved clip, prefab and reference, used when StartRecording gets no name.")]
        [SerializeField] private string _clipName;
        [Tooltip("Replace existing outputs with the same names, keeping their GUIDs.")]
        [SerializeField] private bool _overwriteExistingAssets;
        [Tooltip("Also save every recorded physics step as a reference asset next to the clip, to audit the clip later.")]
        [SerializeField] private bool _saveReference;
        [Header("Key Reduction")]
        [Tooltip("Largest position error (m) linear playback may have at any recorded physics step. 0 keeps every step.")]
        [SerializeField, Min(0f)] private float _positionTolerance = 0.005f;
        [Tooltip("Largest rotation error (degrees) linear playback may have at any recorded physics step. 0 keeps every step.")]
        [SerializeField, Min(0f)] private float _rotationTolerance = 0.5f;

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

        /// <summary>Set by the editor assembly when it loads; null in Players, where takes cannot be saved.</summary>
        internal static IPhysicsRecordingExporter Exporter { get; set; }

        private IPhysicsRecordingExporter _exporter;
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
        public void StartRecording(float duration, string clipName = null)
        {
            if (IsRecording)
                throw new InvalidOperationException("PhysicsRecorder is already recording.");
            IPhysicsRecordingExporter exporter = Exporter
                ?? throw new InvalidOperationException("PhysicsRecorder saves takes only in the Unity Editor.");
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
            PhysicsTakeSettings settings = exporter.Validate(new PhysicsTakeSettings(_savePath,
                string.IsNullOrWhiteSpace(clipName) ? _clipName : clipName,
                _overwriteExistingAssets, _saveReference, _positionTolerance, _rotationTolerance));

            // Include parents without a Rigidbody too, so their motion is preserved in local space.
            Transform[] tracks = _root.GetComponentsInChildren<Transform>(true);
            var paths = new string[tracks.Length];
            var snapshots = new TrackSnapshot[tracks.Length];
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tracks.Length; i++)
            {
                paths[i] = AnimationPath(tracks[i], _root);
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
            _exporter = exporter;
            exporter.BeginTake();
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
                    Result = PhysicsRecordingResult.Failed(captured, PhysicsRecordingStopReason.Failed, exception);
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
            IPhysicsRecordingExporter exporter = _exporter;
            // Reloads stay held until the take is saved.
            End(releaseReloads: false);
            try
            {
                take.Finish(_duration, Time.fixedDeltaTime * 0.01f);
                ValidateHierarchy(tracks, snapshots);
                // tracks[0] is the root captured at the start; a Root changed during the take is ignored.
                Result = PhysicsRecordingResult.Saved(take.Duration, reason, exporter.Export(tracks[0], take, settings));
            }
            catch (Exception exception)
            {
                Result = PhysicsRecordingResult.Failed(take.Duration, reason, exception);
                Debug.LogException(exception, this);
            }
            finally
            {
                exporter.EndTake();
            }
            return Result;
        }

        private void End(bool releaseReloads = true)
        {
            if (_take != null && releaseReloads) _exporter.EndTake();
            if (_routine != null) StopCoroutine(_routine);
            _routine = null;
            _exporter = null;
            _take = null;
            _tracks = null;
            _snapshots = null;
        }

        /// <summary>The clip only animates local poses, so the hierarchy, names and scales must be unchanged.</summary>
        private static void ValidateHierarchy(Transform[] tracks, TrackSnapshot[] snapshots)
        {
            Transform root = tracks[0];
            if (root == null)
                throw new InvalidOperationException("The recording Root was destroyed.");
            if (root.GetComponentsInChildren<Transform>(true).Length != tracks.Length)
                throw new InvalidOperationException("Transforms were added or removed under Root during the take; record again.");
            for (int i = 0; i < tracks.Length; i++)
                if (!snapshots[i].Matches(tracks[i]))
                    throw new InvalidOperationException("A recorded Transform was destroyed, renamed, reparented or rescaled during the take; record again.");
        }

        /// <summary>The Animation binding path of tf below root, such as "Arm/Hand"; empty for the root itself.</summary>
        internal static string AnimationPath(Transform tf, Transform root)
        {
            if (tf == root) return string.Empty;
            var names = new List<string>();
            for (Transform current = tf; current != root; current = current.parent)
                names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
