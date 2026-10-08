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

namespace MateEngineQoL.AI.Fish
{
    /// <summary>
    /// Speech from Fish Audio (<c>POST /v1/tts</c>, https://docs.fish.audio). The voice is a Fish model id
    /// (<c>reference_id</c>) from their library or a clone; the TTS model (s1, s2-pro, ...) goes in the <c>model</c> header.
    /// Asks for raw 16-bit mono PCM at 24 kHz.
    /// </summary>
    public sealed class FishAudioTtsProvider : ITtsProvider
    {
        public const string ProviderId = "fish-audio";
        public const string DefaultBaseUrl = "https://api.fish.audio";
        public const int PcmSampleRate = 24000;

        const long MaxAudioBytes = PcmSampleRate * 2L * 600;
        const int MaxInputChars = 4096;

        readonly HttpClient _http;
        readonly Uri _baseUri;
        readonly string _apiKey;
        readonly TimeSpan _connectTimeout;
        readonly TimeSpan _idleTimeout;

        public string Id => ProviderId;
        public int SampleRate => PcmSampleRate;

        public FishAudioTtsProvider(HttpClient http, string baseUrl, string apiKey,
            TimeSpan? connectTimeout = null, TimeSpan? idleTimeout = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ProviderException("Fish Audio needs an API key.");
            _apiKey = apiKey.Trim();
            _baseUri = EndpointPolicy.ValidateBaseUrl(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl, true);
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
            _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        }

        public async IAsyncEnumerable<float[]> StreamSpeechAsync(string text, VoiceSettings voice,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "v1/tts"))
            {
                Content = new StringContent(BuildBody(text, voice), Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            string model = ModelHeader(voice?.Model);
            if (model != null) message.Headers.TryAddWithoutValidation("model", model);

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

        /// <summary>The model header value, or null to use Fish's default. Only plain ids (letters, digits, . - _) are sent.</summary>
        internal static string ModelHeader(string model)
        {
            model = (model ?? "").Trim();
            if (model.Length == 0 || model.Length > 40) return null;
            foreach (char c in model)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return null;
            return model;
        }

        internal static string BuildBody(string text, VoiceSettings voice)
        {
            if (text.Length > MaxInputChars) text = text.Substring(0, MaxInputChars);
            var body = new JObject
            {
                ["text"] = text,
                ["format"] = "pcm",
                ["sample_rate"] = PcmSampleRate,
                ["latency"] = "balanced",
                ["normalize"] = true,
            };
            string voiceId = (voice?.Voice ?? "").Trim();
            if (voiceId.Length > 0) body["reference_id"] = voiceId;
            float speed = voice?.Speed ?? 1f;
            if (Math.Abs(speed - 1f) > 0.001f)
                body["prosody"] = new JObject { ["speed"] = Math.Max(0.5f, Math.Min(2f, speed)), ["volume"] = 0 };
            return body.ToString(Formatting.None);
        }
    }
}
