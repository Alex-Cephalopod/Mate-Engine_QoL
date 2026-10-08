using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MateEngineQoL.AI.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngineQoL.AI.OpenAI
{
    /// <summary>
    /// Speech from any OpenAI-compatible <c>/audio/speech</c> endpoint with <c>response_format: pcm</c>
    /// (raw 16-bit mono at 24 kHz): Kokoro-FastAPI on localhost, OpenAI, and others.
    /// </summary>
    public sealed class OpenAICompatibleTtsProvider : ITtsProvider
    {
        public const string ProviderId = "openai-compatible";
        public const int PcmSampleRate = 24000;

        /// <summary>About 10 minutes of audio; a sentence is a few seconds.</summary>
        const long MaxAudioBytes = PcmSampleRate * 2L * 600;
        const int MaxInputChars = 4096;

        readonly HttpClient _http;
        readonly Uri _baseUri;
        readonly string _apiKey;
        readonly TimeSpan _connectTimeout;
        readonly TimeSpan _idleTimeout;

        public string Id => ProviderId;
        public int SampleRate => PcmSampleRate;

        /// <param name="apiKey">Null or empty for local servers that need no key (Kokoro-FastAPI).</param>
        public OpenAICompatibleTtsProvider(HttpClient http, string baseUrl, string apiKey,
            TimeSpan? connectTimeout = null, TimeSpan? idleTimeout = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            _baseUri = EndpointPolicy.ValidateBaseUrl(baseUrl, _apiKey != null);
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
            _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        }

        public async IAsyncEnumerable<float[]> StreamSpeechAsync(string text, VoiceSettings voice,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "audio/speech"))
            {
                Content = new StringContent(BuildBody(text, voice), Encoding.UTF8, "application/json"),
            };
            if (_apiKey != null)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_connectTimeout);
                using (HttpResponseMessage response = await ProviderHttp.SendAsync(_http, message, _baseUri, timeout.Token, ct).ConfigureAwait(false))
                {
                    ProviderHttp.RequireRawAudio(response, _baseUri.Host);
                    Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await foreach (float[] chunk in ProviderHttp.ReadPcm16Async(stream, MaxAudioBytes, timeout, _idleTimeout, timeout.Token).ConfigureAwait(false))
                        yield return chunk;
                }
            }
        }

        internal static string BuildBody(string text, VoiceSettings voice)
        {
            if (text.Length > MaxInputChars) text = text.Substring(0, MaxInputChars);
            var body = new JObject
            {
                ["model"] = voice?.Model ?? "",
                ["input"] = text,
                ["voice"] = voice?.Voice ?? "",
                ["response_format"] = "pcm",
            };
            float speed = voice?.Speed ?? 1f;
            if (Math.Abs(speed - 1f) > 0.001f) body["speed"] = Math.Max(0.5f, Math.Min(2f, speed));
            return body.ToString(Formatting.None);
        }
    }
}
