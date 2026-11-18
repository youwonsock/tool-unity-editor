using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>
    /// Combine settings and the scene output it owns. Building and saving run in the editor:
    /// <c>BuildMeshes()</c> and <c>CombineAndSaveMeshes()</c> are extension methods of the editor assembly.
    /// </summary>
    public class MeshCombiner : MonoBehaviour
    {
        [SerializeField] private string _savePath;
        [SerializeField] private string _meshName;
        [SerializeField] private bool _optimizeMesh = true;
        [SerializeField] private bool _includeInactive;
        [SerializeField] private Camera _targetCamera;
        [SerializeField] private MeshFilter[] _targetMeshFilterArray;
        [SerializeField] private bool _collectChildMeshes;
        [SerializeField] private bool _allowReadableCopy;
        [SerializeField] private bool _allowNormalRecalculation;
        [SerializeField] private bool _overwriteExistingAssets;
        [Header("Occlusion Culling Options")]
        [SerializeField] private bool _enableOcclusionCulling;
        [SerializeField] private int _vertexSamplingCount = 100;
        [SerializeField] private double _visibilityThreshold = 0.01;
        [SerializeField] private LayerMask _cullingLayerMask = ~0;
        [Header("BackFace Culling Options")]
        [SerializeField] private bool _enableBackfaceCulling;
        [SerializeField] private float _backfaceCullingAngle = 90f;
        [Header("Final Combine Options")]
        [SerializeField] private bool _combineAllIntoSingleMesh;
        [SerializeField] private bool _createSceneOutput = true;
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
            BackfaceCulling = _enableBackfaceCulling,
            BackfaceAngle = _backfaceCullingAngle,
            OcclusionCulling = _enableOcclusionCulling,
            OcclusionSampleCount = _vertexSamplingCount,
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
