using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MateEngineQoL.AI;
using MateEngineQoL.AI.Http;
using MateEngineQoL.AI.OpenAI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class ChatProviderTests
    {
        sealed class FakeHandler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public HttpRequestMessage LastRequest;
            public string LastBody;

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                LastRequest = request;
                LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                return _respond(request);
            }
        }

        static HttpResponseMessage Sse(string body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

        static string Chunk(string text) =>
            "data: " + new JObject { ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["content"] = text } }) }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";

        static ChatRequest Request() => new ChatRequest
        {
            Model = "llama3",
            Messages = { new ChatMessage(ChatMessage.System, "You are Zome."), new ChatMessage(ChatMessage.User, "hi") },
        };

        static List<string> Collect(IAsyncEnumerable<string> stream) => stream.ToListAsync().GetAwaiter().GetResult();

        [Test]
        public void StreamsDeltasUntilDone()
        {
            var handler = new FakeHandler(_ => Sse(": keep-alive\n\n" + Chunk("Hel") + Chunk("lo") + "data: {\"choices\":[]}\n\n" + Chunk("!") + "data: [DONE]\n\n" + Chunk("ignored")));
            var provider = new OpenAICompatibleChatProvider(new HttpClient(handler), "http://localhost:11434/v1", null);

            Assert.AreEqual(new[] { "Hel", "lo", "!" }, Collect(provider.StreamReplyAsync(Request())));
            Assert.AreEqual("http://localhost:11434/v1/chat/completions", handler.LastRequest.RequestUri.ToString());
            Assert.IsNull(handler.LastRequest.Headers.Authorization, "no key configured, no header expected");

            JObject body = JObject.Parse(handler.LastBody);
            Assert.AreEqual("llama3", (string)body["model"]);
            Assert.AreEqual(true, (bool)body["stream"]);
            Assert.AreEqual("system", (string)body["messages"][0]["role"]);
            Assert.AreEqual("hi", (string)body["messages"][1]["content"]);
        }

        [Test]
        public void SendsBearerKeyOnlyInHeader()
        {
            var handler = new FakeHandler(_ => Sse(Chunk("ok") + "data: [DONE]\n\n"));
            var provider = new OpenAICompatibleChatProvider(new HttpClient(handler), "https://openrouter.ai/api/v1", "sk-test-123");

            Collect(provider.StreamReplyAsync(Request()));
            Assert.AreEqual("Bearer", handler.LastRequest.Headers.Authorization.Scheme);
            Assert.AreEqual("sk-test-123", handler.LastRequest.Headers.Authorization.Parameter);
            StringAssert.DoesNotContain("sk-test-123", handler.LastRequest.RequestUri.ToString());
            StringAssert.DoesNotContain("sk-test-123", handler.LastBody);
        }

        [Test]
        public void HttpErrorBecomesFriendlyMessageWithoutKey()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                ReasonPhrase = "Unauthorized",
                Content = new StringContent("{\"error\":{\"message\":\"Invalid API key provided\"}}"),
            });
            var provider = new OpenAICompatibleChatProvider(new HttpClient(handler), "https://api.openai.com/v1", "sk-secret-999");

            var e = Assert.Throws<ProviderException>(() => Collect(provider.StreamReplyAsync(Request())));
            Assert.AreEqual(401, e.StatusCode);
            StringAssert.Contains("Invalid API key provided", e.Message);
            StringAssert.Contains("Check the API key", e.Message);
            StringAssert.DoesNotContain("sk-secret-999", e.Message);
        }

        [Test]
        public void ErrorInsideStreamIsRaised()
        {
            var handler = new FakeHandler(_ => Sse(Chunk("a") + "data: {\"error\":{\"message\":\"rate limited\"}}\n\n"));
            var provider = new OpenAICompatibleChatProvider(new HttpClient(handler), "http://127.0.0.1:1234/v1", null);

            var e = Assert.Throws<ProviderException>(() => Collect(provider.StreamReplyAsync(Request())));
            StringAssert.Contains("rate limited", e.Message);
        }

        [Test]
        public void CancellationStopsTheStream()
        {
            var handler = new FakeHandler(_ => Sse(Chunk("a") + Chunk("b")));
            var provider = new OpenAICompatibleChatProvider(new HttpClient(handler), "http://localhost:11434/v1", null);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.That(() => Collect(provider.StreamReplyAsync(Request(), cts.Token)), Throws.InstanceOf<OperationCanceledException>());
            }
        }

        [TestCase("http://192.168.1.20:11434/v1", true, false)]  // key over plain http to LAN: refused
        [TestCase("http://192.168.1.20:11434/v1", false, true)]  // no key over LAN http: allowed (e.g. Ollama on another PC)
        [TestCase("http://localhost:11434/v1", true, true)]
        [TestCase("http://127.0.0.1:1234/v1", true, true)]
        [TestCase("https://openrouter.ai/api/v1", true, true)]
        [TestCase("https://user:pass@example.com/v1", false, false)]
        [TestCase("ftp://example.com/v1", false, false)]
        [TestCase("not a url", false, false)]
        public void EndpointPolicyRules(string url, bool sendsSecret, bool allowed)
        {
            if (allowed) Assert.DoesNotThrow(() => EndpointPolicy.ValidateBaseUrl(url, sendsSecret));
            else Assert.Throws<ProviderException>(() => EndpointPolicy.ValidateBaseUrl(url, sendsSecret));
        }

        [Test]
        public void EndpointPolicyNormalizesTrailingSlashAndDropsQuery()
        {
            Assert.AreEqual("https://openrouter.ai/api/v1/", EndpointPolicy.ValidateBaseUrl("https://openrouter.ai/api/v1?x=1", true).ToString());
            Assert.AreEqual("https://openrouter.ai/api/v1/", EndpointPolicy.ValidateBaseUrl(" https://openrouter.ai/api/v1/ ", true).ToString());
        }

        static List<string> ReadSse(string text, int maxLine = 1000) =>
            SseReader.ReadDataAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), maxLine).ToListAsync().GetAwaiter().GetResult();

        [Test]
        public void SseReaderHandlesCrlfMultilineAndMissingTrailingBlankLine()
        {
            Assert.AreEqual(new[] { "a", "b\nc", "d" }, ReadSse("data: a\r\n\r\nevent: x\ndata: b\ndata:c\n\n:comment\n\ndata: d"));
        }

        [Test]
        public void SseReaderRejectsOversizedLines()
        {
            Assert.Throws<ProviderException>(() => ReadSse("data: " + new string('x', 50) + "\n\n", 20));
        }
    }
}
