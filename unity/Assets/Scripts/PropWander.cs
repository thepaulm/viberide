using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Walks a scattered animal around the spot it was placed on.
    ///
    /// The clips translate nothing by themselves -- a walk cycle animates the
    /// legs and leaves the root where it stands, so a dinosaur playing one is a
    /// treadmill. This supplies the ground speed to go with it.
    ///
    /// The speed is not a taste setting. Pick it wrong and the feet skate, which
    /// is more obviously broken than not moving at all, so it is derived from the
    /// clip: one cycle of a walk covers roughly half the animal's height in
    /// stride, so speed is that distance over the cycle's length, scaled by
    /// whatever playback rate the instance was given.
    /// </summary>
    public class PropWander : MonoBehaviour
    {
        public float Speed = 1f;
        /// <summary>Where it was placed. It circles this rather than leaving.</summary>
        public Vector3 Home;
        public float Radius = 26f;
        /// <summary>Road point it was scattered from, and how close it may come
        /// back to it. Scenery wandering onto the carriageway would be a fine
        /// joke exactly once.</summary>
        public Vector3 RoadAnchor;
        public float MinRoadDistance = 20f;
        public float TurnRate = 30f;

        Terrain _terrain;
        float _heading;
        float _nextTurn;
        float _drift;

        void Start()
        {
            _terrain = Terrain.activeTerrain;
            _heading = transform.eulerAngles.y;
            if (Home == Vector3.zero) Home = transform.position;
        }

        void Update()
        {
            if (Speed <= 0.001f) return;
            float dt = Time.deltaTime;

            Vector3 pos = transform.position;
            Vector3 fromHome = pos - Home; fromHome.y = 0f;
            Vector3 fromRoad = pos - RoadAnchor; fromRoad.y = 0f;

            float want;
            if (fromRoad.sqrMagnitude < MinRoadDistance * MinRoadDistance &&
                RoadAnchor != Vector3.zero)
            {
                // Head straight out from the road, no dithering.
                want = Mathf.Atan2(fromRoad.x, fromRoad.z) * Mathf.Rad2Deg;
            }
            else if (fromHome.magnitude > Radius)
            {
                want = Mathf.Atan2(-fromHome.x, -fromHome.z) * Mathf.Rad2Deg;
            }
            else
            {
                if (Time.time > _nextTurn)
                {
                    _nextTurn = Time.time + Random.Range(4f, 10f);
                    _drift = Random.Range(-70f, 70f);
                }
                want = _heading + _drift;
            }

            _heading = Mathf.MoveTowardsAngle(_heading, want, TurnRate * dt);
            transform.rotation = Quaternion.Euler(0f, _heading, 0f);

            Vector3 next = pos + transform.forward * (Speed * dt);
            if (_terrain != null)
                next.y = _terrain.SampleHeight(next) + _terrain.transform.position.y;
            transform.position = next;
        }
    }
}
