using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Captures a screenshot from the running player and optionally quits.
    /// Driven entirely by command-line switches so it costs nothing in normal use.
    ///
    ///   -screenshot &lt;path&gt;     absolute path to write a PNG to
    ///   -shotdelay &lt;seconds&gt;   wait before capturing (default 20)
    ///   -startdistance &lt;m&gt;     jump the rider to this point on the course first
    ///   -shotquit               exit once the file is written
    ///
    /// Uses ScreenCapture rather than a desktop grab: it reads the game's own
    /// framebuffer, so the result is correct even if the window is occluded, and
    /// nothing else on the desktop can leak into the image.
    /// </summary>
    public class AutoScreenshot : MonoBehaviour
    {
        public BikeRider Rider;
        public WorldRegenerator Regenerator;
        public PropScatter Scatter;
        public PlaneFlyby Flyby;
        public HilltopStatue Statue;
        public LakeSurfaces Water;
        public Volcano Volcano;

        void Start() => StartCoroutine(Run());

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        static bool Flag(string name) =>
            Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;

        static float Num(string name, float fallback)
        {
            var raw = Arg(name);
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : fallback;
        }

        IEnumerator Run()
        {
            string path = Arg("-screenshot");
            if (string.IsNullOrEmpty(path)) yield break;

            // Wait out any regenerate before positioning the rider: regeneration
            // finishes by resetting distance to zero, so jumping first would be
            // silently undone.
            yield return null;
            if (Regenerator != null)
            {
                while (Regenerator.Busy) yield return null;
                yield return null;
            }

            // -startnear <kind> puts the rider just before a placed instance of
            // that kind. A dinosaur at ~1 per km is otherwise a long hunt.
            string near = Arg("-startnear");
            float startDistance = Num("-startdistance", -1f);
            if (!string.IsNullOrEmpty(near) && Scatter != null &&
                Scatter.PlacedAt.TryGetValue(near, out var spots) && spots.Count > 0)
            {
                int which = Mathf.Clamp(Mathf.RoundToInt(Num("-startnearindex", 0f)), 0, spots.Count - 1);
                startDistance = Mathf.Max(0f, spots[which] - 55f);
                Debug.Log($"[AutoScreenshot] -startnear {near}: {spots.Count} placed, " +
                          $"using #{which} at {spots[which]:F0} m");
            }
            // -startnearstatue uses the viewpoint the statue already found for
            // itself. Working it out here as well was how an earlier version came
            // to frame a spot with a mountain in the way: two pieces of code
            // deciding separately what "visible" means, and only one of them
            // checking line of sight.
            if (Flag("-startnearstatue") && Statue != null && Statue.Placed)
            {
                startDistance = Statue.BestViewDistance;
                Debug.Log($"[AutoScreenshot] -startnearstatue: km {startDistance / 1000f:F1}, " +
                          $"monument in view over {Statue.VisibleRoadMetres:F0} m of road");
            }

            // -startnearlake [n] frames a lake the same way, asking the lake
            // itself where it can be seen from.
            if (Flag("-startnearlake") && Water != null && Water.LakeCount > 0 &&
                Statue != null && Statue.World != null && Statue.World.Route != null)
            {
                int which = Mathf.Clamp(Mathf.RoundToInt(Num("-startnearlake", 0f)),
                                        0, Water.LakeCount - 1);
                if (Water.TryBestView(Statue.World.Route, which, out float lakeView))
                    startDistance = lakeView;
                else
                    Debug.LogWarning($"[AutoScreenshot] lake {which} has no clear view");
            }

            // -startnearvolcano [n] stands the rider on the stretch of road the
            // plume is best seen from, backed off so it is ahead rather than
            // overhead.
            if (Flag("-startnearvolcano") && Volcano != null && Volcano.Count > 0 &&
                Statue != null && Statue.World != null && Statue.World.Route != null)
            {
                var route = Statue.World.Route;
                int which = Mathf.Clamp(Mathf.RoundToInt(Num("-startnearvolcano", 0f)),
                                        0, Volcano.Count - 1);
                // Ask the volcano where it can be seen from rather than
                // standing at the nearest point of road and hoping.
                if (Volcano.TryBestView(route, which, out float view))
                {
                    startDistance = view;
                    Debug.Log($"[AutoScreenshot] volcano {which} best seen from km {view / 1000f:F1}");
                }
                else Debug.LogWarning($"[AutoScreenshot] volcano {which} has no clear view");
            }

            if (startDistance >= 0f && Rider != null)
            {
                Rider.Jump(startDistance);
                Debug.Log($"[AutoScreenshot] jumped rider to {startDistance:F0} m");
            }

            // Hold position for shots that were aimed at a specific spot. Without
            // this the rider freewheels downhill while the camera settles and the
            // frame is no longer the one the placement search picked -- measured at
            // 90 m of drift over a five second delay, enough to swing the subject
            // from mid-frame to behind the stat bar.
            if (Rider != null && (Flag("-holdstill") || Flag("-startnearstatue") ||
                                  Flag("-statueportrait") || Flag("-startnearlake") ||
                                  Flag("-startnearvolcano")))
                Rider.Frozen = true;

            // Trigger a flyby only now that the rider is in place, with a short
            // run-in so it reaches the crossing point within the shot delay.
            if (System.Array.IndexOf(Environment.GetCommandLineArgs(), "-flyby") >= 0 && Flyby != null)
            {
                Flyby.TriggerNow(Num("-flybyapproach", 420f));
                Debug.Log("[AutoScreenshot] flyby triggered");
            }

            // -statueportrait parks the camera beside the monument. Judging a
            // model from a 70 px smudge on a hillside is guesswork; this separates
            // "is the sculpture any good" from "is it placed and framed well",
            // which are two different bugs with two different fixes.
            if (Flag("-statueportrait") && Statue != null && Statue.Monument != null)
            {
                var cam = Camera.main;
                var chase = cam != null ? cam.GetComponent<ChaseCamera>() : null;
                if (chase != null) chase.enabled = false;
                if (cam != null)
                {
                    Transform m = Statue.Monument;
                    float range = Num("-portraitrange", 105f);
                    Vector3 aim = m.position + Vector3.up * Statue.TotalHeight * 0.55f;
                    // m.right is square to the bike's axis: the profile view.
                    Vector3 dir = Quaternion.Euler(0f, Num("-portraitangle", 0f), 0f) * m.right;
                    cam.transform.position = aim + dir * range + Vector3.up * range * 0.18f;
                    cam.transform.LookAt(aim);
                    Debug.Log($"[AutoScreenshot] statue portrait at {range:F0} m");
                }
            }

            // -lakeportrait looks down on a lake from above. Same reason as the
            // statue portrait: it separates "does the water render and does the
            // basin look right" from "can you see it from the road", which are
            // different failures wanting different fixes.
            if (Flag("-volcanoportrait") && Volcano != null && Volcano.Count > 0)
            {
                int which = Mathf.Clamp(Mathf.RoundToInt(Num("-volcanoportrait", 0f)),
                                        0, Volcano.Count - 1);
                Vector3 peak = Volcano.Peaks[which];
                var cam = Camera.main;
                var chase = cam != null ? cam.GetComponent<ChaseCamera>() : null;
                if (chase != null) chase.enabled = false;
                if (cam != null)
                {
                    float range = Num("-volcanorange", 900f);
                    cam.transform.position = peak + new Vector3(-range * 0.8f, range * 0.35f, -range * 0.6f);
                    cam.transform.LookAt(peak + Vector3.up * range * 0.25f);
                    // The plume climbs for its whole 14 s life, so it can easily
                    // top out past a far plane set for a bike ride.
                    if (cam.farClipPlane < range * 4f) cam.farClipPlane = range * 4f;
                    Debug.Log($"[AutoScreenshot] volcano portrait {which} from {range:F0} m, " +
                              $"far clip {cam.farClipPlane:F0} m");
                }
            }

            if (Flag("-lakeportrait") && Water != null && Water.LakeCount > 0)
            {
                int which = Mathf.Clamp(Mathf.RoundToInt(Num("-lakeportrait", 0f)),
                                        0, Water.LakeCount - 1);
                var lk = Water.Lakes[which];
                var cam = Camera.main;
                var chase = cam != null ? cam.GetComponent<ChaseCamera>() : null;
                if (chase != null) chase.enabled = false;
                if (cam != null)
                {
                    var aim = new Vector3(lk.Centre.x, lk.WaterLevel, lk.Centre.y);
                    float height = Num("-lakeheight", 260f);
                    cam.transform.position = aim + new Vector3(0f, height, -height * 0.75f);
                    cam.transform.LookAt(aim);
                    Debug.Log($"[AutoScreenshot] lake portrait {which} from {height:F0} m up");
                }
            }

            float delay = Num("-shotdelay", 20f);
            Debug.Log($"[AutoScreenshot] capturing to {path} in {delay:F0}s");
            yield return new WaitForSeconds(delay);

            // Where did the monument actually land in frame, and how big is it?
            // The aircraft work established that this is the only reliable way to
            // answer either question -- three "it must be visible now" guesses in
            // a row were all wrong, and one log line settled it.
            if (Statue != null && Statue.Placed)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 baseVp = cam.WorldToViewportPoint(Statue.Position);
                    Vector3 topVp = cam.WorldToViewportPoint(
                        Statue.Position + Vector3.up * Statue.TotalHeight);
                    bool onScreen = baseVp.z > 0f && baseVp.x > 0f && baseVp.x < 1f &&
                                    topVp.y > 0f && baseVp.y < 1f;
                    float px = Mathf.Abs(topVp.y - baseVp.y) * Screen.height;
                    // Distance from the top of the frame down to the statue's head.
                    // The stat bar owns the first 78 px, so this must exceed that
                    // or the salute is hidden behind the telemetry.
                    float headFromTop = (1f - topVp.y) * Screen.height;
                    const float statBar = 78f;
                    Debug.Log($"[AutoScreenshot] statue viewport ({baseVp.x:F2},{baseVp.y:F2}) " +
                              $"depth {baseVp.z:F0} m, {px:F0} px tall, head {headFromTop:F0} px " +
                              $"from top ({(headFromTop > statBar ? "clears" : "BEHIND")} stat bar), " +
                              $"{(onScreen ? "ON SCREEN" : "OFF SCREEN")}");
                }
            }

            if (Water != null && Water.LakeCount > 0)
            {
                var cam = Camera.main;
                for (int i = 0; i < Water.LakeCount && cam != null; i++)
                {
                    var lk = Water.Lakes[i];
                    var centre = new Vector3(lk.Centre.x, lk.WaterLevel, lk.Centre.y);
                    Vector3 vp = cam.WorldToViewportPoint(centre);
                    bool onScreen = vp.z > 0f && vp.x > 0f && vp.x < 1f && vp.y > 0f && vp.y < 1f;
                    Debug.Log($"[AutoScreenshot] lake {i} centre viewport ({vp.x:F2},{vp.y:F2}) " +
                              $"depth {vp.z:F0} m, {(onScreen ? "ON SCREEN" : "off screen")}");
                }
                Debug.Log($"[AutoScreenshot] {Water.SurfaceReport()}");
            }

            if (Volcano != null && Volcano.Count > 0)
            {
                var cam = Camera.main;
                for (int i = 0; i < Volcano.Count && cam != null; i++)
                {
                    Vector3 peak = Volcano.Peaks[i];
                    Vector3 vp = cam.WorldToViewportPoint(peak);
                    Vector3 vpTop = cam.WorldToViewportPoint(peak + Vector3.up * 260f);
                    Vector3 to = peak - cam.transform.position;
                    float flat = new Vector2(to.x, to.z).magnitude;
                    Debug.Log($"[AutoScreenshot] volcano {i} summit viewport " +
                              $"({vp.x:F2},{vp.y:F2}) plume top ({vpTop.x:F2},{vpTop.y:F2}) " +
                              $"depth {vp.z:F0} m, {Mathf.Atan2(to.y, flat) * Mathf.Rad2Deg:F0} deg up, " +
                              $"far clip {cam.farClipPlane:F0} m");
                }
                Debug.Log($"[AutoScreenshot] {Volcano.PlumeReport()}");
            }

            ScreenCapture.CaptureScreenshot(path);

            // CaptureScreenshot completes at end of frame and the write is not
            // instant, so give it a few frames before quitting or the file can be
            // truncated or missing entirely.
            for (int i = 0; i < 10; i++) yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(1.5f);

            Debug.Log($"[AutoScreenshot] wrote {path}");
            if (Flag("-shotquit")) Application.Quit();
        }
    }
}
