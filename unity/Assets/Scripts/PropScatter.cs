using System.Collections.Generic;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// One category of scenery, with the rules for where it may stand.
    /// A building wants flat ground well back from the road; a parked car wants to
    /// be close to it and can tolerate a little slope. Keeping the rules per-kind
    /// is what stops everything ending up in one uniform sprinkle.
    /// </summary>
    [System.Serializable]
    public class PropKind
    {
        public string Name = "prop";

        [Tooltip("Models to place. One is picked per instance, so a kind with " +
                 "several variants does not read as the same object stamped " +
                 "repeatedly. Left empty, a crude placeholder is generated so " +
                 "placement can be tuned before any real model exists.")]
        public List<GameObject> Prefabs = new List<GameObject>();

        [Tooltip("Desired world height in metres, before per-instance scale " +
                 "variation. Imported models arrive in whatever units their author " +
                 "used -- Kenney kits are roughly one unit per tile, not per metre -- " +
                 "so normalising by measured bounds beats guessing a scale factor. " +
                 "Zero keeps the model at its authored size.")]
        public float TargetHeight = 0f;

        [Tooltip("Instances per kilometre of route.")]
        public float PerKilometre = 12f;

        [Tooltip("Metres from the road centreline. The minimum keeps things off the " +
                 "carriageway and the verge.")]
        public float MinOffset = 25f;
        public float MaxOffset = 180f;

        [Tooltip("Steepest ground this will stand on. Buildings need flat, rocks do not.")]
        public float MaxSlopeDegrees = 18f;

        public float MinScale = 0.9f;
        public float MaxScale = 1.3f;

        [Tooltip("Tilt with the ground. Right for rocks, wrong for buildings.")]
        public bool AlignToGround = false;

        [Tooltip("How much of the object is buried. 0 sits it on the surface, 0.5 " +
                 "sinks it to its middle. Rocks look wrong perched on top; trees " +
                 "and buildings look wrong sunk in.")]
        [Range(0f, 0.5f)] public float GroundSink = 0f;

        [Tooltip("Place this many together where it lands, for villages and copses.")]
        public int ClusterMin = 1;
        public int ClusterMax = 1;
        public float ClusterRadius = 30f;

        [Tooltip("Replaces whatever materials the model shipped with. One is " +
                 "picked per instance. Needed where an exported material is not " +
                 "usable -- the Quaternius dinosaurs carry a near-black diffuse " +
                 "(Kd 0.058 0.070 0.050) and render as silhouettes without this.")]
        public List<Material> MaterialOverrides = new List<Material>();

        public Color PlaceholderColor = Color.grey;
        public Vector3 PlaceholderSize = Vector3.one;
        public PrimitiveType PlaceholderShape = PrimitiveType.Cube;
    }

    /// <summary>
    /// Scatters scenery across the landscape, deterministically from the world seed.
    ///
    /// Determinism is the whole point. A saved world stores nothing but its seed,
    /// so every dinosaur and farmhouse has to land in exactly the same spot when
    /// it is loaded back. The random stream is derived from the seed but kept
    /// separate from the one the terrain uses, so adding a prop kind cannot shift
    /// the mountains of an already-saved world.
    /// </summary>
    public class PropScatter : MonoBehaviour
    {
        public RideWorld World;
        public Terrain Terrain;
        public List<PropKind> Kinds = new List<PropKind>();

        [Tooltip("Hard ceiling on instances, so a careless density cannot melt a laptop.")]
        public int MaxInstances = 1700;

        public int Placed { get; private set; }

        /// <summary>Route distances at which each kind was placed. Useful for
        /// finding a rare prop without riding the whole lap looking for it.</summary>
        public readonly Dictionary<string, List<float>> PlacedAt = new Dictionary<string, List<float>>();

        Transform _root;
        readonly List<Material> _ownedMaterials = new List<Material>();
        readonly List<GameObject> _templates = new List<GameObject>();

        /// <summary>Mixed into the seed so scenery gets its own random stream.</summary>
        const int ScatterSalt = unchecked((int)0x5CA77E12);

        void Start()
        {
            // The baked world needs scenery too. RideWorld builds its route in
            // Awake, so it is ready by now. A regenerate clears and redoes this.
            if (World != null && World.Route != null) Rebuild(World.Route, World.Seed);

            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-verifyscatter") >= 0)
                VerifyDeterminism();
        }

        /// <summary>
        /// Scatter twice from the same seed and compare. Saved worlds persist only
        /// a seed, so if this is not bit-identical then loading a favourite gives
        /// you a different landscape than the one you saved -- the exact failure
        /// the feature exists to prevent.
        /// </summary>
        public void VerifyDeterminism()
        {
            if (World == null || World.Route == null) return;

            string first = Fingerprint();
            int firstCount = Placed;

            Rebuild(World.Route, World.Seed);
            string second = Fingerprint();

            bool same = first == second && firstCount == Placed;
            if (same)
                Debug.Log($"[PropScatter] determinism OK: {Placed} objects, fingerprint {first}");
            else
                Debug.LogError($"[PropScatter] DETERMINISM FAILED: {firstCount} vs {Placed} objects, " +
                               $"{first} vs {second}");
        }

        /// <summary>Hash of every placed transform, in placement order (which is
        /// itself deterministic, so order forms part of what is being checked).</summary>
        public string Fingerprint()
        {
            if (_root == null) return "none";
            ulong acc = 1469598103934665603UL;
            foreach (Transform t in _root)
            {
                // Quantise: float formatting differences must not read as a
                // placement change, but a real move of >1 mm must.
                var p = t.position;
                var e = t.eulerAngles;
                var s = t.localScale;
                foreach (long v in new[]
                {
                    (long)Mathf.Round(p.x * 1000f), (long)Mathf.Round(p.y * 1000f), (long)Mathf.Round(p.z * 1000f),
                    (long)Mathf.Round(e.y * 100f),
                    (long)Mathf.Round(s.x * 1000f), (long)Mathf.Round(s.y * 1000f),
                })
                {
                    acc ^= (ulong)v;
                    acc *= 1099511628211UL;
                }
            }
            return acc.ToString("x16");
        }

        public void Rebuild(RoutePath route, int seed)
        {
            Clear();
            if (route == null || Terrain == null) return;

            _root = new GameObject("Props").transform;
            _root.SetParent(transform, false);
            PlacedAt.Clear();

            var rng = new System.Random(seed ^ ScatterSalt);
            float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);

            float lengthKm = route.Length / 1000f;
            int placed = 0;

            // Budget per kind, scaled down together if the total would blow the
            // cap. Spending a single global budget in list order starves whatever
            // comes last -- with trees first, the dinosaurs never appeared at all.
            var budget = new int[Kinds.Count];
            float wanted = 0f;
            for (int k = 0; k < Kinds.Count; k++)
            {
                if (Kinds[k] == null) continue;
                budget[k] = Mathf.RoundToInt(Kinds[k].PerKilometre * lengthKm);
                wanted += budget[k];
            }
            if (wanted > MaxInstances)
            {
                float squeeze = MaxInstances / wanted;
                for (int k = 0; k < budget.Length; k++)
                    budget[k] = Mathf.Max(1, Mathf.RoundToInt(budget[k] * squeeze));
            }

            for (int k = 0; k < Kinds.Count; k++)
            {
                var kind = Kinds[k];
                if (kind == null) continue;

                // PerKilometre counts instances, not attempts, so a kind that
                // clusters needs proportionally fewer attempts to reach its budget.
                float avgCluster = Mathf.Max(1f, (kind.ClusterMin + Mathf.Max(kind.ClusterMin, kind.ClusterMax)) * 0.5f);
                // Generous, because slope and edge tests reject a lot of spots on
                // mountainous ground and every rejection would otherwise be a
                // permanently lost instance.
                int attempts = Mathf.Min(4000, Mathf.CeilToInt(budget[k] / avgCluster) * 8 + 20);
                int placedForKind = 0;
                var variants = new List<GameObject>();
                if (kind.Prefabs != null)
                    foreach (var pf in kind.Prefabs) if (pf != null) variants.Add(pf);
                if (variants.Count == 0) variants.Add(MakePlaceholder(kind));

                for (int i = 0; i < attempts && placedForKind < budget[k] && placed < MaxInstances; i++)
                {
                    float d = (float)rng.NextDouble() * route.Length;
                    Vector2 centre = route.HorizontalAt(d);
                    Vector3 fwd = route.ForwardAt(d, 8f);
                    var side = new Vector2(-fwd.z, fwd.x).normalized;   // perpendicular in XZ
                    float sign = rng.Next(2) == 0 ? -1f : 1f;

                    int cluster = rng.Next(kind.ClusterMin, Mathf.Max(kind.ClusterMin, kind.ClusterMax) + 1);
                    Vector2 anchor = centre + side * (sign * Range(kind.MinOffset, kind.MaxOffset));

                    for (int c = 0; c < cluster && placedForKind < budget[k] && placed < MaxInstances; c++)
                    {
                        Vector2 spot = anchor;
                        if (c > 0)
                        {
                            float a = Range(0f, Mathf.PI * 2f);
                            float r = Range(kind.ClusterRadius * 0.25f, kind.ClusterRadius);
                            spot += new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                        }

                        // Re-check the offset for cluster members: a village will
                        // otherwise creep onto the tarmac one house at a time.
                        if (Vector2.Distance(spot, centre) < kind.MinOffset * 0.8f) continue;
                        var template = variants[rng.Next(variants.Count)];
                        if (TryPlace(template, kind, spot, rng))
                        {
                            placed++; placedForKind++;
                            if (!PlacedAt.TryGetValue(kind.Name, out var list))
                                PlacedAt[kind.Name] = list = new List<float>();
                            list.Add(d);
                        }
                    }
                }

                Debug.Log($"[PropScatter]   {kind.Name}: {placedForKind}/{budget[k]}");
            }

            Placed = placed;
            Debug.Log($"[PropScatter] placed {placed} objects across {Kinds.Count} kinds (seed {seed})");
        }

        bool TryPlace(GameObject template, PropKind kind, Vector2 spot, System.Random rng)
        {
            var tp = Terrain.transform.position;
            var data = Terrain.terrainData;

            float u = (spot.x - tp.x) / data.size.x;
            float v = (spot.y - tp.z) / data.size.z;
            if (u < 0.01f || u > 0.99f || v < 0.01f || v > 0.99f) return false;

            Vector3 normal = data.GetInterpolatedNormal(u, v);
            if (Vector3.Angle(normal, Vector3.up) > kind.MaxSlopeDegrees) return false;

            float ground = Terrain.SampleHeight(new Vector3(spot.x, 0f, spot.y)) + tp.y;
            float s = kind.MinScale + (float)rng.NextDouble() * (kind.MaxScale - kind.MinScale);

            // Normalise authored size to the height this kind wants. Imported
            // models arrive in whatever units their author chose, so measuring
            // beats hardcoding a per-pack scale factor that breaks the moment a
            // model is swapped.
            Bounds local = LocalBounds(template);
            if (kind.TargetHeight > 0.01f && local.size.y > 0.0001f)
            {
                float fit = kind.TargetHeight / (local.size.y * template.transform.localScale.y);
                // Clamp it. Normalising on height alone blows up a model that is
                // wide and flat -- a tree stump forced to 2.2 m tall became an 8 m
                // slab lying in the grass. A pathological aspect ratio should give
                // a slightly wrong size, not a landscape feature.
                s *= Mathf.Clamp(fit, 0.25f, 4f);
            }

            // Models pivot wherever their author put the origin -- centre for
            // Unity primitives, base for many kits. Use the measured bounds so
            // either sits correctly on the ground rather than half sunk.
            float bottom = local.min.y * template.transform.localScale.y * s;
            float lift = -bottom - local.size.y * template.transform.localScale.y * s * kind.GroundSink;

            var go = Instantiate(template, new Vector3(spot.x, ground + lift, spot.y), Quaternion.identity, _root);
            go.SetActive(true);
            go.name = kind.Name;

            if (kind.MaterialOverrides != null && kind.MaterialOverrides.Count > 0)
            {
                var chosen = kind.MaterialOverrides[rng.Next(kind.MaterialOverrides.Count)];
                if (chosen != null)
                {
                    // sharedMaterials, not materials: assigning to .materials would
                    // clone a material per instance and defeat batching entirely.
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        var slots = r.sharedMaterials;
                        for (int m = 0; m < slots.Length; m++) slots[m] = chosen;
                        r.sharedMaterials = slots;
                    }
                }
            }

            float yaw = (float)rng.NextDouble() * 360f;
            go.transform.rotation = kind.AlignToGround
                ? Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, yaw, 0f)
                : Quaternion.Euler(0f, yaw, 0f);

            go.transform.localScale = template.transform.localScale * s;
            return true;
        }

        /// <summary>
        /// Combined bounds of every mesh under this object, in its own space.
        /// An imported FBX usually keeps its meshes on child transforms, so the
        /// root has no renderer of its own to measure.
        /// </summary>
        static Bounds LocalBounds(GameObject go)
        {
            var filters = go.GetComponentsInChildren<MeshFilter>(true);
            bool any = false;
            var result = new Bounds(Vector3.zero, Vector3.zero);

            foreach (var f in filters)
            {
                if (f.sharedMesh == null) continue;
                var b = f.sharedMesh.bounds;
                // Express the child's bounds in the root's space.
                if (f.transform != go.transform)
                {
                    var offset = go.transform.InverseTransformPoint(f.transform.position);
                    var scale = f.transform.lossyScale;
                    b = new Bounds(b.center + offset,
                                   Vector3.Scale(b.size, new Vector3(
                                       Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))));
                }
                if (!any) { result = b; any = true; } else result.Encapsulate(b);
            }

            if (!any) result = new Bounds(Vector3.zero, Vector3.one);
            return result;
        }

        /// <summary>
        /// A crude stand-in, so placement can be seen and tuned before any real
        /// model is downloaded. Swapping in a real one is a single field change.
        /// </summary>
        GameObject MakePlaceholder(PropKind kind)
        {
            var go = GameObject.CreatePrimitive(kind.PlaceholderShape);
            go.name = kind.Name + "_placeholder";
            go.transform.localScale = kind.PlaceholderSize;

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var mat = new Material(Shader.Find("Standard")) { color = kind.PlaceholderColor };
            mat.enableInstancing = true;
            _ownedMaterials.Add(mat);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            go.SetActive(false);        // a template only; instances get activated
            go.transform.SetParent(transform, false);
            _templates.Add(go);
            return go;
        }

        public void Clear()
        {
            if (_root != null) Destroy(_root.gameObject);
            _root = null;

            foreach (var go in _templates) if (go != null) Destroy(go);
            _templates.Clear();

            foreach (var m in _ownedMaterials) if (m != null) Destroy(m);
            _ownedMaterials.Clear();

            Placed = 0;
        }

        /// <summary>
        /// A sensible starting set. Each entry takes a real model in Prefab later;
        /// until then it draws as a coloured primitive of roughly the right size.
        /// </summary>
        public static List<PropKind> DefaultKinds()
        {
            return new List<PropKind>
            {
                new PropKind
                {
                    Name = "conifer", PerKilometre = 58f, TargetHeight = 9f,
                    MinOffset = 16f, MaxOffset = 150f, MaxSlopeDegrees = 34f,
                    MinScale = 0.7f, MaxScale = 1.6f,
                    ClusterMin = 2, ClusterMax = 6, ClusterRadius = 26f,
                    PlaceholderShape = PrimitiveType.Cylinder,
                    PlaceholderSize = new Vector3(2.4f, 5f, 2.4f),
                    PlaceholderColor = new Color(0.16f, 0.32f, 0.16f),
                },
                new PropKind
                {
                    Name = "boulder", PerKilometre = 24f, TargetHeight = 2.2f,
                    MinOffset = 13f, MaxOffset = 140f, MaxSlopeDegrees = 40f,
                    MinScale = 0.6f, MaxScale = 2.2f, AlignToGround = true, GroundSink = 0.3f,
                    PlaceholderShape = PrimitiveType.Sphere,
                    PlaceholderSize = new Vector3(2.6f, 1.9f, 2.4f),
                    PlaceholderColor = new Color(0.40f, 0.38f, 0.35f),
                },
                new PropKind
                {
                    Name = "farmhouse", PerKilometre = 4f, TargetHeight = 11f,
                    MinOffset = 40f, MaxOffset = 130f, MaxSlopeDegrees = 15f,
                    MinScale = 0.9f, MaxScale = 1.25f,
                    ClusterMin = 1, ClusterMax = 4, ClusterRadius = 34f,
                    PlaceholderShape = PrimitiveType.Cube,
                    PlaceholderSize = new Vector3(9f, 6f, 12f),
                    PlaceholderColor = new Color(0.78f, 0.74f, 0.66f),
                },
                new PropKind
                {
                    Name = "parked vehicle", PerKilometre = 2.5f, TargetHeight = 1.6f,
                    MinOffset = 11f, MaxOffset = 17f, MaxSlopeDegrees = 12f,
                    MinScale = 0.95f, MaxScale = 1.05f, AlignToGround = true,
                    PlaceholderShape = PrimitiveType.Cube,
                    PlaceholderSize = new Vector3(1.9f, 1.5f, 4.4f),
                    PlaceholderColor = new Color(0.65f, 0.20f, 0.18f),
                },
                new PropKind
                {
                    Name = "dinosaur", PerKilometre = 1.1f, TargetHeight = 7f,
                    MinOffset = 45f, MaxOffset = 150f, MaxSlopeDegrees = 24f,
                    MinScale = 1.0f, MaxScale = 1.8f,
                    PlaceholderShape = PrimitiveType.Capsule,
                    PlaceholderSize = new Vector3(3.2f, 5.5f, 3.2f),
                    PlaceholderColor = new Color(0.36f, 0.52f, 0.30f),
                },
            };
        }
    }
}
