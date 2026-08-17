using System;
using System.Collections.Generic;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// One stretch of road with a designed gradient. Grade ramps linearly from
    /// start to end so segment joins don't produce a step change in resistance,
    /// which on a real trainer feels like hitting a kerb.
    /// </summary>
    [Serializable]
    public class CourseSegment
    {
        public string Name;
        public float LengthM;
        public float StartGrade;
        public float EndGrade;

        public CourseSegment(string name, float lengthM, float startGrade, float endGrade)
        {
            Name = name;
            LengthM = lengthM;
            StartGrade = startGrade;
            EndGrade = endGrade;
        }

        public CourseSegment(string name, float lengthM, float grade)
            : this(name, lengthM, grade, grade) { }
    }

    /// <summary>
    /// An elevation profile built from segments. Elevation is the analytic
    /// integral of the gradient ramp, so height and slope agree exactly --
    /// no drift between what you see and what the trainer is made to feel.
    /// </summary>
    public class CourseProfile
    {
        readonly List<CourseSegment> _segments = new();
        float[] _startDistance;
        float[] _startElevation;

        public float TotalLength { get; private set; }
        public float TotalAscent { get; private set; }
        public float NetElevation { get; private set; }
        public IReadOnlyList<CourseSegment> Segments => _segments;

        public void Add(CourseSegment segment) => _segments.Add(segment);

        /// <summary>
        /// Scale every segment so the profile spans exactly <paramref name="targetLength"/>.
        /// Gradients are preserved -- only lengths move -- because the gradient is
        /// what determines how the climb actually feels.
        /// </summary>
        public void ScaleToLength(float targetLength)
        {
            float current = 0f;
            foreach (var s in _segments) current += s.LengthM;
            if (current <= 0.01f) throw new InvalidOperationException("Empty course profile");

            float factor = targetLength / current;
            foreach (var s in _segments) s.LengthM *= factor;
            Bake();
        }

        public void Bake()
        {
            int n = _segments.Count;
            _startDistance = new float[n + 1];
            _startElevation = new float[n + 1];

            float d = 0f, e = 0f, ascent = 0f;
            for (int i = 0; i < n; i++)
            {
                _startDistance[i] = d;
                _startElevation[i] = e;
                var s = _segments[i];
                // Integral of a linear grade ramp over the segment.
                float rise = s.LengthM * 0.5f * (s.StartGrade + s.EndGrade);
                if (rise > 0f) ascent += rise;
                d += s.LengthM;
                e += rise;
            }
            _startDistance[n] = d;
            _startElevation[n] = e;

            TotalLength = d;
            NetElevation = e;
            TotalAscent = ascent;
        }

        int IndexFor(float distance)
        {
            // Binary search the segment containing this distance.
            int lo = 0, hi = _segments.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_startDistance[mid] <= distance) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        public float Wrap(float distance)
        {
            if (TotalLength <= 0f) return 0f;
            distance %= TotalLength;
            return distance < 0f ? distance + TotalLength : distance;
        }

        public float GradeAt(float distance)
        {
            distance = Wrap(distance);
            int i = IndexFor(distance);
            var s = _segments[i];
            float t = s.LengthM <= 0.001f ? 0f : (distance - _startDistance[i]) / s.LengthM;
            return Mathf.Lerp(s.StartGrade, s.EndGrade, Mathf.Clamp01(t));
        }

        public float ElevationAt(float distance)
        {
            distance = Wrap(distance);
            int i = IndexFor(distance);
            var s = _segments[i];
            float x = s.LengthM <= 0.001f ? 0f : (distance - _startDistance[i]) / s.LengthM;
            x = Mathf.Clamp01(x);
            // e0 + L * (g0*x + (g1-g0)*x^2/2)
            return _startElevation[i]
                   + s.LengthM * (s.StartGrade * x + (s.EndGrade - s.StartGrade) * x * x * 0.5f);
        }

        public string NameAt(float distance) => _segments[IndexFor(Wrap(distance))].Name;

        /// <summary>
        /// The default course. Net elevation is deliberately zero so the loop
        /// joins seamlessly -- you can ride it forever without a seam or a
        /// teleport. Gradients are real-world rideable, nothing above 12%.
        /// </summary>
        public static CourseProfile CreateDefault()
        {
            var c = new CourseProfile();
            c.Add(new CourseSegment("Neutral roll-out", 1500f, 0f));
            c.Add(new CourseSegment("River road", 2000f, 0f, 0.02f));
            c.Add(new CourseSegment("Rolling hills", 1200f, 0.02f, -0.02f));
            c.Add(new CourseSegment("Rolling hills", 1300f, -0.02f, 0.03f));
            c.Add(new CourseSegment("The Wall", 800f, 0.09f, 0.12f));
            c.Add(new CourseSegment("Recovery shelf", 600f, 0.03f));
            c.Add(new CourseSegment("Col de Carbon", 3000f, 0.06f, 0.075f));
            c.Add(new CourseSegment("Col de Carbon (upper)", 3000f, 0.075f, 0.09f));
            c.Add(new CourseSegment("Summit", 300f, 0.02f, 0f));
            c.Add(new CourseSegment("Hairpin descent", 5000f, -0.07f, -0.04f));
            c.Add(new CourseSegment("Long valley descent", 6000f, -0.05f));
            c.Add(new CourseSegment("Valley run", 2000f, 0f));
            c.Bake();

            // Force an exact loop: nudge the final flat so net elevation is zero.
            // Without this, a few metres of drift per lap becomes a cliff by lap 20.
            float drift = c.NetElevation;
            var last = c._segments[c._segments.Count - 1];
            float correction = -drift / last.LengthM;
            last.StartGrade = correction;
            last.EndGrade = correction;
            c.Bake();
            return c;
        }
    }

    /// <summary>
    /// A closed loop in the horizontal plane, arc-length parameterised, with
    /// elevation supplied by a CourseProfile. Distance in, world position and
    /// heading out.
    /// </summary>
    public class RoutePath
    {
        readonly Vector2[] _points;
        readonly float[] _cumulative;
        readonly CourseProfile _profile;
        readonly float _baseElevation;

        public float Length => _cumulative[_cumulative.Length - 1];
        public CourseProfile Profile => _profile;

        public RoutePath(Vector2[] loopPoints, CourseProfile profile, float baseElevation)
        {
            if (loopPoints == null || loopPoints.Length < 8)
                throw new ArgumentException("Route needs at least 8 points");

            _points = loopPoints;
            _profile = profile;
            _baseElevation = baseElevation;

            _cumulative = new float[_points.Length + 1];
            float total = 0f;
            for (int i = 0; i < _points.Length; i++)
            {
                _cumulative[i] = total;
                Vector2 next = _points[(i + 1) % _points.Length];
                total += Vector2.Distance(_points[i], next);
            }
            _cumulative[_points.Length] = total;
        }

        /// <summary>
        /// Build a wandering closed loop that fits inside a square terrain.
        /// Harmonics on the radius keep it from being a boring circle while
        /// staying smooth enough that the road never doubles back on itself.
        /// </summary>
        public static Vector2[] BuildLoop(Vector2 center, float radius, int samples = 4096, int seed = 12345)
        {
            var rng = new System.Random(seed);
            float p1 = (float)rng.NextDouble() * Mathf.PI * 2f;
            float p2 = (float)rng.NextDouble() * Mathf.PI * 2f;
            float p3 = (float)rng.NextDouble() * Mathf.PI * 2f;

            var pts = new Vector2[samples];
            for (int i = 0; i < samples; i++)
            {
                float theta = (i / (float)samples) * Mathf.PI * 2f;
                float r = radius * (1f
                    + 0.20f * Mathf.Sin(3f * theta + p1)
                    + 0.11f * Mathf.Cos(5f * theta + p2)
                    + 0.05f * Mathf.Sin(7f * theta + p3));
                pts[i] = new Vector2(center.x + r * Mathf.Cos(theta), center.y + r * Mathf.Sin(theta));
            }
            return pts;
        }

        int SegmentFor(float distance)
        {
            int lo = 0, hi = _points.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_cumulative[mid] <= distance) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        public float Wrap(float distance)
        {
            float len = Length;
            distance %= len;
            return distance < 0f ? distance + len : distance;
        }

        public Vector2 HorizontalAt(float distance)
        {
            distance = Wrap(distance);
            int i = SegmentFor(distance);
            Vector2 a = _points[i];
            Vector2 b = _points[(i + 1) % _points.Length];
            float segLen = _cumulative[i + 1] - _cumulative[i];
            float t = segLen <= 0.0001f ? 0f : (distance - _cumulative[i]) / segLen;
            return Vector2.Lerp(a, b, t);
        }

        public float ElevationAt(float distance) => _baseElevation + _profile.ElevationAt(distance);

        public float GradeAt(float distance) => _profile.GradeAt(distance);

        public Vector3 PositionAt(float distance)
        {
            Vector2 xz = HorizontalAt(distance);
            return new Vector3(xz.x, ElevationAt(distance), xz.y);
        }

        /// <summary>Unit heading, including vertical component from the gradient.</summary>
        public Vector3 ForwardAt(float distance, float lookahead = 4f)
        {
            Vector3 a = PositionAt(distance);
            Vector3 b = PositionAt(distance + lookahead);
            Vector3 d = b - a;
            return d.sqrMagnitude < 1e-6f ? Vector3.forward : d.normalized;
        }
    }
}
