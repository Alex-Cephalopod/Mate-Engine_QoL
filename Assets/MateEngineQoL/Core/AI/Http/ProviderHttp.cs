using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MateEngineQoL.Voice;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngineQoL.AI.Http
{
    /// <summary>
    /// Request and response handling shared by the HTTP providers. Error text is trimmed to one line and never
    /// includes request headers or bodies, so it is safe to show (see docs/SECURITY.md).
    /// </summary>
    public static class ProviderHttp
    {
        public const int MaxErrorBodyChars = 4096;

        /// <summary>Sends with a connect timeout and turns network failures and HTTP errors into <see cref="ProviderException"/>.</summary>
        /// <param name="token">Cancelled by the user or by the connect timeout.</param>
        /// <param name="userToken">The user's token alone, to tell the two apart.</param>
        public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage message, Uri baseUri,
            CancellationToken token, CancellationToken userToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
            {
                throw new ProviderException("Timed out connecting to " + baseUri.Host + ".");
            }
            catch (HttpRequestException e)
            {
                throw new ProviderException("Could not reach " + baseUri.Host + ": " + OneLine(e.InnerException?.Message ?? e.Message), null, e);
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
                string hint = code == 401 || code == 403 ? " Check the API key."
                    : code == 402 ? " The account has no API credit left (a free model, if the provider has one, avoids this)."
                    : code == 404 ? " Check the base URL and model name."
                    : "";
                throw new ProviderException(baseUri.Host + " returned " + code + " " + response.ReasonPhrase + ": " + detail + hint, code);
            }
        }

        /// <summary>
        /// Reads a raw 16-bit little-endian mono PCM body as float chunks. Stops with an error past
        /// <paramref name="maxBytes"/>; <paramref name="idle"/> is pushed back by <paramref name="idleTimeout"/> on every read.
        /// </summary>
        public static async IAsyncEnumerable<float[]> ReadPcm16Async(Stream stream, long maxBytes,
            CancellationTokenSource idle, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var decoder = new Pcm16Decoder();
            var buffer = new byte[9600]; // 200 ms at 24 kHz
            long total = 0;
            bool first = true;
            while (true)
            {
                idle.CancelAfter(idleTimeout);
                int n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (n <= 0) yield break;
                if (first && n >= 4 && buffer[0] == 'R' && buffer[1] == 'I' && buffer[2] == 'F' && buffer[3] == 'F')
                    throw new ProviderException("The server sent WAV instead of raw PCM; it may not support response_format pcm.");
                first = false;
                total += n;
                if (total > maxBytes)
                    throw new ProviderException("The audio exceeded " + maxBytes / 1_000_000 + " MB; stopped.");
                float[] samples = decoder.Decode(buffer, n);
                if (samples.Length > 0) yield return samples;
            }
        }

        /// <summary>Throws if the response is clearly not raw PCM (an encoded audio format, JSON or HTML).</summary>
        public static void RequireRawAudio(HttpResponseMessage response, string host)
        {
            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type == null) return;
            if (type.Contains("json") || type.StartsWith("text/") || type.Contains("mpeg") || type.Contains("mp3")
                || type.Contains("ogg") || type.Contains("opus") || type.Contains("aac") || type.Contains("flac")
                || type.Contains("wav") || type.Contains("webm"))
                throw new ProviderException(host + " sent " + type + " instead of raw PCM audio.");
        }

        public static async Task<string> ReadErrorAsync(HttpResponseMessage response)
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
                    string message = ErrorText(json["error"]) ?? ErrorText(json["message"]) ?? ErrorText(json["detail"]);
                    if (!string.IsNullOrEmpty(message)) return OneLine(message);
                }
            }
            catch (JsonException) { }
            return string.IsNullOrWhiteSpace(body) ? "(no details)" : OneLine(body);
        }

        /// <summary>Error text from either <c>"error": "text"</c> or <c>"error": { "message": "text" }</c>.</summary>
        public static string ErrorText(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String) return (string)token;
            if (token is JObject obj && obj["message"] is JToken m && m.Type == JTokenType.String) return (string)m;
            return null;
        }

        public static string OneLine(string s)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }
    }
}
