using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// On-screen readout plus a course elevation profile with a position marker.
    /// IMGUI is used deliberately: it needs no prefabs, canvases or scene wiring,
    /// so the HUD survives the scene being regenerated from script.
    /// </summary>
    public class RideHud : MonoBehaviour
    {
        public BikeRider Rider;
        public TrainerLink Link;
        public RideWorld World;
        public BridgeLauncher Launcher;
        public RideMenu Menu;

        [Header("Profile strip")]
        public int ProfileWidth = 620;
        public int ProfileHeight = 92;

        Texture2D _profileTex;
        Texture2D _pixel;
        GUIStyle _label, _value, _small, _segment;
        float _lapStartDistance;
        int _laps;
        float _lastDistance;

        void Start()
        {
            _pixel = new Texture2D(1, 1);
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();

            // The profile strip is baked from the course, so it has to be thrown
            // away when the course changes or it keeps showing the old one.
            if (World != null) World.RouteChanged += OnRouteChanged;
        }

        void OnDestroy()
        {
            if (World != null) World.RouteChanged -= OnRouteChanged;
        }

        void OnRouteChanged()
        {
            if (_profileTex != null) Destroy(_profileTex);
            _profileTex = null;
            _laps = 0;
            _lastDistance = 0f;
        }

        void Update()
        {
            if (Rider == null) return;
            // Distance wraps at the end of the lap; catching the wrap is how we
            // count laps without needing the rider to track it.
            if (Rider.Distance < _lastDistance - 100f) _laps++;
            _lastDistance = Rider.Distance;
        }

        void BuildProfileTexture()
        {
            var route = World != null ? World.Route : null;
            if (route == null) return;

            int w = ProfileWidth, h = ProfileHeight;
            _profileTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            var profile = route.Profile;
            float minE = float.MaxValue, maxE = float.MinValue;
            var elevations = new float[w];
            for (int x = 0; x < w; x++)
            {
                float d = (x / (float)(w - 1)) * profile.TotalLength;
                float e = profile.ElevationAt(d);
                elevations[x] = e;
                if (e < minE) minE = e;
                if (e > maxE) maxE = e;
            }
            float span = Mathf.Max(1f, maxE - minE);

            var px = new Color32[w * h];
            for (int x = 0; x < w; x++)
            {
                float d = (x / (float)(w - 1)) * profile.TotalLength;
                float grade = profile.GradeAt(d);
                int col = Mathf.RoundToInt(((elevations[x] - minE) / span) * (h - 8)) + 3;

                // Colour the profile by gradient, the way a road book does:
                // green for easy, through amber, to red for anything brutal.
                Color32 fill = GradeColor(grade);
                for (int y = 0; y < h; y++)
                {
                    int i = y * w + x;
                    px[i] = y <= col ? fill : new Color32(0, 0, 0, 90);
                }
            }
            _profileTex.SetPixels32(px);
            _profileTex.filterMode = FilterMode.Bilinear;
            _profileTex.Apply();
        }

        static Color32 GradeColor(float grade)
        {
            float g = grade * 100f;
            if (g < -1f) return new Color32(90, 170, 230, 255);   // descent, blue
            if (g < 2f) return new Color32(105, 190, 110, 255);   // flat, green
            if (g < 5f) return new Color32(215, 200, 90, 255);    // rising, yellow
            if (g < 8f) return new Color32(230, 150, 60, 255);    // hard, orange
            return new Color32(220, 75, 65, 255);                 // brutal, red
        }

        void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(1f, 1f, 1f, 0.62f) } };
            _value = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(1f, 1f, 1f, 0.75f) } };
            _segment = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        }

        void Box(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _pixel);
            GUI.color = prev;
        }

        /// <summary>
        /// One column of the stat bar, sized to fit its own text.
        ///
        /// Fixed widths silently overlap the moment a value grows a character:
        /// at 132 px, GRADE was fine at "+7.9%" and ran straight into ELEV at
        /// "+12.9%". Measuring costs nothing here and cannot be outgrown.
        /// </summary>
        void Stat(ref float x, float y, string label, string value, float minWidth = 104f)
        {
            const float gutter = 24f;
            float w = Mathf.Max(minWidth,
                                _value.CalcSize(new GUIContent(value)).x + gutter,
                                _label.CalcSize(new GUIContent(label)).x + 14f);
            GUI.Label(new Rect(x, y, w, 18f), label, _label);
            GUI.Label(new Rect(x, y + 15f, w, 42f), value, _value);
            x += w;
        }

        /// <summary>h:mm:ss once past an hour, m:ss before that.</summary>
        static string Clock(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int h = total / 3600, m = (total % 3600) / 60, sec = total % 60;
            return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
        }

        void OnGUI()
        {
            if (Rider == null) return;
            EnsureStyles();
            if (_profileTex == null) BuildProfileTexture();

            float gradePct = Rider.Grade * 100f;
            var t = Link != null ? Link.Latest : null;
            bool live = Link != null && Link.Connected;

            // --- stat bar ---
            Box(new Rect(0f, 0f, Screen.width, 78f), new Color(0f, 0f, 0f, 0.55f));
            float x = 18f;
            Stat(ref x, 10f, "POWER", live ? $"{t.power_w:F0}w" : "--");
            Stat(ref x, 10f, "CADENCE", live ? $"{t.cadence_rpm:F0}" : "--", 96f);
            Stat(ref x, 10f, $"SPEED {Units.SpeedSuffix}", $"{Units.Speed(Rider.SpeedMps):F1}");
            Stat(ref x, 10f, "GRADE", $"{gradePct:+0.0;-0.0;0.0}%");
            Stat(ref x, 10f, "ELEV", Units.ElevationText(Rider.Elevation));
            Stat(ref x, 10f, "CLIMBED", Units.ElevationText(Rider.ElevationGain));
            Stat(ref x, 10f, "DIST", Units.DistanceText(Rider.Distance));
            Stat(ref x, 10f, "TIME", Clock(Rider.RideTime));

            // --- segment name ---
            GUI.Label(new Rect(18f, 88f, 520f, 26f), Rider.SegmentName, _segment);
            if (_laps > 0)
                GUI.Label(new Rect(18f, 112f, 300f, 20f), $"lap {_laps + 1}", _small);

            // --- elevation profile ---
            if (_profileTex != null && World != null && World.Route != null)
            {
                float pw = ProfileWidth, ph = ProfileHeight;
                float px = Screen.width - pw - 18f;
                float py = Screen.height - ph - 44f;

                Box(new Rect(px - 6f, py - 6f, pw + 12f, ph + 34f), new Color(0f, 0f, 0f, 0.55f));
                GUI.DrawTexture(new Rect(px, py, pw, ph), _profileTex);

                float frac = Rider.Distance / Mathf.Max(1f, World.Route.Length);
                Box(new Rect(px + frac * pw - 1f, py - 3f, 2f, ph + 6f), Color.white);

                GUI.Label(new Rect(px, py + ph + 4f, pw, 20f),
                    $"{Units.Distance(World.Route.Length):F1} {Units.DistanceSuffix} lap  ·  " +
                    $"{Units.ElevationText(World.Route.Profile.TotalAscent)} of climbing", _small);
            }

            DrawStatusPanel(t, live);
        }

        static readonly Color Good = new Color(0.42f, 0.85f, 0.48f);
        static readonly Color Warn = new Color(1f, 0.76f, 0.35f);
        static readonly Color Bad = new Color(0.95f, 0.45f, 0.40f);

        void Dot(float x, float y, Color c)
        {
            Box(new Rect(x, y, 9f, 9f), c);
        }

        /// <summary>
        /// Bridge and trainer state, with the REASON when something is wrong.
        /// A bare "not connected" sends you digging through logs; the reason
        /// usually tells you the answer outright.
        /// </summary>
        void DrawStatusPanel(Telemetry t, bool live)
        {
            // --- work out what to say ---
            string bridgeLine;
            Color bridgeColor;
            if (live)
            {
                bridgeLine = "connected" + (t.demo ? "  (demo rider)" : "");
                bridgeColor = t.demo ? Warn : Good;
            }
            else
            {
                // Show whatever the launcher is doing rather than a flat
                // "starting". First run builds a Python environment and takes the
                // better part of a minute, and an unexplained wait reads as a hang.
                // Show whatever the launcher last said, not just while it is
                // working. A bridge that could not be found starts no process,
                // so Busy is false and the panel used to read "offline" -- the
                // same thing it says when there is simply no trainer awake. A
                // hard failure looked exactly like an idle one, on the only
                // screen anybody was watching.
                string reason = Launcher != null ? Launcher.Status : null;
                bool worthSaying = !string.IsNullOrEmpty(reason) && reason != "not started";

                bridgeLine = Launcher != null && Launcher.Busy ? reason
                    : worthSaying ? $"{reason}  ·  hold W to pedal"
                    : "offline  ·  hold W to pedal, Shift to surge";
                bridgeColor = Bad;
            }

            string trainerLine, trainerDetail = "";
            Color trainerColor;
            if (!live)
            {
                trainerLine = "unknown (no bridge)";
                trainerColor = Bad;
            }
            else if (t.demo)
            {
                trainerLine = "not used in demo mode";
                trainerColor = Warn;
            }
            else
            {
                string state = string.IsNullOrEmpty(t.trainer_status) ? "unknown" : t.trainer_status;
                trainerLine = state;
                trainerDetail = t.trainer_detail ?? "";
                trainerColor = state == "connected" ? Good
                    : (state == "searching" || state == "retrying" || state == "starting") ? Warn
                    : Bad;
            }

            // --- layout ---
            bool showDetail = !string.IsNullOrEmpty(trainerDetail) && trainerColor != Good;
            float w = 640f;
            float h = showDetail ? 108f : 74f;
            float x = 18f;
            // Sit above the menu button rather than under it. The menu reports how
            // much room it needs, so this stays correct when the popup opens.
            float menuRoom = Menu != null ? Menu.OccupiedHeight : 0f;
            float y = Screen.height - h - 14f - menuRoom;

            Box(new Rect(x - 6f, y - 6f, w + 12f, h + 12f), new Color(0f, 0f, 0f, 0.62f));

            GUI.Label(new Rect(x, y, w, 18f), "BRIDGE", _label);
            Dot(x + 66f, y + 4f, bridgeColor);
            GUI.Label(new Rect(x + 82f, y - 2f, w - 82f, 22f), bridgeLine, _small);

            GUI.Label(new Rect(x, y + 24f, w, 18f), "TRAINER", _label);
            Dot(x + 66f, y + 28f, trainerColor);
            GUI.Label(new Rect(x + 82f, y + 22f, w - 82f, 22f), trainerLine, _small);

            if (showDetail)
            {
                var wrap = new GUIStyle(_small)
                {
                    wordWrap = true,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.62f) }
                };
                GUI.Label(new Rect(x, y + 46f, w, 56f), trainerDetail, wrap);
            }
            else if (live && !t.demo && t.trainer_status == "connected")
            {
                GUI.Label(new Rect(x + 82f, y + 44f, w - 82f, 20f),
                    $"{t.power_w:F0} W · {t.cadence_rpm:F0} rpm", _label);
            }
        }
    }
}
