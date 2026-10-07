using System;
using System.Collections.Generic;
using MateEngineQoL.Settings;

namespace MateEngineQoL.AI
{
    /// <summary>
    /// Builds the active providers from settings and rebuilds them when settings change.
    /// Factories are registered by id; the Bridge registers anything that needs Unity.
    /// </summary>
    public sealed class ProviderRegistry
    {
        public delegate IChatProvider ChatFactory(ChatSettings settings, string apiKey);

        readonly Dictionary<string, ChatFactory> _chatFactories = new Dictionary<string, ChatFactory>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The active chat provider, or null when the upstream local LLM handles chat.</summary>
        public IChatProvider Chat { get; private set; }

        /// <summary>Why the configured provider couldn't be built (bad URL, missing key); null if fine.</summary>
        public string ChatError { get; private set; }

        public event Action Changed;

        public void RegisterChat(string id, ChatFactory factory) => _chatFactories[id] = factory;

        public void Rebuild(QolSettings settings, Func<string, string> getApiKey)
        {
            Chat = null;
            ChatError = null;

            string id = settings?.Chat?.Provider ?? ChatProviderIds.UpstreamLocal;
            if (!string.Equals(id, ChatProviderIds.UpstreamLocal, StringComparison.OrdinalIgnoreCase))
            {
                if (_chatFactories.TryGetValue(id, out ChatFactory factory))
                {
                    try { Chat = factory(settings.Chat, getApiKey(QolSecrets.ChatSlot)); }
                    catch (ProviderException e) { ChatError = e.Message; }
                }
                else
                {
                    ChatError = "Unknown chat provider '" + id + "'.";
                }
            }

            Changed?.Invoke();
        }
    }
}
