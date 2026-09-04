using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KickrWorld
{
    /// <summary>
    /// Starts the Python bridge when the app starts, and guarantees it dies when
    /// the app does.
    ///
    /// Shutdown is layered, because an orphaned bridge holds the trainer's single
    /// BLE connection and blocks the next launch:
    ///
    ///   1. "shutdown" on the child's stdin  -- normal quit, immediate and clean
    ///   2. stdin EOF                        -- app died without saying anything;
    ///                                          the pipe closes on its own
    ///   3. --parent-pid watchdog in Python  -- backstop for a hard crash, where
    ///                                          even the pipe might linger
    ///   4. Process.Kill()                   -- last resort if it ignores all that
    ///
    /// If a bridge is already listening (you started one by hand) this defers to
    /// it and starts nothing, so a manual debugging session isn't fought over.
    /// "Already listening" means it answered the bridge's health check, not just
    /// that the port accepted a connection: an unrelated local server on the same
    /// port once passed for a bridge, and the app then sat forever failing to
    /// open a WebSocket to it.
    /// </summary>
    public class BridgeLauncher : MonoBehaviour
    {
        [Header("Wiring")]
        public TrainerLink Link;

        [Header("Options")]
        public bool LaunchOnStart = true;
        public bool DemoMode = false;
        // Blocks the main thread during quit, so keep it short. The bridge's own
        // parent-PID watchdog cleans up within ~2s even if we give up waiting.
        [Tooltip("Seconds to wait for a polite exit before killing the process.")]
        public float ShutdownGraceSeconds = 2f;

        public string Status { get; private set; } = "not started";
        public bool Managing => _process != null && !SafeHasExited(_process);

        Process _process;

        // First-run setup of the Python environment, if the app finds none.
        Process _setup;
        string _setupTarget;
        string _setupLastLine = "";
        readonly System.Collections.Concurrent.ConcurrentQueue<string> _setupLog = new();

        /// <summary>Running the bridge, or getting ready to.</summary>
        public bool Busy => Managing || _setup != null;

        // Output from the child arrives on background threads. Unity's Debug.Log
        // takes an internal lock, and calling it off the main thread while the
        // engine is still starting can stall the main thread. Queue instead, and
        // drain from Update where logging is safe.
        readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();

        public static bool BridgeDisabled =>
            Array.IndexOf(Environment.GetCommandLineArgs(), "-nobridge") >= 0;

        void Start()
        {
            if (BridgeDisabled)
            {
                Status = "disabled by -nobridge";
                Debug.Log("[BridgeLauncher] -nobridge given; not starting the bridge.");
                return;
            }
            // Wait a frame so the launch never happens during scene load.
            if (LaunchOnStart) StartCoroutine(LaunchNextFrame());
        }

        System.Collections.IEnumerator LaunchNextFrame()
        {
            yield return null;
            Launch();
        }

        void Update()
        {
            int drained = 0;
            while (drained++ < 40 && _pendingLog.TryDequeue(out var line))
                Debug.Log($"[bridge] {line}");

            PumpSetup();
        }

        /// <summary>Watch the first-run environment build, and start the bridge
        /// once it finishes.</summary>
        void PumpSetup()
        {
            int drained = 0;
            while (drained++ < 20 && _setupLog.TryDequeue(out var line))
            {
                Debug.Log($"[setup] {line}");
                // Whatever it last said, verbatim. A first run is most of a
                // minute and silence reads as a hang; and when it fails, the last
                // thing it said IS the error, which is what wants to be on screen.
                _setupLastLine = line.Trim();
                if (_setupLastLine.Length > 0)
                    Status = "setting up: " + Truncate(_setupLastLine, 60);
            }

            if (_setup == null || !SafeHasExited(_setup)) return;

            int code;
            try { code = _setup.ExitCode; } catch { code = -1; }
            string dir = _setupTarget;
            try { _setup.Dispose(); } catch { }
            _setup = null;
            _setupTarget = null;

            if (code == 0 && BridgeProvisioner.HasVenv(dir))
            {
                Debug.Log("[BridgeLauncher] Python environment ready; starting the bridge.");
                StartBridge(dir);
            }
            else
            {
                Status = $"setup failed: {(_setupLastLine.Length > 0 ? _setupLastLine : $"exit {code}")}";
                Debug.LogError($"[BridgeLauncher] Python setup failed (exit {code}). {_setupLastLine}");
            }
        }

        void BeginSetup(string dir)
        {
            _setup = BridgeProvisioner.StartBuild(dir, out string error);
            if (_setup == null)
            {
                Status = error;
                Debug.LogError($"[BridgeLauncher] {error}");
                return;
            }

            _setupTarget = dir;
            _setupLastLine = "";
            Status = "first run: setting up Python";
            _setup.OutputDataReceived += (_, e) => QueueSetup(e.Data);
            _setup.ErrorDataReceived += (_, e) => QueueSetup(e.Data);
            _setup.BeginOutputReadLine();
            _setup.BeginErrorReadLine();
        }

        static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 1) + "\u2026";

        void QueueSetup(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (_setupLog.Count < 200) _setupLog.Enqueue(line);
        }

        // --- locating things ---------------------------------------------------

        /// <summary>
        /// Where the bridge lives, per platform. In a player the bridge is copied
        /// next to the game data; in the editor it is the repo folder.
        /// </summary>
        public static string FindBridgeDirectory()
        {
            var candidates = new List<string>();
            string data = Application.dataPath;

            if (Application.isEditor)
            {
                // <project>/Assets -> <project>/../bridge
                candidates.Add(Path.GetFullPath(Path.Combine(data, "..", "..", "bridge")));
            }
            else
            {
                // dataPath is Foo.app/Contents on macOS -- the bundle, not the
                // Data folder inside it -- so the bundled bridge is one level
                // DOWN from it, at Contents/Resources/bridge. Getting this
                // wrong is invisible for as long as a copy also sits beside the
                // .app, which is what the old zip layout shipped and what kept
                // the "up" candidates below looking correct.
                candidates.Add(Path.GetFullPath(Path.Combine(data, "Resources", "bridge")));
                // Windows: Foo_Data -> <alongside exe>/bridge
                candidates.Add(Path.GetFullPath(Path.Combine(data, "..", "bridge")));
                candidates.Add(Path.GetFullPath(Path.Combine(data, "..", "..", "bridge")));
                // Beside the .app itself. Creating a virtualenv inside a bundle
                // fails if the app lives somewhere the user cannot write, so an
                // external copy has to be allowed to win.
                candidates.Add(Path.GetFullPath(Path.Combine(data, "..", "..", "..", "bridge")));
                candidates.Add(Path.GetFullPath(Path.Combine(data, "..", "..", "..", "..", "bridge")));
            }

            // A bridge that already has its virtualenv beats one that doesn't,
            // wherever it happens to live.
            foreach (var c in candidates)
                if (Directory.Exists(Path.Combine(c, "kickr_bridge")) && HasVenv(c))
                    return Chose(c, candidates, "has a virtualenv");
            foreach (var c in candidates)
                if (Directory.Exists(Path.Combine(c, "kickr_bridge")))
                    return Chose(c, candidates, "no virtualenv yet");

            // Name every path that was tried. A wrong assumption about where the
            // bundle keeps things is invisible from the symptom -- the app simply
            // never connects -- and was invisible here too until the list was
            // printed: dataPath on macOS is the Contents folder, so candidates
            // built by walking UP from it could never reach the bridge sitting
            // one level down in Resources.
            Debug.LogError($"[BridgeLauncher] no bridge found. dataPath is {Application.dataPath}. " +
                           $"Tried:\n  {string.Join("\n  ", candidates)}");
            return null;
        }

        static string Chose(string dir, List<string> candidates, string why)
        {
            Debug.Log($"[BridgeLauncher] using bridge at {dir} ({why}), " +
                      $"from {candidates.Count} candidate(s)");
            return dir;
        }

        /// <summary>The read-only copy shipped inside the app bundle.</summary>
        static string BundledBridgeDirectory()
        {
            if (Application.isEditor) return null;
            string data = Application.dataPath;
            // "Resources" first: that is where the bundle actually carries it on
            // macOS. The "up" entries cover Windows and any layout that reports
            // the Data folder rather than the bundle.
            foreach (var rel in new[] { "Resources", "..", Path.Combine("..", "..") })
            {
                string c = Path.GetFullPath(Path.Combine(data, rel, "bridge"));
                if (Directory.Exists(Path.Combine(c, "kickr_bridge"))) return c;
            }
            return null;
        }

        /// <summary>
        /// Decide which copy of the bridge to run, refreshing the writable one
        /// from the bundle on the way past.
        ///
        /// In a player the answer is always the copy in Application Support, for
        /// two reasons: a virtualenv cannot be built inside a bundle the user may
        /// not own, and anything written into the bundle after signing breaks the
        /// signature that macOS hangs the Bluetooth permission on.
        /// </summary>
        string ResolveBridgeDirectory(out bool needsSetup)
        {
            needsSetup = false;
            if (Application.isEditor) return FindBridgeDirectory();

            string support = BridgeProvisioner.SupportDirectory();
            string bundled = BundledBridgeDirectory();

            if (support != null && bundled != null)
            {
                try
                {
                    BridgeProvisioner.MirrorCode(bundled, support);
                }
                catch (Exception exc)
                {
                    Debug.LogWarning($"[BridgeLauncher] could not refresh {support}: {exc.Message}");
                }
            }

            if (BridgeProvisioner.HasCode(support))
            {
                needsSetup = !BridgeProvisioner.HasVenv(support);
                Debug.Log($"[BridgeLauncher] bridge: {support} " +
                          $"({(needsSetup ? "needs setup" : "ready")})");
                return support;
            }

            // Nothing in Application Support means the bundled copy could not be
            // found to mirror from, so name that too -- it is the same fault, one
            // step earlier, and silence here is what made it hard to see.
            Debug.LogWarning($"[BridgeLauncher] nothing usable in {support}; " +
                             $"falling back to the search");
            return FindBridgeDirectory();
        }

        static bool HasVenv(string bridgeDir)
        {
            return File.Exists(Path.Combine(bridgeDir, ".venv", "bin", "python"))
                   || File.Exists(Path.Combine(bridgeDir, ".venv", "Scripts", "python.exe"));
        }

        /// <summary>
        /// Prefer the bridge's own virtualenv; fall back to a system interpreter.
        /// The venv is platform-specific and is never shipped between machines --
        /// setup_mac.sh builds it on the Mac.
        /// </summary>
        public static string FindPython(string bridgeDir)
        {
            bool windows = Application.platform == RuntimePlatform.WindowsPlayer
                           || Application.platform == RuntimePlatform.WindowsEditor;

            string venv = windows
                ? Path.Combine(bridgeDir, ".venv", "Scripts", "python.exe")
                : Path.Combine(bridgeDir, ".venv", "bin", "python");
            if (File.Exists(venv)) return venv;

            foreach (var name in windows
                         ? new[] { "python.exe" }
                         : new[] { "/usr/bin/python3", "/usr/local/bin/python3", "/opt/homebrew/bin/python3" })
            {
                if (!windows && File.Exists(name)) return name;
                if (windows) return name;   // resolved via PATH by the OS
            }
            return null;
        }

        public enum PortState { Free, Bridge, Foreign }

        /// <summary>What the bridge answers a plain HTTP GET with. Must match
        /// HEALTH_BODY in kickr_bridge/server.py.</summary>
        public const string HealthBody = "viberide-bridge";

        /// <summary>Is something already serving on the bridge port, and is it
        /// ours? Sends a plain HTTP GET; the bridge answers every non-WebSocket
        /// request with <see cref="HealthBody"/>, anything else is a stranger.</summary>
        public static PortState ProbePort(int port)
        {
            try
            {
                using var probe = new TcpClient();
                var result = probe.BeginConnect(IPAddress.Loopback, port, null, null);
                // Short: this runs on the main thread, and a loopback connect
                // either succeeds immediately or is not going to.
                bool open = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(120));
                if (!open) return PortState.Free;
                probe.EndConnect(result);

                probe.ReceiveTimeout = 300;
                probe.SendTimeout = 300;
                var stream = probe.GetStream();
                var request = System.Text.Encoding.ASCII.GetBytes(
                    $"GET /health HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
                stream.Write(request, 0, request.Length);

                var buffer = new byte[4096];
                int total = 0;
                try
                {
                    while (total < buffer.Length)
                    {
                        int n = stream.Read(buffer, total, buffer.Length - total);
                        if (n <= 0) break;
                        total += n;
                        if (System.Text.Encoding.ASCII.GetString(buffer, 0, total).Contains(HealthBody)) break;
                    }
                }
                catch (IOException) { }   // timeout: whatever it is, it did not answer like a bridge

                string reply = System.Text.Encoding.ASCII.GetString(buffer, 0, total);
                return reply.Contains(HealthBody) ? PortState.Bridge : PortState.Foreign;
            }
            catch
            {
                return PortState.Free;
            }
        }

        // --- launching ---------------------------------------------------------

        public void Launch()
        {
            int port = ParsePort(Link != null ? Link.Url : null);

            switch (ProbePort(port))
            {
                case PortState.Bridge:
                    Status = $"using bridge already running on :{port}";
                    Debug.Log($"[BridgeLauncher] {Status}");
                    return;
                case PortState.Foreign:
                    // Starting our own would only fail with "address in use", and
                    // the app would sit forever failing to open a WebSocket to
                    // whatever this is. Say so on the one screen anyone watches.
                    Status = $"port {port} is taken by another program (not the bridge) -- quit it, then relaunch";
                    Debug.LogError($"[BridgeLauncher] {Status}");
                    return;
            }

            string bridgeDir = ResolveBridgeDirectory(out bool needsSetup);
            if (bridgeDir == null)
            {
                Status = "bridge folder not found";
                Debug.LogError("[BridgeLauncher] Could not locate the bridge directory. " +
                               "Expected a folder named 'bridge' containing 'kickr_bridge' " +
                               $"near {Application.dataPath}.");
                return;
            }

            if (needsSetup)
            {
                BeginSetup(bridgeDir);
                return;
            }

            StartBridge(bridgeDir);
        }

        void StartBridge(string bridgeDir)
        {
            int port = ParsePort(Link != null ? Link.Url : null);
            string python = FindPython(bridgeDir);
            if (python == null)
            {
                Status = "Python 3 not found -- install it with: brew install python3";
                Debug.LogError($"[BridgeLauncher] No Python found for {bridgeDir}.");
                return;
            }

            var args = $"-u -m kickr_bridge.server --port {port} --parent-pid {Process.GetCurrentProcess().Id} --watch-stdin";
            if (DemoMode) args += " --demo";

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = args,
                WorkingDirectory = bridgeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,   // our graceful shutdown channel
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, e) => Relay(e.Data, false);
                _process.ErrorDataReceived += (_, e) => Relay(e.Data, false);
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                Status = $"launched (pid {_process.Id})";
                Debug.Log($"[BridgeLauncher] {python} {args}\n[BridgeLauncher] {Status}");
            }
            catch (Exception exc)
            {
                Status = $"launch failed: {exc.Message}";
                Debug.LogError($"[BridgeLauncher] Could not start the bridge: {exc}");
                _process = null;
            }
        }

        void Relay(string line, bool isError)
        {
            if (string.IsNullOrEmpty(line)) return;
            // Called on a background thread: queue only, never touch Unity here.
            // Bounded so a chatty bridge can never grow this without limit.
            if (_pendingLog.Count < 500) _pendingLog.Enqueue(line);
        }

        static int ParsePort(string url)
        {
            try { return new Uri(url).Port; } catch { return TrainerLink.DefaultPort; }
        }

        static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        // --- shutdown ----------------------------------------------------------

        void OnApplicationQuit() => Shutdown();
        void OnDestroy() => Shutdown();

        public void Shutdown()
        {
            // A half-built virtualenv is harmless -- the next launch sees no venv
            // and starts again -- but leaving pip running after the window closes
            // is not something a user would ever guess at.
            var setup = _setup;
            _setup = null;
            if (setup != null)
            {
                try { if (!SafeHasExited(setup)) setup.Kill(); } catch { }
                try { setup.Dispose(); } catch { }
            }

            var proc = _process;
            _process = null;
            if (proc == null) return;

            try
            {
                if (SafeHasExited(proc)) return;

                // Politely first. Closing stdin afterwards gives EOF as a second
                // chance in case the line was missed.
                try
                {
                    proc.StandardInput.WriteLine("shutdown");
                    proc.StandardInput.Flush();
                    proc.StandardInput.Close();
                }
                catch (Exception exc)
                {
                    Debug.LogWarning($"[BridgeLauncher] could not signal the bridge: {exc.Message}");
                }

                int graceMs = Mathf.RoundToInt(Mathf.Max(0.5f, ShutdownGraceSeconds) * 1000f);
                if (!proc.WaitForExit(graceMs))
                {
                    Debug.LogWarning("[BridgeLauncher] bridge did not exit in time; killing it.");
                    try { proc.Kill(); proc.WaitForExit(1500); }
                    catch (Exception exc) { Debug.LogWarning($"[BridgeLauncher] kill failed: {exc.Message}"); }
                }
                else
                {
                    Debug.Log("[BridgeLauncher] bridge exited cleanly.");
                }
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }
    }
}
