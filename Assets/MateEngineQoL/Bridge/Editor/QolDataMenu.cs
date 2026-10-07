using MateEngineQoL.Bridge;
using UnityEditor;
using UnityEngine;

namespace MateEngineQoL.Build
{
    public static class QolDataMenu
    {
        [MenuItem("MateEngine/QoL/Import Steam Data (no overwrite)")]
        public static void ImportSteamData()
        {
            string source = SteamDataImporter.FindSteamDataFolder();
            if (source == null)
            {
                Debug.LogWarning("[QoL Import] No Steam Mate Engine data folder found next to " + Application.persistentDataPath);
                return;
            }
            SteamDataImporter.Run(source, Application.persistentDataPath);
        }

        [MenuItem("MateEngine/QoL/Open Data Folder")]
        public static void OpenDataFolder() => EditorUtility.RevealInFinder(Application.persistentDataPath);
    }
}
