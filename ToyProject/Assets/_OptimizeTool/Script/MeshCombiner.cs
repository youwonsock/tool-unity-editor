using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

namespace Common.OptimizeTool
{
    public class MeshCombiner : MonoBehaviour
    {
#if UNITY_EDITOR


        #region Constants
        
        private const string MESH_EXTENSION = ".asset";
        private const int MAX_VERTEX_COUNT = 65535;
        private const int TRIANGLE_VERTEX_COUNT = 3;
        private const float RAY_OFFSET = 0.01f;
        private const string TEMP_MESH_EXTRACTOR_NAME = "TempMeshExtractor";
        private const string BAKED_SUFFIX = "_Baked";
        private const string BACKFACE_CULLED_SUFFIX = "_BackfaceCulled";
        private const string READABLE_PROPERTY_NAME = "m_IsReadable";
        private const double DEFAULT_VISIBILITY_THRESHOLD = 0.01;
        private const int DEFAULT_VERTEX_SAMPLING_COUNT = 100;
        
        #endregion


        #region Member Variables
        
        [SerializeField] private string _savePath;
        [SerializeField] private string _meshName;
        [SerializeField] private bool _optimizeMesh = true;
        [SerializeField] private bool _includeInactive = false;
        [SerializeField] private Camera _targetCamera;
        [SerializeField] private MeshFilter[] _targetMeshFilterArray;
        [SerializeField] private bool _collectChildMeshes = false;
        [SerializeField] private bool _allowReadableCopy = false;
        [SerializeField] private bool _allowNormalRecalculation = false;
        [SerializeField] private bool _overwriteExistingAssets = false;

        [Header("Occlusion Culling Options")]
        [SerializeField] private bool _enableOcclusionCulling = false;
        [SerializeField] private int _vertexSamplingCount = DEFAULT_VERTEX_SAMPLING_COUNT;  // 최소 샘플 수(버텍스 수가 이 값보다 작으면 모든 버택스에 대해 Occlusion검사 진행)
        [SerializeField] private double _visibilityThreshold = DEFAULT_VISIBILITY_THRESHOLD; // 버텍스 샘플링 결과 화면에 보이는 비율(0.01 = 1%)
        [SerializeField] private LayerMask _cullingLayerMask = ~0; // 컬링 레이어 마스크

        [Header("BackFace Culling Options")]
        [SerializeField] private bool _enableBackfaceCulling = false;
        [SerializeField] private float _backfaceCullingAngle = 90f;
        
        [Header("Final Combine Options")]
        [SerializeField] private bool _combineAllIntoSingleMesh = false; // 최종적으로 1개 메쉬로 합치는 옵션
        
        [Header("Save button")]
        [SerializeField] private bool _saveButton;

        // 캐시된 데이터
        private readonly HashSet<Mesh> _processedMeshes = new HashSet<Mesh>();
        private readonly HashSet<Mesh> _temporaryMeshes = new HashSet<Mesh>();
        private readonly Dictionary<Mesh, Mesh> _readableMeshCache = new Dictionary<Mesh, Mesh>();
        private readonly Dictionary<Mesh, Material> _meshToMaterialMap = new Dictionary<Mesh, Material>();
        
        #endregion


        #region Unity Events & Init/Release
        
