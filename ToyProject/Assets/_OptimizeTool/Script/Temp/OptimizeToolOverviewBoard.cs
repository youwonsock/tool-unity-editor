using System;
using UnityEngine;
using UnityEngine.UI;

namespace Common.OptimizeTool.Samples
{
    /// <summary>
    /// OptimizeTool 쇼케이스의 결과 그룹별 통계를 표시합니다.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class OptimizeToolOverviewBoard : MonoBehaviour
    {
        [SerializeField] private Text _text;

        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

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
                throw new InvalidOperationException("OptimizeToolOverviewBoard is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewBoard is faulted; call Release before Init.", _fault);
            try
            {
                if (_text == null)
                    throw new InvalidOperationException("OptimizeToolOverviewBoard requires a serialized UGUI Text reference.");
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _fault = exception;
                _isFaulted = true;
                throw;
            }
        }

        public void Render(string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (_isFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewBoard is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("OptimizeToolOverviewBoard is not initialized.");
            _text.text = value;
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewBoard has not been initialized.");
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }
    }
}
