using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Renders stills of the generated world from batch mode, so the terrain can
    /// actually be looked at without launching the editor. Picks its viewpoints
    /// from the course itself -- steepest ramp, high point, low point -- rather
    /// than from hardcoded coordinates that would go stale the moment the seed
    /// or the profile changes.
    /// </summary>
    public static class SceneCapture
    {
        const string OutDir = "Captures";
        const int Width = 1600;
        const int Height = 900;

        [MenuItem("VibeRide/Capture Views")]
        public static void Capture()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Ride.unity");
            var settings = new WorldSettings();
            var route = WorldGen.BuildRoute(settings);
            var profile = route.Profile;

            string dir = Path.Combine(Directory.GetCurrentDirectory(), OutDir);
            Directory.CreateDirectory(dir);

            // Scan the course for the points worth photographing.
            float steepD = 0f, steepG = -99f, highD = 0f, highE = -99999f, lowD = 0f, lowE = 99999f;
            int steps = 3000;
            for (int i = 0; i < steps; i++)
            {
                float d = (i / (float)steps) * profile.TotalLength;
                float g = profile.GradeAt(d);
                float e = profile.ElevationAt(d);
                if (g > steepG) { steepG = g; steepD = d; }
                if (e > highE) { highE = e; highD = d; }
                if (e < lowE) { lowE = e; lowD = d; }
            }
            Debug.Log($"[Capture] steepest {steepG * 100f:F1}% at {steepD / 1000f:F2} km; " +
                      $"high {highE:F0} m at {highD / 1000f:F2} km; low {lowE:F0} m at {lowD / 1000f:F2} km");

            var cam = new GameObject("CaptureCam").AddComponent<Camera>();
            cam.farClipPlane = 12000f;
            cam.nearClipPlane = 0.2f;
            cam.fieldOfView = 62f;
            cam.clearFlags = CameraClearFlags.Skybox;

            RiderShot(cam, route, 0f, dir, "01_start");
            RiderShot(cam, route, steepD, dir, "02_steepest");
            RiderShot(cam, route, highD, dir, "03_summit");
            RiderShot(cam, route, highD + 900f, dir, "04_descent");
            RiderShot(cam, route, lowD, dir, "05_valley");

            // Oblique aerial rather than straight down: a top-down shot from
            // 8 km up is entirely fog and shows nothing about the relief.
            float s = settings.TerrainSize;
            cam.transform.position = new Vector3(s * 0.5f, 2600f, s * 0.06f);
            cam.transform.rotation = Quaternion.Euler(26f, 0f, 0f);
            Shoot(cam, dir, "06_overview");

            // Side-on across the steepest ramp, from off the road looking at it,
            // so the gradient can be judged against the horizon instead of being
            // foreshortened away by looking straight up the climb.
            Vector3 sp = route.PositionAt(steepD);
            Vector3 sf = route.ForwardAt(steepD, 8f);
            Vector3 side = Vector3.Cross(Vector3.up, new Vector3(sf.x, 0f, sf.z).normalized);
            cam.transform.position = sp + side * 190f + Vector3.up * 55f;
            cam.transform.rotation = Quaternion.LookRotation(sp - cam.transform.position, Vector3.up);
            Shoot(cam, dir, "07_climb_side");

            UnityEngine.Object.DestroyImmediate(cam.gameObject);

            // Known limitation: these stills only ever show terrain layer 0.
            // Headless batchmode does not have the terrain splat shader variants,
            // so rock and snow are missing from captures no matter what the
            // alphamap contains -- verified by forcing it to 100% rock and getting
            // a pixel-identical green frame. The layer weights ARE correct on the
            // asset (WorldBuilder logs and reads them back). Judge the texturing
            // in the editor, not from these images. Geometry and layout are
            // faithful here; only the ground texturing is unrepresentative.
            Debug.Log("[Capture] NOTE: batchmode renders terrain layer 0 only -- " +
                      "rock/snow will be missing from these stills. Open the scene " +
                      "in the editor to judge terrain texturing.");
            Debug.Log($"[Capture] wrote stills to {dir}");
        }

        /// <summary>Camera placed where the chase cam would sit at this distance.</summary>
        static void RiderShot(Camera cam, RoutePath route, float distance, string dir, string name)
        {
            Vector3 pos = route.PositionAt(distance);
            Vector3 fwd = route.ForwardAt(distance, 8f);
            Vector3 flat = new Vector3(fwd.x, 0f, fwd.z).normalized;

            cam.transform.position = pos - flat * 7.5f + Vector3.up * 2.6f;
            cam.transform.rotation = Quaternion.LookRotation(
                (pos + Vector3.up * 1.4f) - cam.transform.position, Vector3.up);
            Shoot(cam, dir, name);
        }

        static void Shoot(Camera cam, string dir, string name)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4
            };
            var prev = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();

                string path = Path.Combine(dir, name + ".png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                RenderTexture.active = prev;
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        public static void CaptureFromCommandLine()
        {
            try { Capture(); EditorApplication.Exit(0); }
            catch (Exception exc)
            {
                Debug.LogError($"[Capture] FAILED: {exc}");
                EditorApplication.Exit(1);
            }
        }
    }
}
