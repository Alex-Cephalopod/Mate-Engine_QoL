using System.Collections.Generic;
using System.IO;
using MateEngineQoL.Bridge;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization.Tables;

namespace MateEngineQoL.Build
{
    /// <summary>
    /// Creates or updates the fork's own "QoL" string table collection with the English source strings below.
    /// A separate collection keeps upstream's "Languages (UI)" tables untouched, so upstream merges don't conflict.
    /// Translators add other languages in Window/Asset Management/Localization Tables.
    /// </summary>
    public static class QolLocalizationSetup
    {
        const string Folder = "Assets/MateEngineQoL/Localization";

        /// <summary>English source text for every key the fork's UI uses.</summary>
        public static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            ["QOL_AI_PROVIDERS_BUTTON"] = "PROVIDERS",
            ["QOL_AI_PROVIDERS_TITLE"] = "AI PROVIDERS",
            ["QOL_CHAT_PROVIDER"] = "CHAT PROVIDER",
            ["QOL_CHAT_PROVIDER_BUILTIN"] = "Built-in",
            ["QOL_HINT_BUILTIN"] = "Free. Mate Engine's own model runs on this PC.",
            ["QOL_HINT_FREE_LOCAL"] = "Free. Runs on this PC; install the app and pull a model first.",
            ["QOL_HINT_OPENROUTER"] = "Needs a key. Models ending in :free cost nothing; others are paid.",
            ["QOL_HINT_PAID"] = "Needs a key. Paid per use; billed by the provider.",
            ["QOL_HINT_CUSTOM"] = "Any OpenAI-compatible server. https is required when a key is set.",
            ["QOL_BASE_URL"] = "SERVER URL",
            ["QOL_MODEL"] = "MODEL",
            ["QOL_MODEL_HINT"] = "e.g. llama3.2, or an OpenRouter id like meta-llama/llama-3.3-70b-instruct:free",
            ["QOL_API_KEY"] = "API KEY",
            ["QOL_API_KEY_HINT"] = "Paste key, then press Enter",
            ["QOL_API_KEY_SAVED"] = "Key saved (encrypted for your Windows account)",
            ["QOL_API_KEY_ENV"] = "Using MEQOL_CHAT_API_KEY from the environment",
            ["QOL_API_KEY_NOT_NEEDED"] = "Not needed for this provider",
            ["QOL_REMOVE_KEY"] = "REMOVE KEY",
            ["QOL_TEST"] = "TEST CONNECTION",
            ["QOL_TESTING"] = "Testing...",
            ["QOL_TEST_OK"] = "Connected in {0} ms: \"{1}\"",
            ["QOL_TEST_FAILED"] = "Test failed: {0}",
            ["QOL_TEST_BUILTIN"] = "Using the built-in local model. Pick a provider above to use a server instead.",
            ["QOL_STATUS_ERROR"] = "Not ready: {0}",
            ["QOL_STATUS_READY"] = "Ready: replies come from {0}.",
            ["QOL_NOTE_PRIVACY"] = "Your character prompt and chat history are sent to the server you choose. Local servers (Ollama, LM Studio) keep everything on this PC.",
        };

        [MenuItem("MateEngine/QoL/Update QoL String Table")]
        public static void CreateOrUpdate()
        {
            StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(QolText.Table);
            if (collection == null)
            {
                Directory.CreateDirectory(Folder);
                collection = LocalizationEditorSettings.CreateStringTableCollection(QolText.Table, Folder);
            }

            var english = collection.GetTable("en") as StringTable;
            if (english == null)
            {
                Debug.LogError("[QoL] The QoL string table has no English (en) table. Add the English locale to the project first.");
                return;
            }

            int added = 0, updated = 0;
            foreach (KeyValuePair<string, string> pair in English)
            {
                StringTableEntry entry = english.GetEntry(pair.Key);
                if (entry == null) { english.AddEntry(pair.Key, pair.Value); added++; }
                else if (entry.Value != pair.Value) { entry.Value = pair.Value; updated++; }
            }

            EditorUtility.SetDirty(english);
            EditorUtility.SetDirty(english.SharedData);
            EditorUtility.SetDirty(collection);
            AssetDatabase.SaveAssets();
            Debug.Log($"[QoL] QoL string table: {added} added, {updated} updated, {English.Count} total.");
        }
    }
}
