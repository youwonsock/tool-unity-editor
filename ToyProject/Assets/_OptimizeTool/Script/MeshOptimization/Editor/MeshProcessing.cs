using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>
    /// The mesh operations of the build pipeline, for editor tools that assemble their own bake. Every method checks
    /// its arguments before any work and never changes a source mesh or its importer; finite values and ranges are
    /// checked inside each method. A returned mesh is new and transient: the caller saves or destroys it.
    /// </summary>
    public static class MeshProcessing
    {
        /// <summary>
        /// A readable working copy of a static triangle mesh. The raw vertex buffers are copied, so every attribute
        /// and format survives.
        /// </summary>
        /// <param name="allowReadableCopy">Copy a mesh whose Read/Write is off instead of failing.</param>
        /// <param name="allowNormalRecalculation">Recalculate missing normals instead of failing.</param>
        public static Mesh ReadableCopy(Mesh source, bool allowReadableCopy = false, bool allowNormalRecalculation = false)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return MeshGeometry.ReadableCopy(source, allowReadableCopy, allowNormalRecalculation);
        }

        /// <summary>
        /// A compact mesh with the listed triangles of <paramref name="source"/>, one submesh per entry of
        /// <paramref name="submeshes"/>, keeping the complete vertex layout. Null when no entry has a triangle.
        /// </summary>
        public static Mesh SelectTriangles(Mesh source, IList<int[]> submeshes, string name)
        {
            RequireReadable(source, nameof(source));
            if (submeshes == null) throw new ArgumentNullException(nameof(submeshes));
            if (name == null) throw new ArgumentNullException(nameof(name));
            return MeshGeometry.SelectTriangles(source, submeshes, name);
        }

        /// <summary>
        /// The triangles of <paramref name="mesh"/>, drawn by <paramref name="source"/>, whose world normal is within
        /// <paramref name="angle"/> degrees of the direction to <paramref name="camera"/>: toward its position, or
        /// against its forward when it is orthographic. Null when every triangle faces away.
        /// </summary>
        public static Mesh CullBackfaces(Mesh mesh, Transform source, Camera camera, float angle)
        {
            RequireCullInputs(mesh, source, angle);
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            return MeshGeometry.CullBackfaces(mesh, source, camera, angle);
        }

        /// <summary>
        /// The triangles of <paramref name="mesh"/>, drawn by <paramref name="source"/>, whose world normal is within
        /// <paramref name="angle"/> degrees of the reverse of <paramref name="viewDirection"/>, the direction a distant
        /// viewer looks along. Null when every triangle faces away.
        /// </summary>
        public static Mesh CullBackfaces(Mesh mesh, Transform source, Vector3 viewDirection, float angle)
        {
            RequireCullInputs(mesh, source, angle);
            ValidateViewDirection(viewDirection);
            return MeshGeometry.CullBackfaces(mesh, source, viewDirection, angle);
        }

        /// <summary>
        /// Moves the vertices, normals and tangents of a transient mesh by <paramref name="matrix"/> and flips the
        /// winding when the matrix mirrors. The mesh is changed in place, so saved assets are rejected.
        /// </summary>
        public static void TransformMesh(Mesh mesh, Matrix4x4 matrix)
        {
            RequireReadable(mesh, nameof(mesh));
            if (EditorUtility.IsPersistent(mesh))
                throw new ArgumentException("TransformMesh changes the mesh it gets; pass a copy, not a saved asset.", nameof(mesh));
            RequireFinite(matrix, nameof(matrix));
            MeshGeometry.TransformMesh(mesh, matrix);
        }

        /// <summary>
        /// Combines <paramref name="inputs"/> into a new mesh. Inputs that miss a color, tangent or UV channel another
        /// input has get it filled, so no channel is dropped. 32-bit indices are used above 65,535 vertices.
        /// </summary>
        /// <param name="mergeSubmeshes">Merge everything into one submesh instead of one submesh per input.</param>
        /// <param name="optimize">Reorder vertices and indices for the GPU cache. This is not a polygon reduction.</param>
        public static Mesh Combine(IList<CombineInstance> inputs, bool mergeSubmeshes, bool optimize)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (inputs.Count == 0) throw new ArgumentException("At least one input is required.", nameof(inputs));
            foreach (CombineInstance input in inputs)
            {
                RequireReadable(input.mesh, nameof(inputs));
                RequireFinite(input.transform, nameof(inputs));
            }
            return MeshGeometry.Combine(inputs, mergeSubmeshes, optimize);
        }

        /// <summary>
        /// Whether <paramref name="filter"/> is hidden from <paramref name="camera"/>: the share of its sample points
        /// (the vertices and triangle centers of <paramref name="readable"/>, at most <paramref name="sampleCount"/>)
        /// that raycasts reach without hitting a Collider on <paramref name="layerMask"/> is below
        /// <paramref name="visibilityThreshold"/>. The source's own Colliders do not occlude it.
        /// </summary>
        /// <param name="readable">The readable geometry of the filter's mesh, such as its <see cref="ReadableCopy"/>.</param>
        public static bool IsOccluded(MeshFilter filter, Mesh readable, Camera camera, int sampleCount,
            double visibilityThreshold, int layerMask)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            if (!filter.gameObject.scene.IsValid())
                throw new ArgumentException($"'{filter.name}' must be in a scene, whose physics is queried.", nameof(filter));
            RequireReadable(readable, nameof(readable));
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            MeshBuildSettings.ValidateOcclusionSampling(sampleCount, visibilityThreshold, layerMask);
            // Colliders moved since the last physics step are not where the raycasts look until synced.
            Physics.SyncTransforms();
            return MeshOcclusion.IsOccluded(filter, readable, camera, sampleCount, visibilityThreshold, layerMask);
        }

        /// <summary>The view direction rule of every bake: finite and not zero.</summary>
        internal static void ValidateViewDirection(Vector3 direction)
        {
            if (!IsFinite(direction.x) || !IsFinite(direction.y) || !IsFinite(direction.z) || direction.sqrMagnitude < 0.000001f)
                throw new ArgumentException("The view direction must be finite and non-zero.", nameof(direction));
        }

        private static void RequireCullInputs(Mesh mesh, Transform source, float angle)
        {
            RequireReadable(mesh, nameof(mesh));
            if (source == null) throw new ArgumentNullException(nameof(source));
            MeshBuildSettings.ValidateBackfaceAngle(angle, nameof(angle));
        }

        private static void RequireReadable(Mesh mesh, string paramName)
        {
            if (mesh == null) throw new ArgumentNullException(paramName);
            if (!mesh.isReadable)
                throw new ArgumentException($"'{mesh.name}' is not readable; pass its ReadableCopy.", paramName);
            if (mesh.vertexCount == 0) throw new ArgumentException($"'{mesh.name}' has no vertices.", paramName);
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    throw new ArgumentException($"'{mesh.name}' has a submesh that is not made of triangles.", paramName);
        }

        private static void RequireFinite(Matrix4x4 matrix, string paramName)
        {
            for (int i = 0; i < 16; i++)
                if (!IsFinite(matrix[i])) throw new ArgumentException("The matrix must be finite.", paramName);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
