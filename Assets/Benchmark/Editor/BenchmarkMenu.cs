using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Benchmark.Editor
{
    /// <summary>
    /// Tools → Benchmark menu: runs the scene's QualityBenchmark in Play Mode and switches the texture compression
    /// of the Sponza textures, so a run with uncompressed textures can serve as the reference for compressed ones.
    /// </summary>
    public static class BenchmarkMenu
    {
        const string k_Root = "Tools/Benchmark/";
        const string k_CompressionRoot = k_Root + "Sponza Texture Compression/";
        const string k_TextureFolder = "Assets/glTF";

        [MenuItem(k_Root + "Run Benchmark", priority = 0)]
        static void Run()
        {
            if (Object.FindFirstObjectByType<QualityBenchmark>() == null)
            {
                EditorUtility.DisplayDialog("Benchmark", "The open scene has no QualityBenchmark component.", "OK");
                return;
            }

            if (EditorApplication.isPlaying)
            {
                Object.FindFirstObjectByType<QualityBenchmark>().StartBenchmark();
                return;
            }

            SessionState.SetBool(QualityBenchmark.autoRunSessionKey, true);
            EditorApplication.EnterPlaymode();
        }

        [MenuItem(k_Root + "Open Results Folder", priority = 1)]
        static void OpenResults()
        {
            var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BenchmarkResults"));
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        [MenuItem(k_CompressionRoot + "None (uncompressed)", priority = 20)]
        static void SetNone() => SetCompression(TextureImporterCompression.Uncompressed, false);

        [MenuItem(k_CompressionRoot + "Low Quality", priority = 21)]
        static void SetLow() => SetCompression(TextureImporterCompression.CompressedLQ, false);

        [MenuItem(k_CompressionRoot + "Normal Quality", priority = 22)]
        static void SetNormal() => SetCompression(TextureImporterCompression.Compressed, false);

        [MenuItem(k_CompressionRoot + "High Quality", priority = 23)]
        static void SetHigh() => SetCompression(TextureImporterCompression.CompressedHQ, false);

        [MenuItem(k_CompressionRoot + "Normal Quality + Crunch (50)", priority = 24)]
        static void SetCrunch() => SetCompression(TextureImporterCompression.Compressed, true);

        [MenuItem(k_CompressionRoot + "None (uncompressed)", true)]
        static bool ValidateNone() => Check(TextureImporterCompression.Uncompressed, false);

        [MenuItem(k_CompressionRoot + "Low Quality", true)]
        static bool ValidateLow() => Check(TextureImporterCompression.CompressedLQ, false);

        [MenuItem(k_CompressionRoot + "Normal Quality", true)]
        static bool ValidateNormal() => Check(TextureImporterCompression.Compressed, false);

        [MenuItem(k_CompressionRoot + "High Quality", true)]
        static bool ValidateHigh() => Check(TextureImporterCompression.CompressedHQ, false);

        [MenuItem(k_CompressionRoot + "Normal Quality + Crunch (50)", true)]
        static bool ValidateCrunch() => Check(TextureImporterCompression.Compressed, true);

        static TextureImporter[] SponzaTextures() =>
            AssetDatabase.FindAssets("t:Texture2D", new[] { k_TextureFolder })
                .Select(guid => AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter)
                .Where(importer => importer != null)
                .ToArray();

        // Ticks the menu entry that matches the current import settings of the first texture.
        static bool Check(TextureImporterCompression compression, bool crunch)
        {
            var first = SponzaTextures().FirstOrDefault();
            var path = k_CompressionRoot + MenuName(compression, crunch);
            Menu.SetChecked(path, first != null && first.textureCompression == compression && first.crunchedCompression == crunch);
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        static string MenuName(TextureImporterCompression compression, bool crunch)
        {
            if (crunch)
                return "Normal Quality + Crunch (50)";
            switch (compression)
            {
                case TextureImporterCompression.Uncompressed: return "None (uncompressed)";
                case TextureImporterCompression.CompressedLQ: return "Low Quality";
                case TextureImporterCompression.CompressedHQ: return "High Quality";
                default: return "Normal Quality";
            }
        }

        static void SetCompression(TextureImporterCompression compression, bool crunch)
        {
            var importers = SponzaTextures();
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var importer in importers)
                {
                    importer.textureCompression = compression;
                    importer.crunchedCompression = crunch;
                    importer.compressionQuality = 50;
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Debug.Log($"[Benchmark] {importers.Length} Sponza textures set to {MenuName(compression, crunch)}.");
        }
    }
}