        private bool _isInitialized;
        private bool _isFaulted;
        private System.Exception _fault;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("MeshCombiner is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("MeshCombiner is faulted; call Release before Init.", _fault);
            try
            {
                ValidateAssetFolderPath(_savePath);
                if (string.IsNullOrWhiteSpace(_meshName))
                    throw new ArgumentException("Mesh name is required.", nameof(_meshName));
                if (_meshName.IndexOfAny(new[] { '/', '\\' }) >= 0
                    || _meshName == "."
                    || _meshName == ".."
                    || _meshName.IndexOf("..", StringComparison.Ordinal) >= 0)
                    throw new ArgumentException("Mesh name must be a simple asset file name.", nameof(_meshName));
                if ((_enableOcclusionCulling || _enableBackfaceCulling) && _targetCamera == null)
                    throw new InvalidOperationException("Culling requires a serialized target camera.");
                if (!_collectChildMeshes && (_targetMeshFilterArray == null || _targetMeshFilterArray.Length == 0))
                    throw new ArgumentException("Assign target mesh filters or enable child collection.", nameof(_targetMeshFilterArray));
                if (_collectChildMeshes && _targetMeshFilterArray != null && _targetMeshFilterArray.Length > 0)
                    throw new ArgumentException("Choose either direct mesh filters or child collection, not both.");
                if (_enableOcclusionCulling
                    && (_vertexSamplingCount <= 0
                        || !IsFinite(_visibilityThreshold)
                        || _visibilityThreshold < 0d
                        || _visibilityThreshold > 1d
                        || _cullingLayerMask.value == 0))
                    throw new ArgumentOutOfRangeException(nameof(_vertexSamplingCount), "Occlusion sampling settings are invalid.");
                if (_enableBackfaceCulling
                    && (!IsFinite(_backfaceCullingAngle)
                        || _backfaceCullingAngle < 0f
                        || _backfaceCullingAngle > 180f))
                    throw new ArgumentOutOfRangeException(nameof(_backfaceCullingAngle));
                _isInitialized = true;
            }
            catch (System.Exception exception)
            {
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        private void Update()
        {
            if (_isFaulted)
                throw new InvalidOperationException("MeshCombiner is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("MeshCombiner is not initialized.");
            if(_saveButton)
            {
                if (Application.isPlaying)
                    throw new InvalidOperationException("Mesh combination and asset generation are editor-only.");
                _saveButton = false;
                CombineAndSaveMeshes();
            }
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("MeshCombiner has not been initialized.");
            ClearCaches();
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        #endregion


        #region Private Functions

        private void CombineAndSaveMeshes()
        {
            if (_isFaulted)
                throw new InvalidOperationException("MeshCombiner is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("MeshCombiner is not initialized.");
            if (Application.isPlaying)
                throw new InvalidOperationException("Mesh combination and asset generation are editor-only.");
            ClearCaches(); // 시작 전 캐시 정리
            try
            {
                MeshFilter[] meshFilters = _collectChildMeshes
                    ? GetComponentsInChildren<MeshFilter>(_includeInactive)
                    : _targetMeshFilterArray;

                if (meshFilters == null || meshFilters.Length == 0)
                    throw new ArgumentException("No mesh filters were supplied.");
                for (int i = 0; i < meshFilters.Length; i++)
                {
                    MeshFilter filter = meshFilters[i];
                    if (filter == null || filter.sharedMesh == null || filter.sharedMesh.vertexCount == 0)
                        throw new ArgumentException($"Mesh filter at index {i} is missing a mesh.", nameof(meshFilters));
                    if (!_allowNormalRecalculation && (filter.sharedMesh.normals == null || filter.sharedMesh.normals.Length != filter.sharedMesh.vertexCount))
                        throw new ArgumentException($"Mesh '{filter.sharedMesh.name}' has no complete normal data.", nameof(meshFilters));
                }

                Debug.Log($"찾은 MeshFilter 개수: {meshFilters.Length}");

                List<MeshFilter> validMeshFilters = FilterValidMeshFilters(meshFilters, out int totalVertices, out int culledCount);
                if (validMeshFilters.Count == 0)
                    throw new ArgumentException("No valid mesh filters were supplied.");

                Debug.Log($"유효한 MeshFilter 개수: {validMeshFilters.Count}, 총 버텍스: {totalVertices}, 컬링된 메쉬: {culledCount}");

                List<CombineInstance> combinedMesh = CombineMeshes(validMeshFilters.ToArray());
                if (combinedMesh == null || combinedMesh.Count == 0)
                    throw new InvalidOperationException("Mesh combination produced no output.");
                ValidateOutputAssets(combinedMesh.Count);
                CreateSaveFolder();
                ProcessFinalMeshes(combinedMesh);
            }
            finally
            {
                ClearCaches();
            }
        }
        
        private void SaveMesh(Mesh mesh, string fileName)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            SaveMeshAsAsset(mesh, fileName);
        }

        private List<MeshFilter> FilterValidMeshFilters(MeshFilter[] meshFilters, out int totalVertices, out int culledCount)
        {
            List<MeshFilter> validMeshFilters = new List<MeshFilter>();
            totalVertices = 0;
            culledCount = 0;
            
            foreach (MeshFilter meshFilter in meshFilters)
            {
                if (!IsValidMeshFilter(meshFilter))
                    throw new ArgumentException("Every supplied MeshFilter must reference a non-empty Mesh.", nameof(meshFilters));
                
                // 컬링 검사
                if (ShouldCullMeshFilter(meshFilter))
                {
                    culledCount++;
                    continue;
                }
                
                validMeshFilters.Add(meshFilter);
                totalVertices += meshFilter.sharedMesh.vertexCount;
            }
            
            return validMeshFilters;
        }

        private bool IsValidMeshFilter(MeshFilter meshFilter)
        {
            return meshFilter != null && 
                    meshFilter.sharedMesh != null && 
                    meshFilter.sharedMesh.vertexCount > 0;
        }

        private void ProcessFinalMeshes(List<CombineInstance> combinedMesh)
        {
            if (combinedMesh == null || combinedMesh.Count == 0)
                throw new InvalidOperationException("Mesh combination produced no output.");
            if (_combineAllIntoSingleMesh && combinedMesh.Count > 1)
            {
                // 여러 메쉬를 1개로 최종 결합
                Mesh finalCombinedMesh = CombineMultipleMeshesIntoOne(combinedMesh);
                SaveMesh(finalCombinedMesh, _meshName);
            }
            else
            {
                // 개별 메쉬들을 각각 저장
                SaveIndividualMeshes(combinedMesh);
            }
        }

        private void ValidateOutputAssets(int combinedMeshCount)
        {
            if (combinedMeshCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(combinedMeshCount));

            ValidateAssetFolderPath(_savePath);
            HashSet<string> outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_combineAllIntoSingleMesh && combinedMeshCount > 1)
            {
                ValidateOutputAssetName(_meshName, outputPaths);
                return;
            }

            for (int i = 0; i < combinedMeshCount; i++)
                ValidateOutputAssetName($"{_meshName}_{i}", outputPaths);
        }

        private void ValidateOutputAssetName(string fileName, HashSet<string> outputPaths)
        {
            ValidateAssetFileName(fileName);
            string outputDirectory = ValidateAssetFolderPath(_savePath);
            string assetPath = $"{outputDirectory}/{fileName}{MESH_EXTENSION}";
            if (!outputPaths.Add(assetPath))
                throw new InvalidOperationException($"Multiple outputs resolve to the same asset: {assetPath}");

            UnityEngine.Object existingAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existingAsset != null && !(existingAsset is Mesh))
                throw new InvalidOperationException($"Mesh output path is occupied by a non-mesh asset: {assetPath}");
            if (existingAsset != null && !_overwriteExistingAssets)
                throw new InvalidOperationException($"Mesh asset already exists: {assetPath}");
        }

        private void SaveIndividualMeshes(List<CombineInstance> combinedMesh)
        {
            for (int i = 0; i < combinedMesh.Count; i++)
                SaveMesh(combinedMesh[i].mesh, $"{_meshName}_{i}");
        }

        private void ClearCaches()
        {
            // 캐시된 메쉬들 정리 (메모리 누수 방지)
            foreach (var kvp in _readableMeshCache)
            {
                Mesh originalMesh = kvp.Key;
                Mesh cachedMesh = kvp.Value;
                
                // 원본과 다른 메쉬만 제거 (원본 메쉬는 건드리지 않음)
                if (cachedMesh != null && cachedMesh != originalMesh)
                    DestroyImmediate(cachedMesh);
            }

            foreach (Mesh temporaryMesh in _temporaryMeshes)
            {
                if (temporaryMesh != null)
                    DestroyImmediate(temporaryMesh);
            }
            
            _readableMeshCache.Clear();
            _processedMeshes.Clear();
            _meshToMaterialMap.Clear();
            _temporaryMeshes.Clear();
        }
        
        private List<CombineInstance> CombineMeshes(MeshFilter[] meshFilters)
        {
            if (meshFilters == null)
                throw new ArgumentNullException(nameof(meshFilters));
            if (meshFilters.Length == 0)
                throw new ArgumentException("At least one MeshFilter is required.", nameof(meshFilters));
                
            Dictionary<Material, List<CombineInstance>> materialGroups = 
                new Dictionary<Material, List<CombineInstance>>();
            
            // 머티리얼별로 메쉬 그룹화
            foreach (MeshFilter meshFilter in meshFilters)
                ProcessMeshFilter(meshFilter, materialGroups);
            
            // 머테리얼 별 메쉬 추출
            List<CombineInstance> finalCombines = CombineSubmeshes(materialGroups);
            if (finalCombines.Count == 0)
                throw new InvalidOperationException("No triangles remained after mesh processing.");
            return finalCombines;
        }

        private void ProcessMeshFilter(MeshFilter meshFilter, Dictionary<Material, List<CombineInstance>> materialGroups)
        {
            if (!IsValidMeshFilter(meshFilter))
                throw new ArgumentException("MeshFilter must reference a non-empty Mesh.", nameof(meshFilter));
            
            // 읽기 가능한 메쉬 생성 (캐싱 적용)
            Mesh readableMesh = GetReadableMeshCached(meshFilter.sharedMesh);
            if (readableMesh == null)
                throw new InvalidOperationException($"Unable to obtain readable mesh '{meshFilter.sharedMesh.name}'.");
            
            // 백페이스 컬링 적용 (이미 읽기 가능한 메쉬이므로 안전)
            if (_enableBackfaceCulling)
            {
                readableMesh = ApplyBackfaceCulling(readableMesh, meshFilter.transform);
                if (readableMesh == null || readableMesh.vertexCount == 0)
                    throw new InvalidOperationException($"Backface culling removed every triangle from '{meshFilter.name}'.");
                _temporaryMeshes.Add(readableMesh);
            }
            
            MeshRenderer meshRenderer = meshFilter.GetComponent<MeshRenderer>();
            if (!IsValidMeshRenderer(meshRenderer))
                throw new InvalidOperationException($"MeshFilter '{meshFilter.name}' requires a MeshRenderer with materials.");
            
            Material[] materials = meshRenderer.sharedMaterials;
            
            // 서브메쉬가 있는 경우 각각 처리
            if (readableMesh.subMeshCount > 1)
                ProcessMultiSubMesh(readableMesh, materials, meshFilter.transform, materialGroups);
            else
                ProcessSingleSubMesh(readableMesh, materials[0], meshFilter.transform, materialGroups, meshFilter.name);
            
        }

        private bool IsValidMeshRenderer(MeshRenderer meshRenderer)
        {
            return meshRenderer != null && 
                    meshRenderer.sharedMaterials != null && 
                    meshRenderer.sharedMaterials.Length > 0;
        }

        private Mesh GetReadableMeshCached(Mesh originalMesh)
        {
            if (originalMesh == null)
                throw new ArgumentNullException(nameof(originalMesh));
            
            // 캐시에서 확인
            if (_readableMeshCache.TryGetValue(originalMesh, out Mesh cachedMesh))
                return cachedMesh;
            
            // 새로 생성
            Mesh readableMesh = GetReadableMesh(originalMesh);
            _readableMeshCache[originalMesh] = readableMesh;
            
            return readableMesh;
        }
        
        private void ProcessMultiSubMesh(Mesh readableMesh, Material[] materials, Transform meshTransform, Dictionary<Material, List<CombineInstance>> materialGroups)
        {
            for (int subMeshIndex = 0; subMeshIndex < readableMesh.subMeshCount; subMeshIndex++)
            {
                if (subMeshIndex >= materials.Length)
                    throw new ArgumentException($"Mesh submesh {subMeshIndex} has no corresponding material.", nameof(materials));
                
                Material material = materials[subMeshIndex];
                if (material == null)
                    throw new ArgumentException($"Mesh submesh {subMeshIndex} has a null material.", nameof(materials));
                
                // 서브메쉬를 개별 메쉬로 추출
                Mesh subMesh = ExtractSubMesh(readableMesh, subMeshIndex);
                if (subMesh == null || subMesh.vertexCount == 0)
                    throw new InvalidOperationException($"Mesh submesh {subMeshIndex} contains no triangles.");
                
                if (!materialGroups.ContainsKey(material))
                    materialGroups[material] = new List<CombineInstance>();
                
                Matrix4x4 matrix = transform.worldToLocalMatrix * meshTransform.localToWorldMatrix;
                
                CombineInstance combineInstance = new CombineInstance
                {
                    mesh = subMesh,
                    transform = matrix
                };
                 
                materialGroups[material].Add(combineInstance);
                _temporaryMeshes.Add(subMesh);
                
            }
        }
        
        private void ProcessSingleSubMesh(Mesh readableMesh, Material material, Transform meshTransform, Dictionary<Material, List<CombineInstance>> materialGroups, string meshName)
        {
            if (material == null)
                throw new ArgumentException($"Mesh '{meshName}' has no material.", nameof(material));
            
            if (!materialGroups.ContainsKey(material))
                materialGroups[material] = new List<CombineInstance>();
            
            Matrix4x4 matrix = transform.worldToLocalMatrix * meshTransform.localToWorldMatrix;
            
            CombineInstance combineInstance = new CombineInstance
            {
                mesh = readableMesh,
                transform = matrix
            };
            
            materialGroups[material].Add(combineInstance);
            
        }
        
        private Mesh ExtractSubMesh(Mesh originalMesh, int subMeshIndex)
        {
            if (originalMesh == null || subMeshIndex < 0 || subMeshIndex >= originalMesh.subMeshCount)
                throw new ArgumentException("Submesh selection is invalid.", nameof(subMeshIndex));
            
            Vector3[] vertices = originalMesh.vertices;
            Vector3[] normals = originalMesh.normals;
            Vector2[] uvs = originalMesh.uv;
            Color[] colors = originalMesh.colors;
            int[] triangles = originalMesh.GetTriangles(subMeshIndex);
            
            if (triangles == null || triangles.Length == 0)
                throw new ArgumentException("A submesh must contain triangles.", nameof(subMeshIndex));

            bool hasCompleteNormals = normals != null && normals.Length == vertices.Length;
            if (!hasCompleteNormals && !_allowNormalRecalculation)
                throw new ArgumentException($"Mesh '{originalMesh.name}' has no complete normal data.", nameof(originalMesh));
            bool hasCompleteUvs = uvs != null && uvs.Length == vertices.Length;
            bool hasCompleteColors = colors != null && colors.Length == vertices.Length;
            
            // 사용되는 버텍스만 추출
            Dictionary<int, int> vertexMapping = new Dictionary<int, int>();
            List<Vector3> newVertices = new List<Vector3>();
            List<Vector3> newNormals = new List<Vector3>();
            List<Vector2> newUVs = new List<Vector2>();
            List<Color> newColors = new List<Color>();
            List<int> newTriangles = new List<int>();
            
            for (int i = 0; i < triangles.Length; i++)
            {
                int originalIndex = triangles[i];
                if (originalIndex < 0 || originalIndex >= vertices.Length)
                    throw new ArgumentException("Submesh contains an out-of-range vertex index.", nameof(originalMesh));
                
                if (!vertexMapping.ContainsKey(originalIndex))
                {
                    vertexMapping[originalIndex] = newVertices.Count;
                    newVertices.Add(vertices[originalIndex]);
                    
                    if (hasCompleteNormals)
                        newNormals.Add(normals[originalIndex]);
                        
                    if (hasCompleteUvs)
                        newUVs.Add(uvs[originalIndex]);
                        
                    if (hasCompleteColors)
                        newColors.Add(colors[originalIndex]);
                }
                
                newTriangles.Add(vertexMapping[originalIndex]);
            }
            
            // 새 메쉬 생성
            Mesh subMesh = new Mesh();
            subMesh.name = $"{originalMesh.name}_SubMesh_{subMeshIndex}";
            subMesh.vertices = newVertices.ToArray();
            subMesh.triangles = newTriangles.ToArray();
            
            if (hasCompleteNormals)
                subMesh.normals = newNormals.ToArray();
            else
                subMesh.RecalculateNormals();
                
            if (newUVs.Count > 0)
                subMesh.uv = newUVs.ToArray();
                
            if (newColors.Count > 0)
                subMesh.colors = newColors.ToArray();
                
            subMesh.RecalculateBounds();
            
            return subMesh;
        }
        
        private Mesh GetReadableMesh(Mesh originalMesh)
        {
            if (originalMesh == null)
                throw new ArgumentNullException(nameof(originalMesh));
                
            if (originalMesh.isReadable)
                return originalMesh;

            if (!_allowReadableCopy)
                throw new InvalidOperationException($"Mesh '{originalMesh.name}' is not readable.");
            
            // BackfaceCullMeshBaker 방식 적용: 베이킹을 통한 메쉬 데이터 추출
            Mesh readableMesh = CreateReadableMeshFromOriginal(originalMesh);
            
            if (readableMesh != null && readableMesh.vertexCount > 0)
                return readableMesh;
            
            throw new InvalidOperationException($"Unable to create a readable copy of mesh '{originalMesh.name}'.");
        }
        
        private Mesh CreateReadableMeshFromOriginal(Mesh originalMesh)
        {
            GameObject tempGO = new GameObject(TEMP_MESH_EXTRACTOR_NAME);
            Mesh bakedMesh = null;
            bool returnedMesh = false;
            try
            {
                MeshFilter tempMF = tempGO.AddComponent<MeshFilter>();
                tempMF.sharedMesh = originalMesh;

                bakedMesh = new Mesh();
                bakedMesh.name = originalMesh.name + BAKED_SUFFIX;
                SkinnedMeshRenderer tempSMR = tempGO.AddComponent<SkinnedMeshRenderer>();
                tempSMR.sharedMesh = originalMesh;
                tempSMR.BakeMesh(bakedMesh);
                if (bakedMesh.vertexCount <= 0)
                {
                    throw new InvalidOperationException($"Unable to create a readable copy of mesh '{originalMesh.name}'.");
                }

                returnedMesh = true;
                return bakedMesh;
            }
            finally
            {
                DestroyImmediate(tempGO);
                if (!returnedMesh && bakedMesh != null)
                    DestroyImmediate(bakedMesh);
            }
        }
        
        private bool ShouldCullMeshFilter(MeshFilter meshFilter)
        {
            if (!_enableOcclusionCulling)
                return false;
            if (_targetCamera == null)
                throw new InvalidOperationException("Occlusion culling requires a serialized target camera.");
            Renderer renderer = meshFilter.GetComponent<Renderer>();
            if (renderer == null)
                throw new InvalidOperationException($"MeshFilter '{meshFilter.name}' requires a Renderer for occlusion culling.");
            
            // 오클루전 컬링 - 렌더러가 실제로 렌더링되고 있는지 확인
            if (_enableOcclusionCulling && IsVertexRaycastOcclusionCulled(meshFilter, renderer))
                return true;
            
            return false;
        }
        
        private bool IsVertexRaycastOcclusionCulled(MeshFilter meshFilter, Renderer renderer)
        {
            if (_targetCamera == null)
                throw new InvalidOperationException("Occlusion culling requires a serialized target camera.");
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            if (meshFilter == null || meshFilter.sharedMesh == null)
                throw new ArgumentException("Occlusion culling requires a mesh filter with a mesh.", nameof(meshFilter));
            
            Mesh mesh = GetReadableMesh(meshFilter.sharedMesh);
            if (mesh == null || mesh.vertices == null || mesh.vertices.Length == 0)
                throw new InvalidOperationException("Occlusion culling requires readable mesh vertices.");
            
            Vector3[] vertices = mesh.vertices;
            Transform objTransform = renderer.transform;
            Vector3 cameraPos = _targetCamera.transform.position;
            
            // 샘플링할 버텍스 선택
            Vector3[] sampleVertices = GetSampleVertices(vertices, objTransform, cameraPos);
            
            int visibleVertices = 0;
            
            foreach (Vector3 worldVertex in sampleVertices)
            {
                Vector3 direction = (worldVertex - cameraPos).normalized;
                float distance = Vector3.Distance(cameraPos, worldVertex);
                
                // 카메라에서 버텍스로 레이캐스트
                if (!Physics.Raycast(cameraPos + direction * RAY_OFFSET, direction, distance - RAY_OFFSET, _cullingLayerMask))
                    visibleVertices++;
            }
            
            // 가시성 임계값 이하면 가려진 것으로 판단
            double visibilityRatio = (double)visibleVertices / sampleVertices.Length;
            bool isOccluded = visibilityRatio < _visibilityThreshold;
            
            return isOccluded;
        }
        
        private Vector3[] GetSampleVertices(Vector3[] vertices, Transform objTransform, Vector3 cameraPos)
        {
            List<Vector3> sampleVertices = new List<Vector3>();
            
            if (vertices.Length <= _vertexSamplingCount)
            {
                // 모든 버텍스 사용
                foreach (Vector3 vertex in vertices)
                {
                    Vector3 worldVertex = objTransform.TransformPoint(vertex);
                    sampleVertices.Add(worldVertex);
                }
            }
            else
            {
                sampleVertices = GetUniformSamples(vertices, objTransform);
            }
            
            return sampleVertices.ToArray();
        }
        
        private List<Vector3> GetUniformSamples(Vector3[] vertices, Transform objTransform)
        {
            // 균등한 간격으로 샘플링
            List<Vector3> samples = new List<Vector3>();
            float step = (float)vertices.Length / _vertexSamplingCount;
            
            for (int i = 0; i < _vertexSamplingCount && i < vertices.Length; i++)
            {
                int index = Mathf.RoundToInt(i * step);
                if (index < vertices.Length)
                {
                    Vector3 worldVertex = objTransform.TransformPoint(vertices[index]);
                    samples.Add(worldVertex);
                }
            }
            
            return samples;
        }
        
        private Mesh ApplyBackfaceCulling(Mesh originalMesh, Transform meshTransform)
        {
            if (originalMesh == null)
                throw new ArgumentNullException(nameof(originalMesh));
            if (_targetCamera == null)
                throw new InvalidOperationException("Backface culling requires a serialized target camera.");
            
            Vector3[] vertices = originalMesh.vertices;
            Vector3[] normals = originalMesh.normals;
            Vector2[] uvs = originalMesh.uv;
            Color[] colors = originalMesh.colors;
            
            if (vertices == null || vertices.Length == 0)
                throw new ArgumentException("Mesh must contain vertices.", nameof(originalMesh));
            
            if ((normals == null || normals.Length != vertices.Length) && !_allowNormalRecalculation)
                throw new ArgumentException($"Mesh '{originalMesh.name}' has no complete normal data.", nameof(originalMesh));

            // 명시적으로 허용한 경우에만 전체 서브메쉬를 이용해 노멀을 계산한다.
            if (normals == null || normals.Length != vertices.Length)
                normals = CalculateNormalsFromAllSubMeshes(originalMesh, vertices);
            
            Vector3 cameraForward = -_targetCamera.transform.forward.normalized;

            // 서브메쉬별로 처리 (BackfaceCullMeshBaker 방식)
            int subMeshCount = originalMesh.subMeshCount;
            List<int[]> culledSubMeshTriangles = new List<int[]>();
            bool hasAnyTriangles = false;

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                
                if (triangles == null || triangles.Length == 0)
                    throw new ArgumentException($"Submesh {subMeshIndex} contains no triangles.", nameof(originalMesh));

                List<int> culledTriangles = ProcessSubmeshTriangles(triangles, vertices, meshTransform, cameraForward);
                culledSubMeshTriangles.Add(culledTriangles.ToArray());
                
                if (culledTriangles.Count > 0)
                    hasAnyTriangles = true;
            }
            
            if (!hasAnyTriangles)
                throw new InvalidOperationException($"Backface culling removed every triangle from '{originalMesh.name}'.");
            
            return CreateOptimizedMeshWithSubmeshes(vertices, normals, uvs, colors, culledSubMeshTriangles, originalMesh.name);
        }

        private List<int> ProcessSubmeshTriangles(int[] triangles, Vector3[] vertices, Transform meshTransform, Vector3 cameraForward)
        {
            if (triangles == null || triangles.Length == 0 || triangles.Length % TRIANGLE_VERTEX_COUNT != 0)
                throw new ArgumentException("Triangle data must contain complete triangles.", nameof(triangles));
            List<int> culledTriangles = new List<int>();
            
            // 삼각형별로 백페이스 여부 검사
            for (int i = 0; i < triangles.Length; i += TRIANGLE_VERTEX_COUNT)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];
                
                if (v0 < 0 || v1 < 0 || v2 < 0
                    || v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length)
                    throw new ArgumentException("Triangle contains an out-of-range vertex index.", nameof(triangles));
                
                Vector3 worldV0 = meshTransform.TransformPoint(vertices[v0]);
                Vector3 worldV1 = meshTransform.TransformPoint(vertices[v1]);
                Vector3 worldV2 = meshTransform.TransformPoint(vertices[v2]);
                
                // 삼각형의 노멀 계산
                Vector3 triangleNormal = CalculateTriangleNormal(worldV0, worldV1, worldV2);
                
                // 노멀과 카메라 방향의 내적으로 백페이스 판단
                float dotProduct = Vector3.Dot(cameraForward, triangleNormal);
                float angle = Mathf.Acos(Mathf.Clamp(dotProduct, -1f, 1f)) * Mathf.Rad2Deg;
                
                // 각도가 설정값보다 크면 백페이스로 판단하여 제외
                if (angle <= _backfaceCullingAngle)
                {
                    culledTriangles.Add(triangles[i]);
                    culledTriangles.Add(triangles[i + 1]);
                    culledTriangles.Add(triangles[i + 2]);
                }
            }
            
