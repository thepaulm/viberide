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
                                  Flag("-statueportrait")))
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
