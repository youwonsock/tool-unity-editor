using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>
    /// Scene-dependent visibility approximation, kept separate from geometry processing. Sample points of a source are
    /// tested with raycasts from the camera against the occluder Colliders; a source whose visible ratio is below the
    /// threshold is occluded.
    /// </summary>
    internal static class MeshOcclusion
    {
        // A point within this distance of an ancestor Collider counts as inside it.
        private const float InsideToleranceSqr = 1e-6f;

        public static bool IsOccluded(MeshFilter filter, Mesh readable, Camera camera, int samplingCount, double threshold, int layerMask)
        {
            List<Vector3> samples = SamplePoints(readable, samplingCount);
            PhysicsScene physics = filter.gameObject.scene.GetPhysicsScene();
            var hits = new RaycastHit[32];
            int visible = 0;
            foreach (Vector3 local in samples)
            {
                Vector3 point = filter.transform.TransformPoint(local);
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
                    if (BelongsToSource(hits[h].collider, filter.transform, point)) continue;
                    blocked = true;
                    break;
                }
                if (!blocked) visible++;
            }
            return samples.Count > 0 && (double)visible / samples.Count < threshold;
        }

        /// <summary>
        /// Colliders of the source itself and of its children never occlude it. An ancestor's Collider that contains the
        /// sample point is the source's own proxy (a parent Rigidbody with the visual as a child, for example), so it
        /// does not occlude that point either. Only Box, Sphere, Capsule and convex Mesh Colliders can tell whether a
        /// point is inside; every other ancestor Collider still occludes.
        /// </summary>
        private static bool BelongsToSource(Collider collider, Transform source, Vector3 point)
        {
            Transform colliderTransform = collider.transform;
            if (colliderTransform == source || colliderTransform.IsChildOf(source)) return true;
            if (!source.IsChildOf(colliderTransform)) return false;
            bool supportsClosestPoint = collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider
                || collider is MeshCollider mesh && mesh.convex;
            return supportsClosestPoint && (collider.ClosestPoint(point) - point).sqrMagnitude <= InsideToleranceSqr;
        }

        /// <summary>
        /// The vertices followed by the centroid of every non-degenerate triangle, so a large face with few vertices is
        /// also sampled inside. When there are more candidates than <paramref name="count"/>, evenly spaced ones are
        /// taken; the result is deterministic.
        /// </summary>
        private static List<Vector3> SamplePoints(Mesh mesh, int count)
        {
            Vector3[] vertices = mesh.vertices;
            var candidates = new List<Vector3>(vertices);
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                int[] triangles = mesh.GetTriangles(submesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                    if (!MeshGeometry.IsDegenerate(b - a, c - a, Vector3.Cross(b - a, c - a))) candidates.Add((a + b + c) / 3f);
                }
            }
            if (candidates.Count <= count) return candidates;
            var samples = new List<Vector3>(count);
            for (int i = 0; i < count; i++) samples.Add(candidates[(int)((long)i * candidates.Count / count)]);
            return samples;
        }
    }
}
