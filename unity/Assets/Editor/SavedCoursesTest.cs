using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Round-trips the saved-course store through disk. Save/load is the sort of
    /// thing that looks fine until the file is actually re-read in a fresh
    /// session, so every check here forces a reload rather than trusting the
    /// in-memory copy.
    /// </summary>
    public static class SavedCoursesTest
    {
        [MenuItem("VibeRide/Test Saved Courses")]
        public static void Run()
        {
            string path = SavedCourses.FilePath;
            string backup = null;
            if (File.Exists(path))
            {
                backup = File.ReadAllText(path);
                File.Delete(path);
            }
            SavedCourses.Reload();

            int failures = 0;
            void Check(string label, bool ok, string detail = "")
            {
                Debug.Log($"[SavedCoursesTest] [{(ok ? "ok" : "FAIL")}] {label} {detail}");
                if (!ok) failures++;
            }

            try
            {
                Check("starts empty", SavedCourses.All.Count == 0, $"count={SavedCourses.All.Count}");

                SavedCourses.Save("Alpine loop", 1234, 25020f, 545f);
                SavedCourses.Save("Rolling hills", 777, 24940f, 430f);
                SavedCourses.Reload();
                Check("two entries survive a reload", SavedCourses.All.Count == 2,
                      $"count={SavedCourses.All.Count}");

                var found = System.Linq.Enumerable.FirstOrDefault(
                    SavedCourses.All, e => e.name == "Alpine loop");
                Check("seed round-trips", found != null && found.seed == 1234,
                      found == null ? "entry missing" : $"seed={found.seed}");
                Check("lap round-trips", found != null && Mathf.Abs(found.lapKm - 25.02f) < 0.01f,
                      found == null ? "" : $"lapKm={found.lapKm:F2}");

                // Re-saving a name must replace, not duplicate: a rider updating a
                // favourite should not end up with two of them.
                SavedCourses.Save("Alpine loop", 9999, 26000f, 600f);
                SavedCourses.Reload();
                var updated = System.Linq.Enumerable.FirstOrDefault(
                    SavedCourses.All, e => e.name == "Alpine loop");
                Check("re-save replaces", SavedCourses.All.Count == 2 && updated != null && updated.seed == 9999,
                      $"count={SavedCourses.All.Count}, seed={updated?.seed}");

                Check("case-insensitive replace",
                      SavedCourses.Save("ALPINE LOOP", 4242, 25000f, 500f) && SavedCourses.All.Count == 2,
                      $"count={SavedCourses.All.Count}");

                Check("blank name rejected", !SavedCourses.Save("   ", 1, 1f, 1f));
                Check("long name truncated",
                      SavedCourses.Sanitise(new string('x', 100)).Length == SavedCourses.MaxNameLength);

                SavedCourses.Delete("Rolling hills");
                SavedCourses.Reload();
                Check("delete works", SavedCourses.All.Count == 1, $"count={SavedCourses.All.Count}");

                // A corrupt file must not stop the app starting.
                File.WriteAllText(path, "{ this is not json");
                SavedCourses.Reload();
                Check("survives a corrupt file", SavedCourses.All.Count == 0);

                // Name suggestion should pick the biggest climb.
                var profile = CourseProfile.CreateRandom(20260816);
                string suggested = SavedCourses.SuggestName(profile, 20260816);
                Check("suggests a climb name",
                      !string.IsNullOrEmpty(suggested) && !suggested.Contains("(upper)"),
                      $"\"{suggested}\"");

                Debug.Log($"[SavedCoursesTest] {(failures == 0 ? "PASS" : failures + " FAILURES")}");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (backup != null) File.WriteAllText(path, backup);
                SavedCourses.Reload();
            }

            if (failures > 0) throw new Exception($"{failures} saved-course checks failed");
        }

        public static void RunFromCommandLine()
        {
            try { Run(); EditorApplication.Exit(0); }
            catch (Exception exc)
            {
                Debug.LogError($"[SavedCoursesTest] FAILED: {exc.Message}");
                EditorApplication.Exit(1);
            }
        }
    }
}
