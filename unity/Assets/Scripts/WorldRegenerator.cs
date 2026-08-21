using System.Collections;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Rebuilds the whole world at runtime from a new seed: route, terrain
    /// heightmap, splatmap and road mesh.
    ///
    /// The generation code lives in Scripts rather than Editor precisely so it can
    /// run here. The heavy part is the heightmap, which is spread across frames by
    /// HeightmapBuilder -- doing it in one call locks the app up for seconds with
    /// no way to draw a progress indicator.
    /// </summary>
    public class WorldRegenerator : MonoBehaviour
    {
        [Header("Wiring")]
        public RideWorld World;
        public Terrain Terrain;
        public MeshFilter RoadMeshFilter;
        public BikeRider Rider;
        public PropScatter Scatter;
        public HilltopStatue Statue;
        public LakeSurfaces Water;

        [Tooltip("Heightmap rows per frame. Lower is smoother but slower overall.")]
        public int RowsPerFrame = 48;

        public bool Busy { get; private set; }
        public float Progress { get; private set; }
        public string Stage { get; private set; } = "";

        Mesh _roadMesh;

        /// <summary>
        /// Spatial stats for the road layer. The mean alone is useless here: a
        /// narrow strip at full strength and a faint wash over everything give
        /// the same average but look completely different on screen.
        /// </summary>
        public static void LogRoadLayer(string label, float[,,] map)
        {
            int h = map.GetLength(0), w = map.GetLength(1);
            float max = 0f;
            int strong = 0, faint = 0;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    float v = map[z, x, 3];
                    if (v > max) max = v;
                    if (v > 0.5f) strong++;
                    else if (v > 0.02f) faint++;
                }
            float n = h * w;
            Debug.Log($"[WorldRegenerator] road layer {label}: max {max:F3}, " +
                      $"strong(>0.5) {100f * strong / n:F2}%, faint(0.02-0.5) {100f * faint / n:F2}%");
        }

        void Start()
        {
            // Baseline from the baked asset, for comparison after a regenerate.
            if (Terrain != null)
            {
                var d = Terrain.terrainData;
                LogRoadLayer("baked", d.GetAlphamaps(0, 0, d.alphamapResolution, d.alphamapResolution));
            }

            // -regenerate [seed] kicks a rebuild off at launch, so the runtime
            // path can be exercised without a human clicking the menu.
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-regenerate");
            if (i < 0) return;

            int seed = 987654;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed)) seed = parsed;
            Regenerate(seed);
        }

        public void Regenerate() => Regenerate(Random.Range(1, int.MaxValue));

        public void Regenerate(int seed)
        {
            if (Busy) return;
            StartCoroutine(RegenerateRoutine(seed));
        }

        IEnumerator RegenerateRoutine(int seed)
        {
            Busy = true;
            Progress = 0f;
            Stage = "Preparing";
            float t0 = Time.realtimeSinceStartup;

            // Let the overlay draw one frame before anything blocking starts.
            yield return null;

            var settings = World.ToSettings();
            settings.Seed = seed;

            Stage = "Building route";
            yield return null;
            var route = WorldGen.BuildRoute(settings);
            Progress = 0.05f;

            Stage = "Mapping road corridor";
            yield return null;
            WorldGen.BuildRoadField(settings, route, out var distField, out var elevField);
            Progress = 0.15f;

            Stage = "Sculpting terrain";
            yield return null;
            var builder = new WorldGen.HeightmapBuilder(settings, route, distField, elevField);
            while (!builder.Complete)
            {
                builder.Step(Mathf.Max(4, RowsPerFrame));
                Progress = 0.15f + 0.6f * builder.Progress;
                yield return null;
            }

            // Lakes are cut into the heightmap, so they have to be planned and
            // carved while it is still a plain array. Once SetHeights has run the
            // basin would have to be dug out of live TerrainData instead.
            Stage = "Filling lakes";
            yield return null;
            var lakes = LakeGen.Plan(settings, route, builder.Heights);
            LakeGen.Carve(settings, builder.Heights, lakes);

            Stage = "Applying terrain";
            yield return null;
            var data = Terrain.terrainData;
            data.SetHeights(0, 0, builder.Heights);
            // Push the CPU heightmap to the GPU copy. Without this the terrain's
            // derived data stays out of step with what we just wrote.
            data.SyncHeightmap();
            Progress = 0.8f;

            Stage = "Painting ground";
            yield return null;
            int splatRes = data.alphamapResolution;
            var splat = WorldGen.BuildSplatmap(settings, data, distField, splatRes, lakes);
            data.SetAlphamaps(0, 0, splat);

            // Editing a TerrainData in place at runtime leaves the basemap -- the
            // low-resolution composite Unity uses for distant terrain -- built
            // from the previous splatmap. Left stale it smeared the road layer's
            // painted lines across the whole landscape, even though the alphamap
            // itself was byte-for-byte what the baked build produces.
            data.SetBaseMapDirty();
            Terrain.Flush();

            // Coverage, so a runtime regenerate can be compared against the baked
            // build. If the road layer is bigger here than the ~0.5% the editor
            // reports, the splat is wrong; if it matches, any oddity is rendering.
            {
                double g = 0, r = 0, sn = 0, rd = 0;
                for (int z = 0; z < splatRes; z++)
                    for (int x = 0; x < splatRes; x++)
                    {
                        g += splat[z, x, 0]; r += splat[z, x, 1];
                        sn += splat[z, x, 2]; rd += splat[z, x, 3];
                    }
                double n = (double)splatRes * splatRes;
                Debug.Log($"[WorldRegenerator] splat coverage: grass {100 * g / n:F1}%  " +
                          $"rock {100 * r / n:F1}%  snow {100 * sn / n:F1}%  road {100 * rd / n:F1}%");
            }
            LogRoadLayer("regenerated", splat);
            Progress = 0.92f;

            Stage = "Laying road";
            yield return null;
            var mesh = WorldGen.BuildRoadMesh(settings, route);
            if (_roadMesh != null) Destroy(_roadMesh);
            _roadMesh = mesh;
            RoadMeshFilter.sharedMesh = mesh;
            // The mesh is built around its own origin, so the object has to move
            // with it or the road ends up somewhere else entirely.
            RoadMeshFilter.transform.position = WorldGen.RoadMeshOrigin(route);
            Progress = 0.98f;

            World.ApplyRoute(route, seed);

            if (Scatter != null)
            {
                Stage = "Placing scenery";
                yield return null;
                // After the terrain is in place: scatter samples ground height and
                // slope, so doing it earlier would plant everything on the old
                // landscape.
                Scatter.Rebuild(route, seed);
            }

            if (Water != null)
            {
                // After the terrain is live, so the water sits on the basin that
                // actually exists rather than the one that was about to.
                Stage = "Launching boats";
                yield return null;
                Water.Rebuild(lakes, seed);
            }

            if (Statue != null)
            {
                // After the terrain, for the same reason as the scenery: the
                // summit search reads heights straight off the Terrain, so running
                // it any earlier finds a peak on the landscape we just replaced.
                Stage = "Raising the monument";
                yield return null;
                Statue.Rebuild(route, seed);
            }

            // A new world is a new ride: distance, climbing and clock all restart.
            if (Rider != null) Rider.ResetRide();

            Progress = 1f;
            Stage = "Done";
            Busy = false;

            var p = route.Profile;
            Debug.Log($"[WorldRegenerator] seed {seed}: {route.Length / 1000f:F2} km lap, " +
                      $"{p.TotalAscent:F0} m ascent, net {p.NetElevation:F1} m " +
                      $"({Time.realtimeSinceStartup - t0:F1}s)");
        }
    }
}
