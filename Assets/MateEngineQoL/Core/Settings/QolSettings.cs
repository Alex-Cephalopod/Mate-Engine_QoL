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
        public TtsSettings Tts = new TtsSettings();
        public AnimationSettings Animation = new AnimationSettings();
    }

    /// <summary>The animation library (Phase 4): clips in &lt;data folder&gt;/Animations.</summary>
    public sealed class AnimationSettings
    {
        /// <summary>Play the folder's idle clips while the avatar stands idle.</summary>
        public bool LibraryIdles = true;
        /// <summary>With several idles, switch to another one after this many seconds.</summary>
        public float IdleSwitchSeconds = 45f;
        /// <summary>Clip id always used as the idle ("set as idle" in the library panel); null rotates through the idles.</summary>
        public string PinnedIdle;
        /// <summary>Clip ids (paths relative to the folder) starred in the library panel.</summary>
        public List<string> Favourites = new List<string>();
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

    /// <summary>Text-to-speech for replies (Phase 2). Off by default; the API key lives in the "tts" secret slot.</summary>
    public sealed class TtsSettings
    {
        /// <summary>"none", "openai-compatible" (Kokoro-FastAPI, Chatterbox, OpenAI, ...) or "fish-audio".</summary>
        public string Provider = TtsProviderIds.None;
        /// <summary>Id from <see cref="TtsPresets"/>; only used to prefill fields in the UI.</summary>
        public string Preset = "kokoro";
        public string BaseUrl = "http://localhost:8880/v1";
        public string Model = "kokoro";
        /// <summary>Voice name (OpenAI-compatible; for Chatterbox the clip's file name), or voice model id (Fish Audio reference_id).</summary>
        public string Voice = "af_heart";
        /// <summary>0.5 to 2.</summary>
        public float Speed = 1f;
        /// <summary>0 to 1, applied to the voice's AudioSource.</summary>
        public float Volume = 1f;
        /// <summary>Don't speak text between asterisks (*smiles*).</summary>
        public bool SkipActions = true;
        /// <summary>
        /// Turn sound actions (*chuckles*, *sighs*) into [tags] the server plays as sounds (Chatterbox Turbo).
        /// Null follows the preset; servers without tags would read them aloud.
        /// </summary>
        public bool? SoundTags;

        public bool UsesSoundTags() => SoundTags ?? TtsPresets.Find(Preset)?.SoundTags ?? false;
    }

    public static class TtsProviderIds
    {
        public const string None = "none";
        public const string OpenAICompatible = "openai-compatible";
        public const string FishAudio = "fish-audio";
    }

    public sealed class TtsPreset
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly string Provider;
        public readonly string BaseUrl;
        public readonly string Model;
        public readonly string Voice;
        public readonly bool NeedsKey;
        public readonly bool Free;
        /// <summary>The server plays [laugh]-style sound tags (see <see cref="TtsSettings.SoundTags"/>).</summary>
        public readonly bool SoundTags;

        public TtsPreset(string id, string displayName, string provider, string baseUrl, string model, string voice, bool needsKey, bool free,
            bool soundTags = false)
        {
            Id = id;
            DisplayName = displayName;
            Provider = provider;
            BaseUrl = baseUrl;
            Model = model;
            Voice = voice;
            NeedsKey = needsKey;
            Free = free;
            SoundTags = soundTags;
        }
    }

    public static class TtsPresets
    {
        public static readonly IReadOnlyList<TtsPreset> All = new[]
        {
            new TtsPreset("kokoro", "Kokoro (local)", TtsProviderIds.OpenAICompatible, "http://localhost:8880/v1", "kokoro", "af_heart", false, true),
            new TtsPreset("chatterbox", "Chatterbox (local)", TtsProviderIds.OpenAICompatible, "http://127.0.0.1:8004/v1", "chatterbox", "voice.wav", false, true, soundTags: true),
            new TtsPreset("openai", "OpenAI", TtsProviderIds.OpenAICompatible, "https://api.openai.com/v1", "gpt-4o-mini-tts", "alloy", true, false),
            new TtsPreset("fish", "Fish Audio", TtsProviderIds.FishAudio, "https://api.fish.audio", "s2.1-pro-free", "", true, false),
            new TtsPreset("custom", "Custom", TtsProviderIds.OpenAICompatible, "", "", "", false, false),
        };

        public static TtsPreset Find(string id)
        {
            foreach (TtsPreset p in All)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
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
