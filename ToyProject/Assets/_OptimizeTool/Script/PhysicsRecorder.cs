using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using Stopwatch = System.Diagnostics.Stopwatch;
#endif

namespace Common.OptimizeTool
{
    public class PhysicsRecorder : MonoBehaviour
    {
#if UNITY_EDITOR
        [Header("설정")]
        [SerializeField] private Transform _root;
        [SerializeField] private float _recordInterval = 0.02f;
        [SerializeField] private bool _recordOnlyIfMoved = true;
        [SerializeField] private float _positionThreshold = 0.001f;
        [SerializeField] private float _rotationThreshold = 0.001f;
        [SerializeField] private string _savePath;
        [SerializeField] private string _animationFileName;
        [SerializeField] private bool _overwriteExistingAssets;

        private sealed class TransformBinding
        {
            public Transform Transform, Parent;
            public string Name;
            public Vector3 Scale;
        }

        private readonly List<TransformBinding> _tracks = new();
        private readonly Dictionary<Transform, TransformBinding> _trackTable = new();
        private readonly List<Transform> _hierarchyBuffer = new();
        private readonly List<Rigidbody> _rigidbodyBuffer = new();
        private PhysicsRecordingSettings _settings;
        private double _startTime;
        private PhysicsRecordingSession _session;
        private PhysicsRecordingMeasurements _measurements;
        private readonly List<PhysicsPoseSample> _poseBuffer = new();
        private readonly List<string> _paths = new();
        private int _recordingVersion;
        private enum RecordingPhase { Idle, Recording, Saving }
        private RecordingPhase _phase;
        private bool _isInitialized;
        private Exception _fault;

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _fault != null;
        public bool IsRecording => _phase == RecordingPhase.Recording;
        public bool IsSaving => _phase == RecordingPhase.Saving;
        public Exception LastError => _fault;
        public PhysicsRecordingResult Result { get; private set; }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("PhysicsRecorder is already initialized.");
            if (IsFaulted)
                throw new InvalidOperationException("PhysicsRecorder is faulted; call Release before Init.", _fault);
            try
            {
                ReadSettings();
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                SetFault(exception);
                throw;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !IsFaulted)
                throw new InvalidOperationException("PhysicsRecorder has not been initialized.");
            try
            {
                if (IsRecording)
                    StopRecording(PhysicsRecordingStopReason.Released);
            }
            finally
            {
                _phase = RecordingPhase.Idle;
                _recordingVersion++;
                _session = null;
                _measurements = null;
                _tracks.Clear();
                _trackTable.Clear();
                _hierarchyBuffer.Clear();
                _rigidbodyBuffer.Clear();
                _poseBuffer.Clear();
                _paths.Clear();
                _settings = null;
                _isInitialized = false;
                _fault = null;
            }
        }

