using System;
using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

namespace Common.OptimizeTool
{
    public class BackfaceCullMeshBaker : MonoBehaviour
    {
#if UNITY_EDITOR
        
        #region Constants
        
        private const int TRIANGLE_VERTEX_COUNT = 3;
        private const string MESH_EXTENSION = ".asset";
        private const string BACKFACE_CULLED_SUFFIX = "_BackfaceCulled";
        private const string BAKED_SUFFIX = "_Baked";
        private const string TEMP_MESH_EXTRACTOR_NAME = "TempMeshExtractor";
        private const string READABLE_PROPERTY_NAME = "m_IsReadable";
        
        #endregion


        #region Inner Classes

        private class BakedMeshData
        {
            private const int HASH_BASE = 17;
            private const int HASH_MULTIPLIER = 31;

            public Mesh Mesh { get; private set; }
            public Quaternion Rotation { get; private set; }

            public BakedMeshData(Mesh mesh, Quaternion rotation)
            {
                Mesh = mesh;
                Rotation = rotation;
            }

            public override bool Equals(object obj)
            {
                if (obj == null || GetType() != obj.GetType())
                    return false;

                BakedMeshData other = (BakedMeshData)obj;
                return Mesh == other.Mesh && Rotation == other.Rotation;
            }

            public override int GetHashCode()
            {
                int hash = HASH_BASE;
                
                if (Mesh != null)
                    hash = hash * HASH_MULTIPLIER + Mesh.GetHashCode();
                    
                hash = hash * HASH_MULTIPLIER + Rotation.GetHashCode();
                
                return hash;
            }
        }

        #endregion


        #region Member Variables

        private static Dictionary<BakedMeshData, Mesh> _bakedMeshDictionary = new Dictionary<BakedMeshData, Mesh>();
        private static Dictionary<Mesh, Mesh> _readableMeshDictionary = new Dictionary<Mesh, Mesh>();
        private static readonly HashSet<Mesh> _temporaryMeshSet = new HashSet<Mesh>();

        [SerializeField] private string _savePath;
        [SerializeField] private MeshFilter[] _targetMeshFilterArray;
        [SerializeField] private bool _collectChildMeshes = false;
        [SerializeField] private bool _allowReadableCopy = false;
        [SerializeField] private bool _allowNormalRecalculation = false;
        [SerializeField] private bool _overwriteExistingAssets = false;
        [SerializeField] private Camera _targetCamera;
        [SerializeField] private float _backfaceCullingAngle = 90f;

        [Header("Custom Vector")]
        [SerializeField] private bool _useCustomVector = false;
        [SerializeField] private Vector3 _customCameraVector = Vector3.zero;

        [Header("Save Button")]
        [SerializeField] private bool _saveButton = false;
        private bool _isInitialized;
        private bool _isFaulted;
        private System.Exception _fault;

        #endregion


