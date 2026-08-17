using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Bottom-left menu button and its popup: Exit, Regenerate, and a metric /
    /// imperial switch.
    ///
    /// IMGUI to match the rest of the HUD -- no canvas, no prefabs, so it survives
    /// the scene being regenerated from script.
    /// </summary>
    public class RideMenu : MonoBehaviour
    {
        public WorldRegenerator Regenerator;

        [Header("Layout")]
        public float ButtonWidth = 104f;
        public float ButtonHeight = 34f;
        public float Margin = 16f;

        public bool IsOpen { get; private set; }
        /// <summary>Height the menu occupies, so the HUD can sit clear of it.</summary>
        public float OccupiedHeight => ButtonHeight + Margin + (IsOpen ? PanelHeight + 8f : 0f);

        const float PanelWidth = 268f;
        const float PanelHeight = 186f;

        Texture2D _pixel;
        GUIStyle _button, _label, _small, _title;

        void Start()
        {
            _pixel = new Texture2D(1, 1);
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();

            // -menuopen starts with the popup showing, so screenshots can capture
            // it without anyone clicking.
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-menuopen") >= 0)
                IsOpen = true;
        }

        void Update()
        {
            // Escape toggles rather than quits: quitting on Escape mid-ride, with
            // no confirmation, is a good way to lose a session by accident.
            if (Input.GetKeyDown(KeyCode.Escape)) IsOpen = !IsOpen;
        }

        void EnsureStyles()
        {
            if (_button != null) return;
            _button = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(1f, 1f, 1f, 0.72f) } };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(1f, 1f, 1f, 0.45f) } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 1f, 1f, 0.55f) } };
        }

        void Box(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _pixel);
            GUI.color = prev;
        }

        /// <summary>
        /// A sliding two-position switch. Reads as a physical toggle rather than
        /// a checkbox, which suits a binary choice better than a real slider
        /// would -- a continuous control with two valid stops feels broken.
        /// </summary>
        bool UnitSwitch(Rect r, bool imperial)
        {
            var track = new Color(1f, 1f, 1f, 0.14f);
            var knob = new Color(0.45f, 0.72f, 1f, 0.95f);

            Box(r, track);
            float half = r.width * 0.5f;
            var knobRect = new Rect(imperial ? r.x + half : r.x, r.y, half, r.height);
            Box(knobRect, knob);

            var on = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.05f, 0.09f, 0.15f) }
            };
            var off = new GUIStyle(on) { normal = { textColor = new Color(1f, 1f, 1f, 0.6f) }, fontStyle = FontStyle.Normal };

            GUI.Label(new Rect(r.x, r.y, half, r.height), "METRIC", imperial ? off : on);
            GUI.Label(new Rect(r.x + half, r.y, half, r.height), "IMPERIAL", imperial ? on : off);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) return !imperial;
            return imperial;
        }

        void OnGUI()
        {
            EnsureStyles();

            bool busy = Regenerator != null && Regenerator.Busy;
            if (busy) DrawBusyOverlay();

            float bx = Margin;
            float by = Screen.height - ButtonHeight - Margin;

            // --- the popup, drawn above the button ---
            if (IsOpen && !busy)
            {
                float px = bx;
                float py = by - PanelHeight - 8f;
                Box(new Rect(px - 4f, py - 4f, PanelWidth + 8f, PanelHeight + 8f), new Color(0f, 0f, 0f, 0.82f));

                float y = py + 12f;
                GUI.Label(new Rect(px + 14f, y, PanelWidth - 28f, 18f), "UNITS", _title);
                y += 20f;
                bool nowImperial = UnitSwitch(new Rect(px + 14f, y, PanelWidth - 28f, 28f), Units.Imperial);
                if (nowImperial != Units.Imperial) Units.Imperial = nowImperial;
                y += 42f;

                GUI.Label(new Rect(px + 14f, y, PanelWidth - 28f, 18f), "WORLD", _title);
                y += 20f;
                if (GUI.Button(new Rect(px + 14f, y, PanelWidth - 28f, 30f), "Regenerate", _button))
                {
                    if (Regenerator != null) Regenerator.Regenerate();
                    IsOpen = false;
                }
                y += 32f;
                GUI.Label(new Rect(px + 14f, y, PanelWidth - 28f, 16f),
                          "New seed: fresh terrain and a new course", _small);
                y += 24f;

                if (GUI.Button(new Rect(px + 14f, y, PanelWidth - 28f, 30f), "Exit", _button))
                    Quit();
            }

            // --- the button ---
            GUI.enabled = !busy;
            if (GUI.Button(new Rect(bx, by, ButtonWidth, ButtonHeight),
                           IsOpen ? "CLOSE" : "MENU", _button))
                IsOpen = !IsOpen;
            GUI.enabled = true;
        }

        void DrawBusyOverlay()
        {
            Box(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.55f));

            float w = 420f, h = 74f;
            float x = (Screen.width - w) * 0.5f, y = (Screen.height - h) * 0.5f;
            Box(new Rect(x - 6f, y - 6f, w + 12f, h + 12f), new Color(0f, 0f, 0f, 0.8f));

            var head = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            GUI.Label(new Rect(x + 14f, y + 8f, w - 28f, 24f),
                      $"Regenerating world - {Regenerator.Stage}", head);

            var barRect = new Rect(x + 14f, y + 40f, w - 28f, 12f);
            Box(barRect, new Color(1f, 1f, 1f, 0.13f));
            Box(new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(Regenerator.Progress), barRect.height),
                new Color(0.45f, 0.78f, 1f, 0.95f));
            GUI.Label(new Rect(x + 14f, y + 52f, w - 28f, 18f),
                      $"{Regenerator.Progress * 100f:F0}%", _small);
        }

        void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            // BridgeLauncher's OnApplicationQuit stops the bridge, so this is
            // enough to leave nothing running behind us.
            Application.Quit();
#endif
        }
    }
}
