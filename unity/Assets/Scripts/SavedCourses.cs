using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Named worlds, stored as JSON next to the player's other data.
    ///
    /// Only the seed is persisted. Terrain, course and road are all derived from
    /// it by the same generator, so a saved world is a handful of bytes rather
    /// than a heightmap -- and it stays valid as long as the generator does. The
    /// lap and ascent figures are cached purely so the load list can describe a
    /// course without regenerating every entry to find out what it is.
    /// </summary>
    public static class SavedCourses
    {
        [Serializable]
        public class Entry
        {
            public string name;
            public int seed;
            public float lapKm;
            public float ascentM;
            public string savedAt;
        }

        [Serializable]
        class Store
        {
            public List<Entry> items = new List<Entry>();
        }

        public const int MaxNameLength = 32;

        static Store _store;

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, "courses.json");

        static void EnsureLoaded()
        {
            if (_store != null) return;
            _store = new Store();
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var parsed = JsonUtility.FromJson<Store>(json);
                    if (parsed?.items != null) _store = parsed;
                }
                // Say where they live. This file sits next to the Python
                // environment the app builds for itself, and anyone clearing that
                // out by hand should be able to see from the log which folder is
                // which before reaching for rm.
                UnityEngine.Debug.Log($"[SavedCourses] {_store.items.Count} saved in {FilePath}");
            }
            catch (Exception exc)
            {
                // A corrupt file must not stop the app starting; worst case the
                // rider loses their list, which is recoverable by saving again.
                Debug.LogWarning($"[SavedCourses] could not read {FilePath}: {exc.Message}");
                _store = new Store();
            }
        }

        public static IReadOnlyList<Entry> All
        {
            get { EnsureLoaded(); return _store.items; }
        }

        public static string Sanitise(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            name = name.Trim();
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);
            return name;
        }

        /// <summary>Save under this name, replacing any existing entry with the
        /// same name so re-saving a favourite updates it rather than duplicating.</summary>
        public static bool Save(string name, int seed, float lapMetres, float ascentMetres)
        {
            EnsureLoaded();
            name = Sanitise(name);
            if (name.Length == 0) return false;

            _store.items.RemoveAll(e =>
                string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase));

            _store.items.Insert(0, new Entry
            {
                name = name,
                seed = seed,
                lapKm = lapMetres / 1000f,
                ascentM = ascentMetres,
                savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            });

            Persist();
            return true;
        }

        public static void Delete(string name)
        {
            EnsureLoaded();
            _store.items.RemoveAll(e =>
                string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase));
            Persist();
        }

        static void Persist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(_store, true));
            }
            catch (Exception exc)
            {
                Debug.LogError($"[SavedCourses] could not write {FilePath}: {exc.Message}");
            }
        }

        /// <summary>Forget the in-memory copy; the next read reloads from disk.</summary>
        public static void Reload() => _store = null;

        /// <summary>
        /// A human default for the save dialog: the longest climbing feature on
        /// the course, which is the thing a rider would actually name it after.
        /// </summary>
        public static string SuggestName(CourseProfile profile, int seed)
        {
            if (profile == null) return $"Course {seed}";

            string best = null, current = null;
            float bestRise = 0f, currentRise = 0f;

            void Consider()
            {
                if (current != null && currentRise > bestRise) { bestRise = currentRise; best = current; }
            }

            foreach (var s in profile.Segments)
            {
                if (s.Name != current) { Consider(); current = s.Name; currentRise = 0f; }
                float rise = s.LengthM * (s.StartGrade + s.EndGrade) * 0.5f;
                if (rise > 0f) currentRise += rise;
            }
            Consider();

            if (string.IsNullOrEmpty(best)) return $"Course {seed}";
            // Strip the "(upper)" suffix so the two halves of a split climb give
            // the same name.
            int paren = best.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) best = best.Substring(0, paren);
            return best;
        }
    }
}
