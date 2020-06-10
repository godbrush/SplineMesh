using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace SplineMesh {
    /// <summary>
    /// It is specialized version of <see cref="SplineMeshTiling"/>'s repeat mode.
    /// Key features:
    /// - it can part settings to generate spline mesh. 
    /// - automatic split mesh to avoid glitch (it occurs when index buffer is over limit).
    /// </summary>
    [ExecuteInEditMode]
    [SelectionBase]
    [DisallowMultipleComponent]
    public class SplineMeshTilingExt : MonoBehaviour {

        [System.Serializable]
        public enum PlaceType {
            None,
            Sequence,
            Random
        }

        [System.Serializable]
        public struct PartInfo {
            public Mesh mesh;
            public Material material;
            public Vector3 translation;
            public Vector3 rotation;
            public Vector3 scale;
            public PlaceType placeType;
            public float placeValue;
        }

        public PartInfo[] meshInfos;

        private Spline spline = null;

        private bool toUpdate = false;

        private void OnEnable() {
            spline = GetComponentInParent<Spline>();
            spline.NodeListChanged += (s, e) => toUpdate = true;
            spline.CurveChanged.AddListener(() => toUpdate = true);
            toUpdate = true;
        }

        private void OnValidate() {
            if (spline == null) return;
            toUpdate = true;
        }

        private void Update() {
            if (Application.isPlaying) return;

            if (toUpdate) {
                toUpdate = false;
                CreateMeshes();
            }
        }

        private List<int> decisionParts = new List<int>();
        private SourceMesh[] sourceMeshes;

        private void UpdateSourceMeshes()
        {
            if (null == sourceMeshes || sourceMeshes.Length != meshInfos.Length)
            {
                sourceMeshes = new SourceMesh[meshInfos.Length];
            }
            for(int i = 0; i < meshInfos.Length; ++i)
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

        struct MeshChunk {
            public List<MeshVertex> bentVertices;
            public List<int> triangles;
            public List<Vector2>[] uv;
        }

        public void CreateMeshes() {
            if (null == spline || null == meshInfos || 0 == meshInfos.Length) return;

            UpdateSourceMeshes();

            decisionParts.Clear();

            for(float d = 0; d <= spline.Length && decisionParts.Count < 10000;) {
                int sourceMeshIndex = 0;

                d += sourceMeshes[sourceMeshIndex].Length;
                if (d <= spline.Length) {
                    decisionParts.Add(sourceMeshIndex);
                }
            }

            var meshChunkDict = new Dictionary<Material, MeshChunk>();
            var sampleCache = new Dictionary<float, CurveSample>();

            float offset = 0;
            for(int i = 0; i < decisionParts.Count; ++i)
            {
                int index = decisionParts[i];

                if (false == meshChunkDict.ContainsKey(meshInfos[index].material)) {
                    int predictVertexCount = decisionParts.Sum(idx => meshInfos[idx].material == meshInfos[index].material ? meshInfos[idx].mesh.vertexCount : 0);

                    meshChunkDict.Add(meshInfos[index].material, new MeshChunk() {
                        bentVertices = new List<MeshVertex>(predictVertexCount),
                        triangles = new List<int>(predictVertexCount / 3),
                        uv = new List<Vector2>[8],
                    });
                }

                var meshChunk = meshChunkDict[meshInfos[index].material];

                ref SourceMesh sourceMesh = ref sourceMeshes[index];

                meshChunk.triangles.AddRange(sourceMesh.Triangles.Select(idx => idx + meshChunk.bentVertices.Count));
                List<Vector2> UV = new List<Vector2>();
                for(int channel = 0; channel < 8; ++channel) {
                    UV.Clear();
                    sourceMesh.Mesh.GetUVs(channel, UV);
                    if (sourceMesh.Vertices.Count == UV.Count) {
                        if (null == meshChunk.uv[channel]) {
                            meshChunk.uv[channel] = new List<Vector2>(UV);
                        } else {
                            meshChunk.uv[channel].AddRange(UV);
                        }
                    } else {
                        if (null != meshChunk.uv[channel]) {
                            meshChunk.uv[channel].AddRange(Enumerable.Repeat(Vector2.zero, meshChunk.bentVertices.Count + sourceMesh.Vertices.Count - meshChunk.uv[channel].Count));
                        }
                    }
                }
                foreach(var vert in sourceMesh.Vertices) {
                    float distance = vert.position.x - sourceMesh.MinX + offset;

                    CurveSample sample;
                    if (false == sampleCache.TryGetValue(distance, out sample)) {
                        sample = spline.GetSampleAtDistance(distance);
                        sampleCache.Add(distance, sample);
                    }

                    meshChunk.bentVertices.Add(sample.GetBent(vert));
                }

                offset += sourceMeshes[index].Length;
            }

            foreach(var pair in meshChunkDict) {
                var meshChunk = pair.Value;
                // TODO:
            }

            Debug.LogFormat("Parts: {0}, materialCount: {1}, vertices: {2}", decisionParts.Count, meshChunkDict.Count, meshChunkDict.Sum(pair => pair.Value.bentVertices.Count));
        }
    }
}