using System;
using System.Collections.Generic;
using UnityEngine;
using System.Collections;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Common.OptimizeTool
{
    public class PhysicsRecorder : MonoBehaviour
    {
        #if UNITY_EDITOR

        #region Inner Classes

        [System.Serializable]
        public class FrameData
        {
            public float _time;
            public Dictionary<Transform, Vector3> _positionTable = new();
            public Dictionary<Transform, Quaternion> _rotationTable = new();
        }

        #endregion


        #region Member Variables

        [Header("설정")]
        [SerializeField] private Transform _root;
        [SerializeField] private float _recordInterval = 0.02f;
        [SerializeField] private bool _recordOnlyIfMoved = true;
        [SerializeField] private float _positionThreshold = 0.001f;
        [SerializeField] private float _rotationThreshold = 0.001f;
        [SerializeField] private string _savePath;
        [SerializeField] private string _animationFileName;
        [SerializeField] private bool _convertToLegacy = false;
        [SerializeField] private bool _overwriteExistingAssets = false;

        private List<FrameData> _frameList = new();
        private HashSet<Transform> _trackedTransformSet = new();
        private Dictionary<Transform, Vector3> _lastPositionTable = new();
        private Dictionary<Transform, Quaternion> _lastRotationsTable = new();
        private float _timer = 0f;
        private float _duration = 0f;
        private float _nextRecordTime = 0f;
        private bool _isRecording = false;
        private AnimationClip _resultClip = null;
        private bool _isInitialized;
        private bool _isFaulted;
        private System.Exception _fault;

        #endregion


        #region Unity Event Functions

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("PhysicsRecorder is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("PhysicsRecorder is faulted; call Release before Init.", _fault);
            try
            {
                if (_root == null)
                    throw new InvalidOperationException("PhysicsRecorder requires a Root reference.");
                if (!IsFinite(_recordInterval) || _recordInterval <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_recordInterval));
                if (!IsFinite(_positionThreshold) || _positionThreshold < 0f
                    || !IsFinite(_rotationThreshold) || _rotationThreshold < 0f)
                    throw new ArgumentOutOfRangeException(nameof(_positionThreshold));
                if (string.IsNullOrWhiteSpace(_animationFileName))
                    throw new ArgumentException("Animation file name is required.", nameof(_animationFileName));
                ValidateAssetFileName(_animationFileName);
                ValidateAssetFolderPath(_savePath);
                _isInitialized = true;
            }
            catch (System.Exception exception)
            {
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("PhysicsRecorder has not been initialized.");
            if (_isRecording && _isInitialized)
                StopRecording();
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private void FixedUpdate()
        {
            if (_isFaulted)
                throw new InvalidOperationException("PhysicsRecorder is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("PhysicsRecorder is not initialized.");
            if (!_isRecording || _timer >= _duration) 
            {
                if(_isRecording)
                    StopRecording();
                    
                return;
            }

            _timer += Time.fixedDeltaTime;

            if (_timer >= _nextRecordTime)
            {
                _nextRecordTime = _timer + _recordInterval;

                foreach (var rb in _root.GetComponentsInChildren<Rigidbody>())
                {
                    if (!_trackedTransformSet.Contains(rb.transform))
                    {
                        _trackedTransformSet.Add(rb.transform);
                        _lastPositionTable[rb.transform] = rb.transform.localPosition;
                        _lastRotationsTable[rb.transform] = rb.transform.localRotation;
                    }
                }

                FrameData frame = new FrameData() { _time = _timer };
                bool anyChanged = false;

                foreach (var tf in _trackedTransformSet)
                {
                    Vector3 currentPos = tf.localPosition;
                    Quaternion currentRot = tf.localRotation;

                    bool moved = true;

                    if (_recordOnlyIfMoved)
                    {
                        bool posSame = Vector3.Distance(currentPos, _lastPositionTable[tf]) < _positionThreshold;
                        bool rotSame = Quaternion.Angle(currentRot, _lastRotationsTable[tf]) < _rotationThreshold;
                        moved = !(posSame && rotSame);
                    }

                    if (moved)
                    {
                        frame._positionTable[tf] = currentPos;
                        frame._rotationTable[tf] = currentRot;
                        _lastPositionTable[tf] = currentPos;
                        _lastRotationsTable[tf] = currentRot;
                        anyChanged = true;
                    }
                }

                if (anyChanged || !_recordOnlyIfMoved)
                    _frameList.Add(frame);
            }
        }

        #endregion


        #region Member Functions

        public void StartRecording(float duration, string animName = null)
        {
            if (_isFaulted)
                throw new InvalidOperationException("PhysicsRecorder is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("PhysicsRecorder is not initialized.");
            if (!IsFinite(duration) || duration <= 0f)
                throw new ArgumentOutOfRangeException(nameof(duration));

            _timer = 0f;
            _nextRecordTime = 0f;
            _duration = duration;
            if (!string.IsNullOrWhiteSpace(animName))
            {
                ValidateAssetFileName(animName);
                _animationFileName = animName;
            }
            if (string.IsNullOrWhiteSpace(_animationFileName))
                throw new ArgumentException("Animation file name is required.", nameof(animName));
            ValidateAssetFileName(_animationFileName);

            _isRecording = true;
            _frameList.Clear();
            _trackedTransformSet.Clear();
            _lastPositionTable.Clear();
            _lastRotationsTable.Clear();
        }

        public void StopRecording()
        {
            if (_isFaulted)
                throw new InvalidOperationException("PhysicsRecorder is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("PhysicsRecorder is not initialized.");
            if (!_isRecording)
                throw new InvalidOperationException("PhysicsRecorder is not recording.");

            ValidateOutputAssetTargets();
            _isRecording = false;
            try
            {
                SaveAnimationClip();
                StartCoroutine(SaveTrackedObjectsAsPrefabs());
            }
            catch
            {
                if (_resultClip != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(_resultClip)))
                    DestroyImmediate(_resultClip);
                _resultClip = null;
                throw;
            }
        }

        private IEnumerator SaveTrackedObjectsAsPrefabs()
        {
            const string TEMP_PARENT_NAME = "TempParent";
            const string PREFAB_EXTENSION = ".prefab";

            string savePath = ValidateAndCreateSaveFolder();

            GameObject tempParent = null;
            try
            {
                tempParent = Instantiate(_root.gameObject);
                tempParent.name = TEMP_PARENT_NAME;

                Animation animation = tempParent.AddComponent<Animation>();
                animation.AddClip(_resultClip, _animationFileName);

                yield return null;

                string prefabPath = $"{savePath}/{TEMP_PARENT_NAME}{PREFAB_EXTENSION}";
                GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (existingPrefab != null)
                {
                    if (!_overwriteExistingAssets)
                        throw new InvalidOperationException($"Prefab asset already exists: {prefabPath}");
                    if (!AssetDatabase.DeleteAsset(prefabPath))
                        throw new InvalidOperationException($"Unable to replace prefab asset: {prefabPath}");
                }

                GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(tempParent, prefabPath);
                if (savedPrefab == null || AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                    throw new InvalidOperationException($"Unable to save prefab asset: {prefabPath}");
                Debug.Log($"Object saved as prefab: {prefabPath}");
            }
            finally
            {
                if (tempParent != null)
                    DestroyImmediate(tempParent);
            }
        }

        private void SaveAnimationClip()
        {
            const float FRAME_RATE_DIVISOR = 1f;
            const string ANIM_EXTENSION = ".anim";
            const string POSITION_X_PROPERTY = "localPosition.x";
            const string POSITION_Y_PROPERTY = "localPosition.y";
            const string POSITION_Z_PROPERTY = "localPosition.z";
            const string ROTATION_X_PROPERTY = "localRotation.x";
            const string ROTATION_Y_PROPERTY = "localRotation.y";
            const string ROTATION_Z_PROPERTY = "localRotation.z";
            const string ROTATION_W_PROPERTY = "localRotation.w";

            _resultClip = new AnimationClip();
            _resultClip.frameRate = FRAME_RATE_DIVISOR / _recordInterval;

            var posCurves = new Dictionary<string, (AnimationCurve x, AnimationCurve y, AnimationCurve z)>();
            var rotCurves = new Dictionary<string, (AnimationCurve x, AnimationCurve y, AnimationCurve z, AnimationCurve w)>();

            foreach (var tf in _trackedTransformSet)
            {
                string path = AnimationUtility.CalculateTransformPath(tf, _root);
                posCurves[path] = (new(), new(), new());
                rotCurves[path] = (new(), new(), new(), new());
            }

            foreach (var frame in _frameList)
            {
                float t = frame._time;

                foreach (var kvp in frame._positionTable)
                {
                    string path = AnimationUtility.CalculateTransformPath(kvp.Key, _root);
                    var pos = kvp.Value;
                    posCurves[path].x.AddKey(t, pos.x);
                    posCurves[path].y.AddKey(t, pos.y);
                    posCurves[path].z.AddKey(t, pos.z);
                }

                foreach (var kvp in frame._rotationTable)
                {
                    string path = AnimationUtility.CalculateTransformPath(kvp.Key, _root);
                    var rot = kvp.Value;
                    rotCurves[path].x.AddKey(t, rot.x);
                    rotCurves[path].y.AddKey(t, rot.y);
                    rotCurves[path].z.AddKey(t, rot.z);
                    rotCurves[path].w.AddKey(t, rot.w);
                }
            }

            foreach (var (path, (x, y, z)) in posCurves)
            {
                _resultClip.SetCurve(path, typeof(Transform), POSITION_X_PROPERTY, x);
                _resultClip.SetCurve(path, typeof(Transform), POSITION_Y_PROPERTY, y);
                _resultClip.SetCurve(path, typeof(Transform), POSITION_Z_PROPERTY, z);
            }

            foreach (var (path, (x, y, z, w)) in rotCurves)
            {
                _resultClip.SetCurve(path, typeof(Transform), ROTATION_X_PROPERTY, x);
                _resultClip.SetCurve(path, typeof(Transform), ROTATION_Y_PROPERTY, y);
                _resultClip.SetCurve(path, typeof(Transform), ROTATION_Z_PROPERTY, z);
                _resultClip.SetCurve(path, typeof(Transform), ROTATION_W_PROPERTY, w);
            }

            string fileName = _animationFileName;
            ValidateAssetFileName(fileName);
            string savePath = ValidateAndCreateSaveFolder();
            string pathToSave = $"{savePath}/{fileName}{ANIM_EXTENSION}";
            
            // 애니메이션 클립 타입을 Legacy로 설정
            if(_convertToLegacy)
                _resultClip.legacy = true;

            AnimationClip existingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(pathToSave);
            if (existingClip != null)
            {
                if (!_overwriteExistingAssets)
                    throw new InvalidOperationException($"Animation asset already exists: {pathToSave}");
                if (!AssetDatabase.DeleteAsset(pathToSave))
                    throw new InvalidOperationException($"Unable to replace animation asset: {pathToSave}");
            }
            AssetDatabase.CreateAsset(_resultClip, pathToSave);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(pathToSave) == null)
                throw new InvalidOperationException($"Unable to save animation asset: {pathToSave}");
            AssetDatabase.SaveAssets();
            Debug.Log($"애니메이션 저장 완료: {pathToSave}");
        }

        private void ValidateOutputAssetTargets()
        {
            string savePath = ValidateAssetFolderPath(_savePath);
            ValidateAssetFileName(_animationFileName);

            string animationPath = $"{savePath}/{_animationFileName}.anim";
            UnityEngine.Object existingAnimation = AssetDatabase.LoadMainAssetAtPath(animationPath);
            if (existingAnimation != null
                && !(existingAnimation is AnimationClip)
                && _overwriteExistingAssets)
                throw new InvalidOperationException($"Animation output path is occupied by a non-animation asset: {animationPath}");
            if (existingAnimation != null && !_overwriteExistingAssets)
                throw new InvalidOperationException($"Animation asset already exists: {animationPath}");

            const string PREFAB_FILE_NAME = "TempParent.prefab";
            string prefabPath = $"{savePath}/{PREFAB_FILE_NAME}";
            UnityEngine.Object existingPrefab = AssetDatabase.LoadMainAssetAtPath(prefabPath);
            if (existingPrefab != null
                && !(existingPrefab is GameObject)
                && _overwriteExistingAssets)
                throw new InvalidOperationException($"Prefab output path is occupied by a non-prefab asset: {prefabPath}");
            if (existingPrefab != null && !_overwriteExistingAssets)
                throw new InvalidOperationException($"Prefab asset already exists: {prefabPath}");
        }

        #endregion

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private string ValidateAndCreateSaveFolder()
        {
            string normalizedPath = ValidateAssetFolderPath(_savePath);
            if (!AssetDatabase.IsValidFolder(normalizedPath))
            {
                System.IO.Directory.CreateDirectory(normalizedPath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            if (!AssetDatabase.IsValidFolder(normalizedPath))
                throw new InvalidOperationException($"Unable to create recorder output folder: {normalizedPath}");
            return normalizedPath;
        }

        private static string ValidateAssetFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Save path is required.", nameof(path));

            string normalizedPath = path.Replace('\\', '/').TrimEnd('/');
            if (!normalizedPath.StartsWith("Assets/", StringComparison.Ordinal)
                || normalizedPath.IndexOf("/../", StringComparison.Ordinal) >= 0
                || normalizedPath.EndsWith("/..", StringComparison.Ordinal)
                || normalizedPath.IndexOf("//", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Save path must be a normalized folder under Assets/.", nameof(path));
            return normalizedPath;
        }

        private static void ValidateAssetFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.IndexOfAny(new[] { '/', '\\' }) >= 0
                || fileName == "."
                || fileName == ".."
                || fileName.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Animation file name must be a simple file name.", nameof(fileName));
        }

        #endif
    }
}