        private void OnDisable()
        {
            // Complete before the hierarchy is destroyed or Play Mode ends.
            if (!IsRecording)
                return;
            try { StopRecording(PhysicsRecordingStopReason.Disabled); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private void OnDestroy()
        {
            if (!_isInitialized && !IsFaulted)
                return;
            try { Release(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        public void StartRecording(float duration, string animName = null)
        {
            ThrowIfUnavailable();
            if (IsRecording || IsSaving)
                throw new InvalidOperationException("PhysicsRecorder is already recording or saving.");
            if (!IsFinite(duration) || duration <= 0f)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (Application.isPlaying && !isActiveAndEnabled)
                throw new InvalidOperationException("PhysicsRecorder must be active to record in Play Mode.");
            PhysicsRecordingSettings settings = ReadSettings(animName);
            PhysicsRecordingAssetWriter.ValidateOutputAssetTargets(settings);
            CaptureInitialHierarchy();
            _settings = settings;
            _animationFileName = settings.Name;
            _session = new PhysicsRecordingSession(_paths, _poseBuffer, duration, settings.Interval,
                settings.OnlyIfMoved, settings.PositionThreshold, settings.RotationThreshold);
            _measurements = new PhysicsRecordingMeasurements { TransformCount = _tracks.Count, ObservationCount = 1 };
            _startTime = Time.timeAsDouble;
            Result = null;
            _phase = RecordingPhase.Recording;
            int version = ++_recordingVersion;
            if (Application.isPlaying)
                StartCoroutine(RecordAfterPhysics(version));
        }

        private IEnumerator RecordAfterPhysics(int version)
        {
            var wait = new WaitForFixedUpdate();
            while (IsRecording && version == _recordingVersion)
            {
                yield return wait;
                if (!IsRecording || version != _recordingVersion)
                    break;
                try { RecordFrame((float)(Time.fixedTimeAsDouble - _startTime)); }
                catch (Exception exception)
                {
                    SetFault(exception);
                    Debug.LogException(exception, this);
                }
            }
        }

        // Called after a completed physics step, with its actual elapsed time.
        private void RecordFrame(float elapsed)
        {
            ValidateRecordingHierarchy();
            long started = Stopwatch.GetTimestamp();
            CapturePoses(elapsed);
            bool finished = _session.Observe(elapsed, _poseBuffer);
            _measurements.SamplingTicks += Stopwatch.GetTimestamp() - started;
            _measurements.ObservationCount++;
            if (finished)
                CompleteRecording(PhysicsRecordingStopReason.DurationReached);
        }

        private void CapturePoses(float time)
        {
            _poseBuffer.Clear();
            foreach (TransformBinding track in _tracks)
                _poseBuffer.Add(new PhysicsPoseSample(time, track.Transform.localPosition, track.Transform.localRotation));
        }

        /// <summary>Returns after both assets are saved; throws and sets LastError on failure.</summary>
        public void StopRecording()
        {
            StopRecording(PhysicsRecordingStopReason.Stopped);
        }

        private void StopRecording(PhysicsRecordingStopReason reason)
        {
            ThrowIfUnavailable();
            if (!IsRecording)
                throw new InvalidOperationException("PhysicsRecorder is not recording.");
            try
            {
                ValidateRecordingHierarchy();
                float elapsed = Application.isPlaying
                    ? Mathf.Max(_session.Elapsed, (float)(Time.timeAsDouble - _startTime)) : _session.Elapsed;
                long started = Stopwatch.GetTimestamp();
                CapturePoses(elapsed);
                _session.Stop(elapsed, _poseBuffer);
                _measurements.SamplingTicks += Stopwatch.GetTimestamp() - started;
                _measurements.ObservationCount++;
                CompleteRecording(reason);
            }
            catch (Exception exception)
            {
                SetFault(exception);
                throw;
            }
        }

        private void CompleteRecording(PhysicsRecordingStopReason reason)
        {
            _phase = RecordingPhase.Saving;
            _recordingVersion++;
            Result = null;
            long started = Stopwatch.GetTimestamp();
            try
            {
                var assets = PhysicsRecordingAssetWriter.Write(_root, _session.Take, _settings, _measurements);
                _measurements.ExportTicks = Stopwatch.GetTimestamp() - started;
                Result = new PhysicsRecordingResult(_session.Elapsed, reason, assets.Clip, assets.Prefab,
                    metrics: _measurements.Snapshot(_session.Take), reference: assets.Reference);
            }
            catch (Exception exception)
            {
                _measurements.ExportTicks = Stopwatch.GetTimestamp() - started;
                SetFault(exception);
                throw;
            }
            finally { _phase = RecordingPhase.Idle; }
        }

        private void CaptureInitialHierarchy()
        {
            _root.GetComponentsInChildren(true, _rigidbodyBuffer);
            if (_rigidbodyBuffer.Count == 0)
                throw new InvalidOperationException("PhysicsRecorder requires at least one Rigidbody under Root.");
            _tracks.Clear();
            _trackTable.Clear();
            _poseBuffer.Clear();
            _paths.Clear();
            _root.GetComponentsInChildren(true, _hierarchyBuffer);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            // Include non-Rigidbody parents too, so their motion is preserved in local space.
            foreach (Transform tf in _hierarchyBuffer)
            {
                string path = AnimationUtility.CalculateTransformPath(tf, _root);
                if (tf.name.Contains("/") || !paths.Add(path))
                    throw new InvalidOperationException($"Animation paths must be unique and names cannot contain '/': {path}");
                _paths.Add(path);
                _poseBuffer.Add(new PhysicsPoseSample(0f, tf.localPosition, tf.localRotation));
                var track = new TransformBinding
                {
                    Transform = tf, Parent = tf.parent, Name = tf.name,
                    Scale = tf.localScale,
                };
                _tracks.Add(track);
                _trackTable.Add(tf, track);
            }
        }

        private void ValidateRecordingHierarchy()
        {
            long started = Stopwatch.GetTimestamp();
            try { ValidateHierarchy(); }
            finally
            {
                if (_measurements != null) _measurements.ValidationTicks += Stopwatch.GetTimestamp() - started;
            }
        }

        private void ValidateHierarchy()
        {
            if (_root == null)
                throw new InvalidOperationException("The recording Root was destroyed.");
            _root.GetComponentsInChildren(true, _hierarchyBuffer);
            if (_hierarchyBuffer.Count != _tracks.Count)
                throw new InvalidOperationException("The recording hierarchy changed; restart the recording.");
            foreach (Transform tf in _hierarchyBuffer)
                if (!_trackTable.TryGetValue(tf, out TransformBinding track)
                    || tf.parent != track.Parent || tf.name != track.Name || tf.localScale != track.Scale)
                    throw new InvalidOperationException("The recording hierarchy, names or scale changed; restart the recording.");
        }

        private PhysicsRecordingSettings ReadSettings(string animName = null)
        {
            if (_root == null)
                throw new InvalidOperationException("PhysicsRecorder requires a Root reference.");
            if (!IsFinite(_recordInterval) || _recordInterval <= 0f || !IsFinite(1f / _recordInterval))
                throw new ArgumentOutOfRangeException(nameof(_recordInterval));
            if (!IsFinite(_positionThreshold) || _positionThreshold < 0f
                || !IsFinite(_rotationThreshold) || _rotationThreshold < 0f)
                throw new ArgumentOutOfRangeException(nameof(_positionThreshold));
            string name = string.IsNullOrWhiteSpace(animName) ? _animationFileName : animName;
            PhysicsRecordingAssetWriter.ValidateAssetFileName(name);
            return new PhysicsRecordingSettings(_recordInterval, _positionThreshold, _rotationThreshold,
                _recordOnlyIfMoved, _overwriteExistingAssets,
                PhysicsRecordingAssetWriter.ValidateAssetFolderPath(_savePath), name);
        }

        private void ThrowIfUnavailable()
        {
            if (IsFaulted)
                throw new InvalidOperationException("PhysicsRecorder is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("PhysicsRecorder is not initialized.");
        }

        private void SetFault(Exception exception)
        {
            if (IsFaulted && ReferenceEquals(_fault, exception)) return;
            _phase = RecordingPhase.Idle;
            _recordingVersion++;
            _fault = exception;
            Result = new PhysicsRecordingResult(_session?.Elapsed ?? 0f, PhysicsRecordingStopReason.Faulted,
                null, null, exception,
                _measurements?.Snapshot(_session?.Take));
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
#endif
    }
}
