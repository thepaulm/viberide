using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Display units. Everything inside the app stays SI -- the physics, the
    /// course, the wire protocol -- and conversion happens only at the point of
    /// drawing text. Converting any earlier would mean two sources of truth for
    /// every number.
    /// </summary>
    public static class Units
    {
        const string PrefKey = "viberide.imperial";

        static bool _imperial;
        static bool _loaded;

        public static bool Imperial
        {
            get
            {
                if (!_loaded)
                {
                    _imperial = PlayerPrefs.GetInt(PrefKey, 0) == 1;
                    // -imperial / -metric override the saved choice, for testing
                    // and for screenshots.
                    var args = System.Environment.GetCommandLineArgs();
                    if (System.Array.IndexOf(args, "-imperial") >= 0) _imperial = true;
                    else if (System.Array.IndexOf(args, "-metric") >= 0) _imperial = false;
                    _loaded = true;
                }
                return _imperial;
            }
            set
            {
                _imperial = value;
                _loaded = true;
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        // --- speed ---
        public static float Speed(float metresPerSecond) =>
            Imperial ? metresPerSecond * 2.236936f : metresPerSecond * 3.6f;

        public static string SpeedSuffix => Imperial ? "mph" : "km/h";

        // --- distance ---
        public static float Distance(float metres) =>
            Imperial ? metres / 1609.344f : metres / 1000f;

        public static string DistanceSuffix => Imperial ? "mi" : "km";

        // --- elevation ---
        public static float Elevation(float metres) =>
            Imperial ? metres * 3.280840f : metres;

        public static string ElevationSuffix => Imperial ? "ft" : "m";

        /// <summary>Elevation rounded sensibly for its unit -- feet do not want
        /// decimals, and metres rarely do either.</summary>
        public static string ElevationText(float metres) =>
            $"{Elevation(metres):F0}{ElevationSuffix}";

        public static string DistanceText(float metres) =>
            $"{Distance(metres):F2}{DistanceSuffix}";

        /// <summary>How steep a course is overall -- the number riders actually
        /// compare routes by. Metres per kilometre and feet per mile are both
        /// idiomatic; neither audience reads the other one.</summary>
        public static string ClimbRateText(float climbM, float lengthM)
        {
            float perKm = climbM / Mathf.Max(0.1f, lengthM / 1000f);
            return Imperial ? $"{perKm * 5.27999f:F0} ft per mile"
                            : $"{perKm:F0} m per km";
        }

        /// <summary>Round a value to something that reads cleanly in whichever
        /// unit is on screen. Snapping in metres means an imperial rider drags
        /// through 15.53, 15.84, 16.16 and can never land on a whole mile.</summary>
        public static float SnapDistance(float metres) =>
            Imperial ? Mathf.Round(metres / (0.25f * 1609.344f)) * (0.25f * 1609.344f)
                     : Mathf.Round(metres / 500f) * 500f;

        public static float SnapElevation(float metres) =>
            Imperial ? Mathf.Round(metres / (100f / 3.280840f)) * (100f / 3.280840f)
                     : Mathf.Round(metres / 25f) * 25f;
    }
}
