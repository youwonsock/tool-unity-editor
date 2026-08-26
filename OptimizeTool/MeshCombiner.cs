using System.Collections.Generic;
using UnityEngine;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

namespace Supercent.Common.OptimizeTool
{
    public class MeshCombiner : MonoBehaviour
    {
#if UNITY_EDITOR


        #region Constants
        
        private const string SAVE_PATH = "Assets/Supercent/CombinedMeshes/";
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
        
        [SerializeField] private string _meshName = "CombinedMesh";
        [SerializeField] private bool _optimizeMesh = true;
        [SerializeField] private bool _includeInactive = false;
        [SerializeField] private Camera _targetCamera;
        [SerializeField] private MeshFilter[] _targetMeshFilterArray; // 결합할 메쉬 필터 배열 (null인 경우 자신을 포함한 자식 메쉬 결합)

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
        private readonly Dictionary<Mesh, Mesh> _readableMeshCache = new Dictionary<Mesh, Mesh>();
        private readonly Dictionary<Mesh, Material> _meshToMaterialMap = new Dictionary<Mesh, Material>();
        
        #endregion


        #region Unity Events & Init/Release
        
        private void Awake()
        {
            CreateSaveDirectory();
            InitializeCamera();
        }

        private void Update()
        {
            if(_saveButton)
            {
                _saveButton = false;
                CombineAndSaveMeshes();
            }
        }

        private void OnDestroy()
        {
            ClearCaches();
        }

        #endregion


        #region Private Functions

        private void CombineAndSaveMeshes()
        {
            ClearCaches(); // 시작 전 캐시 정리
            
            MeshFilter[] meshFilters = _targetMeshFilterArray.Length <= 0 ? 
                GetComponentsInChildren<MeshFilter>(_includeInactive) : _targetMeshFilterArray;
            
            if (meshFilters.Length == 0)
            {
                Debug.LogWarning("결합할 메쉬가 없습니다.");
                return;
            }
            
            Debug.Log($"찾은 MeshFilter 개수: {meshFilters.Length}");

            List<MeshFilter> validMeshFilters = FilterValidMeshFilters(meshFilters, out int totalVertices, out int culledCount);
            
            if (validMeshFilters.Count == 0)
            {
                Debug.LogWarning("유효한 메쉬가 없습니다.");
                return;
            }

            Debug.Log($"유효한 MeshFilter 개수: {validMeshFilters.Count}, 총 버텍스: {totalVertices}, 컬링된 메쉬: {culledCount}");

            List<CombineInstance> combinedMesh = CombineMeshes(validMeshFilters.ToArray());
            
            if (combinedMesh != null)
                ProcessFinalMeshes(combinedMesh);
            else
                Debug.LogError("메쉬 결합 실패");
        }
        
        private void SaveMesh(Mesh mesh, string fileName)
        {
            if (mesh == null)
            {
                Debug.LogError("저장할 메쉬가 null입니다.");
                return;
            }

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
                    continue;
                
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
            if (_combineAllIntoSingleMesh && combinedMesh.Count > 1)
            {
                // 여러 메쉬를 1개로 최종 결합
                Mesh finalCombinedMesh = CombineMultipleMeshesIntoOne(combinedMesh);
                if (finalCombinedMesh != null)
                {
                    
                    SaveMesh(finalCombinedMesh, _meshName);
                }
                else
                {
                    Debug.LogError("최종 메쉬 결합 실패");
                }
            }
            else
            {
                // 개별 메쉬들을 각각 저장
                SaveIndividualMeshes(combinedMesh);
            }
        }

        private void SaveIndividualMeshes(List<CombineInstance> combinedMesh)
        {
            for (int i = 0; i < combinedMesh.Count; i++)
                SaveMesh(combinedMesh[i].mesh, $"{_meshName}_{i}");
        }

        private void InitializeCamera()
        {
            // 타겟 카메라가 설정되지 않은 경우 메인 카메라 사용
            if (_targetCamera == null)
            {
                _targetCamera = Camera.main;
                if (_targetCamera == null)
                    _targetCamera = FindObjectOfType<Camera>();
            }
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
            
            _readableMeshCache.Clear();
            _processedMeshes.Clear();
            _meshToMaterialMap.Clear();
        }
        
        private List<CombineInstance> CombineMeshes(MeshFilter[] meshFilters)
        {
            if (meshFilters.Length == 0)
                return null;
                
            Dictionary<Material, List<CombineInstance>> materialGroups = 
                new Dictionary<Material, List<CombineInstance>>();
            
            // 머티리얼별로 메쉬 그룹화
            foreach (MeshFilter meshFilter in meshFilters)
            {
                if (!ProcessMeshFilter(meshFilter, materialGroups))
                    continue;
            }
            
            // 머테리얼 별 메쉬 추출
            List<CombineInstance> finalCombines = CombineSubmeshes(materialGroups);
            
            return finalCombines;
        }

