using System;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// PathFollower를 시작해 경로를 반복 이동하는 샘플 Actor입니다.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public sealed class TransformPathSampleController : MonoBehaviour
    {
        [Header("Path")]
        [SerializeField] private PathData _pathData;
        [SerializeField] private PathFollower _pathFollower;
        [SerializeField] private float _speed = 3f;
        [SerializeField] private bool _loop = true;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        public PathFollower PathFollower => _pathFollower;
        public PathData PathData => _pathData;
        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("TransformPathSampleController is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("TransformPathSampleController is faulted; call Release before Init.", _fault);
            try
            {
                if (_pathData == null || _pathFollower == null)
                    throw new InvalidOperationException("TransformPathSampleController requires serialized PathData and PathFollower references.");
                if (!_pathData.IsInitialized || !_pathData.IsReady)
                    throw new InvalidOperationException("PathData must be initialized and ready before the sample controller.");
                if (!_pathFollower.IsInitialized)
                    throw new InvalidOperationException("PathFollower must be initialized before the sample controller.");
                if (float.IsNaN(_speed) || float.IsInfinity(_speed) || _speed <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_speed));
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        private void Start()
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("TransformPathSampleController is not initialized.");
            _pathFollower.PathData = _pathData;
            _pathFollower.CurrentMoveType = PathFollower.EMoveType.SpeedBased;
            _pathFollower.Speed = _speed;
            _pathFollower.Loop = _loop;
            _pathFollower.StartMove(_pathData);
        }

        private void ThrowIfFaulted()
        {
            if (_isFaulted)
                throw new InvalidOperationException(
                    "TransformPathSampleController is faulted; call Release before use.",
                    _fault);
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("TransformPathSampleController has not been initialized.");
            if (_isInitialized && _pathFollower != null && _pathFollower.IsMoving)
                _pathFollower.StopMove();
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }
    }
}
