using System.Collections;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Occasionally sends an aircraft across the sky ahead of the rider.
    ///
    /// Deliberately NOT seeded from the world. Scenery placement has to be
    /// reproducible because a saved world stores only its seed, but a flyby is an
    /// event in time, not a feature of the landscape — having the same plane
    /// appear at the same second of every ride would read as a loop rather than
    /// as weather.
    ///
    /// The plane is generated from primitives unless a model is supplied. At a few
    /// hundred metres of altitude the silhouette is nearly all you see, so the
    /// shape matters far more than the detail.
    /// </summary>
    public class PlaneFlyby : MonoBehaviour
    {
        [Header("Wiring")]
        public BikeRider Rider;
        public Terrain Terrain;

        [Tooltip("Optional real model. Left empty, a low-poly plane is built from " +
                 "primitives. Scaled by measured bounds to TargetWingspan either way.")]
        public GameObject PlanePrefab;

        [Header("How often")]
        public float MinInterval = 50f;
        public float MaxInterval = 160f;
        [Tooltip("Seconds before the first possible flyby, so one does not greet you at t=0.")]
        public float InitialDelay = 25f;

        [Header("Flight")]
        // Elevation angle is what decides whether it is on screen at all. The
        // camera is ~62 degrees FOV and the stat bar eats the top of the frame, so
        // a plane 400 m ahead at 300 m up sits at 37 degrees and is simply not
        // visible. Crossing further out and lower puts it at 8-20 degrees.
        public float MinAltitude = 90f;
        public float MaxAltitude = 190f;
        public float MinSpeed = 55f;
        public float MaxSpeed = 95f;

        // 26 m, not the 14 m of a light aircraft. Measured: at ~900 m a 14 m span
        // subtends 0.8 degrees, about 14 pixels at 1080p -- technically on screen
        // and completely unnoticeable. This reads as a commuter aircraft and is
        // roughly 40 px at the same distance.
        public float TargetWingspan = 26f;

        [Tooltip("Contrail behind the aircraft. Does most of the work of drawing " +
                 "the eye to something small and far away.")]
        public bool Contrail = true;
        [Tooltip("How far out it spawns and despawns, each side of the crossing point.")]
        public float TrackHalfLength = 1400f;

        public int Flybys { get; private set; }

        GameObject _template;
        Material _material;

        void Start()
        {
            StartCoroutine(Schedule());

            // -flyby is handled by AutoScreenshot, which triggers one only after
            // the rider has been positioned. Firing it here would put the plane
            // wherever the rider started, which is not where the camera ends up.
        }

        IEnumerator Schedule()
        {
            yield return new WaitForSeconds(InitialDelay);
            while (true)
            {
                yield return new WaitForSeconds(Random.Range(MinInterval, MaxInterval));
                yield return FlyOne(-1f);
            }
        }

        /// <summary>Send one across now. `approach` overrides how far out it
        /// spawns, which lets a screenshot catch it near the crossing rather than
        /// waiting out a full 1.4 km run-in.</summary>
        public void TriggerNow(float approach = -1f) => StartCoroutine(FlyOne(approach));

        IEnumerator FlyOne(float approach = -1f)
        {
            float runIn = approach > 0f ? approach : TrackHalfLength;
            if (Rider == null) yield break;
            var template = EnsureTemplate();
            if (template == null) yield break;

            // Cross the road ahead of the rider rather than overhead: something
            // passing directly above is behind the camera before it registers.
            Vector3 riderPos = Rider.transform.position;
            var world = GetComponent<RideWorld>();
            var route = world != null ? world.Route : null;
            Vector3 ahead = route != null
                ? route.PositionAt(Rider.Distance + Random.Range(500f, 950f))
                : riderPos + Vector3.forward * 900f;

            float ground = Terrain != null
                ? Terrain.SampleHeight(ahead) + Terrain.transform.position.y
                : ahead.y;
            float altitude = ground + Random.Range(MinAltitude, MaxAltitude);

            // A crossing angle, not a perpendicular one: dead square across looks
            // staged, and straight down the road it barely moves against the sky.
            Vector3 roadDir = route != null
                ? route.ForwardAt(Rider.Distance, 8f)
                : Vector3.forward;
            roadDir.y = 0f;
            roadDir.Normalize();
            float yaw = Random.Range(35f, 145f) * (Random.value < 0.5f ? 1f : -1f);
            Vector3 heading = Quaternion.Euler(0f, yaw, 0f) * roadDir;

            Vector3 crossing = new Vector3(ahead.x, altitude, ahead.z);
            Vector3 start = crossing - heading * runIn;
            Vector3 end = crossing + heading * TrackHalfLength;

            float speed = Random.Range(MinSpeed, MaxSpeed);
            float bank = Random.Range(-8f, 8f);

            var plane = Instantiate(template, start, Quaternion.LookRotation(heading, Vector3.up));
            plane.SetActive(true);
            plane.name = "Flyby";
            if (Contrail) AddContrail(plane);

            Flybys++;
            float total = Vector3.Distance(start, end);
            float travelled = 0f;

            // Measure rather than guess whether it is actually on screen. Viewport
            // coordinates are 0-1 across the frame with z as distance in front.
            bool trace = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-flyby") >= 0;
            Debug.Log($"[PlaneFlyby] start {start} -> {end}, alt {altitude:F0} m " +
                      $"(ground {ground:F0}), speed {speed:F0} m/s, run-in {runIn:F0} m");
            float nextTrace = 0f;

            while (travelled < total && plane != null)
            {
                float step = speed * Time.deltaTime;
                travelled += step;
                plane.transform.position += heading * step;

                if (trace && Time.time >= nextTrace)
                {
                    nextTrace = Time.time + 1f;
                    var cam = Camera.main;
                    if (cam != null)
                    {
                        Vector3 vp = cam.WorldToViewportPoint(plane.transform.position);
                        bool onScreen = vp.z > 0f && vp.x > 0f && vp.x < 1f && vp.y > 0f && vp.y < 1f;
                        Debug.Log($"[PlaneFlyby] t={Time.time:F1} travelled {travelled:F0}/{total:F0} " +
                                  $"viewport ({vp.x:F2},{vp.y:F2}) depth {vp.z:F0} " +
                                  $"{(onScreen ? "ON SCREEN" : "off screen")}");
                    }
                }
                // A touch of bank and a slow roll makes it read as flying rather
                // than as a model being slid along a rail.
                plane.transform.rotation = Quaternion.LookRotation(heading, Vector3.up) *
                                           Quaternion.Euler(0f, 0f, bank);
                yield return null;
            }

            if (plane != null) Destroy(plane);
        }

        GameObject EnsureTemplate()
        {
            if (_template != null) return _template;

            _template = PlanePrefab != null ? Instantiate(PlanePrefab) : BuildPlane();
            _template.name = "PlaneTemplate";
            _template.transform.SetParent(transform, false);
            _template.SetActive(false);

            // Normalise by measured width, the same approach the scenery uses: a
            // model arrives in whatever units its author chose, and wingspan is
            // the dimension a viewer judges an aircraft by.
            var bounds = MeasureBounds(_template);
            if (bounds.size.x > 0.001f && TargetWingspan > 0.01f)
            {
                float k = TargetWingspan / bounds.size.x;
                _template.transform.localScale *= k;
            }
            return _template;
        }

        /// <summary>A thin vapour trail. Cheap, and it turns a distant speck into
        /// something the eye follows.</summary>
        void AddContrail(GameObject plane)
        {
            var trail = plane.AddComponent<TrailRenderer>();
            trail.time = 4.5f;
            trail.startWidth = 2.2f;
            trail.endWidth = 7f;
            trail.numCapVertices = 3;
            trail.minVertexDistance = 4f;
            trail.material = new Material(Shader.Find("Sprites/Default"));

            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = grad;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
        }

        static Bounds MeasureBounds(GameObject go)
        {
            var filters = go.GetComponentsInChildren<MeshFilter>(true);
            bool any = false;
            var result = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var f in filters)
            {
                if (f.sharedMesh == null) continue;
                var b = f.sharedMesh.bounds;
                var offset = go.transform.InverseTransformPoint(f.transform.position);
                var s = f.transform.lossyScale;
                b = new Bounds(b.center + offset,
                               Vector3.Scale(b.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))));
                if (!any) { result = b; any = true; } else result.Encapsulate(b);
            }
            if (!any) result = new Bounds(Vector3.zero, Vector3.one);
            return result;
        }

        /// <summary>
        /// A low-poly aeroplane from primitives, nose along +Z. Crude up close,
        /// but at 130-340 m it is a silhouette against the sky and reads correctly.
        /// </summary>
        GameObject BuildPlane()
        {
            _material = new Material(Shader.Find("Standard"))
            {
                name = "PlaneMat",
                color = new Color(0.90f, 0.91f, 0.93f),
            };
            _material.SetFloat("_Glossiness", 0.35f);

            var root = new GameObject("Plane");

            Part(root, PrimitiveType.Capsule, "Fuselage",
                 new Vector3(0f, 0f, 0f), new Vector3(90f, 0f, 0f), new Vector3(1.0f, 4.2f, 1.0f));
            Part(root, PrimitiveType.Cube, "Wings",
                 new Vector3(0f, 0.1f, 0.2f), Vector3.zero, new Vector3(11.5f, 0.22f, 1.9f));
            Part(root, PrimitiveType.Cube, "Tailplane",
                 new Vector3(0f, 0.35f, -3.5f), Vector3.zero, new Vector3(4.2f, 0.18f, 1.0f));
            Part(root, PrimitiveType.Cube, "Fin",
                 new Vector3(0f, 1.05f, -3.6f), Vector3.zero, new Vector3(0.18f, 1.5f, 1.1f));
            Part(root, PrimitiveType.Cylinder, "Engine",
                 new Vector3(0f, 0f, 4.0f), new Vector3(90f, 0f, 0f), new Vector3(0.75f, 0.35f, 0.75f));

            return root;
        }

        void Part(GameObject parent, PrimitiveType shape, string name,
                  Vector3 pos, Vector3 euler, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.GetComponent<MeshRenderer>().sharedMaterial = _material;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