        private bool ProcessMeshFilter(MeshFilter meshFilter, Dictionary<Material, List<CombineInstance>> materialGroups)
        {
            if (!IsValidMeshFilter(meshFilter))
                return false;
            
            // 읽기 가능한 메쉬 생성 (캐싱 적용)
            Mesh readableMesh = GetReadableMeshCached(meshFilter.sharedMesh);
            if (readableMesh == null)
            {
                Debug.LogWarning($"메쉬를 읽기 가능하게 만들 수 없습니다: {meshFilter.name}");
                return false;
            }
            
            // 백페이스 컬링 적용 (이미 읽기 가능한 메쉬이므로 안전)
            if (_enableBackfaceCulling)
            {
                readableMesh = ApplyBackfaceCulling(readableMesh, meshFilter.transform);
                if (readableMesh == null || readableMesh.vertexCount == 0)
                {
                    Debug.LogWarning($"백페이스 컬링 후 메쉬가 비어있습니다: {meshFilter.name}");
                    return false;
                }
            }
            
            MeshRenderer meshRenderer = meshFilter.GetComponent<MeshRenderer>();
            if (!IsValidMeshRenderer(meshRenderer))
            {
                Debug.LogWarning($"MeshRenderer 또는 머티리얼이 없습니다: {meshFilter.name}");
                return false;
            }
            
            Material[] materials = meshRenderer.sharedMaterials;
            
            // 서브메쉬가 있는 경우 각각 처리
            if (readableMesh.subMeshCount > 1)
                ProcessMultiSubMesh(readableMesh, materials, meshFilter.transform, materialGroups);
            else
                ProcessSingleSubMesh(readableMesh, materials[0], meshFilter.transform, materialGroups, meshFilter.name);
            
            return true;
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
                return null;
            
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
                {
                    Debug.LogWarning($"서브메쉬 인덱스 {subMeshIndex}에 대응하는 머티리얼이 없습니다.");
                    continue;
                }
                
                Material material = materials[subMeshIndex];
                if (material == null)
                {
                    Debug.LogWarning($"서브메쉬 {subMeshIndex}의 머티리얼이 null입니다.");
                    continue;
                }
                
                // 서브메쉬를 개별 메쉬로 추출
                Mesh subMesh = ExtractSubMesh(readableMesh, subMeshIndex);
                if (subMesh == null || subMesh.vertexCount == 0)
                    continue;
                
                if (!materialGroups.ContainsKey(material))
                    materialGroups[material] = new List<CombineInstance>();
                
                Matrix4x4 matrix = transform.worldToLocalMatrix * meshTransform.localToWorldMatrix;
                
                CombineInstance combineInstance = new CombineInstance
                {
                    mesh = subMesh,
                    transform = matrix
                };
                
                materialGroups[material].Add(combineInstance);
                
            }
        }
        
        private void ProcessSingleSubMesh(Mesh readableMesh, Material material, Transform meshTransform, Dictionary<Material, List<CombineInstance>> materialGroups, string meshName)
        {
            if (material == null)
            {
                Debug.LogWarning($"머티리얼이 없습니다: {meshName}");
                return;
            }
            
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
                return null;
            
            Vector3[] vertices = originalMesh.vertices;
            Vector3[] normals = originalMesh.normals;
            Vector2[] uvs = originalMesh.uv;
            Color[] colors = originalMesh.colors;
            int[] triangles = originalMesh.GetTriangles(subMeshIndex);
            
            if (triangles == null || triangles.Length == 0)
                return null;
            
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
                
                if (!vertexMapping.ContainsKey(originalIndex))
                {
                    vertexMapping[originalIndex] = newVertices.Count;
                    newVertices.Add(vertices[originalIndex]);
                    
                    if (normals != null && normals.Length > originalIndex)
                        newNormals.Add(normals[originalIndex]);
                        
                    if (uvs != null && uvs.Length > originalIndex)
                        newUVs.Add(uvs[originalIndex]);
                        
                    if (colors != null && colors.Length > originalIndex)
                        newColors.Add(colors[originalIndex]);
                }
                
                newTriangles.Add(vertexMapping[originalIndex]);
            }
            
            // 새 메쉬 생성
            Mesh subMesh = new Mesh();
            subMesh.name = $"{originalMesh.name}_SubMesh_{subMeshIndex}";
            subMesh.vertices = newVertices.ToArray();
            subMesh.triangles = newTriangles.ToArray();
            
            if (newNormals.Count > 0)
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
                return null;
                
            // 이미 읽기 가능한 경우 그대로 반환
            if (originalMesh.isReadable)
                return originalMesh;
            
