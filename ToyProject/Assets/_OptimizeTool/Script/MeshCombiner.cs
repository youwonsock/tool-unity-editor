using UnityEngine;
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
#endif

namespace Common.OptimizeTool
{
    public class MeshCombiner : MonoBehaviour
    {
#if UNITY_EDITOR
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
        public IReadOnlyList<string> LastBuildWarnings { get; private set; } = Array.Empty<string>();

        /// <summary>Creates in-memory results only. Dispose the result after saving or inspecting it.</summary>
        public MeshBuildResult BuildMeshes()
        {
            LastBuildWarnings = Array.Empty<string>();
            var result = MeshBuildPipeline.Build(CaptureBuildRequest());
            LastBuildWarnings = result.Warnings;
            return result;
        }

        private MeshBuildRequest CaptureBuildRequest()
        {
            MeshProcessingUtility.RequireEditMode();
            if (_collectChildMeshes == (_targetMeshFilterArray != null && _targetMeshFilterArray.Length > 0))
                throw new ArgumentException("Choose direct MeshFilters or child collection, not both.");
            MeshFilter[] filters = _collectChildMeshes
                ? GetComponentsInChildren<MeshFilter>(_includeInactive) : _targetMeshFilterArray;
            var inputs = new List<MeshFilter>();
            foreach (MeshFilter filter in filters)
            {
                if (filter != null && _generatedRoot != null && filter.transform.IsChildOf(_generatedRoot.transform)) continue;
                inputs.Add(filter);
            }
            return new MeshBuildRequest(inputs, transform.worldToLocalMatrix, _targetCamera,
                _allowReadableCopy, _allowNormalRecalculation, _optimizeMesh, _combineAllIntoSingleMesh,
                _enableBackfaceCulling, _backfaceCullingAngle, _enableOcclusionCulling,
                _vertexSamplingCount, _visibilityThreshold, _cullingLayerMask);
        }

        public Mesh[] CombineAndSaveMeshes()
        {
            var save = new MeshSaveOptions(_savePath, _meshName, _overwriteExistingAssets);
            using (MeshBuildResult result = BuildMeshes())
            {
                List<string> paths = save.CreatePaths(result.Meshes.Count, _combineAllIntoSingleMesh);
                Mesh[] saved = MeshAssetTransaction.Save(result.Meshes, paths, save.Overwrite,
                    "Combine meshes", meshes => { if (_createSceneOutput) CreateSceneOutput(meshes, result); });
                return saved;
            }
        }

        private void CreateSceneOutput(Mesh[] meshes, MeshBuildResult result)
        {
            Undo.RegisterCompleteObjectUndo(this, "Assign combined result");
            // Undo can serialize the owner while Destroy/RegisterCreatedObjectUndo runs.
            // Keep its old ledger intact until every scene operation has completed.
            var nextStates = new List<MeshSourceRendererState>(_sourceRendererStates);
            _generatedRoot = MeshSceneOutput.Replace(transform, _meshName, _generatedRoot, meshes, result,
                _disableSourceRenderers, nextStates);
            _sourceRendererStates = nextStates;
            PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }
#endif
    }
}