            return culledTriangles;
        }

        private Mesh CreateOptimizedMeshWithSubmeshes(Vector3[] originalVertices, Vector3[] originalNormals, 
            Vector2[] originalUVs, Color[] originalColors, List<int[]> culledSubMeshTriangles, string meshName)
        {
            if (originalVertices == null || originalVertices.Length == 0)
                throw new ArgumentException("Original vertex data is required.", nameof(originalVertices));
            bool hasCompleteNormals = originalNormals != null && originalNormals.Length == originalVertices.Length;
            if (!hasCompleteNormals && !_allowNormalRecalculation)
                throw new ArgumentException("Complete normal data is required.", nameof(originalNormals));
            if (!hasCompleteNormals)
                originalNormals = CalculateNormalsFromAllSubMeshesForTriangles(originalVertices, culledSubMeshTriangles);
            bool hasCompleteUvs = originalUVs != null && originalUVs.Length == originalVertices.Length;
            bool hasCompleteColors = originalColors != null && originalColors.Length == originalVertices.Length;

            // 모든 서브메쉬에서 사용되는 버텍스 인덱스를 수집
            HashSet<int> usedVertices = new HashSet<int>();
            foreach (int[] subMeshTriangles in culledSubMeshTriangles)
            {
                foreach (int vertexIndex in subMeshTriangles)
                    usedVertices.Add(vertexIndex);
            }
            
            // 사용되는 버텍스만 추려내기
            Dictionary<int, int> vertexMapping = new Dictionary<int, int>();
            List<Vector3> newVertices = new List<Vector3>();
            List<Vector3> newNormals = new List<Vector3>();
            List<Vector2> newUVs = new List<Vector2>();
            List<Color> newColors = new List<Color>();
            
            foreach (int originalIndex in usedVertices)
            {
                vertexMapping[originalIndex] = newVertices.Count;
                newVertices.Add(originalVertices[originalIndex]);
                
                if (originalNormals != null && originalNormals.Length == originalVertices.Length)
                    newNormals.Add(originalNormals[originalIndex]);
                    
                if (hasCompleteUvs)
                    newUVs.Add(originalUVs[originalIndex]);
                    
                if (hasCompleteColors)
                    newColors.Add(originalColors[originalIndex]);
            }
            
            // 새 메쉬 생성
            Mesh culledMesh = new Mesh();
            _temporaryMeshes.Add(culledMesh);
            culledMesh.name = meshName + BACKFACE_CULLED_SUFFIX;
            culledMesh.vertices = newVertices.ToArray();
            culledMesh.subMeshCount = culledSubMeshTriangles.Count;
            
            // 각 서브메쉬의 삼각형 인덱스를 새로운 버텍스 인덱스로 매핑
            for (int subMeshIndex = 0; subMeshIndex < culledSubMeshTriangles.Count; subMeshIndex++)
            {
                int[] subMeshTriangles = culledSubMeshTriangles[subMeshIndex];
                int[] remappedTriangles = new int[subMeshTriangles.Length];
                
                for (int i = 0; i < subMeshTriangles.Length; i++)
                    remappedTriangles[i] = vertexMapping[subMeshTriangles[i]];
                
                culledMesh.SetTriangles(remappedTriangles, subMeshIndex);
            }
            
            if (newNormals.Count == newVertices.Count)
                culledMesh.normals = newNormals.ToArray();
            else
                culledMesh.RecalculateNormals();
                
            if (newUVs.Count > 0)
                culledMesh.uv = newUVs.ToArray();
                
            if (newColors.Count > 0)
                culledMesh.colors = newColors.ToArray();
                
            culledMesh.RecalculateBounds();
            
            return culledMesh;
        }

