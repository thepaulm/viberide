using System;
using UnityEditor;
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Generates a large sample of courses and checks the properties that make
    /// one rideable. A generator that produces a good course for the seed you
    /// happened to test is worth very little -- Regenerate hands the user an
    /// arbitrary seed every time.
    /// </summary>
    public static class CourseAudit
    {
        const int Samples = 300;
        const float LapLength = 25000f;   // courses get scaled onto the loop

        [MenuItem("VibeRide/Audit Course Generator")]
        public static void Audit()
        {
            int failures = 0;
            float minAscent = float.MaxValue, maxAscent = 0f, sumAscent = 0f;
            float worstNet = 0f, worstGrade = 0f, worstStep = 0f, worstShare = 0f;
            int minSegments = int.MaxValue, maxSegments = 0;
            int minFeatures = int.MaxValue, maxFeatures = 0;

            for (int i = 0; i < Samples; i++)
            {
                int seed = 1000 + i * 7919;   // spread out, not consecutive
                var c = CourseProfile.CreateRandom(seed);
                c.ScaleToLength(LapLength);

                float net = Mathf.Abs(c.NetElevation);
                float maxGrade = c.MaxAbsGrade();
                float ascent = c.TotalAscent;

                // Gradient must never step between consecutive segments: on a
                // trainer a discontinuity feels like hitting a kerb.
                float worstStepHere = 0f;
                for (int k = 0; k < c.Segments.Count - 1; k++)
                {
                    float step = Mathf.Abs(c.Segments[k].EndGrade - c.Segments[k + 1].StartGrade);
                    worstStepHere = Mathf.Max(worstStepHere, step);
                }
                // The lap wraps, so the last segment's exit gradient meets the
                // first segment's entry gradient too. Easy join to forget.
                worstStepHere = Mathf.Max(worstStepHere,
                    Mathf.Abs(c.Segments[c.Segments.Count - 1].EndGrade - c.Segments[0].StartGrade));

                int features = 0;
                string prev = null;
                float longestFeature = 0f, thisFeature = 0f;
                foreach (var s in c.Segments)
                {
                    if (s.Name != prev)
                    {
                        longestFeature = Mathf.Max(longestFeature, thisFeature);
                        thisFeature = 0f;
                        features++; prev = s.Name;
                    }
                    thisFeature += s.LengthM;
                }
                longestFeature = Mathf.Max(longestFeature, thisFeature);
                float longestShare = longestFeature / c.TotalLength;

                bool ok = true;
                if (net > 0.5f) { ok = false; Debug.LogError($"[CourseAudit] seed {seed}: net elevation {c.NetElevation:F2} m -- lap will not join"); }
                if (maxGrade > CourseProfile.MaxGrade + 0.001f) { ok = false; Debug.LogError($"[CourseAudit] seed {seed}: gradient {maxGrade * 100f:F1}% exceeds ceiling"); }
                if (worstStepHere > 0.006f) { ok = false; Debug.LogError($"[CourseAudit] seed {seed}: gradient steps {worstStepHere * 100f:F2}% between segments"); }
                if (ascent < 250f || ascent > 950f) { ok = false; Debug.LogError($"[CourseAudit] seed {seed}: ascent {ascent:F0} m outside sane band"); }
                if (features < 6) { ok = false; Debug.LogError($"[CourseAudit] seed {seed}: only {features} features -- too monotonous"); }
                // No single stretch should dominate the lap. This is how an
                // undersized descent shows up: the loop-closer swells to absorb
                // the leftover climbing and you ride half the lap on one gradient.
                if (longestShare > 0.32f) { ok = false; Debug.LogError($"[CourseAudit] seed {seed}: one feature is {longestShare * 100f:F0}% of the lap"); }
                if (!ok) failures++;
                worstShare = Mathf.Max(worstShare, longestShare);

                minAscent = Mathf.Min(minAscent, ascent);
                maxAscent = Mathf.Max(maxAscent, ascent);
                sumAscent += ascent;
                worstNet = Mathf.Max(worstNet, net);
                worstGrade = Mathf.Max(worstGrade, maxGrade);
                worstStep = Mathf.Max(worstStep, worstStepHere);
                minSegments = Mathf.Min(minSegments, c.Segments.Count);
                maxSegments = Mathf.Max(maxSegments, c.Segments.Count);
                minFeatures = Mathf.Min(minFeatures, features);
                maxFeatures = Mathf.Max(maxFeatures, features);
            }

            Debug.Log($"[CourseAudit] {Samples} seeds, scaled to a {LapLength / 1000f:F0} km lap\n" +
                      $"  ascent      {minAscent:F0} - {maxAscent:F0} m (mean {sumAscent / Samples:F0})\n" +
                      $"  worst net   {worstNet:F3} m (must be ~0 for a seamless lap)\n" +
                      $"  steepest    {worstGrade * 100f:F1}% (ceiling {CourseProfile.MaxGrade * 100f:F0}%)\n" +
                      $"  worst step  {worstStep * 100f:F3}% between segments\n" +
                      $"  longest     {worstShare * 100f:F0}% of the lap in one feature\n" +
                      $"  segments    {minSegments} - {maxSegments}\n" +
                      $"  features    {minFeatures} - {maxFeatures}\n" +
                      $"  FAILURES    {failures}");

            // Show a couple of real examples so the output is judgeable, not just
            // a set of numbers that pass.
            foreach (int seed in new[] { 20260816, 4242 })
            {
                var c = CourseProfile.CreateRandom(seed);
                c.ScaleToLength(LapLength);
                Debug.Log($"[CourseAudit] example seed {seed}: " +
                          $"{c.TotalAscent:F0} m ascent, steepest {c.MaxAbsGrade() * 100f:F1}%\n  " +
                          string.Join("\n  ", DistinctSegmentNames(c)));
            }

            if (failures > 0) throw new Exception($"{failures}/{Samples} generated courses failed validation");
        }

        /// <summary>
        /// One line per named feature: entry gradient to exit gradient, not
        /// min-to-max. Showing the range made a ramp down from 13% look like a
        /// ramp up to it, which is actively misleading when reading a profile.
        /// </summary>
        static System.Collections.Generic.List<string> DistinctSegmentNames(CourseProfile profile)
        {
            var lines = new System.Collections.Generic.List<string>();
            string current = null;
            float length = 0f, entry = 0f, exit = 0f;

            void Flush()
            {
                if (current == null) return;
                lines.Add($"{current,-28} {length / 1000f:F2} km  " +
                          $"{entry * 100f:+0.0;-0.0;0.0}% -> {exit * 100f:+0.0;-0.0;0.0}%");
            }

            foreach (var s in profile.Segments)
            {
                if (s.Name != current)
                {
                    Flush();
                    current = s.Name;
                    length = 0f;
                    entry = s.StartGrade;
                }
                length += s.LengthM;
                exit = s.EndGrade;
            }
            Flush();
            return lines;
        }

        public static void AuditFromCommandLine()
        {
            try { Audit(); EditorApplication.Exit(0); }
            catch (Exception exc)
            {
                Debug.LogError($"[CourseAudit] FAILED: {exc.Message}");
                EditorApplication.Exit(1);
            }
        }
    }
}
