using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;            // NamedBuildTarget
using UnityEditor.Build.Reporting;  // BuildReport
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Builds standalone players. The macOS target is the interesting one: it is
    /// cross-compiled from Windows, which constrains the scripting backend (see
    /// SetMacArchitecture).
    /// </summary>
    public static class PlayerBuilder
    {
        const string Scene = "Assets/Scenes/Ride.unity";

        [MenuItem("VibeRide/Build macOS Player")]
        public static void BuildMac() => Build(BuildTarget.StandaloneOSX, "Builds/Mac/VibeRide.app");

        [MenuItem("VibeRide/Build Windows Player")]
        public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "Builds/Windows/VibeRide.exe");

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "VibeRide";
            PlayerSettings.productName = "VibeRide";
            PlayerSettings.SetApplicationIdentifier(
                NamedBuildTarget.Standalone, "com.viberide.app");

            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.runInBackground = true;
            PlayerSettings.macRetinaSupport = true;

            // The player only talks to 127.0.0.1, so it needs no Bluetooth
            // entitlement on macOS -- the bridge process owns that permission.
            // This is a direct benefit of keeping BLE out of Unity.
        }

        /// <summary>
        /// Set the macOS CPU architecture, via reflection so this file still
        /// compiles when the macOS module isn't installed.
        ///
        /// IL2CPP cannot be cross-compiled for macOS from Windows -- it needs the
        /// Apple toolchain -- so a Windows-hosted build is Mono. Where Mono limits
        /// us to x86_64, the result still runs on Apple Silicon under Rosetta 2.
        /// For a native arm64 build, build the project on the Mac itself.
        /// </summary>
        static void SetMacArchitecture()
        {
            try
            {
                var type = Type.GetType("UnityEditor.OSXStandalone.UserBuildSettings, UnityEditor.OSXStandalone.Extensions");
                if (type == null)
                {
                    Debug.LogWarning("[PlayerBuilder] macOS build extension not found; " +
                                     "using whatever architecture default is active.");
                    return;
                }

                var prop = type.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                {
                    Debug.LogWarning("[PlayerBuilder] no 'architecture' property on UserBuildSettings.");
                    return;
                }

                var enumType = prop.PropertyType;
                var names = Enum.GetNames(enumType);
                Debug.Log($"[PlayerBuilder] available macOS architectures: {string.Join(", ", names)}");

                // Unity names the universal (fat) binary "x64ARM64" -- not
                // "Universal", which does not exist in this enum. Getting this
                // wrong silently yields an Intel-only player that runs on Apple
                // Silicon under Rosetta 2 instead of natively.
                foreach (var want in new[] { "x64ARM64", "ARM64", "x64" })
                {
                    foreach (var n in names)
                    {
                        if (!string.Equals(n, want, StringComparison.OrdinalIgnoreCase)) continue;
                        prop.SetValue(null, Enum.Parse(enumType, n));
                        Debug.Log($"[PlayerBuilder] macOS architecture set to {n}");
                        return;
                    }
                }
            }
            catch (Exception exc)
            {
                Debug.LogWarning($"[PlayerBuilder] could not set macOS architecture: {exc.Message}");
            }
        }

        public static void Build(BuildTarget target, string relativePath)
        {
            ConfigurePlayer();
            if (target == BuildTarget.StandaloneOSX)
            {
                // Mono, because IL2CPP for macOS cannot be produced on Windows.
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                SetMacArchitecture();
            }

            string full = Path.GetFullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));

            var options = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = full,
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                // Clean cache: an incremental rebuild after switching build target
                // produced a player whose level0 failed to deserialise at runtime
                // ("file is corrupted" / "Position out of bounds"). Rebuilding the
                // cache costs ~30s and removes a whole class of confusing crashes.
                options = BuildOptions.CleanBuildCache,
            };

            Debug.Log($"[PlayerBuilder] building {target} -> {full}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            Debug.Log($"[PlayerBuilder] result={summary.result} " +
                      $"size={summary.totalSize / (1024f * 1024f):F1} MB " +
                      $"time={summary.totalTime.TotalSeconds:F0}s " +
                      $"errors={summary.totalErrors} warnings={summary.totalWarnings}");

            if (summary.result != BuildResult.Succeeded)
            {
                foreach (var step in report.steps)
                    foreach (var msg in step.messages)
                        if (msg.type == LogType.Error || msg.type == LogType.Exception)
                            Debug.LogError($"[PlayerBuilder] {step.name}: {msg.content}");
                throw new Exception($"Build failed: {summary.result}");
            }
        }

        public static void BuildMacFromCommandLine() => RunFromCommandLine(BuildMac);
        public static void BuildWindowsFromCommandLine() => RunFromCommandLine(BuildWindows);

        /// <summary>
        /// Switch to the target platform FIRST, then regenerate the world, then
        /// build -- all in one editor session.
        ///
        /// The order matters and getting it wrong produces a player whose level0
        /// will not deserialise ("file is corrupted" / "Position out of bounds"),
        /// while the same scene opens perfectly in the editor. Baking the scene
        /// while the active build target is still the previous platform means
        /// BuildPlayer switches target afterwards and re-imports assets under a
        /// scene that was already saved against the old ones.
        ///
        /// This bit me twice: first diagnosed as "two batch sessions", which was
        /// only a proxy for the real variable, and it came back the moment a Mac
        /// build preceded a Windows build.
        /// </summary>
        public static void BuildAllMacFromCommandLine() =>
            RunFromCommandLine(() => BuildAll(BuildTarget.StandaloneOSX, "Builds/Mac/VibeRide.app"));

        public static void BuildAllWindowsFromCommandLine() =>
            RunFromCommandLine(() => BuildAll(BuildTarget.StandaloneWindows64, "Builds/Windows/VibeRide.exe"));

        static void BuildAll(BuildTarget target, string path)
        {
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                Debug.Log($"[PlayerBuilder] switching active build target " +
                          $"{EditorUserBuildSettings.activeBuildTarget} -> {target} before baking");
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, target))
                    throw new Exception($"could not switch active build target to {target}");
            }
            else
            {
                Debug.Log($"[PlayerBuilder] active build target already {target}");
            }

            WorldBuilder.BuildWorld();
            Build(target, path);
        }

        static void RunFromCommandLine(Action build)
        {
            try { build(); EditorApplication.Exit(0); }
            catch (Exception exc)
            {
                Debug.LogError($"[PlayerBuilder] FAILED: {exc}");
                EditorApplication.Exit(1);
            }
        }
    }
}
