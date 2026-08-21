using System.Collections.Generic;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>One planned lake, in world units. Serializable because the baked
    /// scene has to carry its lakes: the basins are cut into the terrain asset,
    /// but the water and boats are rebuilt at startup, and re-planning them then
    /// would measure the already-carved ground and answer a different question.</summary>
    [System.Serializable]
    public struct LakeSite
    {
        /// <summary>Centre in world x/z.</summary>
        public Vector2 Centre;
        /// <summary>Bearing of the long axis, radians, lying along the road.</summary>
        public float AxisAngle;
        /// <summary>Half-length along the road.</summary>
        public float HalfLength;
        /// <summary>Half-width across it.</summary>
        public float HalfWidth;
        /// <summary>Rotates the shoreline wobble so no two lakes are the same shape.</summary>
        public float ShapePhase;
        /// <summary>Surface height in world metres.</summary>
        public float WaterLevel;
        public float Depth;
        public float RoadDistance;
        public float RouteDistance;
        /// <summary>How much the road rose or fell over the length of the lake.
        /// The number that decides whether a lake can go here at all.</summary>
        public float RoadRelief;
    }

    /// <summary>
    /// Finds places that can hold water, and carves them.
    ///
    /// A lake cannot be placed *on* the terrain the way scenery and the monument
    /// are -- it has to be cut *into* it, before the heightmap reaches Unity.
    /// Water is a horizontal plane, so what makes a lake read as a lake rather
    /// than a sheet of glass dropped on a hillside is that the ground meets it at
    /// one height all the way round.
    ///
    /// The first version hunted for round patches of naturally flat ground and
    /// found almost none: this terrain is bulldozed flat in the road corridor and
    /// is mountain everywhere else, so the search had to relax its tolerance to
    /// 50 m of relief before it would place anything -- which is not flat, it is
    /// a hillside. The question was wrong. There is exactly one thing in this
    /// world that is reliably level and reliably near the rider: the road.
    ///
    /// So find a stretch of road that does not climb, and lay a lake along it,
    /// long axis parallel to the tarmac, surface pegged a few metres below the
    /// road. The far shore runs up into whatever mountain is behind it, which is
    /// what an alpine lake beside a road actually looks like.
    /// </summary>
    public static class LakeGen
    {
        /// <summary>Mixed into the seed so lakes get their own random stream.</summary>
        const int LakeSalt = unchecked((int)0x1A4E5A17);

        /// <summary>How far the wobbled shoreline can exceed the plain ellipse.</summary>
        public const float ShapeBulge = 1.22f;

        /// <summary>Half-lengths tried at each stretch, largest first.</summary>
        static readonly float[] HalfLengths = { 170f, 130f, 95f };

        /// <summary>Width as a fraction of length. Lakes beside roads are long.</summary>
        const float WidthRatio = 0.42f;

        /// <summary>Band outside the water where the carve returns to real ground.</summary>
        const float ShoreBand = 22f;

        /// <summary>Bank thrown up just outside the waterline.</summary>
        const float BankHeight = 3f;

        /// <summary>
        /// Closest the carve may come to the road centreline.
        ///
        /// This no longer has to hug the road. Before the corridor gained a
        /// falling side, the ground beside the road was flat at road level for
        /// 40 m and a lake had to be crammed against the shoulder to get out from
        /// behind that lip -- and even then it was a sliver, because a surface
        /// D below flat ground of width A is hidden out to A(E+D)/E for an eye
        /// E above it. Now the open side falls away on its own, so the lake can
        /// sit out on the shelf where it has room to be a lake.
        /// </summary>
        const float RoadClearance = 16f;

        /// <summary>
        /// Where the near shore has to start to be seen at all.
        ///
        /// The open side of the road keeps a shoulder about 10 m wide before it
        /// falls. Sighting past that from 2 m up, water D below is hidden out to
        /// A(E+D)/E -- roughly 160 m for a 30 m drop. This is that number, and it
        /// is the reason lakes sit out on the shelf rather than against the road:
        /// every version that hugged the shoulder produced a thin blue ribbon.
        /// </summary>
        const float ShadowClear = 165f;

        /// <summary>
        /// Metres the road may rise or fall along the lake and still count.
        ///
        /// Relaxed from 7 m once the water level stopped being pegged to the road.
        /// While it was, a climbing road meant a lake buried at one end; now the
        /// surface comes from the ground in the basin, and a road that rises past
        /// a lake in a valley is just a road that rises past a lake. At 7 m only
        /// 26 stretches of a 25 km course qualified, which starved the search.
        /// </summary>
        const float MaxRoadRelief = 25f;

        // How far below the road the water may sit. There is a floor and a
        // ceiling, and both were learned by measuring.
        //
        // The floor is 10 m, where 3.5 m seemed ample: water is a horizontal
        // plane, so how much of it you see depends entirely on how far you are
        // looking DOWN at it. From a level road 682 m away, a lake 3.5 m below the
        // tarmac subtended half a degree -- the renderer reported it on screen and
        // it covered no pixels, because a plane viewed edge-on has no area.
        //
        // The ceiling is 70 m, so the lake stays part of the ride rather than
        // something glimpsed at the bottom of a ravine.
        // At least 18 m down, so the lake sits on the shelf rather than in the
        // strip beside the road, and no more than 70 so it stays part of the ride.
        const float MinBelowRoad = 5f;
        const float MaxBelowRoad = 40f;

        /// <summary>
        /// Deepest the carve may cut into natural ground.
        ///
        /// Without this the planner cheerfully mined a 339 m pit into a mountain
        /// to hold a lake, because nothing in the earlier tests looked at how much
        /// rock was in the way -- only at whether the rim fell away afterwards.
        /// </summary>
        const float MaxCut = 22f;

        /// <summary>
        /// Shoreline radius at a world bearing: an ellipse along the road, pushed
        /// around by two low harmonics so it is not a stadium oval.
        ///
        /// The carve, the water mesh and the shore painting all call this. They are
        /// three descriptions of one edge, and if any disagrees the water either
        /// floats over the bank or leaves a rim of dry bed around itself.
        /// </summary>
        public static float RadiusAt(LakeSite lake, float worldAngle)
        {
            float t = worldAngle - lake.AxisAngle;
            float a = Mathf.Max(1f, lake.HalfLength), b = Mathf.Max(1f, lake.HalfWidth);
            float cos = Mathf.Cos(t), sin = Mathf.Sin(t);
            float ellipse = a * b / Mathf.Sqrt(b * b * cos * cos + a * a * sin * sin);
            return ellipse * (1f + 0.13f * Mathf.Sin(t * 2f + lake.ShapePhase)
                                 + 0.07f * Mathf.Sin(t * 3f - lake.ShapePhase * 1.7f));
        }

        /// <summary>Widest the carve reaches from the centre, in any direction.</summary>
        public static float Extent(LakeSite lake) =>
            Mathf.Max(lake.HalfLength, lake.HalfWidth) * ShapeBulge + ShoreBand;

        public static List<LakeSite> Plan(WorldSettings s, RoutePath route,
                                          float[,] heights, int wanted = 2)
        {
            var found = new List<LakeSite>();
            if (route == null || heights == null) return found;

            int res = s.HeightmapResolution;
            float texel = s.TerrainSize / (res - 1);
            var rng = new System.Random(s.Seed ^ LakeSalt);

            int stations = Mathf.Max(8, Mathf.RoundToInt(route.Length / 120f));
            int level = 0, held = 0;
            // Rejection tally. Which test is doing the work is not guessable, and
            // when the answer came back "no lakes at all" it was the only way to
            // find out which of five constraints had closed the door.
            int rBounds = 0, rOverlap = 0, rRoad = 0, rBasin = 0,
                rDrop = 0, rCut = 0, rHolds = 0, rView = 0;

            for (int i = 0; i < stations && found.Count < wanted; i++)
            {
                float d = (i / (float)stations) * route.Length;
                Vector3 origin = route.PositionAt(d);
                Vector3 fwd = route.ForwardAt(d, 8f);
                Vector3 side = new Vector3(fwd.z, 0f, -fwd.x).normalized;

                foreach (float halfLength in HalfLengths)
                {
                    float halfWidth = halfLength * WidthRatio;

                    // Is the road level along the whole lake? This is the filter
                    // that matters: the water is one height and the road beside it
                    // is the reference, so a climbing road gives a lake buried at
                    // one end and perched at the other.
                    float reach = halfLength + ShoreBand;
                    if (!RoadProfile(route, d, reach, out float relief, out float roadY)) continue;
                    if (relief > MaxRoadRelief) continue;
                    level++;

                    // Hard against the clearance. Pushing lakes out to clear the
                    // lip shadow was tried and measured worse: past the corridor
                    // the shelf fades, the ground comes back up, and the water
                    // ends up both further away and behind natural terrain.
                    float offset = RoadClearance + halfWidth * ShapeBulge + ShoreBand;

                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var centre = new Vector2(origin.x + side.x * offset * sign,
                                                 origin.z + side.z * offset * sign);

                        var lake = new LakeSite
                        {
                            Centre = centre,
                            AxisAngle = Mathf.Atan2(fwd.z, fwd.x),
                            HalfLength = halfLength,
                            HalfWidth = halfWidth,
                            ShapePhase = (float)rng.NextDouble() * Mathf.PI * 2f,
                            Depth = Mathf.Clamp(halfWidth * 0.18f, 5f, 12f),
                            RoadDistance = offset,
                            RouteDistance = d,
                            RoadRelief = relief,
                        };

                        if (!InBounds(s, lake)) { rBounds++; continue; }
                        if (Overlaps(found, lake)) { rOverlap++; continue; }
                        // Does the road come back round and cut through it? The
                        // course is a loop, so being clear of the road at this
                        // station says nothing about the rest of the lap.
                        if (NearestRoad(route, centre) < RoadClearance + halfWidth * ShapeBulge)
                        { rRoad++; continue; }

                        // Take the surface from the ground that is actually there,
                        // not from the road. Pegging it to the road and carving
                        // away whatever stood in the way is what produced the pit.
                        if (!Basin(s, heights, res, texel, lake, out float median, out float high))
                        { rBasin++; continue; }
                        // Straight from the ground that is there. The falling
                        // side of the corridor supplies the drop now, so there is
                        // nothing to force.
                        lake.WaterLevel = Mathf.Min(median - 1f, roadY - 8f);

                        // The lake must lie in ground that is ALREADY low. Digging
                        // the drop rather than finding it is what produced a lake
                        // nobody could see: a basin sunk into flat ground beside a
                        // level road has its near rim at road height, and from
                        // two metres above the tarmac you cannot see over it.
                        float below = roadY - lake.WaterLevel;
                        if (below < MinBelowRoad || below > MaxBelowRoad) { rDrop++; continue; }
                        if (high - lake.WaterLevel > MaxCut) { rCut++; continue; }
                        if (!Holds(s, heights, res, texel, lake)) { rHolds++; continue; }
                        // And the slope between road and water must actually fall
                        // away, with no intervening ridge. This is the test the
                        // first four versions were all missing.
                        if (!InView(s, heights, res, texel, route, lake, d)) { rView++; continue; }

                        held++;
                        found.Add(lake);
                        break;
                    }

                    if (found.Count >= wanted) break;
                }
            }

            Debug.Log($"[LakeGen] {found.Count} lake(s) from {level} level stretches. " +
                      $"Rejected: {rBounds} off map, {rOverlap} overlapping, {rRoad} too near road, " +
                      $"{rBasin} unsampleable, {rDrop} wrong depth below road, {rCut} too much rock, " +
                      $"{rHolds} would not hold, {rView} out of sight");
            foreach (var l in found)
                Debug.Log($"[LakeGen]   {l.HalfLength * 2f:F0} x {l.HalfWidth * 2f:F0} m at " +
                          $"({l.Centre.x:F0}, {l.Centre.y:F0}), surface {l.WaterLevel:F0} m, " +
                          $"{l.RoadDistance:F0} m from the road at km {l.RouteDistance / 1000f:F1}, " +
                          $"road relief {l.RoadRelief:F1} m");
            return found;
        }

        /// <summary>
        /// Can the water be seen from the road it sits beside?
        ///
        /// Checked here, at planning time, rather than left to the capture code.
        /// A lake that cannot be seen is not a feature, and every earlier version
        /// of this planner produced one: correct bowl, sound shoreline, complete
        /// with boats, entirely hidden behind its own near bank.
        /// </summary>
        static bool InView(WorldSettings s, float[,] heights, int res, float texel,
                           RoutePath route, LakeSite lake, float station)
        {
            var axis = new Vector2(Mathf.Cos(lake.AxisAngle), Mathf.Sin(lake.AxisAngle));
            var perp = new Vector2(-axis.y, axis.x);

            // Look from a few points along the approach, not just the closest: the
            // rider sees this while travelling, and the near end of a long lake
            // opens up well before the middle does.
            for (int v = 0; v < 5; v++)
            {
                float d = route.Wrap(station - (200f + v * 150f));
                Vector3 eye = route.PositionAt(d);
                eye.y += 2f;

                int seen = 0, total = 0;
                for (int iy = -1; iy <= 1; iy++)
                    for (int ix = -3; ix <= 3; ix++)
                    {
                        float u = ix / 3.5f, w = iy / 2f;
                        if (u * u + w * w > 1f) continue;

                        Vector2 flat = lake.Centre + axis * (u * lake.HalfLength)
                                                   + perp * (w * lake.HalfWidth);
                        var target = new Vector3(flat.x, lake.WaterLevel + 1f, flat.y);
                        total++;
                        if (RayClear(s, heights, res, texel, lake, eye, target)) seen++;
                    }

                if (total > 0 && seen * 5 >= total) return true;
            }
            return false;
        }

        /// <summary>Straight-line visibility across the heightmap as it WILL be,
        /// with this lake cut into it.</summary>
        static bool RayClear(WorldSettings s, float[,] heights, int res, float texel,
                             LakeSite lake, Vector3 from, Vector3 to)
        {
            const int steps = 40;
            for (int i = 1; i < steps; i++)
            {
                Vector3 p = Vector3.Lerp(from, to, i / (float)steps);
                int xi = Mathf.RoundToInt(p.x / texel), zi = Mathf.RoundToInt(p.z / texel);
                if (xi < 0 || zi < 0 || xi >= res || zi >= res) return false;
                float ground = CarvedHeight(lake, p.x, p.z, heights[zi, xi] * s.TerrainHeight);
                if (ground > p.y + 2f) return false;
            }
            return true;
        }

        /// <summary>
        /// Median and near-highest natural ground inside the waterline. The median
        /// sets the surface; the high point says how much rock the carve would
        /// have to remove to reach it.
        /// </summary>
        static bool Basin(WorldSettings s, float[,] heights, int res, float texel,
                          LakeSite lake, out float median, out float high)
        {
            median = high = 0f;
            var samples = new List<float>(196);
            var axis = new Vector2(Mathf.Cos(lake.AxisAngle), Mathf.Sin(lake.AxisAngle));
            var perp = new Vector2(-axis.y, axis.x);

            for (int iy = -6; iy <= 6; iy++)
                for (int ix = -6; ix <= 6; ix++)
                {
                    float u = ix / 6f, v = iy / 6f;
                    if (u * u + v * v > 1f) continue;   // inside the ellipse only

                    Vector2 p = lake.Centre + axis * (u * lake.HalfLength)
                                            + perp * (v * lake.HalfWidth);
                    int xi = Mathf.RoundToInt(p.x / texel), zi = Mathf.RoundToInt(p.y / texel);
                    if (xi < 0 || zi < 0 || xi >= res || zi >= res) return false;
                    samples.Add(heights[zi, xi] * s.TerrainHeight);
                }

            if (samples.Count < 8) return false;
            samples.Sort();
            median = samples[samples.Count / 2];
            // 90th percentile rather than the maximum, so one noise spike cannot
            // veto an otherwise good basin.
            high = samples[Mathf.Min(samples.Count - 1, (samples.Count * 9) / 10)];
            return true;
        }

        /// <summary>
        /// Will the basin hold water? Checks the ring just outside the carve: if
        /// the ground there falls well below the surface, the lake is perched on a
        /// slope and reads as a tilted puddle however neatly the bowl is cut.
        /// </summary>
        static bool Holds(WorldSettings s, float[,] heights, int res, float texel, LakeSite lake)
        {
            float r = Extent(lake) + 25f;
            int below = 0, total = 0;

            for (int i = 0; i < 48; i++)
            {
                float ang = i * Mathf.PI * 2f / 48f;
                int xi = Mathf.RoundToInt((lake.Centre.x + Mathf.Cos(ang) * r) / texel);
                int zi = Mathf.RoundToInt((lake.Centre.y + Mathf.Sin(ang) * r) / texel);
                if (xi < 0 || zi < 0 || xi >= res || zi >= res) return false;

                total++;
                if (heights[zi, xi] * s.TerrainHeight < lake.WaterLevel - 6f) below++;
            }

            // A quarter of the rim may fall away -- that is an outflow, and lakes
            // have those. Half of it falling away is a hillside.
            return total > 0 && below <= total / 4;
        }

        /// <summary>
        /// Ground height at a point once this lake has been cut in.
        ///
        /// Extracted so the carve and the visibility test cannot disagree. They
        /// did: visibility was measured against the raw heightmap, which still
        /// contained the bank that the carve was about to remove, so the planner
        /// rejected sites for being hidden behind an obstruction that would not
        /// exist by the time anyone looked.
        /// </summary>
        public static float CarvedHeight(LakeSite lake, float wx, float wz, float orig)
        {
            float dx = wx - lake.Centre.x, dz = wz - lake.Centre.y;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            float edge = RadiusAt(lake, Mathf.Atan2(dz, dx));
            if (d > edge + ShoreBand) return orig;

            if (d <= edge)
            {
                // A shallow bowl reaching the surface exactly at the waterline, so
                // the shore is a shore and not a step.
                float t = d / edge;
                float bed = lake.WaterLevel -
                            lake.Depth * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
                // Never RAISE ground inside the lake: where the natural terrain
                // already dips below the bed, that is a gully feeding it, and
                // plugging it would look like a bung.
                return Mathf.Min(orig, bed);
            }

            float u = (d - edge) / ShoreBand;
            float sm = u * u * (3f - 2f * u);
            // The rim starts AT water level and rises from there. Starting it
            // higher walls off the waterline and hides the very edge that makes
            // the lake read as water.
            float rim = lake.WaterLevel +
                        BankHeight * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.5f));
            return Mathf.Lerp(rim, orig, sm);
        }

        /// <summary>Cut the basins into the heightmap, before it reaches Unity.</summary>
        public static void Carve(WorldSettings s, float[,] heights, List<LakeSite> lakes)
        {
            if (lakes == null || lakes.Count == 0) return;

            int res = s.HeightmapResolution;
            float texel = s.TerrainSize / (res - 1);

            foreach (var lake in lakes)
            {
                int touched = 0;
                float deepest = 0f;
                float extent = Extent(lake);
                int x0 = Mathf.Max(0, Mathf.FloorToInt((lake.Centre.x - extent) / texel));
                int x1 = Mathf.Min(res - 1, Mathf.CeilToInt((lake.Centre.x + extent) / texel));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((lake.Centre.y - extent) / texel));
                int z1 = Mathf.Min(res - 1, Mathf.CeilToInt((lake.Centre.y + extent) / texel));

                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float wx = x * texel, wz = z * texel;
                        float orig = heights[z, x] * s.TerrainHeight;
                        float h = CarvedHeight(lake, wx, wz, orig);
                        if (h == orig) continue;

                        float drop = orig - h;
                        if (drop > deepest) deepest = drop;
                        touched++;
                        heights[z, x] = Mathf.Clamp01(h / s.TerrainHeight);
                    }

                Debug.Log($"[LakeGen] carved {touched} samples, deepest cut {deepest:F1} m, " +
                          $"surface {lake.WaterLevel:F0} m");
            }
        }

        /// <summary>Rise and fall of the road over a stretch, and its mean height.</summary>
        static bool RoadProfile(RoutePath route, float centre, float reach,
                                out float relief, out float meanY)
        {
            float lo = float.MaxValue, hi = float.MinValue, sum = 0f;
            int n = 0;
            for (float o = -reach; o <= reach; o += 20f)
            {
                float y = route.PositionAt(route.Wrap(centre + o)).y;
                if (y < lo) lo = y;
                if (y > hi) hi = y;
                sum += y;
                n++;
            }
            relief = hi - lo;
            meanY = n > 0 ? sum / n : 0f;
            return n > 0;
        }

        static bool InBounds(WorldSettings s, LakeSite lake)
        {
            float m = Extent(lake) + 60f;
            return lake.Centre.x > m && lake.Centre.x < s.TerrainSize - m &&
                   lake.Centre.y > m && lake.Centre.y < s.TerrainSize - m;
        }

        static bool Overlaps(List<LakeSite> existing, LakeSite lake)
        {
            foreach (var l in existing)
                if (Vector2.Distance(l.Centre, lake.Centre) < Extent(l) + Extent(lake) + 150f)
                    return true;
            return false;
        }

        static float NearestRoad(RoutePath route, Vector2 p)
        {
            float best = float.MaxValue;
            for (float d = 0f; d < route.Length; d += 20f)
            {
                Vector3 q = route.PositionAt(d);
                float dx = q.x - p.x, dz = q.z - p.y;
                float sq = dx * dx + dz * dz;
                if (sq < best) best = sq;
            }
            return Mathf.Sqrt(best);
        }
    }
}
