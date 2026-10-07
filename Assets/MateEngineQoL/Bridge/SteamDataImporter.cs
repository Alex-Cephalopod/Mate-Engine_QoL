using System;
using System.Diagnostics;
using System.IO;
using MateEngineQoL.Import;
using Newtonsoft.Json;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// The fork has its own data folder (productName MateEngineQoL). On first launch, before any scene
    /// loads and before SaveLoadHandler reads settings.json, copy the Steam install's data folder
    /// (avatars, Workshop models, mods, prompt, chat history, settings) into it. The Steam folder is only read.
    /// </summary>
    public static class SteamDataImporter
    {
        public const string SteamProductFolder = "MateEngineX";
        public const string MarkerFile = "qol_import.json";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ImportOnFirstRun()
        {
            string dest = Application.persistentDataPath;
            if (File.Exists(Path.Combine(dest, MarkerFile)) || File.Exists(Path.Combine(dest, "settings.json")))
                return;

            string source = FindSteamDataFolder();
            if (source == null) return;

            Run(source, dest);
        }

        /// <summary>The Steam build's data folder, a sibling of ours under the same company folder; null if absent.</summary>
        public static string FindSteamDataFolder()
        {
            string dest = Path.GetFullPath(Application.persistentDataPath);
            DirectoryInfo company = Directory.GetParent(dest);
            if (company == null) return null;

            string source = Path.Combine(company.FullName, SteamProductFolder);
            if (string.Equals(Path.GetFullPath(source), dest, StringComparison.OrdinalIgnoreCase)) return null;
            return Directory.Exists(source) ? source : null;
        }

        /// <summary>Copies without overwriting and records the outcome in qol_import.json.</summary>
        public static DataFolderImport.Result Run(string source, string dest)
        {
            var timer = Stopwatch.StartNew();
            DataFolderImport.Result result;
            try
            {
                Directory.CreateDirectory(dest);
                result = DataFolderImport.Run(source, dest);
            }
            catch (Exception e)
            {
                Debug.LogError("[QoL Import] Import from Steam data folder failed: " + e.Message);
                return null;
            }

            var marker = new
            {
                importedAtUtc = DateTime.UtcNow.ToString("o"),
                source,
                result.Copied,
                result.Rewritten,
                result.SkippedExisting,
                result.SkippedExcluded,
                result.BytesCopied,
                result.Errors,
            };
            try { File.WriteAllText(Path.Combine(dest, MarkerFile), JsonConvert.SerializeObject(marker, Formatting.Indented)); }
            catch (Exception e) { Debug.LogWarning("[QoL Import] Could not write " + MarkerFile + ": " + e.Message); }

            Debug.Log($"[QoL Import] Copied {result.Copied} files ({result.BytesCopied / (1024 * 1024)} MB), rewrote paths in {result.Rewritten}, " +
                      $"kept {result.SkippedExisting} existing, skipped {result.SkippedExcluded}, {result.Errors.Count} errors, {timer.ElapsedMilliseconds} ms.");
            foreach (string error in result.Errors)
                Debug.LogWarning("[QoL Import] " + error);
            return result;
        }
    }
}