            // BackfaceCullMeshBaker 방식 적용: 베이킹을 통한 메쉬 데이터 추출
            Mesh readableMesh = CreateReadableMeshFromOriginal(originalMesh);
            
            if (readableMesh != null && readableMesh.vertexCount > 0)
                return readableMesh;
            
            return null;
        }
        
        private Mesh CreateReadableMeshFromOriginal(Mesh originalMesh)
        {
            GameObject tempGO = new GameObject(TEMP_MESH_EXTRACTOR_NAME);
            MeshFilter tempMF = tempGO.AddComponent<MeshFilter>();
            
            tempMF.sharedMesh = originalMesh;
            
            // Bake 방식으로 메쉬 데이터 추출 (BackfaceCullMeshBaker 방식)
            Mesh bakedMesh = new Mesh();
            bakedMesh.name = originalMesh.name + BAKED_SUFFIX;
            
            // SkinnedMeshRenderer를 통한 베이킹 시도
            SkinnedMeshRenderer tempSMR = tempGO.AddComponent<SkinnedMeshRenderer>();
            tempSMR.sharedMesh = originalMesh;
            tempSMR.BakeMesh(bakedMesh);

            DestroyImmediate(tempGO);
            
            if (bakedMesh.vertexCount > 0)
            {
                // BackfaceCullMeshBaker처럼 베이킹된 메쉬를 그대로 반환
                // 서브메쉬 구조는 베이킹 과정에서 손실되지만 이는 불가피함
                return bakedMesh;
            }
            
            Debug.Log($"베이킹된 메쉬가 없습니다: {originalMesh.name}");
            return null;
        }
        
        private bool ShouldCullMeshFilter(MeshFilter meshFilter)
        {
            if (_targetCamera == null)
                return false;
                
            Renderer renderer = meshFilter.GetComponent<Renderer>();
            if (renderer == null)
                return true;
            
            // 오클루전 컬링 - 렌더러가 실제로 렌더링되고 있는지 확인
            if (_enableOcclusionCulling && IsVertexRaycastOcclusionCulled(meshFilter, renderer))
                return true;
            
            return false;
        }
        
        private bool IsVertexRaycastOcclusionCulled(MeshFilter meshFilter, Renderer renderer)
        {
            if (_targetCamera == null || renderer == null)
                return false;
            
            if (meshFilter == null || meshFilter.sharedMesh == null)
                return false;
            
            Mesh mesh = GetReadableMesh(meshFilter.sharedMesh);
            if (mesh == null || mesh.vertices == null || mesh.vertices.Length == 0)
                return false;
            
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
            if (originalMesh == null || _targetCamera == null)
                return originalMesh;
            
            Vector3[] vertices = originalMesh.vertices;
            Vector3[] normals = originalMesh.normals;
            Vector2[] uvs = originalMesh.uv;
            Color[] colors = originalMesh.colors;
            
            if (vertices == null || vertices.Length == 0)
                return originalMesh;
            
            // 노멀이 없으면 전체 서브메쉬를 이용해 계산 (BackfaceCullMeshBaker 방식)
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
                {
                    culledSubMeshTriangles.Add(new int[0]);
                    continue;
                }

                List<int> culledTriangles = ProcessSubmeshTriangles(triangles, vertices, meshTransform, cameraForward);
                culledSubMeshTriangles.Add(culledTriangles.ToArray());
                
                if (culledTriangles.Count > 0)
                    hasAnyTriangles = true;
            }
            
            if (!hasAnyTriangles)
            {
                Debug.Log($"백페이스 컬링: {originalMesh.name}, 제거된 삼각형 없음");
                return originalMesh;
            }
            
            return CreateOptimizedMeshWithSubmeshes(vertices, normals, uvs, colors, culledSubMeshTriangles, originalMesh.name);
        }

        private List<int> ProcessSubmeshTriangles(int[] triangles, Vector3[] vertices, Transform meshTransform, Vector3 cameraForward)
        {
            List<int> culledTriangles = new List<int>();
            
            // 삼각형별로 백페이스 여부 검사
            for (int i = 0; i < triangles.Length; i += TRIANGLE_VERTEX_COUNT)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];
                
                if (v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length)
                    continue;
                
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
                
                if (originalNormals != null && originalNormals.Length > originalIndex)
                    newNormals.Add(originalNormals[originalIndex]);
                    
                if (originalUVs != null && originalUVs.Length > originalIndex)
                    newUVs.Add(originalUVs[originalIndex]);
                    
                if (originalColors != null && originalColors.Length > originalIndex)
                    newColors.Add(originalColors[originalIndex]);
            }
            
            // 새 메쉬 생성
            Mesh culledMesh = new Mesh();
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
            
            if (newNormals.Count > 0)
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
        
        private Vector3[] CalculateNormalsFromAllSubMeshes(Mesh mesh, Vector3[] vertices)
        {
            Vector3[] normals = new Vector3[vertices.Length];
            
            // 모든 서브메쉬의 삼각형을 이용해 노멀 계산 (BackfaceCullMeshBaker 방식)
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                int[] triangles = mesh.GetTriangles(subMeshIndex);
                if (triangles == null || triangles.Length == 0)
                    continue;
                
                // 각 삼각형의 노멀을 계산하여 버텍스에 누적
                for (int i = 0; i < triangles.Length; i += TRIANGLE_VERTEX_COUNT)
                {
                    int v0 = triangles[i];
                    int v1 = triangles[i + 1];
                    int v2 = triangles[i + 2];
                    
                    if (v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length)
                        continue;
                    
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
        
        private Vector3 CalculateTriangleNormal(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            return Vector3.Cross(edge1, edge2).normalized;
        }
        
        private void CreateSaveDirectory()
        {
            if (!AssetDatabase.IsValidFolder(SAVE_PATH.TrimEnd('/')))
            {
                string parentFolder = Path.GetDirectoryName(SAVE_PATH.TrimEnd('/'));
                string folderName = Path.GetFileName(SAVE_PATH.TrimEnd('/'));
                AssetDatabase.CreateFolder(parentFolder, folderName);
            }
        }
        
        private void SaveMeshAsAsset(Mesh mesh, string fileName)
        {
            string fullPath = SAVE_PATH + fileName + MESH_EXTENSION;
            
            // 메쉬 이름을 파일명과 일치하도록 설정 (Unity 경고 해결)
            mesh.name = fileName;
            
            // 기존 에셋이 있다면 덮어쓰기
            Mesh existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(fullPath);
            if (existingMesh != null)
            {
                existingMesh.Clear();
                EditorUtility.CopySerialized(mesh, existingMesh);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 업데이트되었습니다: {fullPath}");
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, fullPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 저장되었습니다: {fullPath}");
            }
            
            SetMeshReadWriteEnabled(fullPath, false);   

            AssetDatabase.Refresh();
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
                            continue;
                        
                        int meshVertexCount = combineInstance.mesh.vertexCount;
                        
                        // 현재 배치에 추가했을 때 한계를 초과하는 경우
                        if (currentVertexCount + meshVertexCount > MAX_VERTEX_COUNT && currentBatch.Count > 0)
                        {
                            // 현재 배치를 완료하고 새 배치 시작
                            Mesh batchMesh = new Mesh();
                            batchMesh.name = $"{_meshName}_{batchIndex}"; // 파일명과 일치하도록 설정
                            batchMesh.CombineMeshes(currentBatch.ToArray(), true);

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
                return null;
            
            // 단일 메쉬인 경우 그대로 반환
            if (combineInstances.Count == 1)
                return combineInstances[0].mesh;
            
            // 총 버텍스 수 계산
            int totalVertices = 0;
            foreach (var combineInstance in combineInstances)
            {
                if (combineInstance.mesh != null)
                    totalVertices += combineInstance.mesh.vertexCount;
            }
            
            // 65535 버텍스 제한 체크
            if (totalVertices > MAX_VERTEX_COUNT)
            {
                Debug.LogWarning($"최종 결합 메쉬의 버텍스 수가 제한을 초과합니다: {totalVertices} > {MAX_VERTEX_COUNT}");
                Debug.LogWarning("개별 메쉬들로 저장됩니다.");
                return null;
            }
            
            // 서브메쉬별 머티리얼 정보 수집 및 출력
            List<Material> subMeshMaterials = CollectSubMeshMaterials(combineInstances);
            OutputSubMeshMaterialInfo(subMeshMaterials);
            
            try
            {
                // 머티리얼별 서브메쉬를 유지하며 결합
                Mesh finalMesh = new Mesh();
                finalMesh.name = _meshName;
                
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
                    {
                        subMeshTriangles.Add(new List<int>());
                        continue;
                    }
                    
                    Mesh mesh = combineInstance.mesh;
                    Matrix4x4 matrix = combineInstance.transform;
                    int vertexOffset = allVertices.Count;
                    
                    // 버텍스 데이터 변환 및 추가
                    Vector3[] vertices = mesh.vertices;
                    Vector3[] normals = mesh.normals;
                    Vector2[] uvs = mesh.uv;
                    Color[] colors = mesh.colors;
                    Vector4[] tangents = mesh.tangents;
                    
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        allVertices.Add(matrix.MultiplyPoint3x4(vertices[i]));
                        
                        if (normals != null && normals.Length > i)
                            allNormals.Add(matrix.MultiplyVector(normals[i]).normalized);
                        else
                            allNormals.Add(Vector3.up);
                            
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
            catch (System.Exception e)
            {
                Debug.LogError($"최종 메쉬 결합 중 오류 발생: {e.Message}");
                return null;
            }
        }

        #endregion


#endif
    }
}