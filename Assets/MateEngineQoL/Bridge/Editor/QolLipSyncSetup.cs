using System.IO;
using MateEngineQoL.Bridge;
using UnityEditor;
using UnityEngine;

namespace MateEngineQoL.Build
{
    /// <summary>
    /// Copies uLipSync's sample profile into Resources so the voice pipeline can load it at runtime
    /// (package assets aren't in a build unless something references them).
    /// </summary>
    public static class QolLipSyncSetup
    {
        const string Source = "Packages/com.hecomi.ulipsync/Assets/Profiles/uLipSync-Profile-Sample.asset";
        const string Folder = "Assets/MateEngineQoL/Resources";
        static string Target => Folder + "/" + QolLipSync.ProfileResource + ".asset";

        [MenuItem("MateEngine/QoL/Create Lip Sync Profile")]
        public static void CreateProfile()
        {
            if (AssetDatabase.LoadAssetAtPath<uLipSync.Profile>(Target) != null)
            {
                Debug.Log("[QoL] Lip sync profile already exists: " + Target);
                return;
            }
            if (AssetDatabase.LoadAssetAtPath<uLipSync.Profile>(Source) == null)
            {
                Debug.LogError("[QoL] uLipSync sample profile not found at " + Source + ". Is the package installed?");
                return;
            }
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
            if (AssetDatabase.CopyAsset(Source, Target)) Debug.Log("[QoL] Created " + Target);
            else Debug.LogError("[QoL] Could not copy " + Source + " to " + Target);
        }
    }
}
