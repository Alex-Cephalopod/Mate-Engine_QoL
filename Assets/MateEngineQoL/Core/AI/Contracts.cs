using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MateEngineQoL.AI
{
    public sealed class ChatMessage
    {
        public const string System = "system";
        public const string User = "user";
        public const string Assistant = "assistant";

        public string Role;
        public string Content;

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    public sealed class ChatRequest
    {
        public List<ChatMessage> Messages = new List<ChatMessage>();
        /// <summary>Model id; empty means the provider's default.</summary>
        public string Model = "";
        public float? Temperature;
        public int? MaxTokens;
    }

    public sealed class VoiceSettings
    {
        public string Model = "";
        public string Voice = "";
        public float Speed = 1f;
    }

    public interface IChatProvider
    {
        string Id { get; }

        /// <summary>Yields reply text deltas as they arrive; non-streaming backends yield once.</summary>
        IAsyncEnumerable<string> StreamReplyAsync(ChatRequest request, CancellationToken ct);
    }

    public interface ITtsProvider
    {
        string Id { get; }

        /// <summary>Sample rate of the mono PCM chunks this provider yields.</summary>
        int SampleRate { get; }

        IAsyncEnumerable<float[]> StreamSpeechAsync(string text, VoiceSettings voice, CancellationToken ct);
    }

    public interface ISttProvider
    {
        string Id { get; }

        Task<string> TranscribeAsync(float[] pcm, int sampleRate, CancellationToken ct);
    }

    /// <summary>
    /// A provider failure with a message that is safe to show the user: it never contains API keys or request bodies.
    /// </summary>
    public sealed class ProviderException : Exception
    {
        public int? StatusCode { get; }

        public ProviderException(string message, int? statusCode = null, Exception inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }
}
