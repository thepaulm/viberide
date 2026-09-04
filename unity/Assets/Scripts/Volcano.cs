using System.Collections.Generic;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Sets one or two of the mountains erupting.
    ///
    /// The summit search is deliberately not the monument's. That one is looking
    /// for something a rider can make out in detail from the road, so it caps the
    /// elevation angle and insists on a clear view cone. A volcano has the
    /// opposite problem: it is enormous, it is meant to be a long way off, and the
    /// plume is visible over intervening ridges that would disqualify a statue.
    /// What matters here is only that the peak stands well above its neighbours
    /// and is somewhere ahead of the ride rather than behind the camera all lap.
    ///
    /// The terrain is not modified. A crater cut into the heightmap would have to
    /// happen back in generation, before SetHeights, and at a 5-17 m texel a
    /// convincing one is a handful of samples across; the eruption reads from the
    /// road, the crater would not.
    /// </summary>
    public class Volcano : MonoBehaviour
    {
        public RideWorld World;
        public Terrain Terrain;

        [Tooltip("Metres the peak must stand above the ground around it.")]
        public float MinProminence = 55f;
        [Tooltip("Nearest and furthest the peak may be from the road.")]
        public float MinRoadDistance = 350f;
        public float MaxRoadDistance = 2600f;

        /// <summary>Seconds between the big bursts, either side of the average.</summary>
        [Tooltip("Highest the summit may sit above the eye at the chosen vantage. " +
                 "The camera holds about 30 deg above centre and the plume needs " +
                 "room above the rock.")]
        public float MaxViewAngle = 14f;

        [Tooltip("Metres the summit may stand above the road it is seen from.")]
        public float MaxRiseAboveRoad = 900f;

        public float MinInterval = 22f;
        public float MaxInterval = 55f;

        public int Count { get; private set; }
        public List<Vector3> Peaks { get; } = new List<Vector3>();

        readonly List<GameObject> _spawned = new List<GameObject>();
        static Texture2D _puff;

        void Start()
        {
            if (World != null && World.Route != null) Rebuild(World.Route, World.Seed);
        }

        public void Clear()
        {
            foreach (var go in _spawned) if (go != null) DestroyImmediate(go);
            _spawned.Clear();
            Peaks.Clear();
            Count = 0;
        }

        public void Rebuild(RoutePath route, int seed)
        {
            Clear();
            if (route == null || Terrain == null) return;

            var rng = new System.Random(seed ^ unchecked((int)0x5E11A17));
            // Usually one, sometimes two or three. A world where every lap has
            // exactly one of everything stops feeling generated.
            int wanted = 1 + (rng.NextDouble() < 0.42 ? 1 : 0)
                           + (rng.NextDouble() < 0.14 ? 1 : 0);

            foreach (var peak in FindPeaks(route, rng, wanted))
            {
                Peaks.Add(peak);
                _spawned.Add(BuildEruption(peak, rng));
                Count++;
            }

            Debug.Log($"[Volcano] {Count} erupting" +
                      (Count == 0 ? " -- no peak stood far enough above its neighbours" : ""));
            foreach (var p in Peaks)
                Debug.Log($"[Volcano]   summit ({p.x:F0}, {p.y:F0}, {p.z:F0}) -- " +
                          $"plume in view from {VisibleStretch(World.Route, p)} of 40 " +
                          $"points around the lap");
        }

        /// <summary>What the plume renderers actually think they are doing.
        /// "It should be visible" has been wrong about this world's scenery
        /// often enough to be worth printing.</summary>
        public string PlumeReport()
        {
            var parts = new List<string>();
            foreach (var go in _spawned)
            {
                if (go == null) continue;
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>())
                {
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    parts.Add($"{ps.gameObject.name}: {ps.particleCount} live, " +
                              $"playing={ps.isPlaying}, visible={(r != null && r.isVisible)}, " +
                              $"shader={(r != null && r.sharedMaterial != null ? r.sharedMaterial.shader.name : "none")}");
                }
            }
            return parts.Count == 0 ? "no plumes" : string.Join("; ", parts);
        }

        float Ground(float x, float z)
        {
            var t = Terrain.transform.position;
            return Terrain.SampleHeight(new Vector3(x, 0f, z)) + t.y;
        }

        /// <summary>Local maxima on a coarse grid, ranked by how far they stand
        /// above a ring around them.</summary>
        List<Vector3> FindPeaks(RoutePath route, System.Random rng, int wanted)
        {
            var size = Terrain.terrainData.size;
            var origin = Terrain.transform.position;
            var found = new List<(Vector3 pos, float score)>();

            // Coarse enough to be cheap, fine enough that a single mountain is a
            // few cells across whatever the map is scaled to.
            const int grid = 56;
            float stepX = size.x / grid, stepZ = size.z / grid;

            for (int i = 2; i < grid - 1; i++)
                for (int j = 2; j < grid - 1; j++)
                {
                    float x = origin.x + i * stepX, z = origin.z + j * stepZ;
                    float h = Ground(x, z);

                    // Local maximum against its eight neighbours.
                    bool top = true;
                    for (int di = -1; di <= 1 && top; di++)
                        for (int dj = -1; dj <= 1 && top; dj++)
                        {
                            if (di == 0 && dj == 0) continue;
                            if (Ground(x + di * stepX, z + dj * stepZ) > h) top = false;
                        }
                    if (!top) continue;

                    // How far it stands above a ring at a couple of cells out --
                    // a high shoulder on a massif is not a volcano.
                    float ring = 0f;
                    const int spokes = 8;
                    for (int k = 0; k < spokes; k++)
                    {
                        float a = k / (float)spokes * Mathf.PI * 2f;
                        ring += Ground(x + Mathf.Cos(a) * stepX * 2.2f,
                                       z + Mathf.Sin(a) * stepZ * 2.2f);
                    }
                    float prominence = h - ring / spokes;
                    if (prominence < MinProminence) continue;

                    float road = NearestRoad(route, new Vector2(x, z), out float roadY);
                    if (road < MinRoadDistance || road > MaxRoadDistance) continue;

                    // Too tall is as bad as too short. Every metre above the road
                    // pushes the only vantage that fits it in frame further back,
                    // and past about 900 m the summit is jammed under the stat bar
                    // from the nearest place it can legally be viewed.
                    float rise = h - roadY;
                    if (rise > MaxRiseAboveRoad) continue;

                    // And it has to be visible, which is not a thing to assume.
                    // The lakes in this world spent four versions being correct
                    // and unseen; a mountain behind a nearer ridge is the same
                    // mistake with a bigger prop.
                    var pos = new Vector3(x, h, z);
                    int seen = VisibleStretch(route, pos);
                    if (seen < 6) continue;

                    // Prefer prominent, prefer seen, prefer close -- but do not
                    // let closeness alone win, since a low bump beside the road
                    // is not a mountain.
                    float score = prominence + seen * 9f - road * 0.02f
                                - Mathf.Max(0f, rise - 500f) * 0.06f
                                + (float)rng.NextDouble() * 12f;
                    found.Add((pos, score));
                }

            found.Sort((a, b) => b.score.CompareTo(a.score));

            var picked = new List<Vector3>();
            foreach (var f in found)
            {
                if (picked.Count >= wanted) break;
                // Two plumes side by side read as one messy one.
                bool clash = false;
                foreach (var p in picked)
                    if (Vector3.Distance(p, f.pos) < 1500f) { clash = true; break; }
                if (!clash) picked.Add(f.pos);
            }
            return picked;
        }

        /// <summary>How many sample points along the lap can see the plume.
        /// Aimed at the column rather than the summit: the ash climbs hundreds of
        /// metres and clears ridges the rock behind it never will.</summary>
        int VisibleStretch(RoutePath route, Vector3 peak)
        {
            Vector3 target = peak + Vector3.up * 220f;
            int seen = 0;
            const int probes = 40;
            for (int i = 0; i < probes; i++)
            {
                Vector3 eye = route.PositionAt(i / (float)probes * route.Length);
                eye.y += 2f;
                if (Clear(eye, target)) seen++;
            }
            return seen;
        }

        /// <summary>Where on the lap the plume is best seen: a clear line, the
        /// peak reasonably ahead rather than off the shoulder, and not so far
        /// that it is a smudge. Exposed so the capture flags ask the volcano
        /// instead of guessing, which is how the statue stopped being
        /// photographed with a mountain in front of it.</summary>
        public bool TryBestView(RoutePath route, int index, out float distance)
        {
            distance = 0f;
            if (route == null || index < 0 || index >= Peaks.Count) return false;

            Vector3 peak = Peaks[index];
            Vector3 target = peak + Vector3.up * 220f;
            float bestScore = float.MinValue;
            bool any = false;

            const int probes = 260;
            for (int i = 0; i < probes; i++)
            {
                float d = i / (float)probes * route.Length;
                Vector3 eye = route.PositionAt(d);
                eye.y += 2f;
                if (!Clear(eye, target)) continue;

                Vector3 fwd = route.ForwardAt(d, 12f);
                Vector3 to = target - eye;
                float rise = to.y;
                to.y = 0f; fwd.y = 0f;
                float flatRange = to.magnitude;

                // Being close to a mountain is not the same as seeing it. A
                // 1100 m peak 1700 m away sits 34 deg up, and the camera has
                // 30 deg of frame above the centre -- so the nearest vantage put
                // the whole eruption above the top of the screen. The monument
                // learned this once already and caps its own angle at 11 deg.
                float elevation = Mathf.Atan2(rise, flatRange) * Mathf.Rad2Deg;
                if (elevation > MaxViewAngle) continue;

                // Head-on matters more than close: a plume 20 deg off the nose is
                // in frame, one at 80 deg is over your shoulder.
                float ahead = Vector3.Dot(fwd.normalized, to.normalized);
                if (ahead < 0.45f) continue;

                // Distance is back in the score. Dropping it when the angle gate
                // went in picked the most head-on point on the whole lap, which
                // was 6.7 km away and rendered the eruption about six pixels
                // wide -- on screen, measurably visible, and no use to anyone.
                float score = ahead * 100f - Mathf.Abs(elevation - 12f) * 2.5f
                            - Mathf.Abs(flatRange - 2000f) * 0.012f;
                if (score > bestScore) { bestScore = score; distance = d; any = true; }
            }
            return any;
        }

        bool Clear(Vector3 from, Vector3 to)
        {
            const int steps = 26;
            for (int i = 1; i < steps; i++)
            {
                Vector3 p = Vector3.Lerp(from, to, i / (float)steps);
                if (Ground(p.x, p.z) > p.y + 3f) return false;
            }
            return true;
        }

        static float NearestRoad(RoutePath route, Vector2 p, out float roadY)
        {
            float best = float.MaxValue;
            roadY = 0f;
            const int probes = 220;
            for (int i = 0; i < probes; i++)
            {
                Vector3 q = route.PositionAt(i / (float)probes * route.Length);
                float d = Vector2.Distance(p, new Vector2(q.x, q.z));
                if (d < best) { best = d; roadY = q.y; }
            }
            return best;
        }

        /// <summary>A soft round dot, so the particles are not white squares.
        /// There is no texture in the project to use and one built here costs a
        /// kilobyte.</summary>
        static Texture2D Puff()
        {
            if (_puff != null) return _puff;
            const int n = 32;
            _puff = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                    float a = Mathf.Clamp01(1f - r);
                    _puff.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            _puff.Apply();
            _puff.wrapMode = TextureWrapMode.Clamp;
            return _puff;
        }

        static Material ParticleMaterial(bool additive)
        {
            var shader = Shader.Find(additive
                ? "Legacy Shaders/Particles/Additive"
                : "Legacy Shaders/Particles/Alpha Blended Premultiply");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var m = new Material(shader);
            m.mainTexture = Puff();
            return m;
        }

        GameObject BuildEruption(Vector3 peak, System.Random rng)
        {
            var root = new GameObject("Volcano");
            root.transform.SetParent(transform, false);
            root.transform.position = peak;

            // Scaled off the mountain itself so a big peak gets a big column.
            float scale = Mathf.Clamp(peak.y * 0.02f, 6f, 26f);

            var smoke = MakeSmoke(root.transform, scale);
            var embers = MakeEmbers(root.transform, scale);
            var glow = MakeGlow(root.transform, scale);

            var burst = root.AddComponent<VolcanoBurst>();
            burst.Smoke = smoke;
            burst.Embers = embers;
            burst.Glow = glow;
            burst.MinInterval = MinInterval;
            burst.MaxInterval = MaxInterval;
            burst.Phase = (float)rng.NextDouble();
            return root;
        }

        ParticleSystem MakeSmoke(Transform parent, float scale)
        {
            var go = new GameObject("Plume");
            go.transform.SetParent(parent, false);
            // A cone emits along its own +Z, and a component added to a bare
            // GameObject inherits identity rotation -- so the first version of
            // this fired the column horizontally across the valley. Only the
            // editor's menu item pre-rotates the transform.
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop();

            var main = ps.main;
            main.loop = true;
            // A column kilometres tall, because that is both what these do and
            // what it takes to be seen. The peaks that qualify stand about 1100 m
            // over the road, and fitting that in a 30 deg half-frame puts the
            // rider close to 4 km away -- at which range a tidy 500 m plume is a
            // smudge a few pixels wide.
            main.startLifetime = 21f;
            main.startSpeed = scale * 2.7f;
            main.startSize = scale * 4.2f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.32f, 0.30f, 0.29f, 0.55f), new Color(0.62f, 0.60f, 0.58f, 0.40f));
            main.gravityModifier = -0.06f;      // ash is hot, it keeps climbing
            main.maxParticles = 820;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            // Dense enough that the puffs overlap into a column. Sparse looks
            // like a bonfire, not a mountain venting.
            emission.rateOverTime = 34f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 17f;
            shape.radius = scale * 0.8f;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, Curve(0.35f, 1f));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(Fade());

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = ParticleMaterial(false);
            r.sortingFudge = -20f;
            ps.Play();
            return ps;
        }

        ParticleSystem MakeEmbers(Transform parent, float scale)
        {
            var go = new GameObject("Embers");
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop();

            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = 6.5f;
            main.startSpeed = scale * 5.5f;
            main.startSize = scale * 0.42f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.72f, 0.20f, 1f), new Color(1f, 0.32f, 0.08f, 1f));
            main.gravityModifier = 1.5f;        // they arc and fall back
            main.maxParticles = 400;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.duration = 1.4f;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 150, 220) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 26f;
            shape.radius = scale * 0.35f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(Fade());

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = ParticleMaterial(true);
            return ps;
        }

        Light MakeGlow(Transform parent, float scale)
        {
            var go = new GameObject("Crater glow");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.up * scale * 0.3f;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.45f, 0.16f);
            light.range = scale * 12f;
            light.intensity = 1.4f;
            light.shadows = LightShadows.None;
            return light;
        }

        static AnimationCurve Curve(float a, float b) =>
            AnimationCurve.EaseInOut(0f, a, 1f, b);

        static Gradient Fade()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f),
                        new GradientAlphaKey(0.6f, 0.6f), new GradientAlphaKey(0f, 1f) });
            return g;
        }
    }

    /// <summary>Fires the eruption every so often and pumps the crater glow in
    /// between, so the mountain smoulders rather than sitting at one brightness.
    /// </summary>
    public class VolcanoBurst : MonoBehaviour
    {
        public ParticleSystem Smoke;
        public ParticleSystem Embers;
        public Light Glow;
        public float MinInterval = 22f;
        public float MaxInterval = 55f;
        public float Phase;

        float _next;
        float _flash;

        void Start() => _next = Time.time + Phase * MinInterval + 4f;

        void Update()
        {
            if (Time.time >= _next)
            {
                _next = Time.time + Random.Range(MinInterval, MaxInterval);
                _flash = 1f;
                if (Embers != null) { Embers.Clear(); Embers.Play(); }
                if (Smoke != null)
                {
                    var e = Smoke.emission;
                    e.rateOverTime = 105f;
                }
            }

            if (_flash > 0f)
            {
                _flash = Mathf.Max(0f, _flash - Time.deltaTime * 0.35f);
                if (_flash <= 0f && Smoke != null)
                {
                    var e = Smoke.emission;
                    e.rateOverTime = 34f;
                }
            }

            if (Glow != null)
            {
                float idle = 1.2f + Mathf.Sin(Time.time * 1.7f) * 0.25f;
                Glow.intensity = idle + _flash * 9f;
            }
        }
    }
}
