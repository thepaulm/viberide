using System.Collections.Generic;
using UnityEngine;

namespace KickrWorld
{
    public class WorldSettings
    {
        public float TerrainSize = 10000f;
        // Road tops out near 1105 m and peaks add ~1450 m on top, so the ceiling
        // must clear ~2600 m or Clamp01 shears the summits into flat mesas.
        public float TerrainHeight = 2800f;

        // 2049, not 4097. A 4097 heightmap is 16.8M samples, and between the CPU
        // copy, the GPU texture and Unity's LOD structures it crashed an 8 GB
        // MacBook Air on launch (data abort in __bzero, kernel reporting memory
        // shortage). It ran fine on a 65 GB desktop, which is exactly why it was
        // not caught here. 2049 is 4x cheaper and gives 4.9 m per texel, which is
        // plenty since the road is separate geometry.
        public int HeightmapResolution = 2049;   // must be 2^n + 1
        public int FieldResolution = 2049;       // road distance field, upsampled
        public float RouteRadiusFraction = 0.34f;

        // The road sits well above the valley floor so the ground has somewhere
        // to fall away to. Without this headroom the terrain clamps at zero and
        // you get flat basins instead of valleys.
        public float BaseElevation = 560f;

        // How far out the terrain is allowed to reach full independent relief.
        // Keep this tight: a wide corridor makes the landscape follow the road,
        // which renders a 545 m climb completely invisible because everything
        // around you rises with you.
        // Tight, because everything between the bench and this radius is a
        // relief-suppressed transition -- and that zone is most of what fills the
        // screen from the saddle. Too wide and every ride is down a green runway.
        public float CorridorRadius = 120f;

        /// <summary>
        /// Metres the ground falls away on the open side of the road by the edge
        /// of the corridor.
        ///
        /// The corridor used to flatten both sides to road level for 120 m, which
        /// made the whole world a shelf and, incidentally, made water impossible
        /// to see: sighting across flat ground of width A from height E, anything
        /// D below is hidden out to A(E+D)/E, and with A=40 m and an eye 2 m up
        /// that put a lake 25 m down out of sight until 540 m away. One side now
        /// drops instead, which is what a mountain road actually does.
        /// </summary>
        public float ValleyDrop = 32f;
        public float BenchRadius = 18f;          // fully flattened road bench

        // Terrain is cut slightly below the road surface. Discretisation of the
        // distance field means the ground would otherwise poke up through the
        // road ribbon on steep curves.
        public float BenchSink = 0.6f;

        public float RoadWidth = 7.5f;
        public int Seed = 20260816;

        /// <summary>Lap length to aim for, in metres. Zero keeps whatever the
        /// loop happens to measure at RouteRadiusFraction.</summary>
        public float TargetLengthM = 0f;

        /// <summary>Metres of climbing to aim for over the lap. Zero leaves it to
        /// the generator's own band. Capped by the gradient ceiling, so a large
        /// ask over a short lap lands short.</summary>
        public float TargetAscentM = 0f;
    }

    public static class WorldGen
    {
        // --- noise ----------------------------------------------------------

        static float Fbm(float x, float z, int octaves, float frequency, float lacunarity, float gain)
        {
            float sum = 0f, amp = 1f, norm = 0f, f = frequency;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * (Mathf.PerlinNoise(x * f, z * f) - 0.5f) * 2f;
                norm += amp;
                amp *= gain;
                f *= lacunarity;
            }
            return sum / Mathf.Max(norm, 0.0001f);
        }

