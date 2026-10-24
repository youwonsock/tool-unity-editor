using System;
using System.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Common.OptimizeTool.Samples
{
    /// <summary>Records real physics, then compares its original observations with the saved prefab.</summary>
    public sealed class OptimizeToolPhysicsDemo : MonoBehaviour
    {
        public const string RecordingFolder = "Assets/_OptimizeTool/Settings/Generated/PhysicsRecordings";
        public const string RecordingName = "OptimizeToolPhysicsTake";
        public const string RecordingPrefabPath = RecordingFolder + "/" + RecordingName + ".prefab";

        [SerializeField] private GameObject _demoRoot;
        [SerializeField] private Transform _sourceRoot;
        [SerializeField] private Transform _replayAnchor;
        [SerializeField] private Transform _view;
        [SerializeField] private PhysicsRecorder _recorder;
        [SerializeField] private GameObject _savedPrefab;
        [SerializeField] private PhysicsRecordingReference _recordingReference;
        [SerializeField] private float _duration = 3.2f;
        [SerializeField] private float _releaseDelay = 0.4f;

        public enum DemoState { Ready, Recording, Comparing, Finished, Error }
        public DemoState State { get; private set; }
        public bool IsBusy => State == DemoState.Recording;
        public bool CanRecord => Application.isEditor && isActiveAndEnabled && !IsBusy;
        public bool CanCompare => _savedPrefab != null && isActiveAndEnabled && !IsBusy;
        public float Elapsed { get; private set; }
        public float Duration => _duration;
        public float ReleaseDelay => _releaseDelay;
        public float MaxPositionError { get; private set; }
        public float MaxRotationError { get; private set; }
        public bool HasRecordingReference => _recordingReference != null;
        public PhysicsRecordingReference RecordingReference => _recordingReference;
        public int ComparisonSampleCount { get; private set; }
        public Transform View => _view;
        public GameObject SavedPrefab => _savedPrefab;
        public GameObject Playback => _playback?.Instance;
        internal int SourceBodyCount => _bodies.Length;
        public Transform SourceRoot => _sourceRoot;
        public string LastError { get; private set; }
        internal bool FailedDuringRecording { get; private set; }
#if UNITY_EDITOR
        public PhysicsRecorder Recorder => _recorder;
#endif

        private Rigidbody[] _bodies;
        private Vector3[] _initialPositions;
        private Quaternion[] _initialRotations;
        private PhysicsPrefabPlayback _playback;
        private Transform[] _referenceSource, _referencePlayback;
        private PhysicsPoseSample[] _referencePoses;
        private Coroutine _routine;
        private double _runStart;
        private bool _running, _released;

#if UNITY_EDITOR
        private void OnEnable() => RegisterReloadCleanup();

        private void RegisterReloadCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= PrepareForAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += PrepareForAssemblyReload;
        }

        private void PrepareForAssemblyReload()
        {
            // Finish an active take through the same synchronous disable/save contract.
            // Managed playback ownership will not survive the reload; dispose its instance now.
            OnDisable();
            DestroyPlayback();
        }
#endif

        private void Awake()
        {
            if (_demoRoot == null || _sourceRoot == null || _replayAnchor == null || _view == null || _recorder == null)
                throw new InvalidOperationException("Rebuild the Physics Recorder demo to assign its scene references.");
            _bodies = _sourceRoot.GetComponentsInChildren<Rigidbody>(true);
            if (_bodies.Length == 0 || _duration <= _releaseDelay || _releaseDelay < 0f)
                throw new InvalidOperationException("The Physics Recorder demo requires bodies and a valid recording duration.");
            _initialPositions = new Vector3[_bodies.Length];
            _initialRotations = new Quaternion[_bodies.Length];
            for (int i = 0; i < _bodies.Length; i++)
            {
                _initialPositions[i] = _bodies[i].transform.localPosition;
                _initialRotations[i] = _bodies[i].transform.localRotation;
            }
#if UNITY_EDITOR
            if (_savedPrefab == null)
                _savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RecordingPrefabPath);
#endif
            LoadRecordingReference();
            ResetSource();
            UpdateSourceLabel();
        }

        private void FixedUpdate()
        {
            if (!_running || _released || Time.fixedTimeAsDouble - _runStart < _releaseDelay - 0.000001d)
                return;
            for (int i = 0; i < _bodies.Length; i++)
            {
                Rigidbody body = _bodies[i];
                body.isKinematic = false;
                body.velocity = new Vector3((i % 2 == 0 ? 1f : -1f) * 0.25f, 0f, 0.12f * (i % 3 - 1));
                body.angularVelocity = new Vector3(0.3f, 0.4f * (i % 2 == 0 ? 1f : -1f), 0.2f);
                body.WakeUp();
            }
            _released = true;
        }

        public void Record()
        {
            if (!CanRecord || !_demoRoot.activeInHierarchy)
                return;
#if UNITY_EDITOR
            StopRoutine();
            ResetSource();
            DestroyPlayback();
            Elapsed = 0f;
            MaxPositionError = 0f;
            MaxRotationError = 0f;
            ComparisonSampleCount = 0;
            SetState(DemoState.Recording);
            _routine = StartCoroutine(RecordPhysics());
#endif
        }

