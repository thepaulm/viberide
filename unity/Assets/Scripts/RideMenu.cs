using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Bottom-left menu button and its popup: units, regenerate, save, load, exit.
    ///
    /// IMGUI to match the rest of the HUD -- no canvas, no prefabs, so it survives
    /// the scene being regenerated from script.
    /// </summary>
    public class RideMenu : MonoBehaviour
    {
        public WorldRegenerator Regenerator;
        public RideWorld World;

        [Header("Layout")]
        public float ButtonWidth = 104f;
        public float ButtonHeight = 34f;
        public float Margin = 16f;

        enum Page { Main, Generate, SaveAs, Load }

        public bool IsOpen { get; private set; }
        Page _page = Page.Main;

        /// <summary>Height the menu occupies, so the HUD can sit clear of it.</summary>
        public float OccupiedHeight => ButtonHeight + Margin + (IsOpen ? PanelHeight + 8f : 0f);

        // Wide enough for a saved row's "distance / climbing / date" line to fit
        // beside its Load and delete buttons without the date being clipped.
        const float NarrowWidth = 372f;
        const float RowHeight = 30f;

        // The generate page is set while looking at it from a bike, at arm's
        // length, usually mid-ride. Everything on it is drawn at roughly double
        // the linear size of the other pages -- four times the area -- and the
        // slider gets a track and thumb of its own rather than the 18 px default,
        // which is a hard target to hit with a mouse and unreadable from further
        // away than a desk.
        float PanelWidth => _page == Page.Generate ? 760f : NarrowWidth;

        float PanelHeight => _page switch
        {
            Page.Generate => 428f,
            Page.SaveAs => 150f,
            Page.Load => 92f + Mathf.Clamp(SavedCourses.All.Count, 1, 5) * 42f,
            _ => 288f,
        };

        Texture2D _pixel;
        GUIStyle _button, _small, _title, _rowName, _rowMeta;
        GUIStyle _bigLabel, _bigHint, _bigTitle, _bigButton, _bigSlider, _bigThumb;
        string _saveName = "";
        bool _focusPending;
        Vector2 _scroll;
        string _toast;
        float _toastUntil;

        void Start()
        {
            _pixel = new Texture2D(1, 1);
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();

            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-menuopen") >= 0)
                IsOpen = true;

            // -menupage saveas|load, so each page can be captured for docs
            Debug.Log($"[VibeRide] version {Application.version}, " +
                      $"running from {Application.dataPath}");

            // without anyone clicking through to it.
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-menupage");
            if (i >= 0 && i + 1 < args.Length)
            {
                IsOpen = true;
                if (args[i + 1].Equals("saveas", System.StringComparison.OrdinalIgnoreCase)) OpenSaveAs();
                else if (args[i + 1].Equals("load", System.StringComparison.OrdinalIgnoreCase)) _page = Page.Load;
                else if (args[i + 1].Equals("generate", System.StringComparison.OrdinalIgnoreCase)) OpenGenerate();
            }
        }

        void Update()
        {
            // Escape toggles rather than quits: quitting on Escape mid-ride, with
            // no confirmation, is a good way to lose a session by accident.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (IsOpen && _page != Page.Main) _page = Page.Main;
                else IsOpen = !IsOpen;
            }
        }

        void OpenSaveAs()
        {
            _page = Page.SaveAs;
            var profile = World != null && World.Route != null ? World.Route.Profile : null;
            _saveName = SavedCourses.SuggestName(profile, World != null ? World.Seed : 0);
            _focusPending = true;
        }

        void Toast(string message)
        {
            _toast = message;
            _toastUntil = Time.unscaledTime + 3f;
        }

        void EnsureStyles()
        {
            if (_button != null) return;
            _button = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(1f, 1f, 1f, 0.45f) } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 1f, 1f, 0.55f) } };
            _rowName = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _rowMeta = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(1f, 1f, 1f, 0.5f) } };
        }

        void Box(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _pixel);
            GUI.color = prev;
        }

        /// <summary>
        /// A sliding two-position switch. Reads as a physical toggle, which suits
        /// a binary choice better than a real slider would -- a continuous control
        /// with two valid stops feels broken.
        /// </summary>
        bool UnitSwitch(Rect r, bool imperial)
        {
            Box(r, new Color(1f, 1f, 1f, 0.14f));
            float half = r.width * 0.5f;
            Box(new Rect(imperial ? r.x + half : r.x, r.y, half, r.height),
                new Color(0.45f, 0.72f, 1f, 0.95f));

            var on = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.05f, 0.09f, 0.15f) }
            };
            var off = new GUIStyle(on)
            {
                normal = { textColor = new Color(1f, 1f, 1f, 0.6f) }, fontStyle = FontStyle.Normal
            };

            GUI.Label(new Rect(r.x, r.y, half, r.height), "METRIC", imperial ? off : on);
            GUI.Label(new Rect(r.x + half, r.y, half, r.height), "IMPERIAL", imperial ? on : off);

            return GUI.Button(r, GUIContent.none, GUIStyle.none) ? !imperial : imperial;
        }

        void OnGUI()
        {
            EnsureStyles();

            bool busy = Regenerator != null && Regenerator.Busy;
            if (busy) DrawBusyOverlay();

            float bx = Margin;
            float by = Screen.height - ButtonHeight - Margin;

            if (IsOpen && !busy)
            {
                float px = bx;
                float ph = PanelHeight;
                float py = by - ph - 8f;
                Box(new Rect(px - 4f, py - 4f, PanelWidth + 8f, ph + 8f), new Color(0f, 0f, 0f, 0.85f));

                switch (_page)
                {
                    case Page.Generate: DrawGenerate(px, py); break;
                    case Page.SaveAs: DrawSaveAs(px, py); break;
                    case Page.Load: DrawLoad(px, py, ph); break;
                    default: DrawMain(px, py); break;
                }
            }

            GUI.enabled = !busy;
            if (GUI.Button(new Rect(bx, by, ButtonWidth, ButtonHeight),
                           IsOpen ? "CLOSE" : "MENU", _button))
            {
                if (IsOpen && _page != Page.Main) _page = Page.Main;
                else IsOpen = !IsOpen;
            }
            GUI.enabled = true;

            if (_toast != null && Time.unscaledTime < _toastUntil)
            {
                var style = new GUIStyle(_small) { normal = { textColor = new Color(0.6f, 0.95f, 0.65f) } };
                GUI.Label(new Rect(bx + ButtonWidth + 10f, by + 9f, 420f, 20f), _toast, style);
            }
        }

        void DrawMain(float px, float py)
        {
            float y = py + 12f;
            float w = PanelWidth - 28f;

            GUI.Label(new Rect(px + 14f, y, w, 18f), "UNITS", _title);
            y += 20f;
            bool imperial = UnitSwitch(new Rect(px + 14f, y, w, 28f), Units.Imperial);
            if (imperial != Units.Imperial) Units.Imperial = imperial;
            y += 42f;

            GUI.Label(new Rect(px + 14f, y, w, 18f), "WORLD", _title);
            y += 20f;

            if (GUI.Button(new Rect(px + 14f, y, w, RowHeight), "Regenerate", _button))
                OpenGenerate();
            y += RowHeight + 2f;
            GUI.Label(new Rect(px + 14f, y, w, 16f), "Fresh terrain, and a course you choose", _small);
            y += 22f;

            float half = (w - 8f) * 0.5f;
            if (GUI.Button(new Rect(px + 14f, y, half, RowHeight), "Save As", _button)) OpenSaveAs();
            if (GUI.Button(new Rect(px + 14f + half + 8f, y, half, RowHeight), "Load", _button))
            {
                SavedCourses.Reload();
                _page = Page.Load;
                _scroll = Vector2.zero;
            }
            y += RowHeight + 2f;
            GUI.Label(new Rect(px + 14f, y, w, 16f),
                      $"{SavedCourses.All.Count} saved", _small);
            y += 24f;

            if (GUI.Button(new Rect(px + 14f, y, w, RowHeight), "Exit", _button)) Quit();
            y += RowHeight + 4f;

            // Which build this is. Absent it, "the app is behaving like the old
            // one" cannot be told apart from "the app IS the old one" -- and with
            // copies capable of sitting in /Applications, ~/Applications and a
            // Downloads folder at once, that is not a rare confusion.
            GUI.Label(new Rect(px + 14f, y, w, 16f), $"VibeRide {Application.version}", _small);
        }

        // Bounds for the two sliders.
        //
        // 40 km is not arbitrary: a longer lap grows the terrain to hold it, and
        // past roughly this the 2049-sample heightmap is stretched far enough
        // that the ground starts to look smoothed.
        const float MinLapM = 8000f;
        // 60 miles. The loop grows the map to fit rather than costing memory --
        // the heightmap stays at 2049 samples either way -- so the price of a lap
        // this long is texel size: 4.9 m on a default world, 17.6 m here. That
        // shows up as gentler ground near the road with the relief pushed out to
        // the horizon, which still reads as open country. It is not free past
        // this point: keep going and the texel approaches the width of the road's
        // own bench, and then the road stops being able to sit flat.
        const float MaxLapM = 96600f;

        // Climbing is expressed per kilometre because that is what bounds it, and
        // 34 is measured rather than reasoned. Gradients are already near the 13%
        // ceiling when the generator hands a course over, so how much a course
        // can be scaled up depends on how steep it came out: across seeds the
        // reachable figure ran 22 to 35 m/km, which no single slider bound can
        // honour. Searching course seeds for one that fits (see
        // WorldGen.FitAscent) lifted the worst case to 34.2, so 34 is a promise
        // every seed can keep.
        const float MinClimbPerKm = 5f;
        const float MaxClimbPerKm = 34f;

        float _genLapM = 25000f;
        float _genClimbM = 600f;

        void OpenGenerate()
        {
            // Start from the ride currently under the wheels, so the sliders open
            // where the rider already is rather than at some default.
            var route = World != null ? World.Route : null;
            if (route != null)
            {
                _genLapM = Mathf.Clamp(route.Length, MinLapM, MaxLapM);
                _genClimbM = route.Profile.TotalAscent;
            }
            _genClimbM = Mathf.Clamp(_genClimbM, MinClimb(_genLapM), MaxClimb(_genLapM));
            _page = Page.Generate;
        }

        static float MinClimb(float lapM) => lapM / 1000f * MinClimbPerKm;
        static float MaxClimb(float lapM) => lapM / 1000f * MaxClimbPerKm;

        /// <summary>Plain-language name for how hilly a lap is, so the figure
        /// means something without doing the division yourself -- and without
        /// picking a unit, which the rider may have set either way.</summary>
        static string Character(float lapM, float climbM)
        {
            float perKm = climbM / Mathf.Max(0.1f, lapM / 1000f);
            if (perKm < 10f) return "flat";
            if (perKm < 20f) return "rolling";
            if (perKm < 32f) return "hilly";
            if (perKm < 45f) return "mountainous";
            return "brutal";
        }

        void DrawGenerate(float px, float py)
        {
            EnsureBigStyles();

            float pad = 28f;
            float y = py + 24f;
            float w = PanelWidth - pad * 2f;

            GUI.Label(new Rect(px + pad, y, w, 26f), "NEW WORLD", _bigTitle);
            y += 40f;

            GUI.Label(new Rect(px + pad, y, w, 40f),
                      $"Distance      {Units.DistanceText(_genLapM)}", _bigLabel);
            y += 48f;
            // Snapped, because without it the number jitters by tens of metres
            // as the mouse moves -- and snapped in display units, so the steps
            // land on round numbers whichever unit is switched on.
            _genLapM = Mathf.Clamp(
                Units.SnapDistance(
                    GUI.HorizontalSlider(new Rect(px + pad, y, w, 34f),
                                         _genLapM, MinLapM, MaxLapM,
                                         _bigSlider, _bigThumb)),
                MinLapM, MaxLapM);
            y += 70f;

            // The ceiling moves with the distance, so shortening the lap under a
            // high climb setting has to bring the climb down with it.
            float lo = MinClimb(_genLapM), hi = MaxClimb(_genLapM);
            _genClimbM = Mathf.Clamp(_genClimbM, lo, hi);

            GUI.Label(new Rect(px + pad, y, w, 40f),
                      $"Climbing      {Units.ElevationText(_genClimbM)}", _bigLabel);
            y += 48f;
            _genClimbM = Mathf.Clamp(
                Units.SnapElevation(
                    GUI.HorizontalSlider(new Rect(px + pad, y, w, 34f),
                                         _genClimbM, lo, hi,
                                         _bigSlider, _bigThumb)),
                lo, hi);
            y += 64f;

            GUI.Label(new Rect(px + pad, y, w, 28f),
                      $"{Character(_genLapM, _genClimbM)}  ·  roughly " +
                      $"{Units.ClimbRateText(_genClimbM, _genLapM)}  ·  approximate",
                      _bigHint);
            y += 48f;

            float bh = 62f;
            float half = (w - 16f) * 0.5f;
            if (GUI.Button(new Rect(px + pad, y, half, bh), "Cancel", _bigButton))
                _page = Page.Main;
            if (GUI.Button(new Rect(px + pad + half + 16f, y, half, bh), "Generate", _bigButton))
            {
                Regenerator?.Regenerate(Random.Range(1, int.MaxValue), _genLapM, _genClimbM);
                _page = Page.Main;
                IsOpen = false;
            }
        }

        /// <summary>
        /// Larger styles for the generate page, including a slider built by hand.
        ///
        /// GUI.HorizontalSlider takes its track and thumb from the skin, and the
        /// built-in thumb is about 10 px wide however large a rect it is given --
        /// so passing a taller rect alone widens the travel and leaves the grab
        /// handle just as small. Both have to be supplied.
        /// </summary>
        void EnsureBigStyles()
        {
            if (_bigLabel != null) return;

            _bigTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 1f, 1f, 0.55f) },
            };
            _bigLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };
            _bigHint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
            };
            _bigButton = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold };

            _bigSlider = new GUIStyle(GUI.skin.horizontalSlider)
            {
                fixedHeight = 26f,
                margin = new RectOffset(0, 0, 0, 0),
            };
            _bigThumb = new GUIStyle(GUI.skin.horizontalSliderThumb)
            {
                fixedHeight = 40f,
                fixedWidth = 40f,
            };
        }

        void DrawSaveAs(float px, float py)
        {
            float y = py + 12f;
            float w = PanelWidth - 28f;

            GUI.Label(new Rect(px + 14f, y, w, 18f), "SAVE THIS WORLD", _title);
            y += 22f;

            GUI.SetNextControlName("saveName");
            _saveName = GUI.TextField(new Rect(px + 14f, y, w, 26f), _saveName, SavedCourses.MaxNameLength);
            if (_focusPending)
            {
                GUI.FocusControl("saveName");
                _focusPending = false;
            }
            y += 30f;

            var route = World != null ? World.Route : null;
            string detail = route != null
                ? $"seed {World.Seed} · {Units.Distance(route.Length):F1} {Units.DistanceSuffix} · " +
                  $"{Units.ElevationText(route.Profile.TotalAscent)} climbing"
                : "no world loaded";
            GUI.Label(new Rect(px + 14f, y, w, 16f), detail, _small);
            y += 22f;

            // Enter confirms, which is what anyone typing a name expects.
            bool enter = Event.current.type == EventType.KeyDown &&
                         (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            float half = (w - 8f) * 0.5f;
            bool save = GUI.Button(new Rect(px + 14f, y, half, RowHeight), "Save", _button);
            if (GUI.Button(new Rect(px + 14f + half + 8f, y, half, RowHeight), "Cancel", _button))
                _page = Page.Main;

            if ((save || enter) && route != null)
            {
                if (SavedCourses.Save(_saveName, World.Seed, route.Length, route.Profile.TotalAscent,
                                      World.TargetLengthM, World.TargetAscentM))
                {
                    Toast($"Saved \"{SavedCourses.Sanitise(_saveName)}\"");
                    _page = Page.Main;
                }
                else
                {
                    Toast("Name cannot be empty");
                }
                if (enter) Event.current.Use();
            }
        }

        void DrawLoad(float px, float py, float ph)
        {
            float y = py + 12f;
            float w = PanelWidth - 28f;

            GUI.Label(new Rect(px + 14f, y, w, 18f), "SAVED WORLDS", _title);
            y += 22f;

            var all = SavedCourses.All;
            if (all.Count == 0)
            {
                GUI.Label(new Rect(px + 14f, y, w, 40f),
                          "Nothing saved yet. Use Save As to keep a world you like.", _small);
            }
            else
            {
                float listHeight = ph - 92f;
                var view = new Rect(px + 14f, y, w, listHeight);
                var content = new Rect(0f, 0f, w - 18f, all.Count * 42f);
                _scroll = GUI.BeginScrollView(view, _scroll, content);

                for (int i = 0; i < all.Count; i++)
                {
                    var e = all[i];
                    float ry = i * 42f;
                    Box(new Rect(0f, ry, content.width, 38f), new Color(1f, 1f, 1f, 0.06f));

                    GUI.Label(new Rect(6f, ry + 2f, content.width - 110f, 18f), e.name, _rowName);
                    // Stored in metric; shown in whatever the rider has chosen.
                    GUI.Label(new Rect(6f, ry + 19f, content.width - 110f, 16f),
                              $"{Units.Distance(e.lapKm * 1000f):F1} {Units.DistanceSuffix} · " +
                              $"{Units.ElevationText(e.ascentM)} · {e.savedAt}", _rowMeta);

                    if (GUI.Button(new Rect(content.width - 96f, ry + 5f, 58f, 28f), "Load", _button))
                    {
                        // A course built to a request has to be reloaded the
                        // same way: the seed alone gives the generator's own idea
                        // of a lap, not the one that was asked for. Entries saved
                        // before this existed carry zeroes and take the old path,
                        // so they come back exactly as they were.
                        if (e.targetLapM > 0f || e.targetClimbM > 0f)
                            Regenerator?.Regenerate(e.seed, e.targetLapM, e.targetClimbM);
                        else
                            Regenerator?.Regenerate(e.seed);
                        Toast($"Loading \"{e.name}\"");
                        IsOpen = false;
                        _page = Page.Main;
                    }
                    if (GUI.Button(new Rect(content.width - 34f, ry + 5f, 28f, 28f), "x", _button))
                    {
                        SavedCourses.Delete(e.name);
                        Toast($"Deleted \"{e.name}\"");
                        break;   // the list just changed underneath us
                    }
                }
                GUI.EndScrollView();
            }

            if (GUI.Button(new Rect(px + 14f, py + ph - 42f, w, RowHeight), "Back", _button))
                _page = Page.Main;
        }

        void DrawBusyOverlay()
        {
            Box(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.55f));

            float w = 420f, h = 74f;
            float x = (Screen.width - w) * 0.5f, y = (Screen.height - h) * 0.5f;
            Box(new Rect(x - 6f, y - 6f, w + 12f, h + 12f), new Color(0f, 0f, 0f, 0.8f));

            var head = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = Color.white }
            };
            GUI.Label(new Rect(x + 14f, y + 8f, w - 28f, 24f),
                      $"Building world - {Regenerator.Stage}", head);

            var bar = new Rect(x + 14f, y + 40f, w - 28f, 12f);
            Box(bar, new Color(1f, 1f, 1f, 0.13f));
            Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(Regenerator.Progress), bar.height),
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
