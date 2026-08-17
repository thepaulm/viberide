using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Builds a player from a deliberately trivial scene: camera, light, cube,
    /// and the bridge launcher. Two jobs -- prove the player pipeline works at
    /// all, and test bridge launch/shutdown without the terrain in the way.
    ///
    /// Exists because the full player failed with a corrupt level0, and there was
    /// no known-good baseline to compare against.
    /// </summary>
    public static class SmokeTestBuilder
    {
        const string ScenePath = "Assets/Scenes/Smoke.unity";

        [MenuItem("VibeRide/Build Smoke Test Player")]
        public static void BuildSmoke()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = new Vector3(0f, 1.5f, -6f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.2f, 0.28f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(45f, 30f, 0f);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Marker";

            var host = new GameObject("Bridge");
            var link = host.AddComponent<TrainerLink>();
            var launcher = host.AddComponent<BridgeLauncher>();
            launcher.Link = link;
            launcher.DemoMode = true;   // no trainer needed for a smoke test
            host.AddComponent<SmokeReadout>().Link = link;

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            string full = Path.GetFullPath("Builds/Smoke/KickrWorldSmoke.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(full));

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = full,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.CleanBuildCache,
            });

            Debug.Log($"[SmokeTest] result={report.summary.result} " +
                      $"size={report.summary.totalSize / (1024f * 1024f):F1} MB -> {full}");
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("smoke build failed");
        }

        public static void BuildSmokeFromCommandLine()
        {
            try { BuildSmoke(); EditorApplication.Exit(0); }
            catch (Exception exc)
            {
                Debug.LogError($"[SmokeTest] FAILED: {exc}");
                EditorApplication.Exit(1);
            }
        }
    }
}