        #region Unity Events & Init/Release

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
                throw new InvalidOperationException("BackfaceCullMeshBaker is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("BackfaceCullMeshBaker is faulted; call Release before Init.", _fault);
            try
            {
                ValidateAssetFolderPath(_savePath);
                if (_targetCamera == null)
                    throw new InvalidOperationException("Backface culling requires a serialized target camera.");
                if (!_collectChildMeshes && (_targetMeshFilterArray == null || _targetMeshFilterArray.Length == 0))
                    throw new ArgumentException("Assign target mesh filters or enable child collection.", nameof(_targetMeshFilterArray));
                if (_collectChildMeshes && _targetMeshFilterArray != null && _targetMeshFilterArray.Length > 0)
                    throw new ArgumentException("Choose either direct mesh filters or child collection, not both.");
                if (!IsFinite(_backfaceCullingAngle) || _backfaceCullingAngle < 0f || _backfaceCullingAngle > 180f)
                    throw new ArgumentOutOfRangeException(nameof(_backfaceCullingAngle));
                if (_useCustomVector
                    && (!IsFinite(_customCameraVector) || _customCameraVector.sqrMagnitude <= 0.000001f))
                    throw new ArgumentOutOfRangeException(nameof(_customCameraVector), "Custom camera vector must be finite and non-zero.");
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
                throw new InvalidOperationException("BackfaceCullMeshBaker is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("BackfaceCullMeshBaker is not initialized.");
            if(_saveButton)
            {
                if (Application.isPlaying)
                    throw new InvalidOperationException("Backface culling and asset generation are editor-only.");
                _saveButton = false;
                BakeBackFaceCullingMesh();
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
                throw new InvalidOperationException("BackfaceCullMeshBaker has not been initialized.");
            ClearTransientCaches();
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private static void ClearTransientCaches()
        {
            foreach (Mesh temporaryMesh in _temporaryMeshSet)
            {
                if (temporaryMesh != null)
                    DestroyImmediate(temporaryMesh);
            }
            _temporaryMeshSet.Clear();

            foreach (KeyValuePair<Mesh, Mesh> pair in _readableMeshDictionary)
            {
                if (pair.Value != null && pair.Value != pair.Key)
                    DestroyImmediate(pair.Value);
            }
            _readableMeshDictionary.Clear();
            _bakedMeshDictionary.Clear();
        }

        #endregion


        #region Private Functions

        private void BakeBackFaceCullingMesh()
        {
            if (_isFaulted)
                throw new InvalidOperationException("BackfaceCullMeshBaker is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("BackfaceCullMeshBaker is not initialized.");
            MeshFilter[] meshes = _collectChildMeshes
                ? GetComponentsInChildren<MeshFilter>()
                : _targetMeshFilterArray;

            if (meshes == null || meshes.Length == 0)
                throw new ArgumentException("At least one MeshFilter is required.", nameof(meshes));

            // Validate every input before touching AssetDatabase or mutating a source filter.
            for (int i = 0; i < meshes.Length; i++)
            {
                MeshFilter meshFilter = meshes[i];
                if (meshFilter == null)
                    throw new ArgumentException("Every supplied MeshFilter must be non-null.", nameof(meshes));
                Mesh mesh = meshFilter.sharedMesh;

                if(mesh == null)
                    throw new ArgumentException($"MeshFilter '{meshFilter.name}' has no mesh.");
                if (mesh.vertexCount == 0)
                    throw new ArgumentException($"MeshFilter '{meshFilter.name}' has an empty mesh.");
            }

            ValidateOutputAssets(meshes);
            CreateSaveFolder();

            try
            {
                foreach (MeshFilter meshFilter in meshes)
                {
                    Mesh mesh = meshFilter.sharedMesh;

                    mesh = GetReadableMesh(mesh);
                    if (mesh == null)
                        throw new InvalidOperationException("Readable mesh creation failed.");

                    BakedMeshData bakedMeshData = new BakedMeshData(mesh, meshFilter.transform.rotation);
                    if (_bakedMeshDictionary.TryGetValue(bakedMeshData, out Mesh bakedMesh))
                    {
                        meshFilter.sharedMesh = bakedMesh;
                        continue;
                    }

                    Mesh backfaceCulledMesh = ApplyBackfaceCulling(mesh, meshFilter.transform, meshFilter.name);
                    if (backfaceCulledMesh == null)
                        throw new InvalidOperationException("Backface culling produced no mesh.");

                    Mesh savedMesh = SaveMeshAsAsset(backfaceCulledMesh, backfaceCulledMesh.name);
                    meshFilter.sharedMesh = savedMesh;
                    _bakedMeshDictionary.Add(bakedMeshData, savedMesh);
                }
            }
            finally
            {
                ClearTransientCaches();
            }
        }

        private void ValidateOutputAssets(MeshFilter[] meshes)
        {
            if (meshes == null || meshes.Length == 0)
                throw new ArgumentException("At least one MeshFilter is required.", nameof(meshes));

            HashSet<string> outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string outputDirectory = ValidateAssetFolderPath(_savePath);
            for (int i = 0; i < meshes.Length; i++)
            {
                MeshFilter meshFilter = meshes[i];
                if (meshFilter == null)
                    throw new ArgumentException("Every supplied MeshFilter must be non-null.", nameof(meshes));

                string fileName = meshFilter.name + BACKFACE_CULLED_SUFFIX;
                ValidateAssetFileName(fileName);
                string assetPath = $"{outputDirectory}/{fileName}{MESH_EXTENSION}";
                if (!outputPaths.Add(assetPath))
                    throw new InvalidOperationException($"Multiple MeshFilters resolve to the same output asset: {assetPath}");

                UnityEngine.Object existingAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (existingAsset != null && !(existingAsset is Mesh))
                    throw new InvalidOperationException($"Backface output path is occupied by a non-mesh asset: {assetPath}");
                if (existingAsset != null && !_overwriteExistingAssets)
                    throw new InvalidOperationException($"Mesh asset already exists: {assetPath}");
            }
        }

        private Mesh ApplyBackfaceCulling(Mesh baseMesh, Transform meshTransform, string meshName)
        {
            if (baseMesh == null)
                throw new ArgumentNullException(nameof(baseMesh));
            if (_targetCamera == null)
                throw new InvalidOperationException("Backface culling requires a serialized target camera.");
            
            Vector3[] vertices = baseMesh.vertices;
            Vector3[] normals = baseMesh.normals;
            Vector2[] uvs = baseMesh.uv;
            Color[] colors = baseMesh.colors;
            
            if (vertices == null || vertices.Length == 0)
                throw new ArgumentException("Mesh must contain vertices.", nameof(baseMesh));
            
            if (normals == null || normals.Length != vertices.Length)
            {
                if (!_allowNormalRecalculation)
                    throw new ArgumentException($"Mesh '{baseMesh.name}' has no normals.");
                normals = CalculateNormalsFromAllSubmeshes(baseMesh, vertices);
            }
            
            Vector3 cameraForward = _useCustomVector ? 
                -_customCameraVector.normalized : -_targetCamera.transform.forward.normalized;

            // 서브메쉬별로 처리
            int subMeshCount = baseMesh.subMeshCount;
            List<int[]> culledSubMeshTriangles = new List<int[]>();
            bool hasAnyTriangles = false;

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] triangles = baseMesh.GetTriangles(subMeshIndex);
                
                if (triangles == null || triangles.Length == 0 || triangles.Length % TRIANGLE_VERTEX_COUNT != 0)
                    throw new ArgumentException($"Submesh {subMeshIndex} contains invalid triangle data.", nameof(baseMesh));

                List<int> culledTriangles = ProcessSubmeshTriangles(triangles, vertices, meshTransform, cameraForward);
                culledSubMeshTriangles.Add(culledTriangles.ToArray());
                
                if (culledTriangles.Count > 0)
                    hasAnyTriangles = true;
            }
            
            if (!hasAnyTriangles)
                throw new InvalidOperationException($"Backface culling removed every triangle from '{baseMesh.name}'.");
            
            return CreateOptimizedMeshWithSubmeshes(vertices, normals, uvs, colors, culledSubMeshTriangles, meshName);
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
            _temporaryMeshSet.Add(culledMesh);
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
            
            // 메쉬를 readable false로 설정하여 메모리 최적화
            culledMesh.UploadMeshData(true);
            
            return culledMesh;
        }

        private Mesh GetReadableMesh(Mesh originalMesh)
        {
            if (originalMesh == null)
                throw new ArgumentNullException(nameof(originalMesh));

            if (originalMesh.isReadable)
                return originalMesh;
            if (!_allowReadableCopy)
                throw new InvalidOperationException($"Mesh '{originalMesh.name}' is not readable.");
                
            if(_readableMeshDictionary.TryGetValue(originalMesh, out Mesh readableMesh))
                return readableMesh;
            
            readableMesh = CreateReadableMeshFromOriginal(originalMesh);
            
            if (readableMesh != null && readableMesh.vertexCount > 0)
            {
                _readableMeshDictionary.Add(originalMesh, readableMesh);
                return readableMesh;
            }
            
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

        private Vector3 CalculateTriangleNormal(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            return Vector3.Cross(edge1, edge2).normalized;
        }
        
        private Vector3[] CalculateNormalsFromAllSubmeshes(Mesh mesh, Vector3[] vertices)
        {
            Vector3[] normals = new Vector3[vertices.Length];
            
            // 모든 서브메쉬의 삼각형을 이용해 노멀 계산
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

        private Mesh SaveMeshAsAsset(Mesh mesh, string fileName)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            ValidateAssetFileName(fileName);
            string outputDirectory = ValidateAssetFolderPath(_savePath);
            string fullPath = $"{outputDirectory}/{fileName}{MESH_EXTENSION}";
            
            // 기존 에셋이 있다면 덮어쓰기
            Mesh existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(fullPath);
            if (existingMesh != null)
            {
                if (!_overwriteExistingAssets)
                    throw new InvalidOperationException($"Mesh asset already exists: {fullPath}");
                existingMesh.Clear();
                EditorUtility.CopySerialized(mesh, existingMesh);
                _temporaryMeshSet.Remove(mesh);
                DestroyImmediate(mesh);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 업데이트되었습니다: {fullPath}");
                mesh = existingMesh;
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, fullPath);
                _temporaryMeshSet.Remove(mesh);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 저장되었습니다: {fullPath}");
            }
            
            // 저장된 메쉬의 Read/Write Enabled를 false로 설정
            SetMeshReadWriteEnabled(fullPath, false);
            
            AssetDatabase.Refresh();
            return mesh;
        }

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

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

        #endregion

#endif
    }
}