        private static Vector3[] CalculateNormalsFromAllSubMeshesForTriangles(
            Vector3[] vertices,
            List<int[]> subMeshTriangles)
        {
            if (subMeshTriangles == null || subMeshTriangles.Count == 0)
                throw new ArgumentException("Triangle data is required.", nameof(subMeshTriangles));

            Vector3[] normals = new Vector3[vertices.Length];
            for (int subMeshIndex = 0; subMeshIndex < subMeshTriangles.Count; subMeshIndex++)
            {
                int[] triangles = subMeshTriangles[subMeshIndex];
                if (triangles == null || triangles.Length == 0 || triangles.Length % TRIANGLE_VERTEX_COUNT != 0)
                    throw new ArgumentException("Triangle data must contain complete triangles.", nameof(subMeshTriangles));
                for (int i = 0; i < triangles.Length; i += TRIANGLE_VERTEX_COUNT)
                {
                    int v0 = triangles[i];
                    int v1 = triangles[i + 1];
                    int v2 = triangles[i + 2];
                    if (v0 < 0 || v1 < 0 || v2 < 0
                        || v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length)
                        throw new ArgumentException("Triangle contains an out-of-range vertex index.", nameof(subMeshTriangles));
                    Vector3 triangleNormal = CalculateTriangleNormal(vertices[v0], vertices[v1], vertices[v2]);
                    normals[v0] += triangleNormal;
                    normals[v1] += triangleNormal;
                    normals[v2] += triangleNormal;
                }
            }

            for (int i = 0; i < normals.Length; i++)
            {
                if (normals[i].sqrMagnitude > Mathf.Epsilon)
                    normals[i].Normalize();
            }
            return normals;
        }
        
