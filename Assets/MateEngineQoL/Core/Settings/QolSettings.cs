using System;
using System.Collections.Generic;

namespace MateEngineQoL.Settings
{
    /// <summary>
    /// Everything in qol_settings.json. No secrets here: API keys live in <see cref="QolSecrets"/>.
    /// New fields need defaults that keep old files working.
    /// </summary>
    public sealed class QolSettings
    {
        public int Version = 1;
        public ChatSettings Chat = new ChatSettings();
        public CharacterSettings Characters = new CharacterSettings();
        public ContextSettings Context = new ContextSettings();
    }

    public sealed class CharacterSettings
    {
        /// <summary>Folder name under Characters/ of the character to load on start.</summary>
        public string ActiveId = "default";
    }

    /// <summary>
    /// Token budgets for what is sent with each message (see ContextBuilder). Estimated tokens, not exact.
    /// For the built-in model the total is also capped by its context size.
    /// </summary>
    public sealed class ContextSettings
    {
        public int TotalTokens = 8192;
        /// <summary>Left free for the reply.</summary>
        public int ReplyReserveTokens = 1024;
        public int FactsTokens = 512;
        public int SummaryTokens = 1024;
        /// <summary>Most earlier messages (user + AI) sent with each request, within the token budget.</summary>
        public int MaxHistoryMessages = 20;
    }

    public sealed class ChatSettings
    {
        /// <summary>"upstream-local" (Mate Engine's built-in LLM) or "openai-compatible".</summary>
        public string Provider = ChatProviderIds.UpstreamLocal;
        /// <summary>Id from <see cref="ChatPresets"/>; only used to prefill the base URL in the UI.</summary>
        public string Preset = "ollama";
        public string BaseUrl = "http://localhost:11434/v1";
        public string Model = "";
        public float? Temperature;
        public int? MaxTokens;
    }

    public static class ChatProviderIds
    {
        public const string UpstreamLocal = "upstream-local";
        public const string OpenAICompatible = "openai-compatible";
    }

    public sealed class ChatPreset
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly string BaseUrl;
        public readonly bool NeedsKey;
        public readonly bool Free;

        public ChatPreset(string id, string displayName, string baseUrl, bool needsKey, bool free)
        {
            Id = id;
            DisplayName = displayName;
            BaseUrl = baseUrl;
            NeedsKey = needsKey;
            Free = free;
        }
    }

    public static class ChatPresets
    {
        public static readonly IReadOnlyList<ChatPreset> All = new[]
        {
            new ChatPreset("ollama", "Ollama", "http://localhost:11434/v1", false, true),
            new ChatPreset("lmstudio", "LM Studio", "http://localhost:1234/v1", false, true),
            new ChatPreset("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", true, false),
            new ChatPreset("openai", "OpenAI", "https://api.openai.com/v1", true, false),
            new ChatPreset("deepseek", "DeepSeek", "https://api.deepseek.com/v1", true, false),
            new ChatPreset("custom", "Custom", "", false, false),
        };

        public static ChatPreset Find(string id)
        {
            foreach (ChatPreset p in All)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }

    /// <summary>
    /// Contents of qol_secrets.json once decrypted. Keys are per capability slot ("chat", "tts", "stt").
    /// </summary>
    public sealed class QolSecrets
    {
        public Dictionary<string, string> ApiKeys = new Dictionary<string, string>();

        public const string ChatSlot = "chat";
        public const string TtsSlot = "tts";
        public const string SttSlot = "stt";
    }
}
