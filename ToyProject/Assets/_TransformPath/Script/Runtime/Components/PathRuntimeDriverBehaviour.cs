using UnityEngine;

namespace Common.TransformPath
{
    [DefaultExecutionOrder(-100)]
    internal sealed class PathRuntimeDriverBehaviour : MonoBehaviour
    {
        private static PathRuntimeDriverBehaviour _instance;
        private readonly PathRuntimeDriver _driver = new PathRuntimeDriver();

        public static bool HasInstance => _instance != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            if (_instance != null)
            {
                if (Application.isPlaying)
                    Destroy(_instance.gameObject);
                else
                    DestroyImmediate(_instance.gameObject);
            }
            _instance = null;
        }

        public static PathRuntimeDriverBehaviour Instance
        {
            get
            {
                if (_instance != null)
                    return _instance;
                GameObject host = new GameObject("TransformPath Runtime Driver");
                host.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<PathRuntimeDriverBehaviour>();
                return _instance;
            }
        }

        public void Register(IPathRuntimeTickable item)
        {
            _driver.Register(item);
        }

        public void Unregister(IPathRuntimeTickable item)
        {
            _driver.Unregister(item);
        }

        private void Update()
        {
            _driver.Tick(
                Time.deltaTime,
                Time.unscaledDeltaTime,
                Time.frameCount);
            if (_driver.LastException != null)
            {
                Debug.LogException(_driver.LastException, this);
            }
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }
    }
}
