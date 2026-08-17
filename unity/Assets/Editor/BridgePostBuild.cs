using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Copies the Python bridge next to the built player so the app can launch
    /// it, and adds the macOS Bluetooth usage string.
    /// </summary>
    public static class BridgePostBuild
    {
        [PostProcessBuild(1)]
        public static void OnPostProcessBuild(BuildTarget target, string builtPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string source = Path.GetFullPath(Path.Combine(projectRoot, "..", "bridge"));
            if (!Directory.Exists(source))
            {
                Debug.LogWarning($"[BridgePostBuild] no bridge folder at {source}; " +
                                 "the built app will not be able to launch one.");
                return;
            }

            string dest = target == BuildTarget.StandaloneOSX
                // Inside the bundle, beside Data, so the app stays self-contained
                // and relocatable.
                ? Path.Combine(builtPath, "Contents", "Resources", "bridge")
                : Path.Combine(Path.GetDirectoryName(builtPath), "bridge");

            CopyBridge(source, dest);
            Debug.Log($"[BridgePostBuild] bridge -> {dest}");

            if (target == BuildTarget.StandaloneOSX)
                AddBluetoothUsage(Path.Combine(builtPath, "Contents", "Info.plist"));
        }

        static void CopyBridge(string source, string dest)
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);

            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dir);
                // The virtualenv holds native binaries for whichever OS built it,
                // and is worse than useless on the other platform. setup_mac.sh
                // creates a fresh one on the Mac.
                if (name == ".venv" || name == "__pycache__") continue;
                if (dir.Contains(".venv") || dir.Contains("__pycache__")) continue;
                Directory.CreateDirectory(dir.Replace(source, dest));
            }

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if (file.Contains(".venv") || file.Contains("__pycache__")) continue;
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".pyc" || ext == ".out" || ext == ".err") continue;
                File.Copy(file, file.Replace(source, dest), true);
            }
        }

        /// <summary>
        /// macOS will not show a Bluetooth permission prompt without a usage
        /// description, and denies the request silently instead.
        ///
        /// This is needed because the app SPAWNS the bridge. macOS attributes a
        /// privacy request to the responsible process -- the app bundle -- not to
        /// the Python child that actually opens CoreBluetooth. Run the bridge
        /// yourself from a terminal and the terminal is responsible instead, which
        /// is why this was not needed before the app started launching it.
        /// </summary>
        static void AddBluetoothUsage(string plistPath)
        {
            if (!File.Exists(plistPath))
            {
                Debug.LogWarning($"[BridgePostBuild] no Info.plist at {plistPath}");
                return;
            }

            string text = File.ReadAllText(plistPath);
            if (text.Contains("NSBluetoothAlwaysUsageDescription"))
                return;

            const string entry =
                "\t<key>NSBluetoothAlwaysUsageDescription</key>\n" +
                "\t<string>VibeRide connects to your smart trainer to read power and cadence, " +
                "and to set resistance from the terrain you are riding.</string>\n" +
                "\t<key>NSBluetoothPeripheralUsageDescription</key>\n" +
                "\t<string>VibeRide connects to your smart trainer to read power and cadence, " +
                "and to set resistance from the terrain you are riding.</string>\n";

            // Insert before the final </dict>, which closes the root dictionary.
            int idx = text.LastIndexOf("</dict>", System.StringComparison.Ordinal);
            if (idx < 0)
            {
                Debug.LogWarning("[BridgePostBuild] Info.plist has no closing </dict>; left unchanged.");
                return;
            }

            File.WriteAllText(plistPath, text.Insert(idx, entry));
            Debug.Log("[BridgePostBuild] added Bluetooth usage descriptions to Info.plist");
        }
    }
}
