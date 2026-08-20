using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Generates the whole ride scene from script: terrain, road, bike, camera,
    /// lighting and wiring. Everything is reproducible from WorldSettings, so
    /// the scene is a build artefact rather than something hand-assembled that
    /// has to be kept in sync by hand.
    /// </summary>
    public static class WorldBuilder
    {
        const string GenDir = "Assets/Generated";
        const string SceneDir = "Assets/Scenes";
        const string ScenePath = SceneDir + "/Ride.unity";

        /// <summary>
        /// Diagnostic: paint the whole terrain with layer 1 (rock). If the render
        /// still comes out green, only layer 0 is reaching the screen and the
        /// problem is the rendering path, not the splat data.
        /// </summary>
        public static bool DebugAllRock;

        public static void BuildAllRockFromCommandLine()
        {
            DebugAllRock = true;
            BuildFromCommandLine();
        }

        [MenuItem("VibeRide/Build World Scene")]
        public static void BuildWorld()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Delete and recreate the generated assets rather than overwriting
            // them in place. Repeatedly calling CreateAsset over the same paths
            // leaves metadata the editor still resolves from its cache but the
            // player cannot, which shows up as a built player crashing with
            // "level0 is corrupted" while the very same scene opens fine in the
            // editor. Regenerating from clean costs a few seconds and makes the
            // build reproducible.
            if (AssetDatabase.IsValidFolder(GenDir))
            {
                Log($"Clearing {GenDir} for a clean regenerate...");
                AssetDatabase.DeleteAsset(GenDir);
            }
            if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
            AssetDatabase.Refresh();

            Directory.CreateDirectory(GenDir);
            Directory.CreateDirectory(SceneDir);
            AssetDatabase.Refresh();

            var settings = new WorldSettings();

            Log("Building route...");
            var route = WorldGen.BuildRoute(settings);
            var profile = route.Profile;
            Log($"  lap {route.Length / 1000f:F2} km, ascent {profile.TotalAscent:F0} m, " +
                $"net {profile.NetElevation:F2} m, steepest {profile.MaxAbsGrade() * 100f:F1}%, " +
                $"{profile.Segments.Count} segments");
            foreach (var name in DistinctSegmentNames(profile))
                Log($"    - {name}");

            Log("Building road distance field...");
            WorldGen.BuildRoadField(settings, route, out var distField, out var elevField);

            Log("Building heightmap...");
            var heights = WorldGen.BuildHeightmap(settings, route, distField, elevField);
            LogHeightStats(heights, settings);
            LogRoadGrade(route);
            LogTransect(heights, settings, route);

            Log("Creating terrain...");
            var data = new TerrainData { name = "RideTerrain" };
            // Resolution must be set before size -- assigning it resets the size.
            data.heightmapResolution = settings.HeightmapResolution;
            data.size = new Vector3(settings.TerrainSize, settings.TerrainHeight, settings.TerrainSize);
            data.SetHeights(0, 0, heights);

            // Resolution before layers: assigning it afterwards reallocates the
            // alphamap and discards what was just painted.
            // 512 rather than 1024 -- a quarter of the memory, and across 10 km
            // the splat resolution was never what limited how this looks.
            data.alphamapResolution = 512;
            data.terrainLayers = BuildLayers();

            foreach (var l in data.terrainLayers)
                Log($"  layer '{l.name}': diffuse={(l.diffuseTexture == null ? "NULL" : l.diffuseTexture.name)} " +
                    $"tile={l.tileSize.x:F0}");

            Log("Painting splatmap...");
            var splat = WorldGen.BuildSplatmap(settings, data, distField, 512);
            if (DebugAllRock)
            {
                Log("  DEBUG: overriding splatmap to 100% rock");
                for (int z = 0; z < 1024; z++)
                    for (int x = 0; x < 1024; x++)
                    {
                        splat[z, x, 0] = 0f; splat[z, x, 1] = 1f;
                        splat[z, x, 2] = 0f; splat[z, x, 3] = 0f;
                    }
            }
            LogSplatStats(splat);
            data.SetAlphamaps(0, 0, splat);

            // Read it straight back out. This separates "we computed the right
            // weights" from "the right weights are actually stored on the asset",
            // which is the difference between a maths bug and a plumbing bug.
            var readback = data.GetAlphamaps(0, 0, data.alphamapResolution, data.alphamapResolution);
            Log("  readback from TerrainData:");
            LogSplatStats(readback);

            AssetDatabase.CreateAsset(data, $"{GenDir}/RideTerrain.asset");

            Log("Building road mesh...");
            var roadMesh = WorldGen.BuildRoadMesh(settings, route);
            AssetDatabase.CreateAsset(roadMesh, $"{GenDir}/RoadRibbon.asset");
            Log($"  road mesh: {roadMesh.vertexCount} verts");

            AssetDatabase.SaveAssets();

            // --- assemble the scene ---
            Log("Assembling scene...");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var terrainGo = Terrain.CreateTerrainGameObject(data);
            terrainGo.name = "Terrain";

            // Drop the collider. The bike's position comes from RoutePath maths,
            // not from raycasting the ground, so nothing ever collides with the
            // terrain -- but PhysX still cooks a heightfield of every sample and
            // holds it for the whole session. Pure waste, and on a large terrain
            // it is one of the biggest allocations in the scene.
            var collider = terrainGo.GetComponent<TerrainCollider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
                Log("  removed TerrainCollider (nothing collides with the terrain)");
            }

            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.heightmapPixelError = 4f;
            terrain.basemapDistance = 2000f;
            terrain.detailObjectDistance = 200f;

            // Assign an explicit terrain material asset. Relying on Unity's
            // built-in default works in the editor, but a built player showed the
            // terrain rendering as a single layer -- consistent with the terrain
            // shader's multi-layer variants being stripped because nothing in the
            // build referenced them. A real material asset in the scene is a hard
            // reference, so the variants survive.
            terrain.materialTemplate = MakeTerrainMaterial();

            var roadGo = new GameObject("Road");
            roadGo.transform.position = WorldGen.RoadMeshOrigin(route);
            var roadFilter = roadGo.AddComponent<MeshFilter>();
            roadFilter.sharedMesh = roadMesh;
            roadGo.AddComponent<MeshRenderer>().sharedMaterial = MakeRoadMaterial();

            var world = new GameObject("World");
            var rideWorld = world.AddComponent<RideWorld>();
            rideWorld.TerrainSize = settings.TerrainSize;
            rideWorld.TerrainHeight = settings.TerrainHeight;
            rideWorld.RouteRadiusFraction = settings.RouteRadiusFraction;
            rideWorld.BaseElevation = settings.BaseElevation;
            rideWorld.RoadWidth = settings.RoadWidth;
            rideWorld.Seed = settings.Seed;

            var link = world.AddComponent<TrainerLink>();
            var launcher = world.AddComponent<BridgeLauncher>();
            launcher.Link = link;
            var rider = world.AddComponent<BikeRider>();
            rider.Link = link;

            var bike = BuildBikeProxy();
            bike.transform.position = route.PositionAt(0f);
            rider.Bike = bike.transform;

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 9000f;
            cam.nearClipPlane = 0.15f;
            cam.fieldOfView = 62f;
            camGo.AddComponent<AudioListener>();
            var chase = camGo.AddComponent<ChaseCamera>();
            chase.Target = bike.transform;

            var hud = world.AddComponent<RideHud>();
            hud.Rider = rider;
            hud.Link = link;
            hud.World = rideWorld;
            hud.Launcher = launcher;

            var regen = world.AddComponent<WorldRegenerator>();
            regen.World = rideWorld;
            regen.Terrain = terrain;
            regen.RoadMeshFilter = roadFilter;
            regen.Rider = rider;

            var menu = world.AddComponent<RideMenu>();
            menu.Regenerator = regen;
            menu.World = rideWorld;
            hud.Menu = menu;

            var flyby = world.AddComponent<PlaneFlyby>();
            flyby.Rider = rider;
            flyby.Terrain = terrain;

            var shot = world.AddComponent<AutoScreenshot>();
            shot.Rider = rider;
            shot.Regenerator = regen;

            var scatter = world.AddComponent<PropScatter>();
            scatter.World = rideWorld;
            scatter.Terrain = terrain;
            scatter.Kinds = PropScatter.DefaultKinds();
            AssignPropModels(scatter);
            regen.Scatter = scatter;
            shot.Scatter = scatter;
            shot.Flyby = flyby;

            var statue = world.AddComponent<HilltopStatue>();
            statue.World = rideWorld;
            statue.Terrain = terrain;
            regen.Statue = statue;
            shot.Statue = statue;

            BuildLighting();
            AssertNoMissingScripts();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            sw.Stop();
            Log($"Done in {sw.Elapsed.TotalSeconds:F1}s -> {ScenePath}");
        }

        static void Log(string msg) => Debug.Log($"[WorldBuilder] {msg}");

        /// <summary>
        /// Point each scenery kind at its imported models. Kept in the editor
        /// because PropScatter is runtime code and cannot touch AssetDatabase.
        /// Anything missing is reported and simply falls back to a placeholder,
        /// so a partial import degrades rather than breaking the build.
        /// </summary>
        static void AssignPropModels(PropScatter scatter)
        {
            var wanted = new Dictionary<string, string[]>
            {
                ["conifer"] = new[]
                {
                    "Nature/tree_default", "Nature/tree_detailed", "Nature/tree_oak",
                    "Nature/tree_fat", "Nature/tree_cone", "Nature/tree_blocks",
                    "Nature/tree_pineDefaultA", "Nature/tree_pineTallA", "Nature/tree_pineRoundA",
                },
                ["boulder"] = new[]
                {
                    "Nature/rock_largeA", "Nature/rock_largeB", "Nature/rock_smallA",
                    "Nature/rock_smallB", "Nature/rock_tallA",
                },
                ["farmhouse"] = new[]
                {
                    "City/building-a", "City/building-c", "City/building-e",
                    "City/building-h", "City/building-k", "City/building-n",
                },
                ["parked vehicle"] = new[]
                {
                    "Cars/sedan", "Cars/hatchback-sports", "Cars/suv", "Cars/van",
                    "Cars/truck", "Cars/delivery", "Cars/ambulance", "Cars/tractor",
                },
                ["dinosaur"] = new[]
                {
                    "Dinosaurs/Trex", "Dinosaurs/Triceratops", "Dinosaurs/Stegosaurus",
                    "Dinosaurs/Apatosaurus", "Dinosaurs/Parasaurolophus", "Dinosaurs/Velociraptor",
                },
            };

            foreach (var kind in scatter.Kinds)
            {
                if (kind == null || !wanted.TryGetValue(kind.Name, out var paths)) continue;
                kind.Prefabs = new List<GameObject>();
                var missing = new List<string>();

                foreach (var rel in paths)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/{rel}.fbx");
                    if (asset != null) kind.Prefabs.Add(asset);
                    else missing.Add(rel);
                }

                // The Quaternius dinosaurs export with a near-black diffuse
                // (Kd 0.058 0.070 0.050) and render as silhouettes, so give them
                // usable colours. A few variants, picked per instance, so a herd
                // is not all one shade.
                if (kind.Name == "dinosaur")
                {
                    kind.MaterialOverrides = new List<Material>
                    {
                        MakeDinoMaterial("DinoOlive", new Color(0.36f, 0.42f, 0.26f)),
                        MakeDinoMaterial("DinoSlate", new Color(0.42f, 0.45f, 0.48f)),
                        MakeDinoMaterial("DinoRust",  new Color(0.52f, 0.36f, 0.26f)),
                        MakeDinoMaterial("DinoSand",  new Color(0.62f, 0.56f, 0.40f)),
                    };
                }

                Log($"  {kind.Name}: {kind.Prefabs.Count} model(s)" +
                    (kind.MaterialOverrides.Count > 0 ? $", {kind.MaterialOverrides.Count} material override(s)" : "") +
                    (missing.Count > 0 ? $", MISSING {string.Join(", ", missing)}" : ""));
            }
        }

        /// <summary>Collapse the transition/body segment pairs into one line per
        /// named feature, with its length and gradient range.</summary>
        static System.Collections.Generic.List<string> DistinctSegmentNames(CourseProfile profile)
        {
            var lines = new System.Collections.Generic.List<string>();
            string current = null;
            float length = 0f, lo = 0f, hi = 0f;

            void Flush()
            {
                if (current == null) return;
                lines.Add($"{current,-28} {length / 1000f:F2} km  " +
                          $"{lo * 100f:+0.0;-0.0;0.0}% to {hi * 100f:+0.0;-0.0;0.0}%");
            }

            foreach (var s in profile.Segments)
            {
                if (s.Name != current)
                {
                    Flush();
                    current = s.Name;
                    length = 0f;
                    lo = Mathf.Min(s.StartGrade, s.EndGrade);
                    hi = Mathf.Max(s.StartGrade, s.EndGrade);
                }
                length += s.LengthM;
                lo = Mathf.Min(lo, Mathf.Min(s.StartGrade, s.EndGrade));
                hi = Mathf.Max(hi, Mathf.Max(s.StartGrade, s.EndGrade));
            }
            Flush();
            return lines;
        }

        /// <summary>
        /// Fail the build if any component failed to resolve to a script.
        ///
        /// This exists because of a genuinely nasty failure mode: Unity only
        /// creates a MonoScript for the class whose name matches the .cs file, so
        /// a second MonoBehaviour sharing that file resolves fine in the editor
        /// (the type is loaded in memory) but becomes a missing script in every
        /// built player. The player then dies with "level0 is corrupted", which
        /// points nowhere near the real cause. Cheap to check, so always check.
        /// </summary>
        static void AssertNoMissingScripts()
        {
            int missing = 0;
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                var components = go.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] != null) continue;
                    missing++;
                    Debug.LogError($"[WorldBuilder] MISSING SCRIPT on GameObject '{go.name}' " +
                                   $"(component slot {i}). Usually means a MonoBehaviour is not in " +
                                   "a .cs file named after its class.");
                }
            }

            if (missing > 0)
                throw new Exception($"{missing} missing script reference(s) in the scene -- " +
                                    "the built player would crash on load. Aborting.");
            Log("  no missing script references");
        }

        /// <summary>
        /// Mean weight per layer. Separates "the splat maths is wrong" from
        /// "the splat maths is fine but the material isn't rendering it" --
        /// which look identical on screen.
        /// </summary>
        static void LogSplatStats(float[,,] map)
        {
            string[] names = { "grass", "rock", "snow", "road" };
            int h = map.GetLength(0), w = map.GetLength(1), layers = map.GetLength(2);
            var sums = new double[layers];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    for (int l = 0; l < layers; l++)
                        sums[l] += map[z, x, l];

            double total = (double)h * w;
            var parts = new System.Text.StringBuilder();
            for (int l = 0; l < layers; l++)
                parts.Append($"{names[l]} {100.0 * sums[l] / total:F1}%  ");
            Log($"  splat coverage: {parts}");
        }

        static float SteepestDistance(RoutePath route)
        {
            float best = 0f, bestG = -99f;
            for (int i = 0; i < 4000; i++)
            {
                float d = (i / 4000f) * route.Profile.TotalLength;
                float g = route.Profile.GradeAt(d);
                if (g > bestG) { bestG = g; best = d; }
            }
            return best;
        }

        /// <summary>
        /// Confirms the road geometry actually rises at the gradient the profile
        /// claims. A perspective render of a 12% ramp viewed along its length is
        /// genuinely hard to judge by eye, so measure it instead.
        /// </summary>
        static void LogRoadGrade(RoutePath route)
        {
            // Sample the whole course rather than one point. Measuring at the
            // single steepest sample is misleading: it always lands within a few
            // metres of a segment join, where the gradient genuinely steps, so a
            // finite-difference window straddles the step and under-reports.
            float worst = 0f, worstAt = 0f, sum = 0f;
            const int n = 500;
            for (int i = 0; i < n; i++)
            {
                float d = (i / (float)n) * route.Length;
                Vector3 a = route.PositionAt(d - 2f);
                Vector3 b = route.PositionAt(d + 2f);
                float run = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
                if (run < 0.01f) continue;
                float measured = (b.y - a.y) / run;
                float err = Mathf.Abs(measured - route.GradeAt(d)) * 100f;
                sum += err;
                if (err > worst) { worst = err; worstAt = d; }
            }
            Log($"  gradient check over {n} points: mean error {sum / n:F3}%, " +
                $"worst {worst:F2}% at {worstAt / 1000f:F2} km (joins step by design)");

            float mid = route.Profile.TotalLength * 0.42f;   // inside the long climb
            Vector3 ma = route.PositionAt(mid - 25f), mb = route.PositionAt(mid + 25f);
            float mrun = Vector3.Distance(new Vector3(ma.x, 0f, ma.z), new Vector3(mb.x, 0f, mb.z));
            Log($"  mid-climb at {mid / 1000f:F2} km: profile {route.GradeAt(mid) * 100f:F2}%, " +
                $"geometry {((mb.y - ma.y) / mrun) * 100f:F2}%");
        }

        /// <summary>
        /// Cross-section of the terrain running away from the road. This is how
        /// you tell a genuine mountainside from a flat plain that merely looks
        /// flat because you are viewing it edge-on.
        /// </summary>
        static void LogTransect(float[,] h, WorldSettings s, RoutePath route)
        {
            int res = h.GetLength(0);
            float texel = s.TerrainSize / (res - 1);
            float d0 = SteepestDistance(route);

            Vector3 p = route.PositionAt(d0);
            Vector3 fwd = route.ForwardAt(d0, 8f);
            Vector3 side = Vector3.Cross(Vector3.up, new Vector3(fwd.x, 0f, fwd.z).normalized);

            var offsets = new[] { 0f, 25f, 50f, 100f, 200f, 400f, 800f, 1600f };
            foreach (float sign in new[] { 1f, -1f })
            {
                var parts = new System.Text.StringBuilder();
                foreach (float o in offsets)
                {
                    Vector3 q = p + side * (o * sign);
                    int gx = Mathf.Clamp(Mathf.RoundToInt(q.x / texel), 0, res - 1);
                    int gz = Mathf.Clamp(Mathf.RoundToInt(q.z / texel), 0, res - 1);
                    float metres = h[gz, gx] * s.TerrainHeight;
                    parts.Append($"{o:F0}m:{metres - p.y:+0;-0;0}  ");
                }
                Log($"  transect {(sign > 0 ? "right" : "left ")} of road (relative to road at {p.y:F0} m): {parts}");
            }
        }

        /// <summary>
        /// Numbers, not eyeballs. Clipping at the ceiling and a collapsed height
        /// range both look plausible in a screenshot but are obvious here.
        /// </summary>
        static void LogHeightStats(float[,] h, WorldSettings s)
        {
            int res = h.GetLength(0);
            float min = 1f, max = 0f, sum = 0f;
            int clipped = 0;
            long n = 0;
            for (int z = 0; z < res; z += 2)
                for (int x = 0; x < res; x += 2)
                {
                    float v = h[z, x];
                    if (v < min) min = v;
                    if (v > max) max = v;
                    if (v >= 0.999f || v <= 0.001f) clipped++;
                    sum += v; n++;
                }
            float pctClipped = 100f * clipped / n;
            Log($"  heights: min {min * s.TerrainHeight:F0} m, max {max * s.TerrainHeight:F0} m, " +
                $"mean {(sum / n) * s.TerrainHeight:F0} m, clipped {pctClipped:F2}%");
            if (pctClipped > 1f)
                Debug.LogWarning($"[WorldBuilder] {pctClipped:F1}% of the terrain is clamped at the " +
                                 "height limits -- summits will be flat mesas. Raise TerrainHeight " +
                                 "or lower the relief amplitude.");
        }

        // --- lighting -------------------------------------------------------

        static void BuildLighting()
        {
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            sunGo.transform.rotation = Quaternion.Euler(38f, 145f, 0f);
            RenderSettings.sun = sun;

            // An empty scene has no skybox at all, which renders as flat grey and
            // makes the terrain impossible to judge. Procedural sky also feeds
            // ambient light, so this affects ground shading too.
            var sky = new Material(Shader.Find("Skybox/Procedural")) { name = "SkyMat" };
            sky.SetFloat("_SunSize", 0.04f);
            sky.SetFloat("_AtmosphereThickness", 1.15f);
            sky.SetColor("_SkyTint", new Color(0.52f, 0.62f, 0.78f));
            // Matched to the fog, so the below-horizon half of the skybox blends
            // into the haze instead of showing as a brown band above the terrain.
            sky.SetColor("_GroundColor", new Color(0.66f, 0.72f, 0.80f));
            sky.SetFloat("_Exposure", 1.25f);
            AssetDatabase.CreateAsset(sky, $"{GenDir}/SkyMat.mat");
            RenderSettings.skybox = sky;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.52f, 0.60f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.45f, 0.47f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.21f, 0.19f);

            // Fog does most of the work of making a 10 km terrain read as
            // distance rather than as a flat painted backdrop.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            // Dense enough to swallow the terrain's hard edge at 10 km, which
            // otherwise sits on the horizon as an obvious straight cut.
            RenderSettings.fogDensity = 0.00030f;
            RenderSettings.fogColor = new Color(0.68f, 0.75f, 0.84f);
        }

        // --- materials & textures -------------------------------------------

        static Material MakeDinoMaterial(string name, Color color)
        {
            var mat = new Material(Shader.Find("Standard")) { name = name, color = color };
            mat.SetFloat("_Glossiness", 0.12f);
            mat.enableInstancing = true;
            AssetDatabase.CreateAsset(mat, $"{GenDir}/{name}.mat");
            return mat;
        }

        static Material MakeTerrainMaterial()
        {
            var shader = Shader.Find("Nature/Terrain/Standard");
            if (shader == null)
            {
                Debug.LogWarning("[WorldBuilder] terrain shader not found; using Unity's default.");
                return null;
            }
            var mat = new Material(shader) { name = "TerrainMat" };
            AssetDatabase.CreateAsset(mat, $"{GenDir}/TerrainMat.mat");
            return mat;
        }

        static Material MakeRoadMaterial()
        {
            var tex = MakeRoadTexture();
            var mat = new Material(Shader.Find("Standard")) { name = "RoadMat" };
            mat.mainTexture = tex;
            mat.SetFloat("_Glossiness", 0.18f);
            mat.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(mat, $"{GenDir}/RoadMat.mat");
            return mat;
        }

        static TerrainLayer[] BuildLayers()
        {
            var layers = new[]
            {
                MakeLayer("Grass", new Color(0.28f, 0.40f, 0.20f), new Color(0.36f, 0.49f, 0.24f), 0.35f, 12f),
                MakeLayer("Rock",  new Color(0.34f, 0.32f, 0.30f), new Color(0.48f, 0.45f, 0.42f), 0.9f, 18f),
                MakeLayer("Snow",  new Color(0.86f, 0.89f, 0.94f), new Color(0.97f, 0.98f, 1.00f), 0.25f, 14f),
                MakeLayer("Road",  new Color(0.16f, 0.16f, 0.17f), new Color(0.22f, 0.22f, 0.23f), 0.6f, 8f),
            };
            return layers;
        }

        static TerrainLayer MakeLayer(string name, Color a, Color b, float roughness, float tile)
        {
            var tex = MakeNoiseTexture(name, a, b, roughness);
            var layer = new TerrainLayer
            {
                name = name,
                diffuseTexture = tex,
                tileSize = new Vector2(tile, tile),
                tileOffset = Vector2.zero,
                specular = Color.black,
                metallic = 0f,
                smoothness = 0.02f,
            };
            AssetDatabase.CreateAsset(layer, $"{GenDir}/Layer_{name}.terrainlayer");
            return layer;
        }

        /// <summary>Procedural mottled texture so layers read as ground rather
        /// than flat colour, without shipping any image assets.</summary>
        static Texture2D MakeNoiseTexture(string name, Color a, Color b, float roughness, int size = 256)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = name };
            var px = new Color[size * size];
            float seed = name.GetHashCode() % 1000;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Two octaves at frequencies that divide the texture size, so
                    // the result tiles seamlessly across the terrain.
                    float n = 0f;
                    n += Mathf.PerlinNoise((x / (float)size) * 8f + seed, (y / (float)size) * 8f + seed) * 0.6f;
                    n += Mathf.PerlinNoise((x / (float)size) * 24f + seed, (y / (float)size) * 24f + seed) * 0.4f;
                    n = Mathf.Clamp01((n - 0.5f) * roughness * 2f + 0.5f);
                    px[y * size + x] = Color.Lerp(a, b, n);
                }
            }
            tex.SetPixels(px);
            tex.Apply();

            var path = $"{GenDir}/Tex_{name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// Asphalt with edge lines and a dashed centre line. The road mesh's V
        /// coordinate advances one unit per 8 m, so one texture repeat is 8 m --
        /// which puts the dash cadence at roughly highway spacing.
        /// </summary>
        static Texture2D MakeRoadTexture(int w = 128, int h = 256)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "RoadTex" };
            var px = new Color[w * h];
            var asphalt = new Color(0.17f, 0.17f, 0.18f);
            var paint = new Color(0.92f, 0.92f, 0.88f);

            for (int y = 0; y < h; y++)
            {
                bool dash = (y % h) < h * 0.55f;   // dashed centre line
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)(w - 1);
                    float grain = Mathf.PerlinNoise(x * 0.28f, y * 0.28f) * 0.06f - 0.03f;
                    var c = asphalt + new Color(grain, grain, grain);

                    bool edge = u < 0.045f || u > 0.955f;
                    bool centre = Mathf.Abs(u - 0.5f) < 0.018f && dash;
                    if (edge || centre) c = paint;

                    px[y * w + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply();

            var path = $"{GenDir}/Tex_Road.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // --- bike proxy -----------------------------------------------------

        static Material SimpleMat(string name, Color color, float smoothness)
        {
            var mat = new Material(Shader.Find("Standard")) { name = name, color = color };
            mat.SetFloat("_Glossiness", smoothness);
            AssetDatabase.CreateAsset(mat, $"{GenDir}/{name}.mat");
            return mat;
        }

        /// <summary>
        /// A stand-in bike built from primitives. Deliberately crude -- it exists
        /// so there is a sense of scale, lean and motion; swapping in a real
        /// model later is just replacing this hierarchy.
        /// </summary>
        static GameObject BuildBikeProxy()
        {
            var frameMat = SimpleMat("BikeFrameMat", new Color(0.85f, 0.18f, 0.15f), 0.55f);
            var rubberMat = SimpleMat("BikeTyreMat", new Color(0.08f, 0.08f, 0.09f), 0.2f);
            var riderMat = SimpleMat("RiderMat", new Color(0.15f, 0.35f, 0.75f), 0.35f);

            var root = new GameObject("Bike");

            Wheel(root.transform, "WheelRear", new Vector3(0f, 0.35f, -0.53f), rubberMat);
            Wheel(root.transform, "WheelFront", new Vector3(0f, 0.35f, 0.53f), rubberMat);

            Box(root.transform, "DownTube", new Vector3(0f, 0.52f, 0.02f),
                new Vector3(0.05f, 0.05f, 1.02f), Quaternion.Euler(12f, 0f, 0f), frameMat);
            Box(root.transform, "SeatTube", new Vector3(0f, 0.66f, -0.28f),
                new Vector3(0.05f, 0.52f, 0.05f), Quaternion.Euler(-16f, 0f, 0f), frameMat);
            Box(root.transform, "Bars", new Vector3(0f, 0.98f, 0.46f),
                new Vector3(0.42f, 0.04f, 0.04f), Quaternion.identity, frameMat);

            var rider = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            rider.name = "Rider";
            rider.transform.SetParent(root.transform, false);
            rider.transform.localPosition = new Vector3(0f, 1.08f, -0.06f);
            rider.transform.localRotation = Quaternion.Euler(62f, 0f, 0f);
            rider.transform.localScale = new Vector3(0.36f, 0.42f, 0.36f);
            rider.GetComponent<MeshRenderer>().sharedMaterial = riderMat;
            UnityEngine.Object.DestroyImmediate(rider.GetComponent<Collider>());

            return root;
        }

        static void Wheel(Transform parent, string name, Vector3 pos, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            // Rotating 90 degrees about Z swings the cylinder's axis onto X, which
            // is what makes it a wheel rather than a bollard.
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            go.transform.localScale = new Vector3(0.7f, 0.022f, 0.7f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        }

        static void Box(Transform parent, string name, Vector3 pos, Vector3 scale,
                        Quaternion rot, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        }

        // --- batch entry point ----------------------------------------------

        public static void BuildFromCommandLine()
        {
            try
            {
                BuildWorld();
                EditorApplication.Exit(0);
            }
            catch (Exception exc)
            {
                Debug.LogError($"[WorldBuilder] FAILED: {exc}");
                EditorApplication.Exit(1);
            }
        }
    }
}
