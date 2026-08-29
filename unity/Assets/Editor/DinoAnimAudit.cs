using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KickrWorld.EditorTools
{
    /// <summary>
    /// Inspect and configure the animation on the dinosaur models.
    ///
    /// They ship as Generic (Mecanim) rigs, which cannot be played without an
    /// AnimatorController asset per species. Legacy needs none: the imported
    /// model carries an Animation component holding the clips, and a caller can
    /// simply name one. For six looping idles on scattered scenery that is the
    /// whole requirement, and it keeps the setup where it belongs -- in the
    /// asset, committed, rather than assembled at build time.
    /// </summary>
    public static class DinoAnimAudit
    {
        const string Folder = "Assets/Models/Dinosaurs";

        [MenuItem("VibeRide/Audit Dinosaur Animations")]
        public static void Audit()
        {
            foreach (var path in ModelPaths())
            {
                var clips = AssetDatabase.LoadAllAssetsAtPath(path)
                                         .OfType<AnimationClip>()
                                         .Where(c => !c.name.StartsWith("__preview__"))
                                         .ToList();

                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                Debug.Log($"[DinoAnim] {System.IO.Path.GetFileName(path)}: " +
                          $"{clips.Count} clip(s), rig={importer?.animationType}" +
                          (clips.Count == 0 ? "" :
                           " -> " + string.Join(", ", clips.Select(c =>
                               $"{c.name} {c.length:F2}s"))));
            }
        }

        /// <summary>Switch the models to Legacy so the clips can be played
        /// without an AnimatorController for each one.</summary>
        [MenuItem("VibeRide/Set Dinosaurs To Legacy Animation")]
        public static void MakeLegacy()
        {
            int changed = 0;
            foreach (var path in ModelPaths())
            {
                if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;
                if (importer.animationType == ModelImporterAnimationType.Legacy) continue;

                importer.animationType = ModelImporterAnimationType.Legacy;
                importer.importAnimation = true;
                importer.SaveAndReimport();
                changed++;
                Debug.Log($"[DinoAnim] {System.IO.Path.GetFileName(path)} -> Legacy");
            }
            Debug.Log($"[DinoAnim] {changed} model(s) switched");
            Audit();
        }

        static string[] ModelPaths() =>
            AssetDatabase.FindAssets("t:Model", new[] { Folder })
                         .Select(AssetDatabase.GUIDToAssetPath)
                         .OrderBy(p => p)
                         .ToArray();

        public static void AuditFromCommandLine()
        {
            Audit();
            EditorApplication.Exit(0);
        }

        public static void MakeLegacyFromCommandLine()
        {
            MakeLegacy();
            EditorApplication.Exit(0);
        }
    }
}