#if UNITY_EDITOR
        private IEnumerator RecordPhysics()
        {
            yield return new WaitForFixedUpdate();
            try
            {
                if (_recorder.IsFaulted) _recorder.Release();
                if (!_recorder.IsInitialized) _recorder.Init();
                _recorder.StartRecording(_duration);
                StartClock();
            }
            catch (Exception exception) { Fail(exception, true); yield break; }
            while (_recorder.IsRecording)
            {
                Elapsed = Mathf.Clamp((float)(Time.timeAsDouble - _runStart), 0f, _duration);
                yield return null;
            }
            _running = false;
            FreezeSource();
            try { AcceptRecordingResult(true); }
            catch (Exception exception) { Fail(exception, true); yield break; }
            _routine = null;
            Compare();
        }

        private void AcceptRecordingResult(bool required)
        {
            if (_recorder.IsFaulted)
                throw _recorder.LastError ?? new InvalidOperationException("Recording assets were not saved.");
            PhysicsRecordingResult result = _recorder.Result;
            if (result?.Succeeded == true)
            {
                _savedPrefab = result.Prefab;
                _recordingReference = result.Reference;
                Elapsed = result.Duration;
            }
            else if (required)
                throw result?.Error ?? new InvalidOperationException("Recording assets were not saved.");
            SetState(DemoState.Ready);
        }