        private Vector3[] CalculateNormalsFromAllSubMeshes(Mesh mesh, Vector3[] vertices)
        {
            Vector3[] normals = new Vector3[vertices.Length];
            
            // 모든 서브메쉬의 삼각형을 이용해 노멀 계산 (BackfaceCullMeshBaker 방식)
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                int[] triangles = mesh.GetTriangles(subMeshIndex);
                if (triangles == null || triangles.Length == 0 || triangles.Length % TRIANGLE_VERTEX_COUNT != 0)
                    throw new ArgumentException("Triangle data must contain complete triangles.", nameof(mesh));
                
                // 각 삼각형의 노멀을 계산하여 버텍스에 누적
                for (int i = 0; i < triangles.Length; i += TRIANGLE_VERTEX_COUNT)
                {
                    int v0 = triangles[i];
                    int v1 = triangles[i + 1];
                    int v2 = triangles[i + 2];
                    
                    if (v0 < 0 || v1 < 0 || v2 < 0
                        || v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length)
                        throw new ArgumentException("Triangle contains an out-of-range vertex index.", nameof(mesh));
                    
                    Vector3 triangleNormal = CalculateTriangleNormal(vertices[v0], vertices[v1], vertices[v2]);
                    
                    normals[v0] += triangleNormal;
                    normals[v1] += triangleNormal;
                    normals[v2] += triangleNormal;
                }
            }
            
