using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KickrWorld
{
    /// <summary>
    /// Builds the bridge's Python environment on first launch, so that
    /// installing the app is just putting it where you want it.
    ///
    /// This used to be a shell script the user ran by hand. That was never a
    /// design decision, only the consequence of two things: a zip built on
    /// Windows could not carry an executable bit, and a virtualenv cannot be
    /// created inside an app bundle the user may not own. The first is fixed in
    /// the packaging; this fixes the second by keeping the environment out of
    /// the bundle entirely.
    ///
    /// The bundle therefore stays exactly as it was signed -- which matters more
    /// than it sounds. macOS attaches the Bluetooth grant to an app's signature,
    /// so anything written inside the bundle after signing invalidates it and
    /// the trainer permission silently stops sticking.
    /// </summary>
    public static class BridgeProvisioner
    {
        /// <summary>
        /// Where the working copy of the bridge lives: outside the bundle, in the
        /// per-user location each platform expects, and writable without admin.
        /// </summary>
        public static string SupportDirectory()
        {
            if (Application.platform == RuntimePlatform.OSXPlayer)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                if (string.IsNullOrEmpty(home)) return null;
                return Path.Combine(home, "Library", "Application Support", "VibeRide", "bridge");
            }

            if (Application.platform == RuntimePlatform.WindowsPlayer)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) return null;
                return Path.Combine(local, "VibeRide", "bridge");
            }

            return null;   // the editor uses the repo copy
        }

        public static bool HasCode(string dir) =>
            !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "kickr_bridge"));

        public static bool HasVenv(string dir) =>
            !string.IsNullOrEmpty(dir) &&
            (File.Exists(Path.Combine(dir, ".venv", "bin", "python")) ||
             File.Exists(Path.Combine(dir, ".venv", "Scripts", "python.exe")));

        /// <summary>
        /// Copy the bridge source out of the bundle, leaving any existing
        /// virtualenv alone.
        ///
        /// Run on every launch, not just the first. An app replaced in place
        /// brings new bridge code with it, and a stale copy in Application
        /// Support would quietly keep winning -- which is exactly the "delete
        /// your old install first" trap this is meant to remove. Rebuilding the
        /// virtualenv every time would be a minute of waiting for nothing, so
        /// that is kept.
        /// </summary>
        public static void MirrorCode(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string rel = Relative(source, dir);
                if (Skip(rel)) continue;
                Directory.CreateDirectory(Path.Combine(target, rel));
            }
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = Relative(source, file);
                if (Skip(rel)) continue;
                string dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file, dest, true);
            }
        }

        static bool Skip(string rel)
        {
            string p = rel.Replace('\\', '/');
            return p == ".venv" || p.StartsWith(".venv/")
                   || p.Contains("__pycache__");
        }

        static string Relative(string root, string full)
        {
            string r = full.Substring(root.Length);
            return r.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// A system Python capable of building the environment -- Windows only.
        ///
        /// macOS deliberately has no equivalent here. setup_mac.sh searches for an
        /// interpreter properly, by running each candidate rather than looking for
        /// the file, and this used to duplicate that badly: it accepted
        /// /usr/bin/python3 on sight, which exists on every Mac since Catalina
        /// even when it is only a stub that opens an installer dialog, and it
        /// never checked the version. Worse, the answer was thrown away -- the
        /// script resolved python3 from PATH independently, so the interpreter
        /// that got vetted was not necessarily the one that got used.
        /// </summary>
        public static string FindSystemPython()
        {
            if (Application.platform == RuntimePlatform.WindowsPlayer)
                return "python.exe";   // resolved through PATH
            return null;
        }

        /// <summary>
        /// Start building the virtualenv. Returns null and sets
        /// <paramref name="error"/> if it cannot even be attempted.
        ///
        /// On macOS this runs the bridge's own setup_mac.sh rather than a copy of
        /// its steps. There is one procedure for building this environment and it
        /// is already written down, already tested, and already the thing anyone
        /// debugging by hand would run. Its selftest is offline -- decode and
        /// physics checks -- so nothing here touches Bluetooth or prompts for a
        /// permission at an odd moment.
        ///
        /// Either way the command is a FILE, never an inline string.
        /// ProcessStartInfo.Arguments is one flat string that each platform
        /// re-splits by its own quoting rules, and a path with a space in it is
        /// the normal case on a Mac.
        /// </summary>
        public static Process StartBuild(string dir, out string error)
        {
            error = null;
            bool windows = Application.platform == RuntimePlatform.WindowsPlayer;

            string file, args;
            if (windows)
            {
                string python = FindSystemPython();
                string cmd = Path.Combine(dir, "provision.cmd");
                try
                {
                    File.WriteAllText(cmd,
                        "@echo off\r\n" +
                        "echo Creating virtualenv in .venv ...\r\n" +
                        $"\"{python}\" -m venv .venv || exit /b 1\r\n" +
                        "echo Installing dependencies ...\r\n" +
                        ".venv\\Scripts\\python.exe -m pip install --quiet --upgrade pip || exit /b 1\r\n" +
                        ".venv\\Scripts\\python.exe -m pip install --quiet bleak websockets || exit /b 1\r\n" +
                        "echo Python environment ready.\r\n");
                }
                catch (Exception exc)
                {
                    error = $"could not write setup script: {exc.Message}";
                    return null;
                }
                file = "cmd.exe";
                args = "/c provision.cmd";
            }
            else
            {
                string script = Path.Combine(dir, "setup_mac.sh");
                if (!File.Exists(script))
                {
                    error = "setup_mac.sh missing from the bridge";
                    return null;
                }
                file = "/bin/bash";
                args = "setup_mac.sh";
            }

            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // setup_mac.sh finds python3 on PATH, and a GUI app inherits a bare
            // PATH from launchd that has neither Homebrew directory in it.
            string existing = psi.EnvironmentVariables.ContainsKey("PATH")
                ? psi.EnvironmentVariables["PATH"] : "";
            psi.EnvironmentVariables["PATH"] =
                "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin" +
                (string.IsNullOrEmpty(existing) ? "" : ":" + existing);

            try
            {
                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.Start();
                Debug.Log($"[BridgeProvisioner] building the Python environment in {dir}");
                return proc;
            }
            catch (Exception exc)
            {
                error = $"could not start setup: {exc.Message}";
                return null;
            }
        }
    }
}
