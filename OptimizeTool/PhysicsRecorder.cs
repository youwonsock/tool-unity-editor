using System.Collections.Generic;
using UnityEngine;
using System.Collections;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Supercent.Common.OptimizeTool
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
        [SerializeField] private string _savePath = "Assets";
        [SerializeField] private string _animationFileName = "";
        [SerializeField] private bool _convertToLegacy = false;

        private List<FrameData> _frameList = new();
        private HashSet<Transform> _trackedTransformSet = new();
        private Dictionary<Transform, Vector3> _lastPositionTable = new();
        private Dictionary<Transform, Quaternion> _lastRotationsTable = new();
        private float _timer = 0f;
        private float _duration = 0f;
        private float _nextRecordTime = 0f;
        private bool _isRecording = false;
        private AnimationClip _resultClip = null;

        #endregion


        #region Unity Event Functions

        private void FixedUpdate()
        {
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
            const float TIMER_RESET_VALUE = 0f;

            _timer = TIMER_RESET_VALUE;
            _nextRecordTime = TIMER_RESET_VALUE;
            _duration = duration;
            if(animName != null)
                _animationFileName = animName;

            _isRecording = true;
            _frameList.Clear();
            _trackedTransformSet.Clear();
            _lastPositionTable.Clear();
            _lastRotationsTable.Clear();
        }

        public void StopRecording()
        {
            _isRecording = false;
            SaveAnimationClip();
            StartCoroutine(SaveTrackedObjectsAsPrefabs());
        }

        private IEnumerator SaveTrackedObjectsAsPrefabs()
        {
            const string DEFAULT_SAVE_PATH = "Assets";
            const string TEMP_PARENT_NAME = "TempParent";
            const string PREFAB_EXTENSION = ".prefab";

            string savePath = string.IsNullOrEmpty(_savePath) ? DEFAULT_SAVE_PATH : _savePath;

            if (!System.IO.Directory.Exists(savePath))
                System.IO.Directory.CreateDirectory(savePath);

            GameObject tempParent = new GameObject(TEMP_PARENT_NAME);
            
            foreach (var tf in _trackedTransformSet)
            {
                Component[] components = tf.GetComponents<Component>();

                foreach (Component component in components)
                {
                    if (!(component is Transform) && !(component is MeshRenderer) 
                        && !(component is MeshFilter) && !(component is SkinnedMeshRenderer))
                        Destroy(component);
                }

                tf.SetParent(tempParent.transform, true);
            }

            Animation animation = tempParent.AddComponent<Animation>();
            animation.AddClip(_resultClip, _animationFileName);

            yield return null;

            string prefabPath = $"{savePath}/{TEMP_PARENT_NAME}{PREFAB_EXTENSION}";
            PrefabUtility.SaveAsPrefabAsset(tempParent, prefabPath);
            Debug.Log($"Object saved as prefab: {prefabPath}");
        }

        private void SaveAnimationClip()
        {
            const float FRAME_RATE_DIVISOR = 1f;
            const string DEFAULT_SAVE_PATH = "Assets";
            const string ANIM_EXTENSION = ".anim";
            const string RUNTIME_SUFFIX = "_Runtime";
            const string POSITION_X_PROPERTY = "localPosition.x";
            const string POSITION_Y_PROPERTY = "localPosition.y";
            const string POSITION_Z_PROPERTY = "localPosition.z";
            const string ROTATION_X_PROPERTY = "localRotation.x";
            const string ROTATION_Y_PROPERTY = "localRotation.y";
            const string ROTATION_Z_PROPERTY = "localRotation.z";
            const string ROTATION_W_PROPERTY = "localRotation.w";

            _resultClip = new AnimationClip();
            if (_recordInterval < 0)
                _recordInterval = 0.02f;

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

            string fileName = string.IsNullOrEmpty(_animationFileName) ? $"Recorded_{_root.name}{RUNTIME_SUFFIX}" : _animationFileName;
            string savePath = string.IsNullOrEmpty(_savePath) ? DEFAULT_SAVE_PATH : _savePath;
            string pathToSave = $"{savePath}/{fileName}{ANIM_EXTENSION}";
            
            if (!System.IO.Directory.Exists(savePath))
                System.IO.Directory.CreateDirectory(savePath);
            
            // 애니메이션 클립 타입을 Legacy로 설정
            if(_convertToLegacy)
                _resultClip.legacy = true;

            AssetDatabase.CreateAsset(_resultClip, pathToSave);
            AssetDatabase.SaveAssets();
            Debug.Log($"애니메이션 저장 완료: {pathToSave}");
        }

        #endregion

        #endif
    }
}