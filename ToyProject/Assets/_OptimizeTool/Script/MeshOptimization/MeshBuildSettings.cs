using System;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>
    /// Geometry options of one mesh build: input handling, view-dependent culling and output layout. It holds no save
    /// path and no scene state, and it is not serialized; <see cref="MeshCombiner"/> creates one from its fields.
    /// </summary>
    public sealed class MeshBuildSettings
    {
        /// <summary>View for backface and occlusion culling. Required when either is enabled.</summary>
        public Camera Camera { get; set; }
        /// <summary>Copy meshes whose Read/Write is off instead of failing. The importer is never changed.</summary>
        public bool AllowReadableCopy { get; set; }
        /// <summary>Recalculate normals of meshes that have none instead of failing.</summary>
        public bool AllowNormalRecalculation { get; set; }
        /// <summary>Reorder vertices and indices of each output for the GPU cache. Not a polygon reduction.</summary>
        public bool OptimizeMesh { get; set; } = true;
        /// <summary>
        /// When the sources are lightmap static, give each source its own tile of the output's lightmap UVs (UV2, or
        /// UV0 for a source without UV2) so their lightmaps do not overlap. Off leaves the UVs as they are.
        /// </summary>
        public bool PackLightmapUvs { get; set; } = true;
        /// <summary>Merge the per-material outputs into one mesh with one submesh per material.</summary>
        public bool CombineAllIntoSingleMesh { get; set; }
        public bool BackfaceCulling { get; set; }
        /// <summary>Largest angle, in degrees, between a triangle normal and the view direction that is kept.</summary>
        public float BackfaceCullingAngle { get; set; } = 90f;
        public bool OcclusionCulling { get; set; }
        /// <summary>Most sample points tested per source.</summary>
        public int OcclusionSampleCount { get; set; } = 100;
        /// <summary>A source whose visible sample ratio is below this value is removed.</summary>
        public double VisibilityThreshold { get; set; } = 0.01;
        /// <summary>Colliders on these layers are occluders.</summary>
        public int CullingLayerMask { get; set; } = ~0;

        /// <summary>The only validation of these settings; the build steps rely on it.</summary>
        public void Validate()
        {
            ValidateBackfaceAngle(BackfaceCullingAngle, nameof(BackfaceCullingAngle));
            if ((BackfaceCulling || OcclusionCulling) && Camera == null)
                throw new ArgumentException("Culling requires a target camera.");
            if (OcclusionCulling) ValidateOcclusionSampling(OcclusionSampleCount, VisibilityThreshold, CullingLayerMask);
        }

        /// <summary>The backface rule of every bake: a finite angle from 0 to 180 degrees.</summary>
        internal static void ValidateBackfaceAngle(float angle, string paramName)
        {
            if (float.IsNaN(angle) || float.IsInfinity(angle) || angle < 0 || angle > 180)
                throw new ArgumentOutOfRangeException(paramName, angle, "The backface angle must be from 0 to 180 degrees.");
        }

        /// <summary>The occlusion rule of every bake: positive samples, a threshold from 0 to 1 and a non-empty mask.</summary>
        internal static void ValidateOcclusionSampling(int sampleCount, double visibilityThreshold, int layerMask)
        {
            if (sampleCount <= 0 || double.IsNaN(visibilityThreshold) || double.IsInfinity(visibilityThreshold)
                || visibilityThreshold < 0 || visibilityThreshold > 1 || layerMask == 0)
                throw new ArgumentException("Occlusion sampling settings are invalid.");
        }
    }
}
