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
        /// Reads a speech body as mono float chunks at <paramref name="outputRate"/>. Accepts a 16-bit PCM WAV stream
        /// (any rate or channel count; resampled and mixed down) or raw 16-bit little-endian mono PCM, taken to be at
        /// <paramref name="outputRate"/> already. Stops with an error past <paramref name="maxBytes"/>;
        /// <paramref name="idle"/> is pushed back by <paramref name="idleTimeout"/> on every read.
        /// </summary>
        public static async IAsyncEnumerable<float[]> ReadSpeechAsync(Stream stream, int outputRate, long maxBytes,
            CancellationTokenSource idle, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var buffer = new byte[9600]; // 200 ms of 16-bit audio at 24 kHz
            var start = new MemoryStream();
            long total = 0;
            Pcm16Decoder raw = null;
            WavStreamDecoder wav = null;
            Resampler resampler = null;
            while (true)
            {
                idle.CancelAfter(idleTimeout);
                int n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                total += Math.Max(n, 0);
                if (total > maxBytes)
                    throw new ProviderException("The audio exceeded " + maxBytes / 1_000_000 + " MB; stopped.");

                byte[] bytes = buffer;
                int count = n;
                if (raw == null && wav == null)
                {
                    // Decide the format from the first 4 bytes ("RIFF" = WAV), however the stream splits them.
                    if (n > 0) start.Write(buffer, 0, n);
                    if (start.Length < 4 && n > 0) continue;
                    bytes = start.ToArray();
                    count = bytes.Length;
                    bool isWav = count >= 4 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F';
                    if (isWav) wav = new WavStreamDecoder();
                    else raw = new Pcm16Decoder();
                }
                if (count <= 0) yield break;

                float[] samples;
                if (wav != null)
                {
                    samples = wav.Decode(bytes, count);
                    if (wav.HeaderDone && resampler == null) resampler = new Resampler(wav.SampleRate, outputRate);
                    if (resampler != null && samples.Length > 0) samples = resampler.Process(samples);
                }
                else
                {
                    samples = raw.Decode(bytes, count);
                }
                if (samples.Length > 0) yield return samples;
                if (n <= 0) yield break;
            }
        }

        /// <summary>Throws if the response is clearly not uncompressed audio (an encoded format, JSON or HTML).</summary>
        public static void RequireUncompressedAudio(HttpResponseMessage response, string host)
        {
            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type == null) return;
            if (type.Contains("json") || type.StartsWith("text/") || type.Contains("mpeg") || type.Contains("mp3")
                || type.Contains("ogg") || type.Contains("opus") || type.Contains("aac") || type.Contains("flac")
                || type.Contains("webm"))
                throw new ProviderException(host + " sent " + type + " instead of WAV or raw PCM audio.");
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
