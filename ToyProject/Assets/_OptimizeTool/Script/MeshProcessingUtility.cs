#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool
{
    /// <summary>Shared static-mesh processing. It never mutates source meshes or importer settings.</summary>
    public static class MeshProcessingUtility
    {
        public static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Bake meshes in Edit Mode.");
        }

        public static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // Vector3.normalized returns zero below a fixed magnitude, which small
        // triangles and large transforms reach while still having a direction.
        private static Vector3 Direction(Vector3 v)
        {
            Vector3 unit = v.normalized;
            if (unit != Vector3.zero) return unit;
            float largest = Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
            return largest > 0f && IsFinite(largest) ? (v / largest).normalized : Vector3.zero;
        }

        // Compared with the edge lengths, so a triangle counts by shape and not by size.
        private static bool IsDegenerate(Vector3 edgeA, Vector3 edgeB, Vector3 cross)
            => cross.sqrMagnitude <= 1e-12f * edgeA.sqrMagnitude * edgeB.sqrMagnitude;
        public static void ValidateAngle(float angle)
        {
            if (!IsFinite(angle) || angle < 0 || angle > 180) throw new ArgumentOutOfRangeException(nameof(angle));
        }

        public static Mesh ReadableCopy(Mesh source, bool allowReadableCopy, bool allowNormalRecalculation)
        {
            if (source == null || source.vertexCount == 0) throw new ArgumentException("A non-empty source mesh is required.");
            if (!source.isReadable && !allowReadableCopy) throw new InvalidOperationException($"'{source.name}' is not readable. Enable Allow Readable Copy.");
            if (source.blendShapeCount > 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight)
                || source.HasVertexAttribute(VertexAttribute.BlendIndices))
                throw new ArgumentException("Only static meshes are supported; bake skinned/blend-shape meshes first.");
            Mesh copy = null;
            try
            {
                // Editor MeshUtility explicitly supports non-readable assets without changing the importer.
                using (Mesh.MeshDataArray read = UnityEditor.MeshUtility.AcquireReadOnlyMeshData(source))
                {
                    Mesh.MeshData data = read[0];
                    copy = new Mesh { name = source.name + "_Working", indexFormat = source.indexFormat };
                    copy.SetVertexBufferParams(data.vertexCount, source.GetVertexAttributes());
                    for (int stream = 0; stream < source.vertexBufferCount; stream++)
                    {
                        NativeArray<byte> bytes = data.GetVertexData<byte>(stream);
                        copy.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream, MeshUpdateFlags.DontRecalculateBounds);
                    }
                    copy.subMeshCount = data.subMeshCount;
                    for (int submesh = 0; submesh < data.subMeshCount; submesh++)
                    {
                        if (data.GetSubMesh(submesh).topology != MeshTopology.Triangles)
                            throw new ArgumentException("Only triangle submeshes are supported.");
                        using (var indices = new NativeArray<int>(data.GetSubMesh(submesh).indexCount, Allocator.Temp))
                        {
                            data.GetIndices(indices, submesh, true);
                            copy.SetTriangles(indices.ToArray(), submesh, false);
                        }
                    }
                }
                foreach (Vector3 vertex in copy.vertices)
                    if (!IsFinite(vertex)) throw new ArgumentException("Mesh contains non-finite positions.");
                if (!copy.HasVertexAttribute(VertexAttribute.Normal))
                {
                    if (!allowNormalRecalculation) throw new ArgumentException($"'{source.name}' has no normals. Enable Allow Normal Recalculation.");
                    copy.RecalculateNormals();
                }
                copy.RecalculateBounds();
                return copy;
            }
            catch { if (copy != null) Object.DestroyImmediate(copy); throw; }
        }

        /// <summary>Compacts vertices using their complete raw layout, including tangents and UV0..UV7.</summary>
        public static Mesh SelectTriangles(Mesh source, IList<int[]> submeshes, string name)
        {
            var map = new Dictionary<int, int>();
            var oldIndices = new List<int>();
            var remapped = new List<int[]>();
            foreach (int[] triangles in submeshes)
            {
                if (triangles == null || triangles.Length % 3 != 0) throw new ArgumentException("Invalid triangle data.");
                var indices = new int[triangles.Length];
                for (int i = 0; i < triangles.Length; i++)
                {
                    int old = triangles[i];
                    if (old < 0 || old >= source.vertexCount) throw new ArgumentException("Triangle index is out of range.");
                    if (!map.TryGetValue(old, out int mapped))
                    {
                        mapped = map.Count;
                        map.Add(old, mapped);
                        oldIndices.Add(old);
                    }
                    indices[i] = mapped;
                }
                remapped.Add(indices);
            }
            if (map.Count == 0) return null;
            var output = new Mesh { name = name, indexFormat = map.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                using (Mesh.MeshDataArray read = Mesh.AcquireReadOnlyMeshData(source))
                {
                    output.SetVertexBufferParams(map.Count, source.GetVertexAttributes());
                    for (int stream = 0; stream < source.vertexBufferCount; stream++)
                    {
                        int stride = source.GetVertexBufferStride(stream);
                        NativeArray<byte> input = read[0].GetVertexData<byte>(stream);
                        using (var bytes = new NativeArray<byte>(map.Count * stride, Allocator.Temp))
                        {
                            for (int i = 0; i < oldIndices.Count; i++)
                                NativeArray<byte>.Copy(input, oldIndices[i] * stride, bytes, i * stride, stride);
                            output.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream, MeshUpdateFlags.DontRecalculateBounds);
                        }
                    }
                }
                output.subMeshCount = remapped.Count;
                for (int i = 0; i < remapped.Count; i++) output.SetTriangles(remapped[i], i, false);
                output.RecalculateBounds();
                return output;
            }
            catch { Object.DestroyImmediate(output); throw; }
        }

        public static Mesh CullBackfaces(Mesh mesh, Transform source, Camera camera, float angle, bool useCustomVector, Vector3 customVector)
        {
            ValidateAngle(angle);
            if (!useCustomVector && camera == null) throw new ArgumentException("Assign a camera.");
            if (useCustomVector && (!IsFinite(customVector) || customVector.sqrMagnitude < 0.000001f))
                throw new ArgumentException("Custom view direction must be finite and non-zero.");
            Matrix4x4 matrix = source.localToWorldMatrix;
            if (matrix.determinant == 0f || !IsFinite(matrix.determinant)) throw new ArgumentException("Zero-scale meshes cannot be baked.");
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            Vector3[] vertices = mesh.vertices;
            var submeshes = new List<int[]>();
            float minimumDot = Mathf.Cos(angle * Mathf.Deg2Rad);
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                int[] triangles = mesh.GetTriangles(submesh);
                var kept = new List<int>();
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    if (IsDegenerate(b - a, c - a, cross)) continue;
                    Vector3 normal = Direction(normalMatrix.MultiplyVector(cross));
                    if (normal.sqrMagnitude < 0.000001f) continue;
                    Vector3 toCamera = useCustomVector ? -customVector.normalized
                        : camera.orthographic ? -camera.transform.forward
                        : (camera.transform.position - matrix.MultiplyPoint3x4((a + b + c) / 3f)).normalized;
                    if (Vector3.Dot(normal, toCamera) + 0.000001f < minimumDot) continue;
                    kept.Add(triangles[i]); kept.Add(triangles[i + 1]); kept.Add(triangles[i + 2]);
                }
                submeshes.Add(kept.ToArray());
            }
            return SelectTriangles(mesh, submeshes, mesh.name + "_BackfaceCulled");
        }

        public static void TransformMesh(Mesh mesh, Matrix4x4 matrix)
        {
            if (matrix.determinant == 0f || !IsFinite(matrix.determinant)) throw new ArgumentException("Zero-scale meshes cannot be combined.");
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            float sign = matrix.determinant < 0 ? -1 : 1;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            for (int i = 0; i < normals.Length; i++) normals[i] = Direction(normalMatrix.MultiplyVector(normals[i]));
            for (int i = 0; i < tangents.Length; i++)
            {
                Vector3 t = matrix.MultiplyVector(tangents[i]);
                if (normals.Length == vertices.Length) t = t - normals[i] * Vector3.Dot(normals[i], t);
                t = Direction(t);
                tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w * sign);
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            if (tangents.Length > 0) mesh.tangents = tangents;
            if (sign < 0)
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    int[] triangles = mesh.GetTriangles(submesh);
                    for (int i = 0; i < triangles.Length; i += 3)
                    { int swap = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = swap; }
                    mesh.SetTriangles(triangles, submesh, false);
                }
            mesh.RecalculateBounds();
        }

        public static Mesh Combine(IList<CombineInstance> inputs, bool mergeSubmeshes, bool optimize)
        {
            int vertices = 0;
            foreach (CombineInstance input in inputs) vertices = checked(vertices + input.mesh.vertexCount);
            var output = new Mesh { indexFormat = vertices > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            var copies = new List<Mesh>();
            try
            {
                // CombineMeshes keeps only channels shared by every input. Supply missing channels explicitly.
                bool colors = false, tangents = false;
                var uvDimensions = new int[8];
                foreach (CombineInstance input in inputs)
                {
                    colors |= input.mesh.HasVertexAttribute(VertexAttribute.Color);
                    tangents |= input.mesh.HasVertexAttribute(VertexAttribute.Tangent);
                    for (int channel = 0; channel < 8; channel++)
                        uvDimensions[channel] = Mathf.Max(uvDimensions[channel], input.mesh.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)));
                }
                var combines = new CombineInstance[inputs.Count];
                for (int i = 0; i < inputs.Count; i++)
                {
                    Mesh source = inputs[i].mesh;
                    bool needsCopy = colors && !source.HasVertexAttribute(VertexAttribute.Color)
                        || tangents && !source.HasVertexAttribute(VertexAttribute.Tangent);
                    for (int channel = 0; channel < 8; channel++)
                        needsCopy |= uvDimensions[channel] != source.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel));
                    if (!needsCopy) { combines[i] = inputs[i]; continue; }
                    Mesh copy = Object.Instantiate(source);
                    copies.Add(copy);
                    if (colors && !copy.HasVertexAttribute(VertexAttribute.Color))
                    {
                        var values = new Color[copy.vertexCount];
                        for (int j = 0; j < values.Length; j++) values[j] = Color.white;
                        copy.colors = values;
                    }
                    if (tangents && !copy.HasVertexAttribute(VertexAttribute.Tangent))
                    {
                        if (copy.HasVertexAttribute(VertexAttribute.TexCoord0)) copy.RecalculateTangents();
                        else
                        {
                            var values = new Vector4[copy.vertexCount];
                            for (int j = 0; j < values.Length; j++) values[j] = new Vector4(1, 0, 0, 1);
                            copy.tangents = values;
                        }
                    }
                    for (int channel = 0; channel < 8; channel++)
                    {
                        if (uvDimensions[channel] == 0) continue;
                        var uvs = new List<Vector4>();
                        copy.GetUVs(channel, uvs);
                        while (uvs.Count < copy.vertexCount) uvs.Add(Vector4.zero);
                        if (uvDimensions[channel] <= 2) copy.SetUVs(channel, uvs.ConvertAll(v => new Vector2(v.x, v.y)));
                        else if (uvDimensions[channel] == 3) copy.SetUVs(channel, uvs.ConvertAll(v => new Vector3(v.x, v.y, v.z)));
                        else copy.SetUVs(channel, uvs);
                    }
                    combines[i] = inputs[i];
                    combines[i].mesh = copy;
                }
                output.CombineMeshes(combines, mergeSubmeshes, true, false);
                if (optimize) output.Optimize();
                output.RecalculateBounds();
                return output;
            }
            catch { Object.DestroyImmediate(output); throw; }
            finally { foreach (Mesh copy in copies) Object.DestroyImmediate(copy); }
        }

        public static bool IsOccluded(MeshFilter filter, Mesh readable, Camera camera, int samplingCount, double threshold, int layerMask)
            => MeshVisibilityQuery.IsOccluded(filter, readable, camera, samplingCount, threshold, layerMask);

    }
}
#endif
