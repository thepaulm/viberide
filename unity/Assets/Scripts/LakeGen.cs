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
        /// <summary>Unit vector from the centre toward the road, so the carve can
        /// tell the near shore from the far one. The two are not alike: the far
        /// shore runs up a mountain, the near one has to be walked down by eye.</summary>
        public Vector2 RoadDir;
        /// <summary>Centre to road centreline. With RoadDir this is everything the
        /// carve needs to know about where the rider will be standing.</summary>
        public float RoadGap;
        /// <summary>Road surface height beside the lake.</summary>
        public float RoadY;
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

        /// <summary>Multipliers on the standoff from the road, nearest first.</summary>
        static readonly float[] OffsetLadder = { 1f, 1.4f, 1.9f };

        /// <summary>Half-lengths tried at each stretch, largest first.</summary>
        static readonly float[] HalfLengths = { 170f, 130f, 95f };

        /// <summary>Width as a fraction of length. Lakes beside roads are long.</summary>
        const float WidthRatio = 0.42f;

        /// <summary>Band outside the water where the carve returns to real ground.
        ///
        /// 22 m was too short to be a shore. Where a lake met rising ground the
        /// carve had to give back the whole height difference inside it, and a
        /// measured 200 m cut across 22 m is not a bank, it is a quarry face --
        /// which is exactly what these lakes looked like from the road.</summary>
        const float ShoreBand = 60f;

        /// <summary>
        /// How close to the road the near shore is graded.
        ///
        /// This is the A in A(E+D)/E, and it is the whole reason lakes were
        /// invisible: the corridor holds its bench flat at road level for about
        /// 40 m, and from an eye 2 m up that lip casts a shadow 248 m long over
        /// water 29 m below. No lake fits beyond that and still reads as being
        /// beside the road. So on the side facing the road the bench is cut back
        /// to the edge of the tarmac and the ground falls from there to the
        /// water, which drops the shadow to a few tens of metres.
        /// </summary>
        const float RoadEdge = 9f;

        /// <summary>Eye height above the road, for the sightline arithmetic.</summary>
        const float EyeHeight = 2f;

        /// <summary>How far above the waterline the surrounding ground may stand.
        /// Beyond this the lake is in a hole, and the carve is cutting a cliff to
        /// put it there.</summary>
        const float MaxShoreRise = 55f;

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
        // Narrowed from 5..40. The ceiling is not a taste question: with the
        // near shore about 90 m out and the bench cut back to RoadEdge, the
        // shadow A(E+D)/E stays clear of the water only while D is under about
        // 20 m. A deeper lake is a better lake right up until it disappears.
        const float MinBelowRoad = 8f;
        // 14, not 20, and the number is forced rather than chosen: the near shore
        // lands about 80 m out, and RoadEdge(E + D)/E reaches 72 m at D = 14 and
        // 99 m at D = 20. Twenty metres of drop puts the water back behind the
        // shoulder, which is where it has been all along.
        const float MaxBelowRoad = 14f;

        /// <summary>
        /// Deepest the carve may cut into natural ground.
        ///
        /// Without this the planner cheerfully mined a 339 m pit into a mountain
        /// to hold a lake, because nothing in the earlier tests looked at how much
        /// rock was in the way -- only at whether the rim fell away afterwards.
        /// </summary>
        const float MaxCut = 22f;

        /// <summary>How far the natural floor may sit below the water surface
        /// before the lake is a sheet hung over a hole rather than a lake.</summary>
        const float MaxUnderfill = 26f;

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
                                          float[,] heights, int wanted = 0)
        {
            var found = new List<LakeSite>();
            if (route == null || heights == null) return found;

            // One lake every dozen kilometres or so. Two was a fixed count from
            // when every course was 25 km; on a 60 mile lap it meant riding an
            // hour between them, which is indistinguishable from having none.
            if (wanted <= 0)
                wanted = Mathf.Clamp(Mathf.RoundToInt(route.Length / 12000f), 2, 6);

            int res = s.HeightmapResolution;
            float texel = s.TerrainSize / (res - 1);
            var rng = new System.Random(s.Seed ^ LakeSalt);

            int stations = Mathf.Max(8, Mathf.RoundToInt(route.Length / 120f));
            int level = 0, held = 0;
            // Rejection tally. Which test is doing the work is not guessable, and
            // when the answer came back "no lakes at all" it was the only way to
            // find out which of five constraints had closed the door.
            int rBounds = 0, rOverlap = 0, rRoad = 0, rBasin = 0,
                rDrop = 0, rCut = 0, rHolds = 0, rView = 0, rShadow = 0, rWall = 0, rDeep = 0;

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
                    // Far enough out that the graded bank has somewhere to go:
                    // the near shore wants to clear the RoadEdge shadow, and a
                    // shore hard against the clearance leaves no slope to walk
                    // the ground down, only a step.
                    float baseOffset = RoadClearance + halfWidth * ShapeBulge + ShoreBand;

                    // Standing the lake further out buys shadow clearance
                    // directly -- the shoulder hides a fixed distance, so the
                    // cure for being inside it is to be beyond it. Tried nearest
                    // first so lakes stay part of the ride when they can.
                    bool placedHere = false;
                    foreach (float push in OffsetLadder)
                    {
                    if (placedHere) break;
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        float offset = baseOffset * push;
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
                            RoadDir = new Vector2(-side.x * sign, -side.z * sign),
                            RoadGap = offset,
                            RoadY = roadY,
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
                        if (!Basin(s, heights, res, texel, lake, out float median,
                                   out float high, out float low))
                        { rBasin++; continue; }
                        // Straight from the ground that is there. The falling
                        // side of the corridor supplies the drop now, so there is
                        // nothing to force.
                        lake.WaterLevel = Mathf.Clamp(Mathf.Min(median - 1f, roadY - MinBelowRoad),
                                                      roadY - MaxBelowRoad, roadY - MinBelowRoad);

                        // The lake must lie in ground that is ALREADY low. Digging
                        // the drop rather than finding it is what produced a lake
                        // nobody could see: a basin sunk into flat ground beside a
                        // level road has its near rim at road height, and from
                        // two metres above the tarmac you cannot see over it.
                        float below = roadY - lake.WaterLevel;
                        if (below < MinBelowRoad || below > MaxBelowRoad) { rDrop++; continue; }
                        if (high - lake.WaterLevel > MaxCut) { rCut++; continue; }

                        // ...and the floor must not fall away underneath it. The
                        // surface is clamped into a band the rider can see into,
                        // which means it no longer simply follows the ground --
                        // so a basin that plunges below that band gets a flat
                        // sheet of water hanging over a canyon.
                        if (lake.WaterLevel - low > MaxUnderfill) { rDeep++; continue; }

                        // The sightline test, stated once and arithmetically
                        // rather than hoped for. Ground held at RoadEdge for the
                        // width of the shoulder hides water `below` metres down
                        // out to RoadEdge(E + below)/E; if the near shore is
                        // inside that, the rider is looking at a bank.
                        float nearShore = lake.RoadGap -
                            RadiusAt(lake, Mathf.Atan2(-lake.RoadDir.y, -lake.RoadDir.x));
                        float shadow = RoadEdge * (EyeHeight + below) / EyeHeight;
                        if (nearShore < shadow * 1.05f) { rShadow++; continue; }

                        // And the ring outside the waterline must not tower over
                        // it. Without this the planner would sink a lake into a
                        // mountainside and let the carve take 200 m off the slope
                        // to make room, which is where the cliffs came from.
                        if (ShoreRise(s, heights, res, texel, lake) > MaxShoreRise)
                        { rWall++; continue; }
                        if (!Holds(s, heights, res, texel, lake)) { rHolds++; continue; }
                        // And the slope between road and water must actually fall
                        // away, with no intervening ridge. This is the test the
                        // first four versions were all missing.
                        if (!InView(s, heights, res, texel, route, lake, d)) { rView++; continue; }

                        held++;
                        found.Add(lake);
                        placedHere = true;
                        break;
                    }
                    }

                    if (placedHere || found.Count >= wanted) break;
                }
            }

            Debug.Log($"[LakeGen] {found.Count} lake(s) from {level} level stretches. " +
                      $"Rejected: {rBounds} off map, {rOverlap} overlapping, {rRoad} too near road, " +
                      $"{rBasin} unsampleable, {rDrop} wrong depth below road, {rCut} too much rock, " +
                      $"{rHolds} would not hold, {rView} out of sight, " +
                      $"{rShadow} in the shoulder's shadow, {rWall} walled in, " +
                      $"{rDeep} floored below the surface");
            foreach (var l in found) Transect(s, heights, res, texel, route, l);
            foreach (var l in found)
                Debug.Log($"[LakeGen]   {l.HalfLength * 2f:F0} x {l.HalfWidth * 2f:F0} m at " +
                          $"({l.Centre.x:F0}, {l.Centre.y:F0}), surface {l.WaterLevel:F0} m, " +
                          $"{l.RoadDistance:F0} m from the road at km {l.RouteDistance / 1000f:F1}, " +
                          $"road relief {l.RoadRelief:F1} m");
            return found;
        }

        /// <summary>How far the natural ground just outside the waterline stands
        /// above it, at the 80th percentile so one spike does not condemn a
        /// site.</summary>
        static float ShoreRise(WorldSettings s, float[,] heights, int res, float texel,
                               LakeSite lake)
        {
            var rises = new List<float>(32);
            for (int i = 0; i < 32; i++)
            {
                float a = i / 32f * Mathf.PI * 2f;
                float r = RadiusAt(lake, a) + ShoreBand * 0.5f;
                float wx = lake.Centre.x + Mathf.Cos(a) * r;
                float wz = lake.Centre.y + Mathf.Sin(a) * r;
                int xi = Mathf.RoundToInt(wx / texel), zi = Mathf.RoundToInt(wz / texel);
                if (xi < 0 || zi < 0 || xi >= res || zi >= res) continue;
                rises.Add(heights[zi, xi] * s.TerrainHeight - lake.WaterLevel);
            }
            if (rises.Count == 0) return float.MaxValue;
            rises.Sort();
            return rises[Mathf.Min(rises.Count - 1, Mathf.RoundToInt(rises.Count * 0.8f))];
        }

        /// <summary>
        /// Ground height every 10 m from the road to the far shore, against the
        /// waterline. A lake can be the right size, in the right place, beside
        /// the road, and still be invisible for want of one ridge in between --
        /// and no overhead view will ever show that. This prints the section.
        /// </summary>
        static void Transect(WorldSettings s, float[,] heights, int res, float texel,
                             RoutePath route, LakeSite lake)
        {
            Vector3 road = route.PositionAt(lake.RouteDistance);
            var from = new Vector2(road.x, road.z);
            Vector2 to = lake.Centre + (lake.Centre - from).normalized * lake.HalfWidth;
            float span = Vector2.Distance(from, to);
            int steps = Mathf.Clamp(Mathf.RoundToInt(span / 10f), 4, 60);

            var sb = new System.Text.StringBuilder();
            float worst = float.MinValue; float worstAt = 0f;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector2 p = Vector2.Lerp(from, to, t);
                int xi = Mathf.Clamp(Mathf.RoundToInt(p.x / texel), 0, res - 1);
                int zi = Mathf.Clamp(Mathf.RoundToInt(p.y / texel), 0, res - 1);
                float g = CarvedHeight(lake, p.x, p.y, heights[zi, xi] * s.TerrainHeight);
                float rel = g - lake.WaterLevel;
                if (i > 0 && rel > worst) { worst = rel; worstAt = t * span; }
                if (i % 2 == 0) sb.Append($"{t * span:F0}m:{rel:+0;-0} ");
            }

            // Can a 2 m eye on the road see the waterline over that?  The ridge
            // hides it when its angle above the eye beats the water's.
            float eye = road.y + 2f - lake.WaterLevel;
            bool blocked = worst > 0f && worstAt > 1f &&
                           (worst - eye) / worstAt > -eye / Mathf.Max(1f, span);

            Debug.Log($"[LakeGen]   section road->water, height above waterline: {sb}");
            Debug.Log($"[LakeGen]   road is {eye:F0} m above the water, " +
                      $"highest ground between is {worst:F0} m at {worstAt:F0} m out " +
                      $"-- waterline {(blocked ? "HIDDEN behind it" : "clear")}");
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
                          LakeSite lake, out float median, out float high, out float low)
        {
            median = high = low = 0f;
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
            low = samples[samples.Count / 10];
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

            // How squarely this bearing faces the road. The near shore is graded
            // right back to the tarmac so there is no lip to hide behind; the far
            // shore keeps its short band and runs up whatever is behind it.
            float facing = 0f;
            if (d > 0.001f && lake.RoadDir.sqrMagnitude > 0.5f)
                facing = Mathf.Clamp01(
                    (Vector2.Dot(new Vector2(dx, dz) / d, lake.RoadDir) - 0.15f) / 0.85f);

            float roomToRoad = Mathf.Max(0f, lake.RoadGap - edge - RoadEdge);
            float band = Mathf.Lerp(ShoreBand, Mathf.Max(ShoreBand, roomToRoad), facing);
            if (d > edge + band) return orig;

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

            float u = (d - edge) / band;
            float sm = u * u * (3f - 2f * u);
            // The rim starts AT water level and rises from there. Starting it
            // higher walls off the waterline and hides the very edge that makes
            // the lake read as water -- and on the road side there is no rim at
            // all, because a bank thrown up between the rider and the water is
            // the whole problem restated.
            float rim = lake.WaterLevel +
                        BankHeight * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.5f))
                        * (1f - facing);
            float graded = Mathf.Lerp(rim, orig, sm);

            // Toward the road, only ever cut. Filling here would rebuild the lip
            // the grading exists to remove.
            return facing > 0.001f ? Mathf.Min(orig, graded) : graded;
        }

        /// <summary>How far back along the road the shore is opened up.</summary>
        const float ApproachLength = 360f;

        /// <summary>
        /// Walk the shoulder down between the road and the near shore, for the
        /// length of the approach rather than only where the lake is squarely
        /// abeam.
        ///
        /// Opening the section opposite the lake is not enough, and the reason is
        /// simply where the rider is looking. Level with the water it is 90 deg
        /// off the nose and out of frame entirely; the only time it can be seen
        /// is on the way in, at a grazing angle -- and that sightline runs the
        /// length of the shoulder, not across it. Carving one clean bowl and
        /// leaving the approach untouched produced a lake that measured as
        /// visible from 200 m back and rendered as a grass bank.
        /// </summary>
        static void OpenApproach(WorldSettings s, float[,] heights, int res, float texel,
                                 RoutePath route, LakeSite lake)
        {
            float nearShore = lake.RoadGap -
                RadiusAt(lake, Mathf.Atan2(-lake.RoadDir.y, -lake.RoadDir.x));
            if (nearShore <= RoadEdge + 4f) return;

            float step = Mathf.Max(texel * 0.5f, 2f);
            int cut = 0;
            for (float back = 0f; back <= ApproachLength; back += step)
            {
                float d = route.Wrap(lake.RouteDistance - back);
                Vector3 p = route.PositionAt(d);
                Vector3 f = route.ForwardAt(d, 8f);
                var flat = new Vector2(p.x, p.z);
                var perp = new Vector2(f.z, -f.x).normalized;
                if (Vector2.Dot(perp, (lake.Centre - flat).normalized) < 0f) perp = -perp;

                // Ease the cut out at the far end so the apron joins the hillside
                // instead of stopping at a step.
                float fade = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(ApproachLength * 0.55f, ApproachLength, back));
                if (fade <= 0.001f) continue;

                for (float off = RoadEdge; off <= nearShore; off += step)
                {
                    Vector2 q = flat + perp * off;
                    int xi = Mathf.RoundToInt(q.x / texel), zi = Mathf.RoundToInt(q.y / texel);
                    if (xi < 0 || zi < 0 || xi >= res || zi >= res) continue;

                    float orig = heights[zi, xi] * s.TerrainHeight;
                    // A ramp from the tarmac edge, where it takes nothing, down to
                    // the waterline at the shore.
                    float t = Mathf.InverseLerp(RoadEdge, nearShore, off);
                    float ramp = Mathf.Lerp(p.y, lake.WaterLevel, t * t * (3f - 2f * t));
                    float target = Mathf.Lerp(orig, ramp, fade);
                    if (target >= orig) continue;

                    heights[zi, xi] = Mathf.Clamp01(target / s.TerrainHeight);
                    cut++;
                }
            }
            Debug.Log($"[LakeGen]   opened the approach: {cut} samples over " +
                      $"{ApproachLength:F0} m out to a shore {nearShore:F0} m from the road");
        }

        /// <summary>Cut the basins into the heightmap, before it reaches Unity.</summary>
        public static void Carve(WorldSettings s, float[,] heights, List<LakeSite> lakes,
                                 RoutePath route = null)
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
                if (route != null) OpenApproach(s, heights, res, texel, route, lake);
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
