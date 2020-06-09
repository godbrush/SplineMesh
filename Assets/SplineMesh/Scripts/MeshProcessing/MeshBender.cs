using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SplineMesh {
    /// <summary>
    /// A component that creates a deformed mesh from a given one along the given spline segment.
    /// The source mesh will always be bended along the X axis.
    /// It can work on a cubic bezier curve or on any interval of a given spline.
    /// On the given interval, the mesh can be place with original scale, stretched, or repeated.
    /// The resulting mesh is stored in a MeshFilter component and automaticaly updated on the next update if the spline segment change.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [ExecuteInEditMode]
    public class MeshBender : MonoBehaviour {
        private bool isDirty = false;
        private Mesh result;
        private bool useSpline;
        private Spline spline;
        private float intervalStart, intervalEnd;
        private CubicBezierCurve curve;
        private Dictionary<float, CurveSample> sampleCache = new Dictionary<float, CurveSample>();

        private SourceMesh source;
        /// <summary>
        /// The source mesh to bend.
        /// </summary>
        public SourceMesh Source {
            get { return source; }
            set {
                if (value == source) return;
                SetDirty();
                source = value;
            }
        }
        
        private SourceMesh[] extraSources;
        public SourceMesh[] ExtraSources
        {
            get { return extraSources; }
            set
            {
                if (value == extraSources) return;
                SetDirty();
                extraSources = value;
            }
        }

        private FillingMode mode = FillingMode.StretchToInterval;
        /// <summary>
        /// The scaling mode along the spline
        /// </summary>
        public FillingMode Mode {
            get { return mode; }
            set {
                if (value == mode) return;
                SetDirty();
                mode = value;
            }
        }

        /// <summary>
        /// Sets a curve along which the mesh will be bent.
        /// The mesh will be updated if the curve changes.
        /// </summary>
        /// <param name="curve">The <see cref="CubicBezierCurve"/> to bend the source mesh along.</param>
        public void SetInterval(CubicBezierCurve curve) {
            if (this.curve == curve) return;
            if (curve == null) throw new ArgumentNullException("curve");
            if (this.curve != null) {
                this.curve.Changed.RemoveListener(SetDirty);
            }
            this.curve = curve;
            spline = null;
            curve.Changed.AddListener(SetDirty);
            useSpline = false;
            SetDirty();
        }

        /// <summary>
        /// Sets a spline's interval along which the mesh will be bent.
        /// If interval end is absent or set to 0, the interval goes from start to spline length.
        /// The mesh will be update if any of the curve changes on the spline, including curves
        /// outside the given interval.
        /// </summary>
        /// <param name="spline">The <see cref="SplineMesh"/> to bend the source mesh along.</param>
        /// <param name="intervalStart">Distance from the spline start to place the mesh minimum X.<param>
        /// <param name="intervalEnd">Distance from the spline start to stop deforming the source mesh.</param>
        public void SetInterval(Spline spline, float intervalStart, float intervalEnd = 0) {
            if (this.spline == spline && this.intervalStart == intervalStart && this.intervalEnd == intervalEnd) return;
            if (spline == null) throw new ArgumentNullException("spline");
            if (intervalStart < 0 || intervalStart >= spline.Length) {
                throw new ArgumentOutOfRangeException("interval start must be 0 or greater and lesser than spline length (was " + intervalStart + ")");
            }
            if (intervalEnd != 0 && intervalEnd <= intervalStart || intervalEnd > spline.Length) {
                throw new ArgumentOutOfRangeException("interval end must be 0 or greater than interval start, and lesser than spline length (was " + intervalEnd + ")");
            }
            if (this.spline != null) {
                // unlistening previous spline
                this.spline.CurveChanged.RemoveListener(SetDirty);
            }
            this.spline = spline;
            // listening new spline
            spline.CurveChanged.AddListener(SetDirty);

            curve = null;
            this.intervalStart = intervalStart;
            this.intervalEnd = intervalEnd;
            useSpline = true;
            SetDirty();
        }

        private void OnEnable() {
            if(GetComponent<MeshFilter>().sharedMesh != null) {
                result = GetComponent<MeshFilter>().sharedMesh;
            } else {
                GetComponent<MeshFilter>().sharedMesh = result = new Mesh();
                result.name = "Generated by " + GetType().Name;
            }
        }

        private void Update() {
            ComputeIfNeeded();
        }

        public void ComputeIfNeeded() {
            if (isDirty) {
                Compute();
            }
        }

        private void SetDirty() {
            isDirty = true;
        }

        /// <summary>
        /// Bend the mesh. This method may take time and should not be called more than necessary.
        /// Consider using <see cref="ComputeIfNeeded"/> for faster result.
        /// </summary>
        private  void Compute() {
            isDirty = false;
            switch (Mode) {
                case FillingMode.Once:
                    FillOnce();
                    break;
                case FillingMode.Repeat:
                    FillRepeat();
                    break;
                case FillingMode.StretchToInterval:
                    FillStretch();
                    break;
            }
        }

        private void OnDestroy() {
            if(curve != null) {
                curve.Changed.RemoveListener(Compute);
            }
        }

        /// <summary>
        /// The mode used by <see cref="MeshBender"/> to bend meshes on the interval.
        /// </summary>
        public enum FillingMode {
            /// <summary>
            /// In this mode, source mesh will be placed on the interval by preserving mesh scale.
            /// Vertices that are beyond interval end will be placed on the interval end.
            /// </summary>
            Once,
            /// <summary>
            /// In this mode, the mesh will be repeated to fill the interval, preserving
            /// mesh scale.
            /// This filling process will stop when the remaining space is not enough to
            /// place a whole mesh, leading to an empty interval.
            /// </summary>
            Repeat,
            /// <summary>
            /// In this mode, the mesh is deformed along the X axis to fill exactly the interval.
            /// </summary>
            StretchToInterval
        }

        private void FillOnce() {
            sampleCache.Clear();
            var bentVertices = new List<MeshVertex>(source.Vertices.Count);
            // for each mesh vertex, we found its projection on the curve
            foreach (var vert in source.Vertices) {
                float distance = vert.position.x - source.MinX;
                CurveSample sample;
                if (!sampleCache.TryGetValue(distance, out sample)) {
                    if (!useSpline) {
                        if (distance > curve.Length) distance = curve.Length;
                        sample = curve.GetSampleAtDistance(distance);
                    } else {
                        float distOnSpline = intervalStart + distance;
                        if (distOnSpline > spline.Length) {
                            if (spline.IsLoop) {
                                while (distOnSpline > spline.Length) {
                                    distOnSpline -= spline.Length;
                                }
                            } else {
                                distOnSpline = spline.Length;
                            }
                        }
                        sample = spline.GetSampleAtDistance(distOnSpline);
                    }
                    sampleCache[distance] = sample;
                }

                bentVertices.Add(sample.GetBent(vert));
            }

            MeshUtility.Update(result,
                source.Mesh,
                source.Triangles,
                bentVertices.Select(b => b.position),
                bentVertices.Select(b => b.normal));
        }

        private ref SourceMesh GetCurrentRepeatSource(int repeatStep, int repetitionCount)
        {
            UnityEngine.Random.InitState(repeatStep);
            if (null != extraSources && 0 < extraSources.Length)
            {
                for(int i = 0; i < extraSources.Length; ++i)
                {
                    ref SourceMesh extraSource = ref extraSources[i];
                    switch(extraSource.placeType)
                    {
                        case MeshPlaceType.Sequence:
                            {
                                int seqIndex = Mathf.RoundToInt(extraSource.placeWeight);
                                if (0 <= Mathf.Sign(extraSource.placeWeight) && seqIndex == repeatStep) return ref extraSource;
                                if (-0 >= Mathf.Sign(extraSource.placeWeight) && seqIndex == (repeatStep - (repetitionCount - 1))) return ref extraSource;
                            }
                            break;
                        case MeshPlaceType.Random:
                            if (UnityEngine.Random.value <= extraSource.placeWeight) return ref extraSource;
                            break;
                    }
                }
            }
            return ref source;
        }

        private IEnumerable<Vector2> MakeSafeUV(Vector2[] uv, int verticesCount)
        {
            if (null == uv || verticesCount != uv.Length)
            {
                return Enumerable.Repeat(Vector2.zero, verticesCount);
            }
            return uv;
        }

        private void FillRepeat() {
            float intervalLength = useSpline ?
                (intervalEnd == 0 ? spline.Length : intervalEnd) - intervalStart :
                curve.Length;
            int repetitionCount = 0;

            for (float d = 0; d <= intervalLength;)
            {
                d += GetCurrentRepeatSource(repetitionCount, -1).Length;
                if (d < intervalLength)
                {
                    ++repetitionCount;
                }
            }

            bool[] useUV = new bool[8];
            useUV[0] = null != source.Mesh.uv && 0 < source.Mesh.uv.Length;
            useUV[1] = null != source.Mesh.uv2 && 0 < source.Mesh.uv2.Length;
            useUV[2] = null != source.Mesh.uv3 && 0 < source.Mesh.uv3.Length;
            useUV[3] = null != source.Mesh.uv4 && 0 < source.Mesh.uv4.Length;
            useUV[4] = null != source.Mesh.uv5 && 0 < source.Mesh.uv5.Length;
            useUV[5] = null != source.Mesh.uv6 && 0 < source.Mesh.uv6.Length;
            useUV[6] = null != source.Mesh.uv7 && 0 < source.Mesh.uv7.Length;
            useUV[7] = null != source.Mesh.uv8 && 0 < source.Mesh.uv8.Length;

            if (extraSources != null)
            {
                foreach (var extraSource in extraSources)
                {
                    useUV[0] |= null != extraSource.Mesh.uv && 0 < extraSource.Mesh.uv.Length;
                    useUV[1] |= null != extraSource.Mesh.uv2 && 0 < extraSource.Mesh.uv2.Length;
                    useUV[2] |= null != extraSource.Mesh.uv3 && 0 < extraSource.Mesh.uv3.Length;
                    useUV[3] |= null != extraSource.Mesh.uv4 && 0 < extraSource.Mesh.uv4.Length;
                    useUV[4] |= null != extraSource.Mesh.uv5 && 0 < extraSource.Mesh.uv5.Length;
                    useUV[5] |= null != extraSource.Mesh.uv6 && 0 < extraSource.Mesh.uv6.Length;
                    useUV[6] |= null != extraSource.Mesh.uv7 && 0 < extraSource.Mesh.uv7.Length;
                    useUV[7] |= null != extraSource.Mesh.uv8 && 0 < extraSource.Mesh.uv8.Length;
                }
            }

            // building triangles and UVs for the repeated mesh
            var trianglesDict = new Dictionary<int, List<int>>();
            var uv = new List<Vector2>();
            var uv2 = new List<Vector2>();
            var uv3 = new List<Vector2>();
            var uv4 = new List<Vector2>();
            var uv5 = new List<Vector2>();
            var uv6 = new List<Vector2>();
            var uv7 = new List<Vector2>();
            var uv8 = new List<Vector2>();
            int vertexCount = 0;
            for (int i = 0; i < repetitionCount; i++) {
                var currentSource = GetCurrentRepeatSource(i, repetitionCount);

                int dictKey = i % (null == ExtraSources ? 1 : ExtraSources.Length + 1);

                if (false == trianglesDict.ContainsKey(dictKey))
                {
                    trianglesDict.Add(dictKey, new List<int>());
                }

                List<int> triangles = trianglesDict[dictKey];

                foreach (var index in currentSource.Triangles) {
                    triangles.Add(index + vertexCount);
                }
                if (useUV[0])
                {
                    uv.AddRange(MakeSafeUV(currentSource.Mesh.uv, currentSource.Vertices.Count));
                }
                if (useUV[1])
                {
                    uv2.AddRange(MakeSafeUV(currentSource.Mesh.uv2, currentSource.Vertices.Count));
                }
                if (useUV[2])
                {
                    uv3.AddRange(MakeSafeUV(currentSource.Mesh.uv3, currentSource.Vertices.Count));
                }
                if (useUV[3])
                {
                    uv4.AddRange(MakeSafeUV(currentSource.Mesh.uv4, currentSource.Vertices.Count));
                }
#if UNITY_2018_2_OR_NEWER
                if (useUV[4])
                {
                    uv5.AddRange(MakeSafeUV(currentSource.Mesh.uv5, currentSource.Vertices.Count));
                }
                if (useUV[5])
                {
                    uv6.AddRange(MakeSafeUV(currentSource.Mesh.uv6, currentSource.Vertices.Count));
                }
                if (useUV[6])
                {
                    uv7.AddRange(MakeSafeUV(currentSource.Mesh.uv7, currentSource.Vertices.Count));
                }
                if (useUV[7])
                {
                    uv8.AddRange(MakeSafeUV(currentSource.Mesh.uv8, currentSource.Vertices.Count));
                }
#endif
                vertexCount += currentSource.Vertices.Count;
            }

            // computing vertices and normals
            var bentVertices = new List<MeshVertex>(vertexCount);
            float offset = 0;
            for (int i = 0; i < repetitionCount; i++) {
                var currentSource = GetCurrentRepeatSource(i, repetitionCount);
                sampleCache.Clear();
                // for each mesh vertex, we found its projection on the curve
                foreach (var vert in currentSource.Vertices) {
                    float distance = vert.position.x - currentSource.MinX + offset;
                    CurveSample sample;
                    if (!sampleCache.TryGetValue(distance, out sample)) {
                        if (!useSpline) {
                            if (distance > curve.Length) continue;
                            sample = curve.GetSampleAtDistance(distance);
                        } else {
                            float distOnSpline = intervalStart + distance;
                            //if (true) { //spline.isLoop) {
                            while (distOnSpline > spline.Length) {
                                distOnSpline -= spline.Length;
                            }
                            //} else if (distOnSpline > spline.Length) {
                            //    continue;
                            //}
                            sample = spline.GetSampleAtDistance(distOnSpline);
                        }
                        sampleCache[distance] = sample;
                    }
                    bentVertices.Add(sample.GetBent(vert));
                }
                offset += currentSource.Length;
            }

            result.Clear();

            result.hideFlags = source.Mesh.hideFlags;
            result.indexFormat = source.Mesh.indexFormat;

            result.vertices = bentVertices.Select(b => b.position).ToArray();
            result.normals = bentVertices.Select(b => b.normal).ToArray();
            if (0 < uv.Count) result.SetUVs(0, uv);
            if (0 < uv2.Count) result.SetUVs(1, uv2);
            if (0 < uv3.Count) result.SetUVs(2, uv3);
            if (0 < uv4.Count) result.SetUVs(3, uv4);
            if (0 < uv5.Count) result.SetUVs(4, uv5);
            if (0 < uv6.Count) result.SetUVs(5, uv6);
            if (0 < uv7.Count) result.SetUVs(6, uv7);
            if (0 < uv8.Count) result.SetUVs(7, uv8);

            result.subMeshCount = trianglesDict.Count;
            int subMeshIndex = 0;
            foreach(var triangles in trianglesDict.Values)
            {
                result.SetTriangles(triangles, subMeshIndex);
                ++subMeshIndex;
            }

            result.RecalculateBounds();
            result.RecalculateTangents();
        }

        private void FillStretch() {
            var bentVertices = new List<MeshVertex>(source.Vertices.Count);
            sampleCache.Clear();
            // for each mesh vertex, we found its projection on the curve
            foreach (var vert in source.Vertices) {
                float distanceRate = source.Length == 0 ? 0 : Math.Abs(vert.position.x - source.MinX) / source.Length;
                CurveSample sample;
                if (!sampleCache.TryGetValue(distanceRate, out sample)) {
                    if (!useSpline) {
                        sample = curve.GetSampleAtDistance(curve.Length * distanceRate);
                    } else {
                        float intervalLength = intervalEnd == 0 ? spline.Length - intervalStart : intervalEnd - intervalStart;
                        float distOnSpline = intervalStart + intervalLength * distanceRate;
                        if(distOnSpline > spline.Length) {
                            distOnSpline = spline.Length;
                            Debug.Log("dist " + distOnSpline + " spline length " + spline.Length + " start " + intervalStart);
                        }

                        sample = spline.GetSampleAtDistance(distOnSpline);
                    }
                    sampleCache[distanceRate] = sample;
                }

                bentVertices.Add(sample.GetBent(vert));
            }

            MeshUtility.Update(result,
                source.Mesh,
                source.Triangles,
                bentVertices.Select(b => b.position),
                bentVertices.Select(b => b.normal));
        }


    }
}