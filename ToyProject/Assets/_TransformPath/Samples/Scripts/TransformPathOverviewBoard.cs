using UnityEngine;
using UnityEngine.UI;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// Shows the path, follower, queue and event state of the TransformPath showcase.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class TransformPathOverviewBoard : MonoBehaviour
    {
        #region Member Variables

        [SerializeField] private Text _text;

        private bool _isInitialized;

        #endregion


        #region Unity Events

        public void Init()
        {
            if (_isInitialized)
                return;
            if (_text == null)
                _text = GetComponentInChildren<Text>(true);
            if (_text == null)
            {
                Debug.LogError("TransformPathOverviewBoard requires a UGUI Text reference.", this);
                return;
            }
            _isInitialized = true;
        }

        public void Release()
        {
            _isInitialized = false;
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnDestroy()
        {
            Release();
        }

        #endregion


        #region Public Methods

        public void Render(string value)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized || _text == null)
                return;
            _text.text = value;
        }

        #endregion
    }
}