#endif

        public void Compare()
        {
            if (!CanCompare || !_demoRoot.activeInHierarchy)
                return;
            StopRoutine();
            ResetSource();
            try { CreatePlayback(); }
            catch (Exception exception) { Fail(exception); return; }
            Elapsed = 0f;
            MaxPositionError = 0f;
            MaxRotationError = 0f;
            ComparisonSampleCount = 0;
            SetState(DemoState.Comparing);
            _routine = StartCoroutine(ComparePhysics());
        }

        private IEnumerator ComparePhysics()
        {
            try
            {
                yield return new WaitForFixedUpdate();
                double started = Time.fixedTimeAsDouble;
                float length;
                try
                {
                    _playback.Start();
                    length = _playback.Length;
                    SampleComparison(0f);
                }
                catch (Exception exception) { Fail(exception); yield break; }
                while (Elapsed < length)
                {
                    yield return new WaitForFixedUpdate();
                    try
                    {
                        Elapsed = Mathf.Min((float)(Time.fixedTimeAsDouble - started), length);
                        SampleComparison(Elapsed);
                    }
                    catch (Exception exception) { Fail(exception); yield break; }
                }
                SetState(DemoState.Finished);
            }
            finally { _routine = null; }
        }

        private void SampleComparison(float time)
        {
            // Audit every captured physics observation, even if a render frame skips several ticks.
            // This reads raw observations, never the exported curves used by the right-hand side.
            while (ComparisonSampleCount < _recordingReference.ObservationCount
                && _recordingReference.TimeAt(ComparisonSampleCount) <= time)
            {
                float observedTime = _recordingReference.TimeAt(ComparisonSampleCount);
                _playback.Sample(observedTime);
                for (int i = 0; i < _referencePlayback.Length; i++)
                {
                    PhysicsPoseSample pose = _recordingReference.Observation(i, ComparisonSampleCount);
                    MaxPositionError = Mathf.Max(MaxPositionError,
                        Vector3.Distance(pose.Position, _referencePlayback[i].localPosition));
                    MaxRotationError = Mathf.Max(MaxRotationError,
                        PhysicsPoseSample.RotationDifference(pose.Rotation, _referencePlayback[i].localRotation));
                }
                ComparisonSampleCount++;
            }
            _recordingReference.SampleFrame(time, _referencePoses);
            for (int i = 0; i < _referenceSource.Length; i++)
            {
                PhysicsPoseSample pose = _referencePoses[i];
                _referenceSource[i].SetLocalPositionAndRotation(pose.Position, pose.Rotation);
            }
            foreach (Rigidbody body in _bodies)
            {
                body.position = body.transform.position;
                body.rotation = body.transform.rotation;
            }
            Physics.SyncTransforms();
            _playback.Sample(time);
        }

        public void ResetDemo()
        {
            if (IsBusy) return;
            StopRoutine();
            ResetSource();
            Elapsed = 0f;
            MaxPositionError = 0f;
            MaxRotationError = 0f;
            ComparisonSampleCount = 0;
            SetState(DemoState.Ready);
            if (_savedPrefab != null)
            {
                try { CreatePlayback(); }
                catch (Exception exception) { Fail(exception); }
            }
            UpdateSourceLabel();
        }

        public void SetPresentationActive(bool active)
        {
            if (active) ResetDemo();
            else if (!IsBusy)
            {
                StopRoutine();
                FreezeSource();
                if (State == DemoState.Comparing) SetState(DemoState.Ready);
            }
        }

        public string GetStatusText() => OptimizeToolPhysicsStatusFormatter.Format(this);

        private void StartClock()
        {
            _runStart = Time.fixedTimeAsDouble;
            _released = false;
            _running = true;
        }

        private void CreatePlayback()
        {
#if UNITY_EDITOR
            // Reset can create a preview even when this component is disabled.
            RegisterReloadCleanup();
#endif
            DestroyPlayback();
            _playback = PhysicsPrefabPlayback.Create(_savedPrefab, _replayAnchor, _bodies);
            try
            {
                LoadRecordingReference();
                if (!HasRecordingReference)
                    throw new InvalidOperationException("Recording reference is required; RECORD a new take before comparing.");
                _recordingReference.Validate(_savedPrefab.GetComponent<Animation>().clip);
                _referenceSource = new Transform[_recordingReference.TrackCount];
                _referencePlayback = new Transform[_referenceSource.Length];
                _referencePoses = new PhysicsPoseSample[_referenceSource.Length];
                for (int i = 0; i < _referenceSource.Length; i++)
                {
                    string path = _recordingReference.Path(i);
                    _referenceSource[i] = path.Length == 0 ? _sourceRoot : _sourceRoot.Find(path);
                    _referencePlayback[i] = path.Length == 0 ? _playback.Instance.transform : _playback.Instance.transform.Find(path);
                    if (_referenceSource[i] == null || _referencePlayback[i] == null)
                        throw new InvalidOperationException("The recording reference hierarchy differs from this demo; record a new take.");
                }
            }
            catch { DestroyPlayback(); throw; }
        }

        private void LoadRecordingReference()
        {
#if UNITY_EDITOR
            AnimationClip clip = _savedPrefab != null ? _savedPrefab.GetComponent<Animation>()?.clip : null;
            string path = clip != null ? AssetDatabase.GetAssetPath(clip) : null;
            _recordingReference = string.IsNullOrEmpty(path) ? null
                : AssetDatabase.LoadAssetAtPath<PhysicsRecordingReference>(PhysicsRecordingReference.AssetPathForClip(path));
#endif
        }

        private void UpdateSourceLabel()
        {
            TextMesh label = _sourceRoot.parent.GetComponentInChildren<TextMesh>(true);
            if (label != null) label.text = State == DemoState.Recording ? "LIVE RECORDING"
                : State == DemoState.Error ? (FailedDuringRecording ? "RECORDING FAILED" : "PLAYBACK FAILED")
                : HasRecordingReference ? "RECORDED ORIGINAL" : "REFERENCE MISSING";
        }

        private void ResetSource()
        {
            _running = false;
            _released = false;
            FreezeSource();
            for (int i = 0; i < _bodies.Length; i++)
            {
                _bodies[i].transform.localPosition = _initialPositions[i];
                _bodies[i].transform.localRotation = _initialRotations[i];
                _bodies[i].position = _bodies[i].transform.position;
                _bodies[i].rotation = _bodies[i].transform.rotation;
            }
            Physics.SyncTransforms();
        }

        private void FreezeSource()
        {
            if (_bodies == null) return;
            foreach (Rigidbody body in _bodies)
            {
                if (body == null) continue;
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                }
            }
        }

        private void StopRoutine()
        {
            _running = false;
            if (_routine != null) StopCoroutine(_routine);
            _routine = null;
            _playback?.Stop();
        }

        private void DestroyPlayback()
        {
            _playback?.Destroy();
            _playback = null;
            _referenceSource = null;
            _referencePlayback = null;
            _referencePoses = null;
        }

        private void SetState(DemoState state, string error = null, bool recordingFailure = false)
        {
            State = state;
            LastError = error;
            FailedDuringRecording = recordingFailure;
            UpdateSourceLabel();
        }

        private void Fail(Exception exception, bool recordingFailure = false)
        {
            _running = false;
            _routine = null;
            DestroyPlayback();
            FreezeSource();
            SetState(DemoState.Error, exception.Message, recordingFailure);
            Debug.LogException(exception, this);
        }

        private void OnDisable()
        {
            StopRoutine();
            FreezeSource();
            if (State == DemoState.Comparing) SetState(DemoState.Ready);
            if (State != DemoState.Recording) return;
#if UNITY_EDITOR
            try
            {
                // Also finish when only this component is disabled. The recorder's
                // OnDisable may already have saved if the whole hierarchy was disabled.
                if (_recorder.IsRecording) _recorder.StopRecording();
                AcceptRecordingResult(false);
            }
            catch (Exception exception) { Fail(exception, true); return; }
#endif
#if !UNITY_EDITOR
            SetState(DemoState.Ready);
#endif
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            // A disabled demo can still own a stopped preview. Keep reload cleanup
            // registered for its lifetime so that preview cannot outlive its managed owner.
            AssemblyReloadEvents.beforeAssemblyReload -= PrepareForAssemblyReload;
#endif
            DestroyPlayback();
        }
    }
}
