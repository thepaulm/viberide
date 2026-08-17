using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Moves the rider along the route from bridge telemetry, and reports the
    /// gradient under the wheels back to the bridge so the trainer's resistance
    /// matches what is on screen.
    ///
    /// One MonoBehaviour per file, named to match. RideWorld and ChaseCamera used
    /// to live here, which made them missing scripts in every built player while
    /// still working in the editor.
    /// </summary>
    [RequireComponent(typeof(RideWorld))]
    public class BikeRider : MonoBehaviour
    {
        [Header("Wiring")]
        public TrainerLink Link;
        public Transform Bike;

        [Header("Fallback control (no bridge running)")]
        public bool AllowKeyboard = true;
        public float KeyboardPower = 240f;

        [Header("Feel")]
        [Tooltip("Metres of road ahead used to compute the gradient sent to the trainer. " +
                 "Looking slightly ahead stops resistance lagging behind the visuals.")]
        public float GradeLookahead = 12f;
        public float GradeSendHz = 10f;
        public float MaxLeanDegrees = 14f;

        RideWorld _world;
        FallbackRideModel _fallback;
        float _distance;
        float _speed;
        float _nextGradeSend;
        float _lean;

        public float Distance => _distance;
        public float SpeedMps => _speed;
        public float Grade { get; private set; }
        public string SegmentName { get; private set; } = "";
        public float Elevation { get; private set; }

        void Awake()
        {
            _world = GetComponent<RideWorld>();
            _fallback = new FallbackRideModel();
        }

        void Update()
        {
            var route = _world.Route;
            if (route == null) return;

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            bool live = Link != null && Link.Connected;

            if (live)
            {
                _speed = Link.Latest.speed_mps;
            }
            else if (AllowKeyboard)
            {
                // Keyboard stand-in so the world can be ridden without the
                // bridge running. Uses the same physics as the bridge, so the
                // terrain feels the same either way.
                float watts = 0f;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) watts = KeyboardPower;
                if (Input.GetKey(KeyCode.LeftShift)) watts *= 2f;
                _speed = _fallback.Step(watts, Grade, dt);
            }

            _distance = route.Wrap(_distance + _speed * dt);

            // Gradient a little way ahead of the wheels, averaged over the
            // lookahead, so resistance changes arrive with the visuals rather
            // than after them.
            Grade = 0.5f * (route.GradeAt(_distance) + route.GradeAt(_distance + GradeLookahead));
            SegmentName = route.Profile.NameAt(_distance);

            Vector3 pos = route.PositionAt(_distance);
            Vector3 fwd = route.ForwardAt(_distance, 8f);
            Elevation = pos.y;

            if (Bike != null)
            {
                Bike.position = pos;

                // Bank into corners from the rate of heading change. Purely
                // cosmetic, but a bike that stays bolt upright through a hairpin
                // reads as wrong immediately.
                Vector3 ahead = route.ForwardAt(_distance + 25f, 8f);
                float turn = Vector3.SignedAngle(fwd, ahead, Vector3.up);
                float target = Mathf.Clamp(-turn * 0.55f * Mathf.Clamp01(_speed / 8f),
                                           -MaxLeanDegrees, MaxLeanDegrees);
                _lean = Mathf.Lerp(_lean, target, 1f - Mathf.Exp(-3f * dt));
                Bike.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0f, 0f, _lean);
            }

            if (Link != null && Link.Connected && Time.time >= _nextGradeSend)
            {
                _nextGradeSend = Time.time + 1f / Mathf.Max(1f, GradeSendHz);
                Link.SendGrade(Grade);
            }
        }
    }
}
