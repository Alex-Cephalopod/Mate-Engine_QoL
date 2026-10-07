using System.Net.Http;
using System.Threading;
using MateEngineQoL.AI;
using MateEngineQoL.AI.OpenAI;
using MateEngineQoL.Settings;
using UnityEngine;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Fork-wide services: settings, secrets and the provider registry. Initialized lazily on first use,
    /// which is after SaveLoadHandler.Awake, so the per-instance data folder (--datadir) is known.
    /// </summary>
    public static class QolServices
    {
        static bool _initialized;
        static QolSecrets _secrets;
        static HttpClient _http;

        public static QolConfigStore Store { get; private set; }
        public static QolSettings Settings { get; private set; }
        public static ProviderRegistry Registry { get; private set; }

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            // Settings per instance (next to settings.json); API keys shared at the data root.
            Store = new QolConfigStore(SaveLoadHandler.DataDirectory, SecretProtectors.CreateForPlatform(),
                secretsDir: Application.persistentDataPath);

            // One client for the app's lifetime; providers apply their own connect/idle timeouts.
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            Registry = new ProviderRegistry();
            Registry.RegisterChat(ChatProviderIds.OpenAICompatible,
                (s, key) => new OpenAICompatibleChatProvider(_http, s.BaseUrl, key));

            Reload();

            // Write defaults once so the file exists for hand-editing before the settings UI lands.
            if (!System.IO.File.Exists(System.IO.Path.Combine(Store.Directory, QolConfigStore.SettingsFile)))
                Store.SaveSettings(Settings);
        }

        public static void Reload()
        {
            Settings = Store.LoadSettings();
            _secrets = Store.LoadSecrets();
            if (Store.LastWarning != null) Debug.LogWarning("[QoL] " + Store.LastWarning);
            Rebuild();
        }

        public static void SaveSettings(QolSettings settings)
        {
            EnsureInitialized();
            Store.SaveSettings(settings);
            Settings = settings;
            Rebuild();
        }

        /// <summary>Stores (or clears, with null/empty) the API key for a slot such as <see cref="QolSecrets.ChatSlot"/>.</summary>
        public static void SetApiKey(string slot, string key)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(key)) _secrets.ApiKeys.Remove(slot);
            else _secrets.ApiKeys[slot] = key.Trim();
            Store.SaveSecrets(_secrets);
            Rebuild();
        }

        public static bool HasApiKey(string slot)
        {
            EnsureInitialized();
            return Store.GetApiKey(_secrets, slot) != null;
        }

        static void Rebuild()
        {
            Registry.Rebuild(Settings, slot => Store.GetApiKey(_secrets, slot));
            if (Registry.ChatError != null) Debug.LogWarning("[QoL] Chat provider not ready: " + Registry.ChatError);
            else Debug.Log("[QoL] Chat provider: " + (Registry.Chat?.Id ?? ChatProviderIds.UpstreamLocal));
        }
    }
}
