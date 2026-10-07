using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MateEngineQoL.Build
{
    /// <summary>
    /// Menu and command-line build helpers.
    /// Batchmode: Unity.exe -batchmode -quit -projectPath . -executeMethod MateEngineQoL.Build.QolBuild.BuildWindows
    /// </summary>
    public static class QolBuild
    {
        const string MainScene = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
        const string DefaultOutput = "Builds/Windows/MateEngineX.exe";

        [MenuItem("MateEngine/QoL/Add Main Scene To Build")]
        public static void EnsureMainSceneInBuild()
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == MainScene))
            {
                Debug.Log("[QolBuild] Main scene already in Build Settings.");
                return;
            }
            scenes.Insert(0, new EditorBuildSettingsScene(MainScene, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[QolBuild] Added main scene to Build Settings.");
        }

        [MenuItem("MateEngine/QoL/Build Windows Player")]
        public static void BuildWindows()
        {
            EnsureMainSceneInBuild();
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = GetArg("-qolBuildOutput") ?? DefaultOutput,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"[QolBuild] {summary.result}: {summary.outputPath}, {summary.totalErrors} errors, {summary.totalWarnings} warnings, {summary.totalTime}");

            if (Application.isBatchMode)
                EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>
        /// On a fresh Library, UniVRM can import .vrm files before the MToon shaders exist and fail with
        /// "ArgumentNullException: Parameter name: Shader". Force-reimporting them afterwards fixes it.
        /// </summary>
        [MenuItem("MateEngine/QoL/Reimport VRM Assets")]
        public static void ReimportVrmAssets()
        {
            string[] paths = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/") && p.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string p in paths)
                    AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            Debug.Log($"[QolBuild] Reimported {paths.Length} VRM assets.");
        }

        static string GetArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
