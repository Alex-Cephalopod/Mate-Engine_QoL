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
            ["QOL_VOICE_TITLE"] = "VOICE",
            ["QOL_TTS_PROVIDER"] = "VOICE PROVIDER",
            ["QOL_TTS_OFF"] = "Off",
            ["QOL_TTS_HINT_OFF"] = "Replies are not spoken.",
            ["QOL_TTS_HINT_KOKORO"] = "Free. Runs on this PC: start Kokoro-FastAPI on port 8880 first.",
            ["QOL_TTS_HINT_OPENAI"] = "Needs an OpenAI key. Paid per use (about $0.015 per minute of speech).",
            ["QOL_TTS_HINT_FISH"] = "Needs a Fish Audio key. Model s2.1-pro-free costs nothing; s1 and s2.1-pro need prepaid API credit. VOICE takes a voice model id from fish.audio.",
            ["QOL_TTS_HINT_CUSTOM"] = "Any OpenAI-compatible /audio/speech server that returns raw PCM.",
            ["QOL_TTS_VOICE"] = "VOICE",
            ["QOL_TTS_VOICE_HINT"] = "e.g. af_heart (Kokoro), alloy (OpenAI), or a Fish voice model id",
            ["QOL_TTS_MODEL_HINT"] = "e.g. kokoro, gpt-4o-mini-tts, or s2.1-pro-free for Fish",
            ["QOL_TTS_KEY_ENV"] = "Using MEQOL_TTS_API_KEY from the environment",
            ["QOL_TTS_SPEED"] = "SPEED {0}x",
            ["QOL_TTS_VOLUME"] = "VOLUME {0}%",
            ["QOL_TTS_TEST"] = "TEST VOICE",
            ["QOL_TTS_TEST_OK"] = "Speaking. First audio after {0} ms.",
            ["QOL_TTS_SAMPLE"] = "Hi! This is how I sound. Do you like my voice?",
            ["QOL_TTS_STATUS_OFF"] = "Voice is off.",
            ["QOL_TTS_STATUS_READY"] = "Ready: replies are spoken by {0}.",
            ["QOL_TTS_STATUS_LAST_ERROR"] = "Last voice error: {0}",
            ["QOL_TTS_NOTE"] = "Reply text is sent to the voice server; Kokoro keeps it on this PC. Text between *asterisks* is not spoken. Grab the avatar or send a new message to stop it talking.",
            ["QOL_CHARACTERS_TITLE"] = "CHARACTERS",
            ["QOL_CHARACTER"] = "CHARACTER",
            ["QOL_CHARACTER_NEW"] = "+ New character",
            ["QOL_CHARACTER_NEW_NAME"] = "New character",
            ["QOL_CHARACTER_NAME"] = "NAME",
            ["QOL_GREETING"] = "GREETING",
            ["QOL_GREETING_HINT"] = "Optional first message in a new chat (not sent to the model)",
            ["QOL_NEW_CHAT"] = "NEW CHAT",
            ["QOL_CHARACTER_NOTE"] = "Edit the character's prompt in the AI section's prompt box. In chat: /char <name> switches, /chars lists, /new starts a new chat.",
            ["QOL_CMD_NO_CHARACTER"] = "No character called \"{0}\". Characters: {1}",
            ["QOL_CMD_CHARACTERS"] = "Talking to {0}. Characters: {1}. Switch with /char <name>.",
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
