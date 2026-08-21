using System.Collections.Generic;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Builds the water for each carved lake and puts boats on it.
    ///
    /// The basin itself is cut by LakeGen before the heightmap reaches Unity;
    /// this is only the surface and what floats on it. The two must agree about
    /// where the shoreline is, which is why both ask LakeGen.RadiusAt rather than
    /// each having its own idea of the outline.
    /// </summary>
    public class LakeSurfaces : MonoBehaviour
    {
        [Header("Wiring")]
        public Terrain Terrain;

        [Header("Models")]
        [Tooltip("Sailing boats. White sails carry a long way; these do most of " +
                 "the work of making a distant lake look inhabited.")]
        public List<GameObject> SailboatPrefabs = new List<GameObject>();
        public List<GameObject> RowboatPrefabs = new List<GameObject>();

        [Header("Size")]
        // 13 m and 5 m: both larger than the real thing. A lake has to sit a few
        // hundred metres off the road to fit on flat ground, and a true-to-life
        // 7 m dinghy at 300 m is a handful of pixels -- the same problem the
        // aircraft had at a 14 m wingspan.
        public float SailboatLength = 13f;
        public float RowboatLength = 5f;

        [Header("Water")]
        public Color WaterColor = new Color(0.10f, 0.28f, 0.36f);
        [Tooltip("Glassy. A still alpine lake is mostly a mirror, and the " +
                 "specular highlight is what stops it reading as flat paint.")]
        [Range(0f, 1f)] public float Smoothness = 0.88f;
        [Tooltip("Segments around the shoreline.")]
        public int Segments = 72;

        public int LakeCount { get; private set; }
        public int BoatCount { get; private set; }
        public IReadOnlyList<LakeSite> Lakes => _lakes;

        /// <summary>Mixed into the seed so boats get their own random stream.</summary>
        const int BoatSalt = unchecked((int)0xB0A75A17);

        [SerializeField, HideInInspector] List<LakeSite> _lakes = new List<LakeSite>();
        [SerializeField, HideInInspector] int _seed;
        readonly List<Boat> _boats = new List<Boat>();
        readonly List<Mesh> _meshes = new List<Mesh>();
        GameObject _root;
        Material _water;

        class Boat
        {
            public Transform T;
            public Vector2 Centre, Axis;
            public float SemiA, SemiB, Angle, AngularSpeed, WaterY;
            public float BobAmplitude, BobSpeed, BobPhase, Heel;
        }

        /// <summary>Throw away the water and boats, keeping the site data.</summary>
        public void ClearObjects()
        {
            if (_root != null) DestroyImmediate(_root);
            _root = null;
            foreach (var m in _meshes) if (m != null) DestroyImmediate(m);
            _meshes.Clear();
            _boats.Clear();
            LakeCount = BoatCount = 0;
        }

        public void Clear()
        {
            ClearObjects();
            _lakes.Clear();
        }

        /// <summary>
        /// Record the lakes without building anything. The editor bake uses this:
        /// the basins go into the terrain asset, and the surfaces are built at
        /// startup instead of being saved into the scene.
        ///
        /// Baking the objects too was the first attempt and it half-worked --
        /// the water appeared, but the site list is what the boats animate
        /// against and what the capture code asks for a viewpoint, and none of
        /// that survives being saved as a pile of GameObjects.
        /// </summary>
        public void SetSites(List<LakeSite> lakes, int seed)
        {
            _lakes = lakes == null ? new List<LakeSite>() : new List<LakeSite>(lakes);
            _seed = seed;
        }

        void Start()
        {
            if (_root == null && _lakes.Count > 0) Build();
        }

        public void Rebuild(List<LakeSite> lakes, int seed)
        {
            SetSites(lakes, seed);
            Build();
        }

        void Build()
        {
            ClearObjects();
            if (_lakes.Count == 0) return;

            _root = new GameObject("Lakes");
            _root.transform.SetParent(transform, false);
            EnsureWaterMaterial();

            var rng = new System.Random(_seed ^ BoatSalt);
            foreach (var lake in _lakes)
            {
                BuildWater(lake);
                PlaceBoats(lake, rng);
            }

            LakeCount = _lakes.Count;
            BoatCount = _boats.Count;
            Debug.Log($"[LakeSurfaces] {LakeCount} lake(s), {BoatCount} boat(s)");
        }

        void EnsureWaterMaterial()
        {
            if (_water != null) return;
            // Opaque, not transparent. At these distances you never see the bed,
            // and an opaque surface avoids sort order fights with the boats
            // sitting in it. What sells it is the specular, not the depth.
            _water = new Material(Shader.Find("Standard")) { name = "LakeWater", color = WaterColor };
            _water.SetFloat("_Glossiness", Smoothness);
            _water.SetFloat("_Metallic", 0.15f);
        }

        void BuildWater(LakeSite lake)
        {
            var go = new GameObject("Water");
            go.transform.SetParent(_root.transform, false);
            // A shade below the carved waterline. The shoreline crosses that
            // height somewhere inside a single 4.9 m texel, and dropping the
            // surface slightly puts the intersection on a real slope instead of
            // leaving a ring of coplanar z-fighting.
            go.transform.position = new Vector3(lake.Centre.x, lake.WaterLevel - 0.3f, lake.Centre.y);

            int n = Mathf.Max(12, Segments);
            var verts = new Vector3[n + 1];
            var normals = new Vector3[n + 1];
            var uvs = new Vector2[n + 1];
            var tris = new int[n * 3];

            verts[0] = Vector3.zero;
            normals[0] = Vector3.up;
            uvs[0] = new Vector2(0.5f, 0.5f);

            for (int i = 0; i < n; i++)
            {
                float ang = i * Mathf.PI * 2f / n;
                // Overlap the bank by a few metres so the water runs underneath
                // it rather than stopping at a visible seam.
                float r = LakeGen.RadiusAt(lake, ang) + 6f;
                verts[i + 1] = new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                normals[i + 1] = Vector3.up;
                uvs[i + 1] = new Vector2(Mathf.Cos(ang) * 0.5f + 0.5f, Mathf.Sin(ang) * 0.5f + 0.5f);

                int a = i + 1, b = (i + 1) % n + 1;
                tris[i * 3] = 0;
                tris[i * 3 + 1] = b;
                tris[i * 3 + 2] = a;
            }

            var mesh = new Mesh { name = "LakeWater" };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            _meshes.Add(mesh);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _water;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void PlaceBoats(LakeSite lake, System.Random rng)
        {
            bool haveSail = SailboatPrefabs.Count > 0;
            bool haveRow = RowboatPrefabs.Count > 0;
            if (!haveSail && !haveRow) return;

            // Boats follow the shape of the lake rather than a circle inside
            // it: these are long and narrow, so a circular track would either be
            // tiny or run the boats aground at the pinched ends.
            var axis = new Vector2(Mathf.Cos(lake.AxisAngle), Mathf.Sin(lake.AxisAngle));

            int count = lake.HalfLength > 170f ? 3 : lake.HalfLength > 130f ? 2 : 1;
            for (int i = 0; i < count; i++)
            {
                bool sail = haveSail && (!haveRow || rng.Next(3) > 0);
                var pool = sail ? SailboatPrefabs : RowboatPrefabs;
                var prefab = pool[rng.Next(pool.Count)];
                if (prefab == null) continue;

                var go = Instantiate(prefab, _root.transform);
                go.name = sail ? "Sailboat" : "Rowboat";

                var bounds = LocalBounds(go);
                float length = Mathf.Max(bounds.size.x, bounds.size.z);
                float target = sail ? SailboatLength : RowboatLength;
                if (length > 0.001f) go.transform.localScale *= target / length;

                foreach (var c in go.GetComponentsInChildren<Collider>()) DestroyImmediate(c);

                // 0.45-0.7 of the way out. The wobble pulls the real shoreline
                // in by up to 20%, so anything past ~0.75 beaches itself.
                float k = 0.45f + 0.25f * (float)rng.NextDouble();
                _boats.Add(new Boat
                {
                    T = go.transform,
                    Centre = lake.Centre,
                    Axis = axis,
                    SemiA = lake.HalfLength * k,
                    SemiB = lake.HalfWidth * k,
                    Angle = (float)rng.NextDouble() * Mathf.PI * 2f,
                    // Slow. A boat crossing a 200 m lake in twenty seconds reads
                    // as a jet ski; this is a drift you notice only if you watch.
                    AngularSpeed = (sail ? 0.020f : 0.013f) *
                                   (rng.Next(2) == 0 ? 1f : -1f) *
                                   (0.7f + 0.6f * (float)rng.NextDouble()),
                    WaterY = lake.WaterLevel - (sail ? 0.55f : 0.35f),
                    BobAmplitude = sail ? 0.22f : 0.14f,
                    BobSpeed = 0.8f + 0.5f * (float)rng.NextDouble(),
                    BobPhase = (float)rng.NextDouble() * 10f,
                    Heel = sail ? 5.5f : 3f,
                });
            }
        }

        /// <summary>
        /// Closest point of road with the lake in frame and nothing in the way.
        ///
        /// This lives here rather than in the capture code on purpose. When the
        /// monument had its framing worked out separately by the screenshot
        /// harness, the two disagreed about what "visible" meant and the shot
        /// came back with a mountain in front of the subject.
        /// </summary>
        public bool TryBestView(RoutePath route, int index, out float routeDistance)
        {
            routeDistance = 0f;
            if (route == null || index < 0 || index >= _lakes.Count) return false;

            var lake = _lakes[index];
            var surface = SurfacePoints(lake);
            var centre = new Vector3(lake.Centre.x, lake.WaterLevel, lake.Centre.y);

            float bestRange = float.MaxValue;
            int bestSeen = 0;
            bool found = false;
            int offAxis = 0, hidden = 0, tried = 0;

            for (float back = 120f; back <= 2200f; back += 25f)
            {
                float d = route.Wrap(lake.RouteDistance - back);
                Vector3 here = route.PositionAt(d);
                Vector3 to = centre - here;
                to.y = 0f;
                Vector3 fwd = route.ForwardAt(d, 8f);
                fwd.y = 0f;
                tried++;

                // Generous: a 300 m lake fills a lot of frame even when its centre
                // is well off axis.
                if (Vector3.Angle(fwd, to) > 40f) { offAxis++; continue; }

                // Count how much of the WATER is actually in view. Testing a
                // single proxy point does not work here: the surface sits below
                // the surrounding ground, so a ray to any one point on it dives
                // into the near bank and reports blocked, which is true of that
                // point and says nothing about the lake.
                int seen = 0;
                foreach (var p in surface) if (Clear(here, p)) seen++;
                if (seen < surface.Count / 4) { hidden++; continue; }

                float range = to.magnitude;
                if (range >= bestRange) continue;
                bestRange = range;
                bestSeen = seen;
                routeDistance = d;
                found = true;
            }

            Debug.Log($"[LakeSurfaces] lake {index}: {tried} viewpoints, {offAxis} off axis, " +
                      $"{hidden} mostly hidden" +
                      (found ? $", best from km {routeDistance / 1000f:F1} at {bestRange:F0} m " +
                               $"with {bestSeen}/{surface.Count} of the water in view"
                             : ", NONE usable"));
            return found;
        }

        /// <summary>Points spread over the water, for asking how much is in view.</summary>
        List<Vector3> SurfacePoints(LakeSite lake)
        {
            var pts = new List<Vector3>();
            var axis = new Vector2(Mathf.Cos(lake.AxisAngle), Mathf.Sin(lake.AxisAngle));
            var perp = new Vector2(-axis.y, axis.x);

            for (int iy = -2; iy <= 2; iy++)
                for (int ix = -4; ix <= 4; ix++)
                {
                    float u = ix / 5f, v = iy / 3f;
                    if (u * u + v * v > 1f) continue;
                    Vector2 p = lake.Centre + axis * (u * lake.HalfLength)
                                            + perp * (v * lake.HalfWidth);
                    pts.Add(new Vector3(p.x, lake.WaterLevel + 0.5f, p.y));
                }
            return pts;
        }

        /// <summary>Nothing between the rider and this point.</summary>
        bool Clear(Vector3 from, Vector3 to)
        {
            if (Terrain == null) return true;
            Vector3 eye = from + Vector3.up * 2f;
            float baseY = Terrain.transform.position.y;
            const int steps = 28;
            for (int i = 1; i < steps; i++)
            {
                Vector3 p = Vector3.Lerp(eye, to, i / (float)steps);
                if (Terrain.SampleHeight(p) + baseY > p.y + 2f) return false;
            }
            return true;
        }

        /// <summary>What the water renderers actually are, for capture traces.</summary>
        public string SurfaceReport()
        {
            if (_root == null) return "no water built";
            var parts = new List<string>();
            foreach (var mr in _root.GetComponentsInChildren<MeshRenderer>())
            {
                if (mr.gameObject.name != "Water") continue;
                var b = mr.bounds;
                parts.Add($"{mr.gameObject.name} at y={mr.transform.position.y:F1} " +
                          $"size {b.size.x:F0}x{b.size.z:F0} visible={mr.isVisible} " +
                          $"tris={mr.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3}");
            }
            return string.Join("; ", parts);
        }

        void Update()
        {
            if (_boats.Count == 0) return;
            float t = Time.time;

            foreach (var b in _boats)
            {
                if (b.T == null) continue;
                b.Angle += b.AngularSpeed * Time.deltaTime;

                var perp = new Vector2(-b.Axis.y, b.Axis.x);
                float ca = Mathf.Cos(b.Angle), sa = Mathf.Sin(b.Angle);

                Vector2 flat = b.Centre + b.Axis * (ca * b.SemiA) + perp * (sa * b.SemiB);
                float y = b.WaterY + Mathf.Sin(t * b.BobSpeed + b.BobPhase) * b.BobAmplitude;
                b.T.position = new Vector3(flat.x, y, flat.y);

                // Face the way it is going, and lean. A boat that stays dead level
                // reads as a decal lying on the water.
                Vector2 dir = (b.Axis * (-sa * b.SemiA) + perp * (ca * b.SemiB)).normalized *
                              Mathf.Sign(b.AngularSpeed);
                var tangent = new Vector3(dir.x, 0f, dir.y);
                b.T.rotation = Quaternion.LookRotation(tangent, Vector3.up) *
                               Quaternion.Euler(0f, 0f, Mathf.Sin(t * b.BobSpeed * 0.7f + b.BobPhase) * b.Heel);
            }
        }

        static Bounds LocalBounds(GameObject go)
        {
            var filters = go.GetComponentsInChildren<MeshFilter>(true);
            bool any = false;
            var result = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var f in filters)
            {
                if (f.sharedMesh == null) continue;
                var b = f.sharedMesh.bounds;
                var offset = go.transform.InverseTransformPoint(f.transform.position);
                var scale = f.transform.lossyScale;
                b = new Bounds(b.center + offset,
                               Vector3.Scale(b.size, new Vector3(Mathf.Abs(scale.x),
                                                                 Mathf.Abs(scale.y),
                                                                 Mathf.Abs(scale.z))));
                if (!any) { result = b; any = true; } else result.Encapsulate(b);
            }
            if (!any) result = new Bounds(Vector3.zero, Vector3.one);
            return result;
        }

        void OnDestroy()
        {
            if (_water != null) Destroy(_water);
        }
    }
}
