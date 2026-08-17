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

            float startDistance = Num("-startdistance", -1f);
            if (startDistance >= 0f && Rider != null)
            {
                Rider.Jump(startDistance);
                Debug.Log($"[AutoScreenshot] jumped rider to {startDistance:F0} m");
            }

            float delay = Num("-shotdelay", 20f);
            Debug.Log($"[AutoScreenshot] capturing to {path} in {delay:F0}s");
            yield return new WaitForSeconds(delay);

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
