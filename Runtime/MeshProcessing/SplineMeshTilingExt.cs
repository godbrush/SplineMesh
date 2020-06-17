using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace SplineMesh
{
    /// <summary>
    /// It is specialized version of <see cref="SplineMeshTiling"/>'s repeat mode.
    /// Key features:
    /// - it can part settings to generate spline mesh. 
    /// - automatic split mesh to avoid glitch (it occurs when index buffer is over limit).
    /// </summary>
    [ExecuteInEditMode]
    [SelectionBase]
    [DisallowMultipleComponent]
    public class SplineMeshTilingExt : MonoBehaviour
    {

        [System.Serializable]
        public enum PlaceType
        {
            None,
            Sequence,
            Random,
        }

        [System.Serializable]
        public struct PartInfo
        {
            public Mesh mesh;
            public Material material;
            public Vector3 translation;
            public Vector3 rotation;
            public Vector3 scale;
            public PlaceType placeType;
            public float placeValue;
        }

        public PartInfo[] meshInfos;
        public bool stretchScale = false;

        public int PerChunkMaxVertices = 65535;
        public float PerChunkMaxLength = 10;
        public int Seed = -1;

        [Tooltip("If true, a mesh collider will be generated.")]
        public bool generateCollider = true;

        [Tooltip("If true, the mesh will be bent on play mode. If false, the bent mesh will be kept from the editor mode, allowing lighting baking.")]
        public bool updateInPlayMode;

        private Spline spline = null;

        private bool toUpdate = false;

        private void OnEnable()
        {
            spline = GetComponentInParent<Spline>();
            spline.NodeListChanged += (s, e) => toUpdate = true;
            spline.CurveChanged.AddListener(() => toUpdate = true);
            toUpdate = true;
        }

        private void OnValidate()
        {
            if (spline == null) return;
            toUpdate = true;
        }

        private void Update()
        {
            if (false == updateInPlayMode && Application.isPlaying) return;

            if (toUpdate)
            {
                toUpdate = false;
                CreateMeshes();
            }
        }

        private List<int> decisionParts = new List<int>();
        private List<float> decisionPartsLength = new List<float>();
        private float partsScale = 1;
        private SourceMesh[] sourceMeshes;

        private void UpdateSourceMeshes()
        {
            if (null == sourceMeshes || sourceMeshes.Length != meshInfos.Length)
            {
                sourceMeshes = new SourceMesh[meshInfos.Length];
            }
            for (int i = 0; i < meshInfos.Length; ++i)
            {
                var newSourceMesh = SourceMesh.Build(meshInfos[i].mesh)
                    .Translate(meshInfos[i].translation)
                    .Rotate(Quaternion.Euler(meshInfos[i].rotation))
                    .Scale(meshInfos[i].scale);
                if (false == sourceMeshes[i].Equals(newSourceMesh))
                {
                    sourceMeshes[i] = newSourceMesh;
                }
            }
        }

        private void UpdateDecisionParts()
        {
            decisionParts.Clear();

            Random.InitState(Seed);

            // Phase 1: Fill w/o backward sequence.
            for (float d = 0; d <= spline.Length && decisionParts.Count < 1000;)
            {
                int sourceMeshIndex = -1;

                // Sequence : Forward only
                if (-1 == sourceMeshIndex)
                {
                    sourceMeshIndex = System.Array.FindIndex(meshInfos, info =>
                    {
                        if (PlaceType.Sequence == info.placeType && 0 <= info.placeValue)
                        {
                            int seqIndex = Mathf.RoundToInt(info.placeValue);
                            return seqIndex == decisionParts.Count;
                        }
                        return false;
                    });
                }
                // Random
                if (-1 == sourceMeshIndex)
                {
                    sourceMeshIndex = System.Array.FindIndex(meshInfos, info =>
                    {
                        if (PlaceType.Random == info.placeType)
                        {
                            return Random.value <= info.placeValue;
                        }
                        return false;
                    });
                }
                // None
                if (-1 == sourceMeshIndex)
                {
                    sourceMeshIndex = System.Array.FindIndex(meshInfos, info => PlaceType.None == info.placeType);
                }

                if (-1 == sourceMeshIndex)
                {
                    sourceMeshIndex = 0;
                }

                float sourceMeshLength = sourceMeshes[sourceMeshIndex].Length;

                if (Mathf.Approximately(sourceMeshLength, 0))
                {
                    Debug.LogError("SourceMesh.Length is must larger than zero.");
                    return;
                }

                d += sourceMeshLength;
                if (d <= spline.Length)
                {
                    decisionParts.Add(sourceMeshIndex);
                }
            }

            float partsLength = 0.0f;
            // Phase 2: Replace backward sequence.
            for (int i = 0; i < decisionParts.Count; ++i)
            {
                int sourceMeshIndex = decisionParts[i];

                if (PlaceType.Sequence != meshInfos[sourceMeshIndex].placeType)
                {
                    int backwardSeqMeshIndex = System.Array.FindIndex(meshInfos, info =>
                    {
                        if (PlaceType.Sequence == info.placeType && 0 > Mathf.Sign(info.placeValue))
                        {
                            int seqIndex = Mathf.RoundToInt(info.placeValue);
                            return seqIndex == ((decisionParts.Count - 1) - i);
                        }
                        return false;
                    });
                    if (-1 != backwardSeqMeshIndex)
                    {
                        decisionParts[i] = backwardSeqMeshIndex;
                        sourceMeshIndex = backwardSeqMeshIndex;
                    }
                }

                var sourceMeshLength = sourceMeshes[sourceMeshIndex].Length;

                partsLength += sourceMeshLength;
            }

            partsScale = 1;
            if (stretchScale)
            {
                if (false == Mathf.Approximately(partsLength, spline.Length))
                {
                    partsScale = spline.Length / partsLength;
                }
            }
        }

        class MeshChunk
        {
            public List<MeshVertex> bentVertices;
            public List<int> triangles;
            public List<Vector2>[] uv;
            public float length;
        }

        [HideInInspector]
        [SerializeField]
        private List<Transform> generatedChildren = new List<Transform>();

        public void CreateMeshes()
        {
            if (null == spline || null == meshInfos || 0 == meshInfos.Length) return;

            UpdateSourceMeshes();
            UpdateDecisionParts();

            var meshChunkDict = new Dictionary<Material, List<MeshChunk>>();
            var sampleCache = new Dictionary<float, CurveSample>();

            float offset = 0;
            for (int i = 0; i < decisionParts.Count; ++i)
            {
                int index = decisionParts[i];

                if (false == meshChunkDict.ContainsKey(meshInfos[index].material))
                {
                    meshChunkDict.Add(meshInfos[index].material, new List<MeshChunk>());
                }

                var meshChunkList = meshChunkDict[meshInfos[index].material];

                int vertexCount = meshInfos[index].mesh.vertices.Length;

                bool isReachedMaxVertices = 0 < meshChunkList.Count && PerChunkMaxVertices < (meshChunkList.Last().bentVertices.Count + vertexCount);
                bool isReachedMaxLength = 0 < meshChunkList.Count && PerChunkMaxLength < meshChunkList.Last().length;
                if (0 == meshChunkList.Count || isReachedMaxVertices || isReachedMaxLength)
                {
                    meshChunkList.Add(new MeshChunk()
                    {
                        bentVertices = new List<MeshVertex>(vertexCount),
                        triangles = new List<int>(vertexCount / 3),
                        uv = new List<Vector2>[8],
                        length = 0
                    });
                }

                var meshChunk = meshChunkList.Last();

                ref SourceMesh sourceMesh = ref sourceMeshes[index];

                meshChunk.triangles.AddRange(sourceMesh.Triangles.Select(idx => idx + meshChunk.bentVertices.Count));
                List<Vector2> UV = new List<Vector2>();
                for (int channel = 0; channel < 8; ++channel)
                {
                    UV.Clear();
                    sourceMesh.Mesh.GetUVs(channel, UV);
                    if (0 < UV.Count)
                    {
                        if (null == meshChunk.uv[channel])
                        {
                            meshChunk.uv[channel] = new List<Vector2>();
                        }
                        int fillCount = Mathf.Max(0, (meshChunk.bentVertices.Count - UV.Count) - meshChunk.uv[channel].Count);
                        if (0 < fillCount)
                        {
                            meshChunk.uv[channel].AddRange(Enumerable.Repeat(Vector2.zero, fillCount));
                        }
                        meshChunk.uv[channel].AddRange(UV);
                    }
                }
                foreach (var vertex in sourceMesh.Vertices)
                {
                    var vert = new MeshVertex(vertex.position, vertex.normal, vertex.uv);

                    vert.position.x *= partsScale;

                    float distance = vert.position.x - sourceMesh.MinX * partsScale + offset;

                    distance = Mathf.Clamp(distance, 0, spline.Length);

                    CurveSample sample;
                    if (false == sampleCache.TryGetValue(distance, out sample))
                    {
                        sample = spline.GetSampleAtDistance(distance);
                        sampleCache.Add(distance, sample);
                    }

                    meshChunk.bentVertices.Add(sample.GetBent(vert));
                }

                offset += sourceMeshes[index].Length * partsScale;
                meshChunk.length += sourceMeshes[index].Length * partsScale;
            }

            List<Transform> newGeneratedTransform = new List<Transform>();

            foreach (var pair in meshChunkDict)
            {
                var material = pair.Key;
                var meshChunkList = pair.Value;

                for (int segment = 0; segment < meshChunkList.Count; ++segment)
                {
                    var meshChunk = meshChunkList[segment];

                    string chunkName = $"{name}-{material.name}-{segment + 1}";
                    Transform chunkTransform = transform.Find(chunkName);
                    if (null == chunkTransform)
                    {
                        var go = UOUtility.Create(chunkName,
                            gameObject,
                            typeof(MeshFilter),
                            typeof(MeshRenderer),
                            typeof(MeshCollider));
                        chunkTransform = go.transform;
                    }
                    newGeneratedTransform.Add(chunkTransform);
                    generatedChildren.Remove(chunkTransform);

                    chunkTransform.gameObject.isStatic = false == updateInPlayMode;

                    var meshFilter = chunkTransform.GetComponent<MeshFilter>();
                    var meshCollider = chunkTransform.GetComponent<MeshCollider>();
                    var meshRenderer = chunkTransform.GetComponent<MeshRenderer>();

                    Mesh result = meshFilter.sharedMesh;
                    if (null == result || result.name != chunkName)
                    {
                        result = new Mesh();
                    }
                    else if (result.vertexCount != meshChunk.bentVertices.Count)
                    {
                        result.Clear();
                    }

                    result.name = chunkName;
                    result.hideFlags = HideFlags.HideInHierarchy;
                    result.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;

                    result.SetVertices(meshChunk.bentVertices.Select(b => b.position).ToList());
                    result.SetNormals(meshChunk.bentVertices.Select(b => b.normal).ToList());

                    for (int channel = 0; channel < meshChunk.uv.Length; ++channel)
                    {
                        if (null != meshChunk.uv[channel] && 0 < meshChunk.uv[channel].Count)
                        {
                            if (null != meshChunk.uv[channel])
                            {
                                int fillCount = Mathf.Max(0, meshChunk.bentVertices.Count - meshChunk.uv[channel].Count);
                                if (0 < fillCount)
                                {
                                    meshChunk.uv[channel].AddRange(Enumerable.Repeat(Vector2.zero, fillCount));
                                }
                            }
                            result.SetUVs(channel, meshChunk.uv[channel]);
                        }
                    }

                    result.SetTriangles(meshChunk.triangles, 0, false);

                    result.RecalculateBounds();
                    result.RecalculateTangents();

                    meshFilter.sharedMesh = result;
                    meshCollider.sharedMesh = result;
                    meshRenderer.sharedMaterial = material;

                    meshCollider.enabled = generateCollider;
                }
            }

            foreach (var deprecatedTransform in generatedChildren)
            {
                if (deprecatedTransform != null)
                {
                    if (false == Application.isPlaying)
                    {
                        GameObject.DestroyImmediate(deprecatedTransform.gameObject);
                    }
                    else if (updateInPlayMode)
                    {
                        GameObject.Destroy(deprecatedTransform.gameObject);
                    }
                }
            }
            generatedChildren = newGeneratedTransform;
        }
    }
}