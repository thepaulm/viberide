using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Builds the Ride scene with one piece removed, to find what makes the
    /// player's level0 fail to deserialise. The scene itself loads and renders
    /// fine in the editor, so the fault is specific to player serialisation.
    ///
    /// Usage: -executeMethod KickrWorld.EditorTools.BisectBuilder.NoTerrain (etc.)
    /// </summary>
    public static class BisectBuilder
    {
        public static void NoTerrain() => Run("NoTerrain", "Terrain");
        public static void NoRoad() => Run("NoRoad", "Road");
        public static void NoBike() => Run("NoBike", "Bike");
        public static void NoWorld() => Run("NoWorld", "World");

        // Component-level: the fault is somewhere on the World object, and the
        // smoke-test player already cleared TrainerLink and BridgeLauncher.
        public static void NoHud() => RunComponents("NoHud", typeof(RideHud));
        public static void NoRider() => RunComponents("NoRider", typeof(BikeRider));
        public static void NoRideWorld() => RunComponents("NoRideWorld", typeof(BikeRider), typeof(RideWorld));

        static void RunComponents(string label, params Type[] types)
        {
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/Scenes/Ride.unity");
                var world = GameObject.Find("World");
                if (world == null) throw new Exception("no World object");

                foreach (var t in types)
                {
                    var c = world.GetComponent(t);
                    if (c != null)
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                        Debug.Log($"[Bisect] removed component {t.Name}");
                    }
                }

                BuildVariant(label, scene);
            }
            catch (Exception exc)
            {
                Debug.LogError($"[Bisect] FAILED: {exc}");
                EditorApplication.Exit(1);
            }
        }

        static void BuildVariant(string label, UnityEngine.SceneManagement.Scene scene)
        {
            string scenePath = $"Assets/Scenes/Bisect_{label}.unity";
            EditorSceneManager.SaveScene(scene, scenePath);

            string full = Path.GetFullPath($"Builds/Bisect_{label}/Player.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(full));

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = full,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.CleanBuildCache,
            });

            Debug.Log($"[Bisect] {label}: result={report.summary.result} " +
                      $"size={report.summary.totalSize / (1024f * 1024f):F1} MB");
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static void Run(string label, string objectToDelete)
        {
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/Scenes/Ride.unity");

                var target = GameObject.Find(objectToDelete);
                if (target == null)
                    Debug.LogWarning($"[Bisect] no object named '{objectToDelete}' in the scene");
                else
                {
                    UnityEngine.Object.DestroyImmediate(target);
                    Debug.Log($"[Bisect] removed '{objectToDelete}'");
                }

                string scenePath = $"Assets/Scenes/Bisect_{label}.unity";
                EditorSceneManager.SaveScene(scene, scenePath);

                string full = Path.GetFullPath($"Builds/Bisect_{label}/Player.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(full));

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = full,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.CleanBuildCache,
                });

                string dataDir = Path.Combine(Path.GetDirectoryName(full), "Player_Data");
                long level0 = 0;
                var l0 = Path.Combine(dataDir, "level0");
                if (File.Exists(l0)) level0 = new FileInfo(l0).Length;

                Debug.Log($"[Bisect] {label}: result={report.summary.result} " +
                          $"size={report.summary.totalSize / (1024f * 1024f):F1} MB " +
                          $"level0={level0} bytes");

                EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
            }
            catch (Exception exc)
            {
                Debug.LogError($"[Bisect] FAILED: {exc}");
                EditorApplication.Exit(1);
            }
        }
    }
}
