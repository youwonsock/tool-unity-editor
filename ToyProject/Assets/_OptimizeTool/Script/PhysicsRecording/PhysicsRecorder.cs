using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// Records the local poses of a Rigidbody hierarchy after physics steps in editor Play Mode, then saves its clip
    /// (a Legacy clip with a physics-free prefab that plays it, or a Generic clip for an Animator) and, optionally,
    /// the unreduced observations as a reference.
    /// </summary>
    public sealed class PhysicsRecorder : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Top of the recorded hierarchy. Every Transform below it is recorded, and Rigidbodies added below it during the take join it; at least one needs a Rigidbody.")]
        [SerializeField] private Transform _root;
        [Header("Sampling")]
        [Tooltip("Seconds between recorded physics steps. 0 records every step. The step that reaches the duration, and the last step before a stop, are always recorded.")]
        [SerializeField, Min(0f)] private float _sampleInterval;
        [Header("Output")]
        [Tooltip("Folder under Assets/ for the clip, the playback prefab and the reference.")]
        [SerializeField] private string _savePath;
        [Tooltip("Name of the saved clip, prefab and reference, used when StartRecording gets no name.")]
        [SerializeField] private string _clipName;
        [Tooltip("LegacyPrefab saves a Legacy clip and a physics-free prefab that plays it. Generic saves only a Generic clip, for an Animator Controller.")]
        [SerializeField] private PhysicsClipFormat _clipFormat = PhysicsClipFormat.LegacyPrefab;
        [Tooltip("Replace existing outputs with the same names, keeping their GUIDs.")]
        [SerializeField] private bool _overwriteExistingAssets;
        [Tooltip("Also save every recorded physics step as a reference asset next to the clip, to audit the clip later.")]
        [SerializeField] private bool _saveReference;
        [Header("Key Reduction")]
        [Tooltip("Largest position error (m) linear playback may have at any recorded physics step. 0 keeps every step.")]
        [SerializeField, Min(0f)] private float _positionTolerance = 0.005f;
        [Tooltip("Largest rotation error (degrees) linear playback may have at any recorded physics step. 0 keeps every step.")]
        [SerializeField, Min(0f)] private float _rotationTolerance = 0.5f;
        [Tooltip("Save tracks that never move beyond the tolerances without curves; the saved prefab holds their first pose.")]
        [SerializeField] private bool _omitStillTracks;

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

        private readonly List<Transform> _tracks = new();
        private readonly List<TrackSnapshot> _snapshots = new();
        private readonly HashSet<Transform> _tracked = new();
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
        private readonly List<Rigidbody> _bodies = new();
        private IPhysicsRecordingExporter _exporter;
        private PhysicsTake _take;
        private PhysicsTakeSettings _settings;
        private Coroutine _routine;
        private double _startTime;
        private bool _started;
        private float _duration;

        public bool IsRecording => _take != null;
        /// <summary>The last finished take; null while recording, after a cancel, or before the first take.</summary>
        public PhysicsRecordingResult Result { get; private set; }
        /// <summary>What the next take is saved as; it is fixed when a take starts.</summary>
        public PhysicsClipFormat ClipFormat => _clipFormat;

        /// <summary>
        /// The <c>Time.fixedTimeAsDouble</c> of the current take's first recorded physics step, which is time zero of
        /// its clip. False while not recording and before that first step.
        /// </summary>
        public bool TryGetTakeStartTime(out double fixedTime)
        {
            fixedTime = _startTime;
            return IsRecording && _started;
        }

        /// <summary>
        /// Starts a take; its first recorded pose is the next completed physics step, at time zero. Throws for invalid
        /// calls or settings.
        /// </summary>
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
                string.IsNullOrWhiteSpace(clipName) ? _clipName : clipName, _overwriteExistingAssets, _saveReference,
                _positionTolerance, _rotationTolerance, _sampleInterval, _omitStillTracks, _clipFormat));

            // Include parents without a Rigidbody too, so their motion is preserved in local space.
            Transform[] tracks = _root.GetComponentsInChildren<Transform>(true);
            var paths = new string[tracks.Length];
            var hidden = new bool[tracks.Length];
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tracks.Length; i++)
            {
                paths[i] = AnimationPath(tracks[i], _root);
                if (tracks[i].name.Contains("/") || !unique.Add(paths[i]))
                    throw new InvalidOperationException($"Animation paths must be unique and names cannot contain '/': {paths[i]}");
                // The clip keeps an inactive child hidden; active state changes during the take are not recorded.
                hidden[i] = i > 0 && !tracks[i].gameObject.activeSelf;
            }

            Result = null;
            _settings = settings;
            _duration = duration;
            _tracks.AddRange(tracks);
            _tracked.UnionWith(tracks);
            _paths.UnionWith(paths);
            foreach (Transform tf in tracks) _snapshots.Add(new TrackSnapshot(tf));
            _started = false;
            _take = new PhysicsTake(paths, hidden);
            _exporter = exporter;
            exporter.BeginTake();
            _routine = StartCoroutine(RecordAfterPhysics());
        }

        /// <summary>
        /// Ends the take at the last completed physics step and saves it. Save failures are in the result, including a
        /// take stopped before its first physics step.
        /// </summary>
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
            // Within this share of a step, summed step times count as equal.
            float tolerance = Time.fixedDeltaTime * 0.01f;
            while (IsRecording)
            {
                yield return wait;
                bool end;
                try
                {
                    if (!_started)
                    {
                        _startTime = Time.fixedTimeAsDouble;
                        _started = true;
                    }
                    float elapsed = (float)(Time.fixedTimeAsDouble - _startTime);
                    AddSpawnedTracks(elapsed);
                    end = elapsed >= _duration;
                    if (end || _take.IsDue(elapsed, _settings.SampleInterval, tolerance))
                    {
                        // A last step past the end is interpolated with the step right before it.
                        if (end && elapsed > _duration) _take.CommitHeld();
                        _take.Capture(elapsed, _tracks);
                    }
                    else _take.Hold(elapsed, _tracks);
                }
                catch (Exception exception)
                {
                    // A tracked Transform was destroyed or an added one cannot be bound; saving would fail too.
                    float captured = _take.Duration;
                    End();
                    Result = PhysicsRecordingResult.Failed(captured, PhysicsRecordingStopReason.Failed, exception);
                    Debug.LogException(exception, this);
                    yield break;
                }
                if (end)
                {
                    Complete(PhysicsRecordingStopReason.DurationReached);
                    yield break;
                }
            }
        }

        /// <summary>
        /// Adds every active Rigidbody that appeared under the root since the last step, with the new Transforms
        /// above it. Each is hidden in the clip until the time it appeared.
        /// </summary>
        private void AddSpawnedTracks(float elapsed)
        {
            Transform root = _tracks[0];
            if (root == null) return;
            root.GetComponentsInChildren(false, _bodies);
            foreach (Rigidbody body in _bodies)
            {
                if (_tracked.Contains(body.transform)) continue;
                var added = new List<Transform>();
                for (Transform tf = body.transform; !_tracked.Contains(tf); tf = tf.parent) added.Add(tf);
                for (int i = added.Count - 1; i >= 0; i--)
                {
                    Transform tf = added[i];
                    string path = AnimationPath(tf, root);
                    if (tf.name.Contains("/") || !_paths.Add(path))
                        throw new InvalidOperationException($"An object added under Root during the take needs a unique Animation path and a name without '/': {path}");
                    _tracks.Add(tf);
                    _tracked.Add(tf);
                    _snapshots.Add(new TrackSnapshot(tf));
                    _take.AddTrack(path, tf, elapsed);
                }
            }
        }

        private PhysicsRecordingResult Complete(PhysicsRecordingStopReason reason)
        {
            PhysicsTake take = _take;
            Transform[] tracks = _tracks.ToArray();
            TrackSnapshot[] snapshots = _snapshots.ToArray();
            PhysicsTakeSettings settings = _settings;
            IPhysicsRecordingExporter exporter = _exporter;
            // Reloads stay held until the take is saved.
            End(releaseReloads: false);
            try
            {
                take.CommitHeld();
                if (take.FrameCount == 0)
                    throw new InvalidOperationException("No physics step was recorded; stop the take after at least one physics step.");
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
            _started = false;
            _tracks.Clear();
            _tracked.Clear();
            _paths.Clear();
            _snapshots.Clear();
        }

        /// <summary>
        /// The clip only animates local poses, so every recorded Transform must still exist with its parent, name and
        /// scale. Transforms added under the root are allowed; added Rigidbodies are already tracks.
        /// </summary>
        private static void ValidateHierarchy(Transform[] tracks, TrackSnapshot[] snapshots)
        {
            if (tracks[0] == null)
                throw new InvalidOperationException("The recording Root was destroyed.");
            for (int i = 0; i < tracks.Length; i++)
                if (!snapshots[i].Matches(tracks[i]))
                    throw new InvalidOperationException("A recorded Transform was destroyed, renamed, reparented or rescaled during the take; record again.");
        }

        /// <summary>The Animation binding path of tf below root, such as "Arm/Hand"; empty for the root itself.</summary>
        private static string AnimationPath(Transform tf, Transform root)
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
