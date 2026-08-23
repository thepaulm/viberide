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

        /// <summary>Steepest gradient anywhere on a generated course.</summary>
        public const float MaxGrade = 0.13f;

        /// <summary>Average metres climbed per metre ridden, kept in this band so
        /// a lap is neither pancake-flat nor an unbroken wall.</summary>
        public const float MinClimbRatio = 0.014f;
        public const float MaxClimbRatio = 0.032f;

        public float MaxAbsGrade()
        {
            float max = 0f;
            foreach (var s in _segments)
                max = Mathf.Max(max, Mathf.Max(Mathf.Abs(s.StartGrade), Mathf.Abs(s.EndGrade)));
            return max;
        }

        /// <summary>
        /// Scale every gradient so the lap climbs about <paramref name="target"/>
        /// metres, and report what was actually achieved.
        ///
        /// One factor across every gradient is the whole trick: net elevation is
        /// a sum that is already zero, and scaling zero leaves it at zero, so the
        /// lap still joins itself exactly.
        ///
        /// What limits the answer is the ceiling on any single gradient. Asking
        /// for more climbing than MaxGrade allows over this much road reduces the
        /// factor instead of producing a wall, so the result can be less than was
        /// asked for -- which is why this returns the real figure rather than
        /// assuming it succeeded.
        /// </summary>
        public float ScaleAscentTo(float target)
        {
            if (target <= 0f || TotalAscent <= 0.01f) return TotalAscent;

            float k = target / TotalAscent;
            float maxAbs = MaxAbsGrade();
            if (maxAbs * k > MaxGrade) k = MaxGrade / Mathf.Max(maxAbs, 0.0001f);
            if (Mathf.Abs(k - 1f) > 0.005f) ScaleGrades(k);
            return TotalAscent;
        }

        /// <summary>Scale every gradient by k. Net elevation is preserved when it
        /// is already zero, since scaling a sum of zero leaves it at zero.</summary>
        void ScaleGrades(float k)
        {
            foreach (var s in _segments)
            {
                s.StartGrade *= k;
                s.EndGrade *= k;
            }
            Bake();
        }

        /// <summary>
        /// Append a closing stretch that cancels whatever net elevation has
        /// accumulated, so the lap joins exactly and can be ridden forever.
        ///
        /// Three parts: a ramp in from the gradient we were on, a steady body,
        /// and a ramp back to zero. Both ramps matter. Without the first, the
        /// gradient steps the moment the closing segment starts; without the
        /// second, it steps at the lap boundary where the course wraps back to
        /// the flat roll-out. Either one feels like riding into a kerb.
        ///
        /// The body gradient is solved for rather than guessed, because the ramps
        /// themselves contribute elevation and have to be part of the sum.
        /// </summary>
        void CloseTheLoop(string name, float entryGrade)
        {
            Bake();
            float drift = NetElevation;

            const float rampIn = 220f, rampOut = 220f, maxClosingGrade = 0.02f;
            float body = 1200f;
            float grade = 0f;

            // rise = rampIn*(entry+g)/2 + body*g + rampOut*(g+0)/2 = -drift
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float numerator = -drift - rampIn * entryGrade * 0.5f;
                float denominator = rampIn * 0.5f + body + rampOut * 0.5f;
                grade = numerator / denominator;
                if (Mathf.Abs(grade) <= maxClosingGrade) break;
                // Too steep: lengthen the body so the same correction is gentler.
                body = Mathf.Abs(numerator) / maxClosingGrade;
            }

            Add(new CourseSegment(name, rampIn, entryGrade, grade));
            Add(new CourseSegment(name, body, grade, grade));
            Add(new CourseSegment(name, rampOut, grade, 0f));
            Bake();

            // Mop up float error on the body, which leaves both ramps -- and so
            // both joins -- untouched.
            float residual = NetElevation;
            var bodySeg = _segments[_segments.Count - 2];
            float fix = -residual / bodySeg.LengthM;
            bodySeg.StartGrade += fix;
            bodySeg.EndGrade += fix;
            // Keep the ramps meeting the body exactly after that nudge.
            _segments[_segments.Count - 3].EndGrade = bodySeg.StartGrade;
            _segments[_segments.Count - 1].StartGrade = bodySeg.EndGrade;
            Bake();
        }

        static readonly string[] ClimbPrefix = { "Col de", "Mont", "Alto de", "Puerto de" };
        static readonly string[] PlaceNames =
        {
            "Carbon", "Ardoise", "Brume", "Sable", "Pierre", "Nuage",
            "Vireux", "Corbeau", "Solane", "Fresne", "Aubrac", "Verdon",
        };
        static readonly string[] WallNames =
        {
            "The Wall", "The Ramp", "The Kicker", "The Chimney", "The Step",
        };
        static readonly string[] FlatNames =
        {
            "Valley run", "River road", "False flat", "The plateau", "Long drag",
        };

        /// <summary>
        /// Build a course from a seed.
        ///
        /// Constraints that make the result actually rideable, rather than merely
        /// random: nothing steeper than 13%, gradient never steps discontinuously
        /// (every feature is entered through a transition ramp), average climbing
        /// held to a sane band, and net elevation exactly zero so the lap joins
        /// seamlessly.
        /// </summary>
        public static CourseProfile CreateRandom(int seed)
        {
            var rng = new System.Random(seed);
            float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            int Pick(int n) => rng.Next(n);

            var c = new CourseProfile();
            float prevGrade = 0f;
            float running = 0f;   // metres of elevation accumulated so far

            // Every feature is entered via a short ramp from whatever gradient we
            // were on. Without this the joins step instantly, which on a real
            // trainer feels like riding into a kerb.
            void Feature(string name, float length, float g0, float g1)
            {
                g0 = Mathf.Clamp(g0, -MaxGrade, MaxGrade);
                g1 = Mathf.Clamp(g1, -MaxGrade, MaxGrade);
                float ramp = Mathf.Min(200f, length * 0.3f);
                if (ramp > 25f && Mathf.Abs(g0 - prevGrade) > 0.004f)
                {
                    c.Add(new CourseSegment(name, ramp, prevGrade, g0));
                    running += ramp * (prevGrade + g0) * 0.5f;
                    length -= ramp;
                }
                float body = Mathf.Max(length, 50f);
                c.Add(new CourseSegment(name, body, g0, g1));
                running += body * (g0 + g1) * 0.5f;
                prevGrade = g1;
            }

            Feature("Neutral roll-out", Range(900f, 1800f), 0f, 0f);

            int climbs = 2 + Pick(3);          // 2-4 climbs
            var usedNames = new System.Collections.Generic.HashSet<string>();

            for (int i = 0; i < climbs; i++)
            {
                float elevationBefore = running;

                // Approach: gentle rise or rolling ground before the real climb.
                Feature(FlatNames[Pick(FlatNames.Length)], Range(600f, 2000f),
                        Range(-0.01f, 0.015f), Range(-0.005f, 0.02f));

                // Occasional short wall, steeper than the climb it precedes.
                if (rng.NextDouble() < 0.45)
                {
                    Feature(WallNames[Pick(WallNames.Length)], Range(350f, 800f),
                            Range(0.085f, 0.10f), Range(0.10f, MaxGrade));
                    Feature("Recovery shelf", Range(300f, 700f), Range(0.015f, 0.04f), Range(0.02f, 0.045f));
                }

                string name;
                int guard = 0;
                do
                {
                    name = $"{ClimbPrefix[Pick(ClimbPrefix.Length)]} {PlaceNames[Pick(PlaceNames.Length)]}";
                } while (!usedNames.Add(name) && ++guard < 16);

                // Split longer climbs so the gradient can build toward the top,
                // which is what makes a climb feel like it has a story.
                float total = Range(1400f, 5200f);
                float lower = Range(0.04f, 0.07f);
                float upper = Mathf.Clamp(lower + Range(0.005f, 0.03f), 0.04f, 0.105f);
                if (total > 2600f)
                {
                    Feature(name, total * 0.55f, lower, (lower + upper) * 0.5f);
                    Feature($"{name} (upper)", total * 0.45f, (lower + upper) * 0.5f, upper);
                }
                else
                {
                    Feature(name, total, lower, upper);
                }

                Feature("Summit", Range(150f, 450f), Range(0.005f, 0.025f), 0f);

                // Size the descent against what this climb actually gained,
                // rather than picking a length at random. Guessing left most of
                // the climbing uncancelled, and the loop-closer then had to
                // absorb it -- on one seed that made the closing stretch 13 km of
                // a 25 km lap, half the ride spent on one gentle false descent.
                float gained = running - elevationBefore;
                float dg0 = -Range(0.045f, 0.085f);
                float dg1 = -Range(0.03f, 0.065f);
                float avgDescent = Mathf.Abs((dg0 + dg1) * 0.5f);
                float drop = Mathf.Max(0f, gained) * Range(0.88f, 1.0f);
                float descentLength = Mathf.Clamp(drop / Mathf.Max(avgDescent, 0.005f), 700f, 9000f);

                // Break a long descent in two, steeper up top easing off lower
                // down, the same way the climbs are split. A single unbroken
                // gradient for a third of the lap is monotonous to ride, and a
                // real mountain descent does not hold one angle the whole way.
                if (descentLength > 2600f)
                {
                    float mid = (dg0 + dg1) * 0.5f;
                    Feature($"{name} descent", descentLength * 0.55f, dg0, mid);
                    Feature($"{name} descent (lower)", descentLength * 0.45f, mid, dg1);
                }
                else
                {
                    Feature($"{name} descent", descentLength, dg0, dg1);
                }
            }

            c.CloseTheLoop(FlatNames[Pick(FlatNames.Length)], prevGrade);

            // Nudge the overall climbing into a sensible band. Scaling every
            // gradient by the same factor keeps net elevation at zero.
            float ratio = c.TotalAscent / Mathf.Max(1f, c.TotalLength);
            if (ratio > 0.0001f)
            {
                float target = Mathf.Clamp(ratio, MinClimbRatio, MaxClimbRatio);
                float k = target / ratio;
                // Never scale a gradient past the ceiling.
                float maxAbs = c.MaxAbsGrade();
                if (maxAbs * k > MaxGrade) k = MaxGrade / Mathf.Max(maxAbs, 0.0001f);
                if (Mathf.Abs(k - 1f) > 0.01f) c.ScaleGrades(k);
            }

            c.Bake();
            return c;
        }

        /// <summary>
        /// The original hand-designed course. Kept as a reference and a fallback;
        /// worlds now generate their course from the seed instead.
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