            // 정규화
            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].normalized;
            
            return normals;
        }
        
        private static Vector3 CalculateTriangleNormal(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            return Vector3.Cross(edge1, edge2).normalized;
        }
        
        private void CreateSaveFolder()
        {
            string normalizedPath = ValidateAssetFolderPath(_savePath);
            string[] pathParts = normalizedPath.Split('/');
            string currentPath = pathParts[0];
            for (int i = 1; i < pathParts.Length; i++)
            {
                string nextPath = $"{currentPath}/{pathParts[i]}";
                if (!AssetDatabase.IsValidFolder(nextPath)
                    && string.IsNullOrEmpty(AssetDatabase.CreateFolder(currentPath, pathParts[i])))
                    throw new InvalidOperationException($"Unable to create mesh output folder '{nextPath}'.");
                currentPath = nextPath;
            }
            if (!AssetDatabase.IsValidFolder(normalizedPath))
                throw new InvalidOperationException($"Unable to create mesh output folder '{normalizedPath}'.");
        }
        
        private void SaveMeshAsAsset(Mesh mesh, string fileName)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            ValidateAssetFileName(fileName);
            string outputDirectory = ValidateAssetFolderPath(_savePath);
            string fullPath = $"{outputDirectory}/{fileName}{MESH_EXTENSION}";
            
            // 메쉬 이름을 파일명과 일치하도록 설정 (Unity 경고 해결)
            mesh.name = fileName;
            
            // 기존 에셋이 있다면 덮어쓰기
            Mesh existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(fullPath);
            if (existingMesh != null)
            {
                if (!_overwriteExistingAssets)
                    throw new InvalidOperationException($"Mesh asset already exists: {fullPath}");
                existingMesh.Clear();
                EditorUtility.CopySerialized(mesh, existingMesh);
                _temporaryMeshes.Remove(mesh);
                DestroyImmediate(mesh);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 업데이트되었습니다: {fullPath}");
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, fullPath);
                _temporaryMeshes.Remove(mesh);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 저장되었습니다: {fullPath}");
            }
            
            SetMeshReadWriteEnabled(fullPath, false);   

            AssetDatabase.Refresh();
        }

        private static void ValidateAssetFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.IndexOfAny(new[] { '/', '\\' }) >= 0
                || fileName == "."
                || fileName == ".."
                || fileName.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Mesh file name must be a simple file name.", nameof(fileName));

            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalidCharacters.Length; i++)
            {
                if (fileName.IndexOf(invalidCharacters[i]) >= 0)
                    throw new ArgumentException("Mesh file name contains an invalid character.", nameof(fileName));
            }
        }

        private static string ValidateAssetFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Mesh output path is required.", nameof(path));

            string normalizedPath = path.Replace('\\', '/').TrimEnd('/');
            if (!normalizedPath.StartsWith("Assets/", StringComparison.Ordinal)
                || normalizedPath.IndexOf("/../", StringComparison.Ordinal) >= 0
                || normalizedPath.EndsWith("/..", StringComparison.Ordinal)
                || normalizedPath.IndexOf("//", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Mesh output path must be a normalized folder under Assets/.", nameof(path));
            return normalizedPath;
        }

        private void SetMeshReadWriteEnabled(string assetPath, bool enabled)
        {
            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer != null)
            {
                importer.isReadable = enabled;
                importer.SaveAndReimport();
            }
            else
            {
                // 직접 생성한 메쉬 에셋의 경우 SerializedObject를 통해 설정
                Mesh meshAsset = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (meshAsset != null)
                {
                    SerializedObject serializedMesh = new SerializedObject(meshAsset);
                    SerializedProperty readableProperty = serializedMesh.FindProperty(READABLE_PROPERTY_NAME);
                    if (readableProperty != null)
                    {
                        readableProperty.boolValue = enabled;
                        serializedMesh.ApplyModifiedProperties();
                        EditorUtility.SetDirty(meshAsset);
                        AssetDatabase.SaveAssets();
                    }
                }
            }
        }

        private List<Material> CollectSubMeshMaterials(List<CombineInstance> combineInstances)
        {
            List<Material> materials = new List<Material>();
            
            // 각 CombineInstance의 메쉬에서 저장된 머티리얼 정보를 수집
            for (int i = 0; i < combineInstances.Count; i++)
            {
                var combineInstance = combineInstances[i];
                if (combineInstance.mesh != null)
                {
                    Material material = GetMaterialFromCombineInstance(combineInstance.mesh);
                    materials.Add(material);
                }
            }
            
            return materials;
        }

        private Material GetMaterialFromCombineInstance(Mesh mesh)
        {
            // 저장된 메쉬-머티리얼 매핑에서 머티리얼 정보 반환
            if (_meshToMaterialMap.TryGetValue(mesh, out Material material))
                return material;
            
            // 매핑에 없는 경우 null 반환
            return null;
        }

        private void OutputSubMeshMaterialInfo(List<Material> materials)
        {
            const int ESTIMATED_STRING_LENGTH = 50;
            const string SEPARATOR_LINE = "===";
            const string HEADER_TEXT = "최종 결합 메쉬 서브메쉬별 머티리얼 정보";
            const string UNKNOWN_MATERIAL = "Unknown";
            
            System.Text.StringBuilder sb = new System.Text.StringBuilder(materials.Count * ESTIMATED_STRING_LENGTH);
            sb.AppendLine($"{SEPARATOR_LINE} {HEADER_TEXT} {SEPARATOR_LINE}");
            sb.AppendLine($"총 서브메쉬 개수: {materials.Count}");
            
            for (int i = 0; i < materials.Count; i++)
            {
                Material material = materials[i];
                string materialName = material != null ? material.name : UNKNOWN_MATERIAL;
                sb.AppendLine($"서브메쉬 [{i}]: {materialName}");
            }
            
            sb.AppendLine($"{SEPARATOR_LINE} 머티리얼 정보 출력 완료 {SEPARATOR_LINE}");
            Debug.Log(sb.ToString());
        }

        private List<CombineInstance> CombineSubmeshes(Dictionary<Material, List<CombineInstance>> materialGroups)
        {
            List<CombineInstance> finalCombines = new List<CombineInstance>();
            
            foreach (var group in materialGroups)
            {
                Material currentMaterial = group.Key;
                // 총 버텍스 수 계산
                int totalVertices = 0;
                foreach (var combineInstance in group.Value)
                {
                    if (combineInstance.mesh != null)
                        totalVertices += combineInstance.mesh.vertexCount;
                }
                
                // MAX_VERTEX_COUNT 버텍스 초과 시 여러 메시로 분할
                if (totalVertices > MAX_VERTEX_COUNT)
                {
                    // 메시 분할 처리
                    List<CombineInstance> currentBatch = new List<CombineInstance>();
                    int currentVertexCount = 0;
                    int batchIndex = 0;
                    
                    foreach (var combineInstance in group.Value)
                    {
                        if (combineInstance.mesh == null)
                            throw new ArgumentException("Combine instance mesh cannot be null.", nameof(materialGroups));
                        
                        int meshVertexCount = combineInstance.mesh.vertexCount;
                        
                        // 현재 배치에 추가했을 때 한계를 초과하는 경우
                        if (currentVertexCount + meshVertexCount > MAX_VERTEX_COUNT && currentBatch.Count > 0)
                        {
                            // 현재 배치를 완료하고 새 배치 시작
                            Mesh batchMesh = new Mesh();
                            batchMesh.name = $"{_meshName}_{batchIndex}"; // 파일명과 일치하도록 설정
                            batchMesh.CombineMeshes(currentBatch.ToArray(), true);
                            _temporaryMeshes.Add(batchMesh);

                            // 머티리얼 정보 저장
                            _meshToMaterialMap[batchMesh] = currentMaterial;

                            finalCombines.Add(new CombineInstance
                            {
                                mesh = batchMesh,
                                transform = Matrix4x4.identity
                            });
                            
                            currentBatch.Clear();
                            currentVertexCount = 0;
                            batchIndex++;
                        }
                        
                        currentBatch.Add(combineInstance);
                        currentVertexCount += meshVertexCount;
                    }
                    
                    // 마지막 배치 처리
                    if (currentBatch.Count > 0)
                    {
                        Mesh batchMesh = new Mesh();
                        batchMesh.name = $"{_meshName}_{batchIndex}"; // 파일명과 일치하도록 설정
                        batchMesh.CombineMeshes(currentBatch.ToArray(), true);
                        _temporaryMeshes.Add(batchMesh);
                        
                        // 머티리얼 정보 저장
                        _meshToMaterialMap[batchMesh] = currentMaterial;
                        
                        finalCombines.Add(new CombineInstance
                        {
                            mesh = batchMesh,
                            transform = Matrix4x4.identity
                        });
                    }
                }
                else
                {
                    Mesh subMesh = new Mesh();
                    subMesh.name = $"{_meshName}_{finalCombines.Count}"; // 파일명과 일치하도록 설정
                    subMesh.CombineMeshes(group.Value.ToArray(), true);
                    _temporaryMeshes.Add(subMesh);

                    // 머티리얼 정보 저장
                    _meshToMaterialMap[subMesh] = currentMaterial;

                    CombineInstance combineInstance = new CombineInstance
                    {
                        mesh = subMesh,
                        transform = Matrix4x4.identity
                    };
                    finalCombines.Add(combineInstance);
                }
            }
            
            return finalCombines;
        }

        private Mesh CombineMultipleMeshesIntoOne(List<CombineInstance> combineInstances)
        {
            if (combineInstances == null || combineInstances.Count == 0)
                throw new ArgumentException("At least one combine instance is required.", nameof(combineInstances));
            
            // 단일 메쉬인 경우 그대로 반환
            if (combineInstances.Count == 1)
            {
                if (combineInstances[0].mesh == null)
                    throw new ArgumentException("Combine instance mesh cannot be null.", nameof(combineInstances));
                return combineInstances[0].mesh;
            }
            
            // 총 버텍스 수 계산
            int totalVertices = 0;
            foreach (var combineInstance in combineInstances)
            {
                if (combineInstance.mesh == null)
                    throw new ArgumentException("Combine instance mesh cannot be null.", nameof(combineInstances));
                totalVertices += combineInstance.mesh.vertexCount;
            }
            
            // 65535 버텍스 제한 체크
            if (totalVertices > MAX_VERTEX_COUNT)
                throw new InvalidOperationException($"Final combined mesh exceeds the {MAX_VERTEX_COUNT} vertex limit.");
            
            // 서브메쉬별 머티리얼 정보 수집 및 출력
            List<Material> subMeshMaterials = CollectSubMeshMaterials(combineInstances);
            OutputSubMeshMaterialInfo(subMeshMaterials);
            
            // 머티리얼별 서브메쉬를 유지하며 결합
                Mesh finalMesh = new Mesh();
                finalMesh.name = _meshName;
                _temporaryMeshes.Add(finalMesh);
                
                // 서브메쉬 개수 설정 (각 CombineInstance가 하나의 서브메쉬)
                finalMesh.subMeshCount = combineInstances.Count;
                
                // 모든 버텍스 데이터를 수집
                List<Vector3> allVertices = new List<Vector3>();
                List<Vector3> allNormals = new List<Vector3>();
                List<Vector2> allUVs = new List<Vector2>();
                List<Color> allColors = new List<Color>();
                List<Vector4> allTangents = new List<Vector4>();
                
                List<List<int>> subMeshTriangles = new List<List<int>>();
                
                foreach (var combineInstance in combineInstances)
                {
                    if (combineInstance.mesh == null)
                        throw new ArgumentException("Combine instance mesh cannot be null.", nameof(combineInstances));
                    
                    Mesh mesh = combineInstance.mesh;
                    Matrix4x4 matrix = combineInstance.transform;
                    int vertexOffset = allVertices.Count;
                    
                    // 버텍스 데이터 변환 및 추가
                    Vector3[] vertices = mesh.vertices;
                    Vector3[] normals = mesh.normals;
                    Vector2[] uvs = mesh.uv;
                    Color[] colors = mesh.colors;
                    Vector4[] tangents = mesh.tangents;

                    if (vertices == null || vertices.Length == 0)
                        throw new ArgumentException("Combine instance mesh must contain vertices.", nameof(combineInstances));
                    if (normals == null || normals.Length != vertices.Length)
                    {
                        if (!_allowNormalRecalculation)
                            throw new ArgumentException($"Mesh '{mesh.name}' has no complete normal data.", nameof(combineInstances));
                        normals = CalculateNormalsFromAllSubMeshes(mesh, vertices);
                    }
                    if (normals.Length != vertices.Length)
                        throw new ArgumentException($"Mesh '{mesh.name}' normal data does not match its vertices.", nameof(combineInstances));
                    
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        allVertices.Add(matrix.MultiplyPoint3x4(vertices[i]));
                        
                        allNormals.Add(matrix.MultiplyVector(normals[i]).normalized);
                            
                        if (uvs != null && uvs.Length > i)
                            allUVs.Add(uvs[i]);
                        else
                            allUVs.Add(Vector2.zero);
                            
                        if (colors != null && colors.Length > i)
                            allColors.Add(colors[i]);
                        else
                            allColors.Add(Color.white);
                            
                        if (tangents != null && tangents.Length > i)
                        {
                            Vector3 transformedTangent = matrix.MultiplyVector(tangents[i]).normalized;
                            allTangents.Add(new Vector4(transformedTangent.x, transformedTangent.y, transformedTangent.z, tangents[i].w));
                        }
                        else
                            allTangents.Add(new Vector4(1, 0, 0, 1));
                    }
                    
                    // 삼각형 인덱스 추가 (버텍스 오프셋 적용)
                    List<int> triangles = new List<int>();
                    int[] meshTriangles = mesh.triangles;
                    
                    for (int i = 0; i < meshTriangles.Length; i++)
                        triangles.Add(meshTriangles[i] + vertexOffset);
                    
                    subMeshTriangles.Add(triangles);
                }
                
                // 최종 메쉬에 데이터 설정
                finalMesh.vertices = allVertices.ToArray();
                finalMesh.normals = allNormals.ToArray();
                finalMesh.uv = allUVs.ToArray();
                finalMesh.colors = allColors.ToArray();
                finalMesh.tangents = allTangents.ToArray();
                
                // 각 서브메쉬 설정
                for (int i = 0; i < subMeshTriangles.Count; i++)
                {
                    finalMesh.SetTriangles(subMeshTriangles[i].ToArray(), i);
                }
                
                if (_optimizeMesh)
                    finalMesh.Optimize();
                
                finalMesh.RecalculateBounds();
                
                Debug.Log($"최종 메쉬 결합 완료: 버텍스 {finalMesh.vertexCount}, 서브메쉬 {finalMesh.subMeshCount}개");
                
                return finalMesh;
        }

        #endregion

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);


#endif
    }
}
