using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Runtime-only PlayerLoop entry point for the Core cooperative
    /// scheduler. Core never creates this component itself.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    internal sealed class FlowFieldBuildDriver : MonoBehaviour
    {
        private static FlowFieldBuildDriver _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeDriver()
        {
            if (!Application.isPlaying || _instance != null)
                return;

            FlowFieldBuildDriver[] existing = FindObjectsByType<FlowFieldBuildDriver>(FindObjectsSortMode.None);
            if (existing != null && existing.Length > 0)
            {
                _instance = existing[0];
                for (int index = 1; index < existing.Length; index++)
                    Destroy(existing[index].gameObject);
                return;
            }

            GameObject driverObject = new GameObject(nameof(FlowFieldBuildDriver));
            driverObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(driverObject);
            _instance = driverObject.AddComponent<FlowFieldBuildDriver>();
        }

        private void LateUpdate()
        {
            if (Application.isPlaying)
                FlowFieldBuildScheduler.PumpAll(2.0d);
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }
    }
}