        /// <summary>Ridged noise -- gives sharp mountain crests instead of blobs.</summary>
        static float Ridged(float x, float z, int octaves, float frequency, float lacunarity, float gain)
        {
            float sum = 0f, amp = 1f, norm = 0f, f = frequency;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs((Mathf.PerlinNoise(x * f, z * f) - 0.5f) * 2f);
                sum += amp * n * n;
                norm += amp;
                amp *= gain;
                f *= lacunarity;
            }
            return sum / Mathf.Max(norm, 0.0001f);
        }

        // --- route ----------------------------------------------------------

        /// <summary>Room left between the outermost point of the loop and the
        /// edge of the map: the road corridor, plus whatever a lake or a monument
        /// beside it might reach for.</summary>
        const float EdgeMargin = 460f;

        static float ArcLength(Vector2[] loop)
        {
            float arc = 0f;
            for (int i = 0; i < loop.Length; i++)
                arc += Vector2.Distance(loop[i], loop[(i + 1) % loop.Length]);
            return arc;
        }

        /// <summary>
        /// Find a course that climbs close to <paramref name="target"/> metres.
        ///
        /// Scaling the gradients of one course is not enough on its own. The
        /// generator already pushes its steepest feature near the 13% ceiling, so
        /// how far a given course can be scaled up depends on how steep it
        /// happened to come out -- measured across seeds, the reachable climbing
        /// ranged from 22 to 35 m/km for the same request. A slider cannot honour
        /// a promise like that.
        ///
        /// Climbing more over a fixed distance means spending more of the
        /// distance climbing, which is a property of the course rather than
        /// something a scale factor can produce. Rather than rewrite the
        /// generator, this asks it for several courses and keeps whichever comes
        /// closest. They are cheap -- no terrain is involved -- so a couple of
        /// dozen attempts cost nothing next to the heightmap that follows.
        ///
        /// The loop, and so the terrain, is untouched by this: it is keyed off
        /// the world seed and only the course varies.
        /// </summary>
        static CourseProfile FitAscent(int seed, float arc, float target, out float achieved)
        {
            CourseProfile best = null;
            float bestErr = float.MaxValue;
            achieved = 0f;

            for (int i = 0; i < 24; i++)
            {
                var p = CourseProfile.CreateRandom(seed + i * 7919);
                p.ScaleToLength(arc);
                float got = p.ScaleAscentTo(target);

                float err = Mathf.Abs(got - target);
                if (err < bestErr) { bestErr = err; best = p; achieved = got; }
                if (err <= target * 0.02f) break;
            }
            return best;
        }

        public static RoutePath BuildRoute(WorldSettings s)
        {
            var center = new Vector2(s.TerrainSize * 0.5f, s.TerrainSize * 0.5f);
            float radius = s.TerrainSize * s.RouteRadiusFraction;
            var loop = RoutePath.BuildLoop(center, radius, 4096, s.Seed);
            float arc = ArcLength(loop);

            if (s.TargetLengthM > 100f)
            {
                // Arc length is linear in radius, so one measurement gives the
                // radius needed exactly and there is nothing to search for.
                float k = s.TargetLengthM / arc;

                // The loop wanders up to about a third outside its nominal
                // radius, and the default already sits close to the edge of a
                // 10 km map -- so a longer lap has to grow the terrain rather
                // than run off it. The heightmap resolution is fixed, so this
                // costs no memory at all; it only makes each texel larger.
                float reach = 0f;
                foreach (var p in loop) reach = Mathf.Max(reach, Vector2.Distance(p, center));
                float needed = 2f * (reach * k + EdgeMargin);
                if (needed > s.TerrainSize) s.TerrainSize = needed;

                center = new Vector2(s.TerrainSize * 0.5f, s.TerrainSize * 0.5f);
                loop = RoutePath.BuildLoop(center, radius * k, 4096, s.Seed);
                arc = ArcLength(loop);
            }

            // Seed-driven, so Regenerate gives a genuinely different ride rather
            // than the same climbs draped over new scenery.
            //
            // Stretching the elevation profile onto the loop's true arc length
            // keeps the gradients and changes only the lengths, so the road never
            // has to pinch to fit.
            CourseProfile profile;
            if (s.TargetAscentM > 1f)
            {
                profile = FitAscent(s.Seed, arc, s.TargetAscentM, out float got);
                if (Mathf.Abs(got - s.TargetAscentM) > s.TargetAscentM * 0.05f)
                    Debug.LogWarning($"[WorldGen] asked for {s.TargetAscentM:F0} m of climbing " +
                                     $"over {arc / 1000f:F1} km, got {got:F0} m -- " +
                                     $"the {CourseProfile.MaxGrade * 100f:F0}% gradient ceiling is the limit.");
            }
            else
            {
                profile = CourseProfile.CreateRandom(s.Seed);
                profile.ScaleToLength(arc);
            }

            Debug.Log($"[WorldGen] lap {arc / 1000f:F2} km, {profile.TotalAscent:F0} m of climbing, " +
                      $"terrain {s.TerrainSize / 1000f:F1} km square, " +
                      $"{s.TerrainSize / (s.HeightmapResolution - 1):F1} m per texel");

            return new RoutePath(loop, profile, s.BaseElevation);
        }

        // --- road distance field -------------------------------------------

        /// <summary>
        /// Scatter the route into a grid of (distance to road, road elevation).
        /// Scatter rather than gather: stamping ~1500 route samples into a local
        /// neighbourhood is orders of magnitude cheaper than asking every one of
        /// four million texels which road point is nearest.
        /// </summary>
        /// <summary>
        /// Distance to the road, the road elevation there, and which side of it
        /// each texel lies on.
        ///
        /// The side field carries a signed value in roughly [-1, 1]: positive
        /// where the ground should fall away, negative on the side that keeps its
        /// hillside, zero along the road and wherever the two swap over. The swap
        /// is driven by a slow noise of world position, so the drop wanders from
        /// one side of the road to the other over kilometres instead of pinning
        /// itself to the left for the whole lap.
        /// </summary>
        public static void BuildRoadField(WorldSettings s, RoutePath route,
                                          out float[,] dist, out float[,] elev,
                                          out float[,] side)
        {
            int res = s.FieldResolution;
            float texel = s.TerrainSize / (res - 1);
            dist = new float[res, res];
            elev = new float[res, res];
            side = new float[res, res];

            float far = s.CorridorRadius * 2f;
            for (int i = 0; i < res; i++)
                for (int j = 0; j < res; j++)
                    dist[i, j] = far;

            int stampRadius = Mathf.CeilToInt(s.CorridorRadius / texel);
            float step = Mathf.Max(texel * 0.75f, 6f);
            int samples = Mathf.CeilToInt(route.Length / step);

            for (int k = 0; k < samples; k++)
            {
                float d = k * step;
                Vector2 p = route.HorizontalAt(d);
                float e = route.ElevationAt(d);

                Vector3 fwd = route.ForwardAt(d, 8f);
                var lateral = new Vector2(fwd.z, -fwd.x).normalized;
                // Which way the ground falls here. ~1.6 km wavelength, so a given
                // stretch of road keeps its drop on one side long enough to read
                // as landscape rather than as noise.
                // Saturated hard on purpose. At a gentle gain the noise spends
                // most of its time near zero, and measurement showed only 28% of
                // the corridor getting a decisive side -- so half the road had no
                // fall-off at all. Multiplying up makes the drop commit to one
                // side and reserves the smooth part for the crossings.
                float bias = Mathf.Clamp(Fbm(p.x + 4200f, p.y - 1700f, 2, 0.0004f, 2f, 0.5f) * 5f,
                                         -1f, 1f);

                int cx = Mathf.RoundToInt(p.x / texel);
                int cz = Mathf.RoundToInt(p.y / texel);

                for (int dz = -stampRadius; dz <= stampRadius; dz++)
                {
                    int z = cz + dz;
                    if (z < 0 || z >= res) continue;
                    for (int dx = -stampRadius; dx <= stampRadius; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= res) continue;

                        float wx = x * texel - p.x;
                        float wz = z * texel - p.y;
                        float dd = Mathf.Sqrt(wx * wx + wz * wz);
                        if (dd < dist[z, x])
                        {
                            dist[z, x] = dd;
                            elev[z, x] = e;
                            // Signed offset across the road, saturating at 40 m so
                            // the two sides separate quickly, times the local bias.
                            float across = wx * lateral.x + wz * lateral.y;
                            side[z, x] = Mathf.Clamp(across / 40f, -1f, 1f) * bias;
                        }
                    }
                }
            }

            // How much of the corridor actually has a side. If this is small the
            // shelf can never bite, and no amount of staring at screenshots will
            // say whether the cause is the bias, the field, or the profile.
            int strong = 0, inside = 0;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    if (dist[z, x] >= s.CorridorRadius) continue;
                    inside++;
                    if (Mathf.Abs(side[z, x]) > 0.5f) strong++;
                }
            Debug.Log($"[WorldGen] side field: {100f * strong / Mathf.Max(1, inside):F0}% of the " +
                      $"corridor has |side| > 0.5 ({inside} texels inside)");
        }

        /// <summary>
        /// A smooth elevation trend covering the WHOLE map, derived from the route.
        ///
        /// This exists because the road distance field is only populated near the
        /// road. Using it as the terrain base everywhere puts the rest of the map
        /// at zero, which clamps a quarter of the terrain flat. The landscape needs
        /// a base elevation that is defined everywhere and still agrees with the
        /// road where they meet.
        ///
        /// Inverse-distance weighting gives that directly: near the road it tends
        /// to the local road height, far away it blends between the nearer parts of
        /// the loop, and it is smooth by construction so no separate blur is needed.
        /// Because it is a smoothed trend, summits sit above it and valleys below --
        /// which is exactly the "you are up on a pass" look we want.
        /// </summary>
        public static float[,] BuildRegionalField(WorldSettings s, RoutePath route, int res = 128)
        {
            int samples = 1400;
            var pts = new Vector2[samples];
            var els = new float[samples];
            for (int k = 0; k < samples; k++)
            {
                float d = (k / (float)samples) * route.Length;
                pts[k] = route.HorizontalAt(d);
                els[k] = route.ElevationAt(d);
            }

            var field = new float[res, res];
            float texel = s.TerrainSize / (res - 1);
            // Softening term stops the weight blowing up on top of a sample and
            // sets the scale over which the trend stays local (~200 m).
            const float soften = 200f * 200f;

            for (int z = 0; z < res; z++)
            {
                float wz = z * texel;
                for (int x = 0; x < res; x++)
                {
                    float wx = x * texel;
                    float wsum = 0f, esum = 0f;
                    for (int k = 0; k < samples; k++)
                    {
                        float dx = wx - pts[k].x, dz = wz - pts[k].y;
                        float d2 = dx * dx + dz * dz + soften;
                        float w = 1f / (d2 * d2);   // inverse 4th power -> nearby road dominates
                        wsum += w;
                        esum += els[k] * w;
                    }
                    field[z, x] = esum / wsum;
                }
            }
            return field;
        }

        static float Sample(float[,] grid, int res, float u, float v)
        {
            float fx = Mathf.Clamp(u * (res - 1), 0f, res - 1.001f);
            float fz = Mathf.Clamp(v * (res - 1), 0f, res - 1.001f);
            int x0 = (int)fx, z0 = (int)fz;
            int x1 = Mathf.Min(x0 + 1, res - 1), z1 = Mathf.Min(z0 + 1, res - 1);
            float tx = fx - x0, tz = fz - z0;
            float a = Mathf.Lerp(grid[z0, x0], grid[z0, x1], tx);
            float b = Mathf.Lerp(grid[z1, x0], grid[z1, x1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        // --- heightmap ------------------------------------------------------

        /// <summary>
        /// Builds the heightmap a slice of rows at a time.
        ///
        /// Exists so the runtime Regenerate can spread the work across frames.
        /// Doing it in one call freezes the app for several seconds on a laptop,
        /// and the noise evaluation is far too large to hide.
        /// </summary>
        public class HeightmapBuilder
        {
            public readonly float[,] Heights;
            public readonly int Rows;
            public int Done { get; private set; }
            public bool Complete => Done >= Rows;
            public float Progress => Rows == 0 ? 1f : Done / (float)Rows;

            readonly WorldSettings _s;
            readonly float[,] _dist, _elev, _side, _regional;
            readonly int _regRes, _fres, _res;
            readonly float _ox, _oz, _invSize, _texel;

            public HeightmapBuilder(WorldSettings s, RoutePath route,
                                    float[,] distField, float[,] elevField,
                                    float[,] sideField, int regRes = 128)
            {
                _s = s;
                _dist = distField;
                _elev = elevField;
                _side = sideField;
                _regRes = regRes;
                _regional = BuildRegionalField(s, route, regRes);

                _res = s.HeightmapResolution;
                _fres = s.FieldResolution;
                Rows = _res;
                Heights = new float[_res, _res];

                var rng = new System.Random(s.Seed);
                _ox = (float)rng.NextDouble() * 1000f;
                _oz = (float)rng.NextDouble() * 1000f;
                _invSize = 1f / s.TerrainSize;
                _texel = s.TerrainSize / (_res - 1);
            }

            /// <summary>Process up to <paramref name="rows"/> more rows.</summary>
            public void Step(int rows)
            {
                int end = Mathf.Min(Done + rows, Rows);
                FillRows(_s, Heights, _dist, _elev, _side, _regional, _regRes,
                         _fres, _res, _ox, _oz, _invSize, _texel, Done, end);
                Done = end;
            }

            public void Finish() => Step(Rows - Done);
        }

        public static float[,] BuildHeightmap(WorldSettings s, RoutePath route,
                                              float[,] distField, float[,] elevField,
                                              float[,] sideField)
        {
            var builder = new HeightmapBuilder(s, route, distField, elevField, sideField);
            builder.Finish();
            return builder.Heights;
        }

        static void FillRows(WorldSettings s, float[,] heights,
                             float[,] distField, float[,] elevField, float[,] sideField,
                             float[,] regionalField, int regRes,
                             int fres, int res, float ox, float oz,
                             float invSize, float texel, int zStart, int zEnd)
        {
            for (int z = zStart; z < zEnd; z++)
            {
                float wz = z * texel;
                for (int x = 0; x < res; x++)
                {
                    float wx = x * texel;
                    float u = wx * invSize, v = wz * invSize;

                    float d = Sample(distField, fres, u, v);
                    float roadE = Sample(elevField, fres, u, v);

                    // How much this point is on the falling side, 0..1.
                    float valley = Mathf.Max(0f, Sample(sideField, fres, u, v));

                    // The bench is the flat platform carrying the road, and its
                    // outer edge is the lip that decides what can be seen from the
                    // saddle. On the open side it is pulled in hard -- 40 m of
                    // level ground beside you hides everything below it for
                    // hundreds of metres, and shrinking the lip is worth more than
                    // any amount of digging further out.
                    // The lip distance A is the single number that decides how far
                    // down the rider can see: the steepest depression available is
                    // eye height over A, whatever happens further out. At the old
                    // 40 m that is 3 degrees; at 15 m it is 7.5; at 9 m it is 13,
                    // which is enough to look into a valley. So the open side gets
                    // a shoulder of a few metres and then falls, which is what a
                    // mountain road has.
                    float benchInner = Mathf.Lerp(s.BenchRadius, s.BenchRadius * 0.30f, valley);
                    float benchEdge = Mathf.Lerp(s.BenchRadius * 2.2f, s.BenchRadius * 0.55f, valley);

                    // Flat bench right at the road, fading out quickly.
                    float bench = 1f - Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01((d - benchInner) / (benchEdge - benchInner)));
                    // Relief grows in from the bench edge to full strength.
                    float reliefAmp = Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(benchEdge, s.CorridorRadius, d));

                    // Ground falling away past the bench on the open side, easing
                    // back into the natural landscape well beyond the corridor so
                    // there is no step where the two meet.
                    float descent = Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(benchEdge, s.CorridorRadius, d));
                    // Mathf.SmoothStep(a, b, t) interpolates BETWEEN a and b by t;
                    // it is not the GLSL smoothstep(edge0, edge1, x). Passing the
                    // distance as t returned 288 for every point on the map, so
                    // easeOut was about -287, Lerp clamped that to zero, and the
                    // shelf silently never applied -- three different bias values
                    // in a row produced byte-identical terrain.
                    // Full depth is held out to twice the corridor radius before
                    // easing back into natural ground. The shelf has to be wide
                    // enough to hold a lake at the distance the sightline needs:
                    // clearing a lip at A with an eye E above it puts the nearest
                    // visible water at A(E+D)/E, which for a 10 m lip and a 30 m
                    // drop is 160 m out. A shelf that has already faded by then is
                    // no use to anything standing on it.
                    float easeOut = 1f - Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(s.CorridorRadius, s.CorridorRadius * 2.4f, d));
                    float shelf = roadE - s.ValleyDrop * descent;

                    float nx = wx + ox, nz = wz + oz;

                    // Four scales, largest to smallest. Terrain that only has
                    // low frequencies reads as smooth green pudding no matter
                    // how tall it is -- the mid and fine octaves are what make
                    // it look like rock rather than fabric.

                    // Regional: broad highlands and basins, ~4 km wavelength.
                    float regional = Fbm(nx + 3000f, nz - 2000f, 3, 0.00022f, 2.0f, 0.5f);

                    // Mask kept broad and smooth so crests form connected ranges.
                    // A tight mask produces isolated cones sitting on a plain.
                    float mask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.45f, 0.35f, regional));

                    // Fewer octaves and a faster falloff than feels natural to
                    // write: ridged noise carries a lot of high-frequency energy
                    // and stacking it produces needle spikes rather than peaks.
                    float crests = Ridged(nx, nz, 5, 0.00042f, 2.07f, 0.48f);
                    float peaks = crests * crests * 1450f * mask;

                    // Gain above 0.5 deliberately, to keep energy in the 100-300 m
                    // band. That is the scale the rider actually sees from the
                    // saddle, and without it the roadside reads as a flat lawn.
                    float mid = Fbm(nx + 811f, nz - 517f, 5, 0.0016f, 2.11f, 0.62f) * 90f;
                    float fine = Fbm(nx - 233f, nz + 97f, 3, 0.0075f, 2.0f, 0.5f) * 11f;
                    // ~30 m wavelength, just above what a 2.44 m heightmap texel
                    // can resolve. Stops slopes reading as smooth moulded plastic.
                    float grain = Fbm(nx + 57f, nz - 91f, 2, 0.033f, 2.0f, 0.5f) * 3.5f;

                    // Signed, with a negative bias so the average ground sits
                    // below road level and the land falls away from the route.
                    float relief = regional * 300f + peaks + mid + fine + grain - 120f;

                    // Landscape is built on the regional trend, which is defined
                    // across the whole map. Near the road we blend to the exact
                    // road elevation so the two always meet cleanly.
                    float landscape = Sample(regionalField, regRes, u, v) + relief;
                    float h = Mathf.Lerp(roadE, landscape, reliefAmp);

                    // On the open side the shelf is a CEILING, not a subtraction.
                    // Subtracting a fixed drop from a hillside that is already
                    // rising still leaves a hillside -- and because the bench edge
                    // is pulled in on this side, the relief blended in from 15 m
                    // instead of 40 and built a wall right at the rider's elbow.
                    // Capping instead means the ground can only ever fall away
                    // here, and where it is naturally lower it is left alone.
                    if (valley > 0.001f)
                        h = Mathf.Lerp(h, Mathf.Min(h, shelf), valley * easeOut);

                    h = Mathf.Lerp(h, roadE - s.BenchSink, bench);

                    heights[z, x] = Mathf.Clamp01(h / s.TerrainHeight);
                }
            }
        }

        // --- splatmap -------------------------------------------------------

        /// <summary>
        /// Four layers: asphalt near the road, grass on gentle ground, rock on
        /// anything steep, snow up high. Driven by slope and altitude so the
        /// mountains read correctly without hand painting.
        /// </summary>
        public static float[,,] BuildSplatmap(WorldSettings s, TerrainData data,
                                              float[,] distField, int res = 512,
                                              System.Collections.Generic.List<LakeSite> lakes = null)
        {
            int fres = s.FieldResolution;
            var map = new float[res, res, 4];

            for (int z = 0; z < res; z++)
            {
                float v = z / (float)(res - 1);
                for (int x = 0; x < res; x++)
                {
                    float u = x / (float)(res - 1);

                    float height = data.GetInterpolatedHeight(u, v);
                    Vector3 normal = data.GetInterpolatedNormal(u, v);
                    float slope = 1f - Mathf.Clamp01(normal.y);
                    float d = Sample(distField, fres, u, v);

                    // The road terrain layer is deliberately left unpainted.
                    //
                    // The road is separate mesh geometry sitting proud of the
                    // ground, so painting asphalt underneath it achieves nothing
                    // visible -- and it actively hurts: the layer's texture
                    // carries lane markings, and a terrain layer tiles every 8 m,
                    // so any weight at all scatters white lines across the
                    // landscape. The baked build never painted it (measured: max
                    // weight 0.000) and looks correct, which is what tipped this
                    // off when a runtime regenerate painted it properly and the
                    // stripes appeared.
                    //
                    // The layer itself stays so the alphamap keeps four channels
                    // and matches the baked asset's shape.
                    const float road = 0f;

                    // Threshold starts at ~29 deg, not 37. Most mountainside here
                    // sits in the 30-40 deg band, and a higher threshold leaves it
                    // rendering as grass with a faint grey wash instead of rock.
                    float rockSlope = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.12f, 0.38f, slope));
                    // Bare rock above the treeline regardless of steepness.
                    float rockAlt = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1100f, 1500f, height));
                    float rock = Mathf.Clamp01(Mathf.Max(rockSlope, rockAlt * 0.85f));

                    // Snow line above the highest road point (~1105 m) so summits
                    // read as high country without icing the route. Scaled down on
                    // steep faces -- snow does not sit on a cliff.
                    float snow = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1250f, 1650f, height))
                                 * (1f - Mathf.Clamp01(slope * 1.6f));
                    float grass = Mathf.Max(0f, 1f - rock - snow);

                    // Road wins outright where it exists.
                    grass *= 1f - road; rock *= 1f - road; snow *= 1f - road;

                    float sum = grass + rock + snow + road;
                    if (sum < 0.0001f) { grass = 1f; sum = 1f; }

                    map[z, x, 0] = grass / sum;
                    map[z, x, 1] = rock / sum;
                    map[z, x, 2] = snow / sum;
                    map[z, x, 3] = road / sum;
                }
            }
            PaintLakeShores(s, map, res, lakes);
            return map;
        }

        /// <summary>
        /// Shingle around the waterline. Grass running straight into water looks
        /// like a flooded lawn; a band of rock reads as a shore and, incidentally,
        /// disguises the seam where the water mesh tucks under the bank.
        /// </summary>
        static void PaintLakeShores(WorldSettings s, float[,,] map, int res,
                                    System.Collections.Generic.List<LakeSite> lakes)
        {
            if (lakes == null || lakes.Count == 0) return;
            float step = s.TerrainSize / (res - 1);

            foreach (var lake in lakes)
            {
                float outer = LakeGen.Extent(lake) + 10f;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((lake.Centre.x - outer) / step));
                int x1 = Mathf.Min(res - 1, Mathf.CeilToInt((lake.Centre.x + outer) / step));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((lake.Centre.y - outer) / step));
                int z1 = Mathf.Min(res - 1, Mathf.CeilToInt((lake.Centre.y + outer) / step));

                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x * step - lake.Centre.x;
                        float dz = z * step - lake.Centre.y;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        float edge = LakeGen.RadiusAt(lake, Mathf.Atan2(dz, dx));

                        // From a little inside the waterline out to the top of the
                        // bank, faded so the band has no hard outer border.
                        float band = (d - (edge - 12f)) / 46f;
                        if (band < 0f || band > 1f) continue;
                        float strength = 1f - Mathf.SmoothStep(0f, 1f, band);

                        map[z, x, 0] *= 1f - strength;
                        map[z, x, 1] = map[z, x, 1] * (1f - strength) + strength;
                        map[z, x, 2] *= 1f - strength;

                        float sum = map[z, x, 0] + map[z, x, 1] + map[z, x, 2] + map[z, x, 3];
                        if (sum > 0.0001f)
                            for (int l = 0; l < 4; l++) map[z, x, l] /= sum;
                    }
            }
        }

        // --- road ribbon ----------------------------------------------------

        /// <summary>
        /// A ribbon mesh following the route. Built as its own geometry rather
        /// than painted on the terrain so it stays crisp regardless of heightmap
        /// resolution, and so it can sit a few centimetres proud of the ground.
        /// </summary>
        public static Mesh BuildRoadMesh(WorldSettings s, RoutePath route, float step = 4f, float lift = 0.25f)
        {
            int segments = Mathf.Max(16, Mathf.CeilToInt(route.Length / step));
            float half = s.RoadWidth * 0.5f;

            var verts = new List<Vector3>(segments * 2 + 2);
            var uvs = new List<Vector2>(segments * 2 + 2);
            var norms = new List<Vector3>(segments * 2 + 2);
            var tris = new List<int>(segments * 6);

            Vector3 origin = route.PositionAt(0f);

            for (int i = 0; i <= segments; i++)
            {
                float d = (i / (float)segments) * route.Length;
                Vector3 p = route.PositionAt(d);
                Vector3 fwd = route.ForwardAt(d, 6f);
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 up = Vector3.Cross(fwd, right).normalized;

                Vector3 lifted = p + Vector3.up * lift;
                verts.Add(lifted - right * half - origin);
                verts.Add(lifted + right * half - origin);
                norms.Add(up); norms.Add(up);

                // V runs in metres so the centre line repeats at a fixed spacing
                // no matter how long the course is.
                float vCoord = d / 8f;
                uvs.Add(new Vector2(0f, vCoord));
                uvs.Add(new Vector2(1f, vCoord));

                if (i < segments)
                {
                    int b = i * 2;
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
                }
            }

            var mesh = new Mesh { name = "RoadRibbon", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Vector3 RoadMeshOrigin(RoutePath route) => route.PositionAt(0f);
    }
}
