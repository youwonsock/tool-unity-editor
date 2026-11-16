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
        // Enter Play Mode Options disable domain reload in this project, so
        // static scheduler state is reset here, before any scene object wakes.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            FlowFieldBuildSettings.ResetToDefault();
            FlowFieldBuildScheduler.ResetForNewPlaySession();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeDriver()
        {
            if (!Application.isPlaying)
                return;

            // This runs once per Play session, so any driver already present
            // is a leftover. FindObjectsByType cannot see a hidden object.
            foreach (FlowFieldBuildDriver stale in Resources.FindObjectsOfTypeAll<FlowFieldBuildDriver>())
                Destroy(stale.gameObject);

            GameObject driverObject = new GameObject(nameof(FlowFieldBuildDriver));
            // Without DontSave the driver ends with Play Mode instead of leaking into the Editor.
            driverObject.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(driverObject);
            driverObject.AddComponent<FlowFieldBuildDriver>();
        }

        private void LateUpdate()
        {
            if (Application.isPlaying)
                FlowFieldBuildScheduler.PumpAll(FlowFieldBuildSettings.BudgetMilliseconds);
        }
    }
}
