#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.OptimizeTool
{
    /// <summary>Scene-dependent visibility approximation, kept separate from geometry processing.</summary>
    internal static class MeshVisibilityQuery
    {
        public static bool IsOccluded(MeshFilter filter, Mesh readable, Camera camera, int samplingCount, double threshold, int layerMask)
        {
            if (samplingCount <= 0 || double.IsNaN(threshold) || threshold < 0 || threshold > 1 || camera == null)
                throw new ArgumentException("Invalid occlusion settings.");
            Vector3[] vertices = readable.vertices;
            int count = Mathf.Min(vertices.Length, samplingCount), visible = 0;
            PhysicsScene physics = filter.gameObject.scene.GetPhysicsScene();
            var hits = new RaycastHit[32];
            for (int i = 0; i < count; i++)
            {
                Vector3 point = filter.transform.TransformPoint(vertices[(int)((long)i * vertices.Length / count)]);
                Vector3 origin = camera.transform.position;
                if (camera.orthographic)
                    origin = point + camera.transform.forward * Vector3.Dot(origin - point, camera.transform.forward);
                Vector3 delta = point - origin;
                float distance = delta.magnitude;
                if (distance < 0.002f) { visible++; continue; }
                Vector3 direction = delta / distance;
                int hitCount;
                do
                {
                    hitCount = physics.Raycast(origin + direction * 0.001f, direction, hits, distance - 0.002f,
                        layerMask, QueryTriggerInteraction.Ignore);
                    if (hitCount < hits.Length) break;
                    Array.Resize(ref hits, checked(hits.Length * 2));
                } while (true);
                bool blocked = false;
                for (int h = 0; h < hitCount; h++)
                {
                    Transform colliderTransform = hits[h].collider.transform;
                    if (colliderTransform == filter.transform || colliderTransform.IsChildOf(filter.transform)) continue;
                    blocked = true;
                    break;
                }
                if (!blocked) visible++;
            }
            return count > 0 && (double)visible / count < threshold;
        }
    }
}
#endif
