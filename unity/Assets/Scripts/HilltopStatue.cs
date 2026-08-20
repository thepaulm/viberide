using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Puts one big monument on a summit overlooking the course.
    ///
    /// Not scenery: PropScatter throws hundreds of things near the road at random
    /// offsets, which is right for trees and wrong for a landmark. A landmark has
    /// to be findable -- the same rock every lap, visible from a long way out, in
    /// a place that looks chosen. So this searches the generated terrain for a
    /// summit that is genuinely prominent and genuinely in view of the road, and
    /// puts exactly one thing on it.
    ///
    /// The subject is a cyclist with both arms up, which is what real mountains
    /// get (Simpson on the Ventoux, Pantani at the Mortirolo). It also happens to
    /// be the best silhouette available: arms in a V read against sky from any
    /// angle, which a hunched sprinter does not.
    /// </summary>
    public class HilltopStatue : MonoBehaviour
    {
        [Header("Wiring")]
        public RideWorld World;
        public Terrain Terrain;

        [Tooltip("Optional real model, nose along +Z and standing on y=0. Left " +
                 "empty, the monument is built from primitives.")]
        public GameObject StatuePrefab;

        [Header("Size")]
        [Tooltip("Plinth base to fingertips, in metres, after normalising by " +
                 "measured bounds.")]
        // 50 m, against Christ the Redeemer's 30. A hilltop statue is stuck with
        // an awkward geometry: it must be a few hundred metres off the road to be
        // on a summit at all, and it is only ever looked at from further back
        // still. Measured at ~450 m, 30 m gave 23 px, 42 m gave 75 px of which the
        // plinth was a third. 50 m with a shorter plinth puts ~65 px into the
        // figure itself, which is where it starts to read as a cyclist.
        public float TotalHeight = 50f;

        [Header("Where it may stand")]
        // Pulled in as close as a real summit allows. The lateral offset sets the
        // floor on viewing distance -- you cannot get nearer than the point where
        // the statue is square beside you, and by then it is out of frame.
        public float MinRoadDistance = 240f;
        public float MaxRoadDistance = 900f;
        public float IdealRoadDistance = 450f;

        [Tooltip("Metres the summit must stand above its own surroundings. This is " +
                 "what separates a peak from a spot partway up a slope.")]
        public float MinProminence = 18f;
        [Tooltip("Metres the summit must stand above the nearest road, or you are " +
                 "looking up at nothing.")]
        public float MinRiseAboveRoad = 45f;
        [Tooltip("Metres above the road we aim for. A hill overlooking the course, " +
                 "not the highest peak on the map.")]
        public float IdealRiseAboveRoad = 80f;
        [Tooltip("Degrees above horizontal, seen from the viewpoint.")]
        // 11, where the naive FOV arithmetic says 24 would be fine. Three things
        // eat the difference: the chase camera looks about 9 degrees DOWN the road
        // rather than level, the stat bar covers the top 78 px, and the figure has
        // to fit above its own base -- it is the arms that must clear the HUD, not
        // the plinth. Measured at 15 degrees the base sat at viewport y 0.89 and
        // the rider's head was behind the stat bar.
        //
        // This also argues for a modest hill over a big one. At a fixed elevation
        // angle the viewing distance scales with the rise, so a summit twice as
        // high is seen from twice as far and looks no larger -- it just gets
        // harder to fit in frame.
        public float MaxElevationAngle = 11f;
        [Tooltip("Degrees either side of straight ahead that still count as in " +
                 "view. Horizontal half-FOV is about 47 at 16:9; 34 keeps it off " +
                 "the very edge of the frame.")]
        public float ViewConeDegrees = 34f;
        [Tooltip("How many of the sampled approach viewpoints must have a clear " +
                 "view. Each one is 50 m of road, so 5 is a 250 m stretch.")]
        public int MinVisibleViewpoints = 5;

        public bool Placed { get; private set; }
        public Vector3 Position { get; private set; }
        /// <summary>Distance along the course of the closest point of road, so a
        /// screenshot or a test can start the rider where the statue is in view.</summary>
        public float RouteDistance { get; private set; }
        public float Prominence { get; private set; }
        public float RiseAboveRoad { get; private set; }
        public float RoadDistance { get; private set; }

        /// <summary>Course distance of the nearest point of road with an
        /// unobstructed, in-frame view. This is where to stand to photograph it,
        /// and it is NOT simply a fixed run-back from the closest approach: the
        /// closest approach is square beside the statue and often behind a ridge.</summary>
        public float BestViewDistance { get; private set; }
        /// <summary>Metres of road from which the monument can actually be seen.</summary>
        public float VisibleRoadMetres { get; private set; }

        /// <summary>The placed monument, for tests and close-up captures.</summary>
        public Transform Monument => _instance != null ? _instance.transform : null;

        /// <summary>Mixed into the seed so the monument gets its own random
        /// stream and does not shift when scenery tuning changes.</summary>
        const int StatueSalt = unchecked((int)0x57A7DE12);

        GameObject _instance;
        Material _stone, _dark;

        void Start()
        {
            if (World != null && World.Route != null) Rebuild(World.Route, World.Seed);
        }

        public void Clear()
        {
            if (_instance != null) DestroyImmediate(_instance);
            _instance = null;
            Placed = false;
        }

        public void Rebuild(RoutePath route, int seed)
        {
            Clear();
            if (route == null || Terrain == null) return;

            var rng = new System.Random(seed ^ StatueSalt);
            float t0 = Time.realtimeSinceStartup;
            if (!FindSummit(route, rng, out var summit)) return;
            float searchMs = (Time.realtimeSinceStartup - t0) * 1000f;

            var template = StatuePrefab != null ? Instantiate(StatuePrefab) : BuildMonument();
            template.name = "HilltopStatue";
            template.transform.SetParent(transform, false);

            // Normalise by measured height, the same way scenery and the aircraft
            // do: a supplied model arrives in whatever units its author chose.
            var bounds = LocalBounds(template);
            if (bounds.size.y > 0.001f && TotalHeight > 0.01f)
                template.transform.localScale *= TotalHeight / bounds.size.y;

            float footprint = Mathf.Max(bounds.size.x, bounds.size.z) *
                              template.transform.localScale.x * 0.5f;

            // Stand it just below the crown of the hill and let a buried footing
            // bridge whatever the ground does around it. Dropping the whole plinth
            // to the LOWEST ground under its footprint, as the first version did,
            // sinks it by however much a steep summit falls away -- which was most
            // of the plinth, with the peak poking up in front of it.
            float crown = Ground(summit.Pos.x, summit.Pos.z);
            float sill = GroundUnderFootprint(summit.Pos, footprint * 0.8f);
            float baseY = crown - 2.0f;
            AddFooting(template, (crown - sill) + 5f);

            // Three-quarter to the VIEWPOINT. The two halves of this sculpture
            // want opposite angles: the bike is legible side-on and disappears
            // head-on, while the raised arms spread across the bike's axis and so
            // vanish exactly when the bike looks best. Dead broadside gave a
            // perfect bicycle under a figure that read as a stick. At 58 degrees
            // the bike keeps 85% of its length and the arms open to 53%.
            //
            // Aiming at the road's nearest point instead of the viewpoint is also
            // wrong -- that point is off at ninety degrees from where anyone ever
            // looks at this.
            Vector3 viewer = route.PositionAt(summit.BestViewDistance);
            Vector3 sight = summit.Pos - viewer;
            sight.y = 0f;
            float facing = Mathf.Atan2(sight.x, sight.z) * Mathf.Rad2Deg;
            float quarter = facing + (rng.Next(2) == 0 ? 58f : -58f) +
                            (float)(rng.NextDouble() * 24.0 - 12.0);

            template.transform.position = new Vector3(summit.Pos.x, baseY, summit.Pos.z);
            template.transform.rotation = Quaternion.Euler(0f, quarter, 0f);

            _instance = template;
            Placed = true;
            Position = template.transform.position;
            RouteDistance = summit.RouteDistance;
            Prominence = summit.Prominence;
            RiseAboveRoad = summit.RiseAboveRoad;
            RoadDistance = summit.RoadDistance;
            BestViewDistance = summit.BestViewDistance;
            VisibleRoadMetres = summit.VisibleViewpoints * 50f;

            float elev = Mathf.Atan2(RiseAboveRoad, RoadDistance) * Mathf.Rad2Deg;
            Debug.Log($"[HilltopStatue] placed at ({Position.x:F0}, {Position.y:F0}, {Position.z:F0}) " +
                      $"-- {Prominence:F0} m prominence, {RiseAboveRoad:F0} m above the road, " +
                      $"{RoadDistance:F0} m from it ({elev:F0} deg up) at km {RouteDistance / 1000f:F1}; " +
                      $"in view over {VisibleRoadMetres:F0} m of road, best from km " +
                      $"{BestViewDistance / 1000f:F1} at {summit.BestViewRange:F0} m " +
                      $"(search {searchMs:F0} ms)");
        }

        // ---------------------------------------------------------------- summit

        struct Summit
        {
            public Vector3 Pos;
            public Vector3 RoadPoint;
            public float RouteDistance;
            public float RoadDistance;
            public float Prominence;
            public float RiseAboveRoad;
            public float BestViewDistance;
            public float BestViewRange;
            public int VisibleViewpoints;
            public float Score;
        }

        float Ground(float x, float z) =>
            Terrain.SampleHeight(new Vector3(x, 0f, z)) + Terrain.transform.position.y;

        /// <summary>
        /// Hill-climb from seed points thrown out sideways from the road, then
        /// score whatever summits that converges on.
        ///
        /// Seeding from the route rather than sweeping the whole 10 km map is both
        /// cheaper and better targeted: a spectacular peak in the far corner of
        /// the terrain is not a landmark for this ride, because you never see it.
        /// </summary>
        bool FindSummit(RoutePath route, System.Random rng, out Summit best)
        {
            best = default;

            var found = new System.Collections.Generic.Dictionary<long, Summit>();
            float[] offsets = { 320f, 560f, 820f, 1150f, 1500f };
            int stations = Mathf.Max(8, Mathf.RoundToInt(route.Length / 250f));
            int considered = 0;

            for (int i = 0; i < stations; i++)
            {
                float d = (i / (float)stations) * route.Length;
                Vector3 fwd = route.ForwardAt(d, 8f);
                Vector3 side = new Vector3(fwd.z, 0f, -fwd.x).normalized;
                Vector3 origin = route.PositionAt(d);

                foreach (float off in offsets)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        Vector3 seedPt = origin + side * (off * s);
                        if (!InBounds(seedPt)) continue;
                        considered++;

                        Vector3 top = ClimbToPeak(seedPt);

                        // Round onto a 150 m grid so the many seeds that walk up
                        // the same hill collapse into one entry.
                        long key = ((long)Mathf.RoundToInt(top.x / 150f) << 20) ^
                                    (long)Mathf.RoundToInt(top.z / 150f);
                        if (found.ContainsKey(key)) continue;

                        if (Evaluate(route, top, out var summit)) found[key] = summit;
                    }
            }

            if (found.Count == 0)
            {
                Debug.LogWarning($"[HilltopStatue] no summit from {considered} seed points");
                return false;
            }

            // Sort by score, then pick from the top few with the seeded stream.
            // Always taking the single best makes every world put the statue on
            // whatever the generator's favourite shape is; a little choice keeps
            // it varied while staying reproducible for a saved seed.
            var ranked = new System.Collections.Generic.List<Summit>(found.Values);
            ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
            int pool = Mathf.Min(3, ranked.Count);
            best = ranked[rng.Next(pool)];

            Debug.Log($"[HilltopStatue] {considered} seeds -> {found.Count} summits, " +
                      $"top score {ranked[0].Score:F0}, chose from best {pool}");
            return true;
        }

        bool InBounds(Vector3 p)
        {
            var tp = Terrain.transform.position;
            var size = Terrain.terrainData.size;
            const float margin = 120f;
            return p.x > tp.x + margin && p.x < tp.x + size.x - margin &&
                   p.z > tp.z + margin && p.z < tp.z + size.z - margin;
        }

        /// <summary>Walk uphill until no neighbour is higher. Coarse steps first
        /// so it crosses shallow ground quickly, then finer to settle on the top.</summary>
        Vector3 ClimbToPeak(Vector3 from)
        {
            Vector2 p = new Vector2(from.x, from.z);
            float h = Ground(p.x, p.y);

            foreach (float step in new[] { 90f, 45f, 22f })
                for (int iter = 0; iter < 30; iter++)
                {
                    Vector2 bestP = p;
                    float bestH = h;
                    for (int a = 0; a < 8; a++)
                    {
                        float ang = a * Mathf.PI * 0.25f;
                        Vector2 q = p + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * step;
                        var probe = new Vector3(q.x, 0f, q.y);
                        if (!InBounds(probe)) continue;
                        float qh = Ground(q.x, q.y);
                        if (qh > bestH) { bestH = qh; bestP = q; }
                    }
                    if (bestP == p) break;
                    p = bestP;
                    h = bestH;
                }

            return new Vector3(p.x, h, p.y);
        }

        bool Evaluate(RoutePath route, Vector3 top, out Summit summit)
        {
            summit = default;

            // Prominence: how far it stands above its own surroundings. A point
            // halfway up a long slope is higher than everything behind it and
            // lower than everything ahead, so the ring MEAN is what separates a
            // peak from a hillside -- the max or the min would not.
            float ring = 0f;
            const int samples = 16;
            for (int a = 0; a < samples; a++)
            {
                float ang = a * Mathf.PI * 2f / samples;
                ring += Ground(top.x + Mathf.Cos(ang) * 260f, top.z + Mathf.Sin(ang) * 260f);
            }
            float prominence = top.y - ring / samples;

            NearestRoad(route, top, out var roadPoint, out float roadDist, out float routeD);
            float rise = top.y - roadPoint.y;

            // Cheap rejects before the expensive part.
            if (roadDist < MinRoadDistance || roadDist > MaxRoadDistance) return false;
            if (prominence < MinProminence) return false;
            if (rise < MinRiseAboveRoad) return false;

            // Now the question that actually matters: riding the approach, how
            // much of it can you SEE this from? Checking one point is not enough
            // -- the first version cleared line of sight at the closest approach
            // and still put the monument squarely behind a mountain, because the
            // closest approach is not where you are looking at it from.
            int visible = 0;
            float bestRange = float.MaxValue, bestViewD = -1f;
            const float stride = 50f;

            for (float back = 150f; back <= 1800f; back += stride)
            {
                float d = route.Wrap(routeD - back);
                Vector3 here = route.PositionAt(d);
                Vector3 fwd = route.ForwardAt(d, 8f);
                Vector3 to = top - here;

                float riseHere = to.y;
                Vector3 flatTo = new Vector3(to.x, 0f, to.z);
                float flat = flatTo.magnitude;

                if (Vector3.Angle(new Vector3(fwd.x, 0f, fwd.z), flatTo) > ViewConeDegrees) continue;
                if (Mathf.Atan2(riseHere, flat) * Mathf.Rad2Deg > MaxElevationAngle) continue;
                if (!HasLineOfSight(here, top)) continue;

                visible++;
                if (flat < bestRange) { bestRange = flat; bestViewD = d; }
            }

            if (visible < MinVisibleViewpoints) return false;

            summit = new Summit
            {
                Pos = top,
                RoadPoint = roadPoint,
                RouteDistance = routeD,
                RoadDistance = roadDist,
                Prominence = prominence,
                RiseAboveRoad = rise,
                BestViewDistance = bestViewD,
                BestViewRange = bestRange,
                VisibleViewpoints = visible,
                // Prominence is what makes a summit look like a summit, but it
                // SATURATES. Rewarding it linearly just elects the biggest
                // mountain on the map: an early run picked a peak 1128 m above the
                // road and 1160 m away, which is 44 degrees up -- spectacular, and
                // never once in frame. Past about 140 m it already reads as a
                // mountain top and the rest is a liability.
                Score = Mathf.Min(prominence, 110f)
                        + visible * 3f
                        - bestRange * 0.09f
                        - Mathf.Abs(rise - IdealRiseAboveRoad) * 0.25f,
            };
            return true;
        }

        static void NearestRoad(RoutePath route, Vector3 p, out Vector3 point,
                                out float distance, out float routeDistance)
        {
            point = Vector3.zero;
            routeDistance = 0f;
            float best = float.MaxValue;

            // 20 m stride: finer than the width of anything we are measuring, and
            // 1250 probes on a 25 km lap is nothing next to the hill climbing.
            for (float d = 0f; d < route.Length; d += 20f)
            {
                Vector3 q = route.PositionAt(d);
                float dx = q.x - p.x, dz = q.z - p.z;
                float sq = dx * dx + dz * dz;
                if (sq >= best) continue;
                best = sq;
                point = q;
                routeDistance = d;
            }
            distance = Mathf.Sqrt(best);
        }

        /// <summary>
        /// Can the monument be seen from this bit of road? Walks the straight line
        /// between them and asks whether the ground ever rises through it.
        /// </summary>
        bool HasLineOfSight(Vector3 fromRoad, Vector3 summit)
        {
            // Eye height on the bike, and aim at the monument's middle rather than
            // its feet: a plinth tucked behind a low rise is still fine if the
            // rider on top of it clears the skyline.
            Vector3 eye = new Vector3(fromRoad.x, fromRoad.y + 2f, fromRoad.z);
            Vector3 aim = new Vector3(summit.x, summit.y + TotalHeight * 0.6f, summit.z);

            const int steps = 48;
            for (int i = 1; i < steps; i++)
            {
                Vector3 p = Vector3.Lerp(eye, aim, i / (float)steps);
                // 2 m of slack absorbs the difference between the sampled
                // heightmap and the rendered mesh.
                if (Ground(p.x, p.z) > p.y + 2f) return false;
            }
            return true;
        }

        /// <summary>Lowest ground anywhere under the base, so nothing overhangs.</summary>
        float GroundUnderFootprint(Vector3 centre, float radius)
        {
            float low = Ground(centre.x, centre.z);
            for (int a = 0; a < 12; a++)
            {
                float ang = a * Mathf.PI * 2f / 12f;
                float g = Ground(centre.x + Mathf.Cos(ang) * radius,
                                 centre.z + Mathf.Sin(ang) * radius);
                if (g < low) low = g;
            }
            return low;
        }

        // ------------------------------------------------------------- the model

        /// <summary>
        /// A cyclist out of the saddle with both arms up, on a stepped plinth.
        /// Built nose along +Z, standing on y=0, roughly 33 units tall before the
        /// caller normalises it to TotalHeight.
        /// </summary>
        GameObject BuildMonument()
        {
            _stone = new Material(Shader.Find("Standard"))
            {
                name = "StatueStone",
                // Bright and slightly warm. The monument is usually seen against
                // a shaded green hillside, and a mid grey disappears into it.
                color = new Color(0.90f, 0.88f, 0.82f),
            };
            _stone.SetFloat("_Glossiness", 0.12f);

            _dark = new Material(Shader.Find("Standard"))
            {
                name = "StatuePlinth",
                color = new Color(0.40f, 0.39f, 0.37f),
            };
            _dark.SetFloat("_Glossiness", 0.08f);

            var root = new GameObject("Monument");

            // --- plinth, 10 units, longer across Z because the bike is long ---
            // Deliberately under a third of the total. Monumental plinths look
            // right up close and waste the budget at distance: whatever height
            // goes into the base is height not spent on the part that has to be
            // recognisable from half a kilometre away.
            Block(root, "Step1", new Vector3(0f, 0.6f, 0f), new Vector3(13f, 1.2f, 21f), _dark);
            Block(root, "Step2", new Vector3(0f, 1.7f, 0f), new Vector3(11f, 1.0f, 18.5f), _dark);
            Block(root, "Column", new Vector3(0f, 5.8f, 0f), new Vector3(8.5f, 7.2f, 15.5f), _dark);
            Block(root, "Cap", new Vector3(0f, 9.7f, 0f), new Vector3(10f, 0.6f, 17.5f), _dark);

            var bike = new GameObject("Cyclist");
            bike.transform.SetParent(root.transform, false);
            bike.transform.localPosition = new Vector3(0f, 10.0f, 0f);

            // --- bike, wheels 6.4 across on a 10.4 wheelbase ---
            Vector3 rearHub = new Vector3(0f, 3.2f, -5.2f);
            Vector3 frontHub = new Vector3(0f, 3.2f, 5.2f);
            Wheel(bike, "WheelRear", rearHub, 6.4f);
            Wheel(bike, "WheelFront", frontHub, 6.4f);

            Vector3 bb = new Vector3(0f, 3.4f, -0.4f);
            Vector3 seat = new Vector3(0f, 9.4f, -2.6f);
            Vector3 head = new Vector3(0f, 8.4f, 3.6f);

            // Tubes at monument thickness, not bicycle thickness. A real 30 mm
            // tube scaled to 42 m is under half a metre and renders as nothing;
            // carved stone has to be stout enough to hold itself up, and it has to
            // survive being 70 px tall.
            Strut(bike, "Chainstay", bb, rearHub, 0.70f);
            Strut(bike, "SeatTube", bb, seat, 0.85f);
            Strut(bike, "SeatStay", seat, rearHub, 0.60f);
            Strut(bike, "DownTube", bb, head, 0.95f);
            Strut(bike, "TopTube", seat, head, 0.75f);
            Strut(bike, "Fork", head, frontHub, 0.72f);
            Strut(bike, "Bars", new Vector3(-2.2f, 8.6f, 3.9f), new Vector3(2.2f, 8.6f, 3.9f), 0.60f);
            Block(bike, "Saddle", seat + new Vector3(0f, 0.4f, -0.2f), new Vector3(1.1f, 0.45f, 2.6f), _stone);

            // --- rider, hands off the bars in a finish-line salute ---
            Vector3 hipL = new Vector3(-1.0f, 11.2f, -1.3f);
            Vector3 hipR = new Vector3(1.0f, 11.2f, -1.3f);
            Vector3 shoulderL = new Vector3(-1.4f, 15.3f, 0.3f);
            Vector3 shoulderR = new Vector3(1.4f, 15.3f, 0.3f);

            Strut(bike, "Torso", new Vector3(0f, 11.2f, -1.3f), new Vector3(0f, 15.4f, 0.35f), 3.4f);
            Strut(bike, "Hips", hipL, hipR, 2.1f);
            Ball(bike, "Head", new Vector3(0f, 17.2f, 0.9f), 3.0f);

            // Legs, knees pushed forward of the line hip-to-pedal so they bend the
            // way a driving leg does instead of reading as stilts.
            Strut(bike, "ThighL", hipL, new Vector3(-1.05f, 7.6f, 0.9f), 2.0f);
            Strut(bike, "ThighR", hipR, new Vector3(1.05f, 7.6f, 0.9f), 2.0f);
            Strut(bike, "ShinL", new Vector3(-1.05f, 7.6f, 0.9f), new Vector3(-1.05f, 4.15f, -0.5f), 1.6f);
            Strut(bike, "ShinR", new Vector3(1.05f, 7.6f, 0.9f), new Vector3(1.05f, 4.15f, -0.5f), 1.6f);
            Block(bike, "FootL", new Vector3(-1.05f, 3.85f, -0.35f), new Vector3(1.0f, 0.5f, 2.2f), _stone);
            Block(bike, "FootR", new Vector3(1.05f, 3.85f, -0.35f), new Vector3(1.0f, 0.5f, 2.2f), _stone);

            // Arms in a V, and thrown FORWARD as well as out. Spread purely
            // sideways they lie in the one plane a side-on viewer cannot see, so
            // the salute -- the entire point of the pose -- was invisible from the
            // angle that shows the bike best. Carrying the hands forward of the
            // shoulders costs nothing head-on and gives them a diagonal to trace
            // from the side.
            Vector3 elbowL = new Vector3(-3.6f, 18.0f, 1.0f);
            Vector3 elbowR = new Vector3(3.6f, 18.0f, 1.0f);
            Vector3 handL = new Vector3(-5.4f, 20.8f, 1.5f);
            Vector3 handR = new Vector3(5.4f, 20.8f, 1.5f);

            Strut(bike, "UpperArmL", shoulderL, elbowL, 1.7f);
            Strut(bike, "UpperArmR", shoulderR, elbowR, 1.7f);
            Strut(bike, "ForearmL", elbowL, handL, 1.45f);
            Strut(bike, "ForearmR", elbowR, handR, 1.45f);
            Ball(bike, "HandL", handL + new Vector3(-0.2f, 0.35f, 0.1f), 1.6f);
            Ball(bike, "HandR", handR + new Vector3(0.2f, 0.35f, 0.1f), 1.6f);

            return root;
        }

        /// <summary>
        /// A buried block under the plinth, deep enough to reach the lowest ground
        /// the base overhangs. Added AFTER the model has been normalised to
        /// TotalHeight so it cannot contribute to the measured height -- it is
        /// foundation, not monument. On the downhill side it shows as a retaining
        /// wall, which is what a real hilltop terrace looks like anyway.
        /// </summary>
        void AddFooting(GameObject root, float depthMetres)
        {
            float scale = root.transform.localScale.y;
            if (scale < 0.0001f) return;
            float depth = Mathf.Clamp(depthMetres, 4f, 90f) / scale;

            var mat = _dark != null ? _dark : root.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            var go = Prim(root, PrimitiveType.Cube, "Footing", mat);
            go.transform.localPosition = new Vector3(0f, -depth * 0.5f, 0f);
            go.transform.localScale = new Vector3(10.5f, depth, 18f);
        }

        GameObject Prim(GameObject parent, PrimitiveType shape, string name, Material mat)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            var col = go.GetComponent<Collider>();
            if (col != null) DestroyImmediate(col);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        void Block(GameObject parent, string name, Vector3 centre, Vector3 size, Material mat)
        {
            var go = Prim(parent, PrimitiveType.Cube, name, mat);
            go.transform.localPosition = centre;
            go.transform.localScale = size;
        }

        void Ball(GameObject parent, string name, Vector3 centre, float diameter)
        {
            var go = Prim(parent, PrimitiveType.Sphere, name, _stone);
            go.transform.localPosition = centre;
            go.transform.localScale = Vector3.one * diameter;
        }

        /// <summary>A disc standing in the XY plane -- a wheel seen from the side.
        /// Unity's cylinder runs along its own Y, so it is laid over onto X.</summary>
        void Wheel(GameObject parent, string name, Vector3 centre, float diameter)
        {
            var go = Prim(parent, PrimitiveType.Cylinder, name, _stone);
            go.transform.localPosition = centre;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            go.transform.localScale = new Vector3(diameter, 0.8f, diameter);
        }

        /// <summary>A box stretched between two points. Limbs and frame tubes are
        /// all far easier to place by their endpoints than by centre-and-euler.</summary>
        void Strut(GameObject parent, string name, Vector3 a, Vector3 b, float thick)
        {
            var go = Prim(parent, PrimitiveType.Cube, name, _stone);
            Vector3 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.001f) { DestroyImmediate(go); return; }

            go.transform.localPosition = (a + b) * 0.5f;
            go.transform.localRotation = Quaternion.LookRotation(delta / len, Vector3.up);
            go.transform.localScale = new Vector3(thick, thick, len);
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
                var s = f.transform.lossyScale;
                b = new Bounds(b.center + offset,
                               Vector3.Scale(b.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))));
                if (!any) { result = b; any = true; } else result.Encapsulate(b);
            }
            if (!any) result = new Bounds(Vector3.zero, Vector3.one);
            return result;
        }

        void OnDestroy()
        {
            if (_stone != null) Destroy(_stone);
            if (_dark != null) Destroy(_dark);
        }
    }
}
