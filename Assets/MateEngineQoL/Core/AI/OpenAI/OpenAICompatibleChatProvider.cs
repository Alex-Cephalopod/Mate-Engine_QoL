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
    /// Streams chat replies from any OpenAI-compatible <c>/chat/completions</c> endpoint:
    /// Ollama, LM Studio, OpenRouter, OpenAI, DeepSeek and others, chosen by base URL.
    /// </summary>
    public sealed class OpenAICompatibleChatProvider : IChatProvider
    {
        public const string ProviderId = "openai-compatible";

        const int MaxLineChars = 1_000_000;
        const int MaxReplyChars = 200_000;
        const int MaxErrorBodyChars = 4096;

        readonly HttpClient _http;
        readonly Uri _baseUri;
        readonly string _apiKey;
        readonly TimeSpan _connectTimeout;
        readonly TimeSpan _idleTimeout;

        public string Id => ProviderId;

        /// <param name="http">Shared client; its own Timeout should be infinite, this class applies its own timeouts.</param>
        /// <param name="apiKey">Null or empty for servers that need no key (Ollama, LM Studio).</param>
        public OpenAICompatibleChatProvider(HttpClient http, string baseUrl, string apiKey,
            TimeSpan? connectTimeout = null, TimeSpan? idleTimeout = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            _baseUri = EndpointPolicy.ValidateBaseUrl(baseUrl, _apiKey != null);
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
            _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(60);
        }

        public async IAsyncEnumerable<string> StreamReplyAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_connectTimeout);
                using (HttpResponseMessage response = await SendAsync(request, timeout.Token, ct).ConfigureAwait(false))
                {
                    timeout.CancelAfter(_idleTimeout);
                    Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    int total = 0;

                    await foreach (string data in SseReader.ReadDataAsync(stream, MaxLineChars, timeout.Token).ConfigureAwait(false))
                    {
                        timeout.CancelAfter(_idleTimeout);
                        if (data == "[DONE]") yield break;

                        string delta = ParseDelta(data);
                        if (string.IsNullOrEmpty(delta)) continue;

                        total += delta.Length;
                        if (total > MaxReplyChars)
                            throw new ProviderException("The reply exceeded " + MaxReplyChars + " characters; stopped.");
                        yield return delta;
                    }
                }
            }
        }

        async Task<HttpResponseMessage> SendAsync(ChatRequest request, CancellationToken token, CancellationToken userToken)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "chat/completions"))
            {
                Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (_apiKey != null)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
            {
                throw new ProviderException("Timed out connecting to " + _baseUri.Host + ".");
            }
            catch (HttpRequestException e)
            {
                throw new ProviderException("Could not reach " + _baseUri.Host + ": " + OneLine(e.InnerException?.Message ?? e.Message), null, e);
            }
            finally
            {
                message.Dispose();
            }

            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                string detail = await ReadErrorAsync(response).ConfigureAwait(false);
                int code = (int)response.StatusCode;
                string hint = code == 401 || code == 403 ? " Check the API key." : code == 404 ? " Check the base URL and model name." : "";
                throw new ProviderException(_baseUri.Host + " returned " + code + " " + response.ReasonPhrase + ": " + detail + hint, code);
            }
        }

        internal static string BuildBody(ChatRequest request)
        {
            var messages = new JArray();
            foreach (ChatMessage m in request.Messages)
                messages.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content ?? "" });

            var body = new JObject
            {
                ["model"] = request.Model ?? "",
                ["messages"] = messages,
                ["stream"] = true,
            };
            if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
            if (request.MaxTokens.HasValue) body["max_tokens"] = request.MaxTokens.Value;
            return body.ToString(Formatting.None);
        }

        internal static string ParseDelta(string data)
        {
            JObject obj;
            try { obj = JObject.Parse(data); }
            catch (JsonReaderException) { return null; }

            if (obj["error"] is JToken error && error.Type != JTokenType.Null)
                throw new ProviderException("Provider error: " + OneLine(ErrorText(error) ?? error.ToString(Formatting.None)));

            if (!(obj["choices"] is JArray choices) || choices.Count == 0) return null; // e.g. usage-only chunks
            JToken content = choices[0]["delta"]?["content"];
            return content != null && content.Type == JTokenType.String ? (string)content : null;
        }

        static async Task<string> ReadErrorAsync(HttpResponseMessage response)
        {
            string body;
            try
            {
                using (Stream s = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(s, Encoding.UTF8))
                {
                    var buffer = new char[MaxErrorBodyChars];
                    int n = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    body = new string(buffer, 0, n);
                }
            }
            catch (Exception)
            {
                return "(no details)";
            }

            try
            {
                if (JToken.Parse(body) is JObject json)
                {
                    string message = ErrorText(json["error"]) ?? ErrorText(json["message"]);
                    if (!string.IsNullOrEmpty(message)) return OneLine(message);
                }
            }
            catch (JsonException) { }
            return string.IsNullOrWhiteSpace(body) ? "(no details)" : OneLine(body);
        }

        /// <summary>Error text from either <c>"error": "text"</c> or <c>"error": { "message": "text" }</c>.</summary>
        static string ErrorText(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String) return (string)token;
            if (token is JObject obj && obj["message"] is JToken m && m.Type == JTokenType.String) return (string)m;
            return null;
        }

        static string OneLine(string s)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }
    }
}
