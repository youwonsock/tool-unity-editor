using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>
    /// Combine settings and the scene output it owns. Building, saving and restoring run in the editor:
    /// <c>BuildMeshes()</c>, <c>CombineAndSaveMeshes()</c> and <c>RestoreSources()</c> are extension methods of the
    /// editor assembly.
    /// </summary>
    public class MeshCombiner : MonoBehaviour
    {
        [Tooltip("Folder under Assets/ where the combined meshes are saved.")]
        [SerializeField] private string _savePath;
        [Tooltip("Asset name: name.asset for a single mesh, otherwise name_0.asset, name_1.asset, ...")]
        [SerializeField] private string _meshName;
        [Tooltip("Reorder vertices and indices for the GPU cache. This is not a polygon reduction.")]
        [SerializeField] private bool _optimizeMesh = true;
        [Tooltip("With child collection, also collect MeshFilters on inactive GameObjects.")]
        [SerializeField] private bool _includeInactive;
        [Tooltip("View for backface and occlusion culling. Required when either is enabled.")]
        [SerializeField] private Camera _targetCamera;
        [Tooltip("MeshFilters to combine. Leave empty when collecting child meshes.")]
        [SerializeField] private MeshFilter[] _targetMeshFilterArray;
        [Tooltip("Combine the MeshFilters under this object instead of the assigned list.")]
        [SerializeField] private bool _collectChildMeshes;
        [Tooltip("Copy meshes whose Read/Write is off instead of failing. The importer is not changed.")]
        [SerializeField] private bool _allowReadableCopy;
        [Tooltip("Recalculate normals of meshes that have none instead of failing.")]
        [SerializeField] private bool _allowNormalRecalculation;
        [Tooltip("Replace existing mesh assets at the output paths, keeping their GUIDs.")]
        [SerializeField] private bool _overwriteExistingAssets;
        [Tooltip("Keep Read/Write on the saved meshes, for scripts or colliders that read them at runtime. Off saves memory in Players.")]
        [SerializeField] private bool _keepReadable;
        [Header("Occlusion Culling Options")]
        [Tooltip("Remove sources whose sample points are hidden from the camera by Colliders.")]
        [SerializeField] private bool _enableOcclusionCulling;
        [Tooltip("Most sample points tested per source: vertices and triangle centers.")]
        [FormerlySerializedAs("_vertexSamplingCount")]
        [SerializeField] private int _occlusionSampleCount = 100;
        [Tooltip("A source whose visible sample ratio is below this value is removed.")]
        [SerializeField] private double _visibilityThreshold = 0.01;
        [Tooltip("Colliders on these layers are occluders.")]
        [SerializeField] private LayerMask _cullingLayerMask = ~0;
        [Header("Backface Culling Options")]
        [Tooltip("Remove triangles that face away from the camera.")]
        [SerializeField] private bool _enableBackfaceCulling;
        [Tooltip("Largest angle, in degrees, between a triangle normal and the view direction that is kept.")]
        [SerializeField] private float _backfaceCullingAngle = 90f;
        [Header("Final Combine Options")]
        [Tooltip("Merge the per-material meshes into one mesh with one submesh per material.")]
        [SerializeField] private bool _combineAllIntoSingleMesh;
        [Tooltip("For lightmap static sources, pack each source's lightmap UVs into its own tile so baked lightmaps do not overlap.")]
        [SerializeField] private bool _packLightmapUVs = true;
        [Tooltip("Create renderers for the saved meshes under this object.")]
        [SerializeField] private bool _createSceneOutput = true;
        [Tooltip("Disable the source Renderers after creating the scene output. Restore Sources turns them back on.")]
        [SerializeField] private bool _disableSourceRenderers = true;
        [SerializeField, HideInInspector] private GameObject _generatedRoot;
        [SerializeField, HideInInspector] private List<MeshSourceRendererState> _sourceRendererStates = new List<MeshSourceRendererState>();

        /// <summary>Warnings of the last build, shown by the inspector.</summary>
        public IReadOnlyList<string> LastBuildWarnings { get; internal set; } = Array.Empty<string>();

        /// <summary>The geometry options of these fields. A new object each call; editing it does not change the component.</summary>
        internal MeshBuildSettings CreateSettings() => new MeshBuildSettings
        {
            Camera = _targetCamera,
            AllowReadableCopy = _allowReadableCopy,
            AllowNormalRecalculation = _allowNormalRecalculation,
            OptimizeMesh = _optimizeMesh,
            CombineIntoSingleMesh = _combineAllIntoSingleMesh,
            PackLightmapUVs = _packLightmapUVs,
            BackfaceCulling = _enableBackfaceCulling,
            BackfaceAngle = _backfaceCullingAngle,
            OcclusionCulling = _enableOcclusionCulling,
            OcclusionSampleCount = _occlusionSampleCount,
            VisibilityThreshold = _visibilityThreshold,
            OcclusionLayerMask = _cullingLayerMask,
        };

        // Input selection, save target and scene output: what the editor commands need besides the build options.
        internal MeshFilter[] TargetMeshFilters => _targetMeshFilterArray;
        internal bool CollectChildMeshes => _collectChildMeshes;
        internal bool IncludeInactive => _includeInactive;
        internal string SavePath => _savePath;
        internal string MeshName => _meshName;
        internal bool OverwriteExistingAssets => _overwriteExistingAssets;
        internal bool KeepReadable => _keepReadable;
        internal bool CreateSceneOutput => _createSceneOutput;
        internal bool DisableSourceRenderers => _disableSourceRenderers;

        /// <summary>The scene output of the last combine; excluded from child collection.</summary>
        internal GameObject GeneratedRoot { get => _generatedRoot; set => _generatedRoot = value; }

        /// <summary>The original enabled state of every source Renderer the scene output disabled.</summary>
        internal List<MeshSourceRendererState> SourceRendererStates
        {
            get => _sourceRendererStates;
            set => _sourceRendererStates = value;
        }
    }
}
