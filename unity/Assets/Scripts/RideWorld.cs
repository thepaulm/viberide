using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Holds the world parameters and rebuilds the route at runtime.
    /// Route generation is deterministic from these settings, so the runtime
    /// path lands exactly on the terrain that was baked from the same values.
    /// Change a field here and the terrain must be rebaked or the road will
    /// float or sink.
    ///
    /// NOTE: this lives in its own file on purpose. Unity only creates a
    /// MonoScript for the class whose name matches the file, so a MonoBehaviour
    /// sharing a file with another one ends up as a missing script in any built
    /// player -- while still working in the editor, which makes it a nasty one
    /// to track down.
    /// </summary>
    public class RideWorld : MonoBehaviour
    {
        [Header("Must match the values the terrain was baked with")]
        public float TerrainSize = 10000f;
        public float TerrainHeight = 2800f;
        public float RouteRadiusFraction = 0.34f;
        public float BaseElevation = 560f;
        public float RoadWidth = 7.5f;
        public int Seed = 20260816;

        public RoutePath Route { get; private set; }

        /// <summary>Raised after a regenerate, so anything caching route-derived
        /// state (the HUD's elevation profile, for one) can rebuild it.</summary>
        public event System.Action RouteChanged;

        /// <summary>Swap in a new route. Used by the runtime regenerate; the
        /// terrain must be rebuilt from the same settings or the road will float.</summary>
        public void ApplyRoute(RoutePath route, int seed)
        {
            Seed = seed;
            Route = route;
            RouteChanged?.Invoke();
        }

        public WorldSettings ToSettings() => new WorldSettings
        {
            TerrainSize = TerrainSize,
            TerrainHeight = TerrainHeight,
            RouteRadiusFraction = RouteRadiusFraction,
            BaseElevation = BaseElevation,
            RoadWidth = RoadWidth,
            Seed = Seed,
        };

        void Awake()
        {
            Route = WorldGen.BuildRoute(ToSettings());
            var p = Route.Profile;
            Debug.Log($"[RideWorld] Course: {Route.Length / 1000f:F2} km lap, " +
                      $"{p.TotalAscent:F0} m ascent, net {p.NetElevation:F1} m");
        }
    }
}
