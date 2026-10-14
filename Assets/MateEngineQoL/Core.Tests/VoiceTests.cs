using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MateEngineQoL.AI;
using MateEngineQoL.AI.Fish;
using MateEngineQoL.AI.OpenAI;
using MateEngineQoL.Settings;
using MateEngineQoL.Voice;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class SentenceSplitterTests
    {
        static List<string> SplitAll(SentenceSplitter s, params string[] deltas)
        {
            var all = new List<string>();
            foreach (string d in deltas) all.AddRange(s.Push(d));
            string rest = s.Flush();
            if (rest != null) all.Add(rest);
            return all;
        }

        [Test]
        public void SplitsOnTerminatorsAcrossDeltas()
        {
            var s = new SentenceSplitter();
            Assert.AreEqual(new string[0], s.Push("Hello there"));
            Assert.AreEqual(new[] { "Hello there!" }, s.Push("! How are"));
            Assert.AreEqual(new[] { "How are you?" }, s.Push(" you? I'm"));
            Assert.AreEqual("I'm fine.", Flush(s, " fine."));
        }

        static string Flush(SentenceSplitter s, string last)
        {
            Assert.AreEqual(new string[0], s.Push(last), "a terminator at the end waits for more text");
            return s.Flush();
        }

        [Test]
        public void SplitsOnNewlinesAndKeepsClosingQuotes()
        {
            Assert.AreEqual(new[] { "First line", "\"Really?!\"", "Ok." },
                SplitAll(new SentenceSplitter(), "First line\n\"Really?!\" Ok."));
        }

        [Test]
        public void DoesNotSplitDecimalsAbbreviationsOrListNumbers()
        {
            Assert.AreEqual(new[] { "It costs 3.50 today, says Dr. Smith.", "1. Eggs", "Done." },
                SplitAll(new SentenceSplitter(), "It costs 3.50 today, says Dr. Smith. 1. Eggs\nDone."));
        }

        [Test]
        public void LongTextWithoutEndsIsCutAtASpace()
        {
            var s = new SentenceSplitter(40);
            string text = string.Join(" ", Enumerable.Repeat("word", 30));
            List<string> parts = SplitAll(s, text);
            Assert.Greater(parts.Count, 2);
            foreach (string p in parts)
            {
                Assert.LessOrEqual(p.Length, 41);
                StringAssert.DoesNotStartWith(" ", p);
                StringAssert.DoesNotContain("wo rd", string.Join(" ", parts));
            }
            Assert.AreEqual(text, string.Join(" ", parts));
        }

        [Test]
        public void SkipsEmptyPieces()
        {
            Assert.AreEqual(new[] { "Hi." }, SplitAll(new SentenceSplitter(), "\n\n  Hi. \n\n"));
        }
    }

    public class SpeechFilterTests
    {
        [Test]
        public void DropsActionsAcrossSentences()
        {
            var f = new SpeechFilter(skipActions: true);
            Assert.AreEqual("Hello!", f.Filter("*waves* Hello!"));
            Assert.IsNull(f.Filter("*walks over"));
            Assert.AreEqual("Hi again.", f.Filter("and sits* Hi again."));
        }

        [Test]
        public void KeepsActionTextWhenAsked()
        {
            Assert.AreEqual("waves Hello!", new SpeechFilter(skipActions: false).Filter("*waves* Hello!"));
        }

        [Test]
        public void StripsTagsUrlsMarkdownAndEmoji()
        {
            var f = new SpeechFilter(true);
            Assert.AreEqual("See for more.", f.Filter("See https://example.com/x?y=1 for more."));
            Assert.AreEqual("Yay!", f.Filter("[emotion:happy] <b>Yay!</b> \U0001F600"));
            Assert.AreEqual("Title text", f.Filter("## Title `text`"));
            Assert.IsNull(f.Filter("\U0001F600 ... !!"));
        }

        [Test]
        public void SoundActionsBecomeTagsWhenAsked()
        {
            var f = new SpeechFilter(skipActions: true, soundTags: true);
            Assert.AreEqual("[chuckle] Oh, you!", f.Filter("*chuckles softly* Oh, you!"));
            Assert.AreEqual("Fine. [sigh]", f.Filter("Fine. *sighs*"));
            Assert.AreEqual("Hey.", f.Filter("*winks* Hey."), "silent actions stay silent");
            Assert.IsNull(f.Filter("*winks*"));
            Assert.AreEqual("[sigh]", f.Filter("*sighs*"), "a lone sound is still sent");
        }

        [Test]
        public void SoundActionAcrossSentencesPlaysWhereItCloses()
        {
            var f = new SpeechFilter(true, true);
            Assert.IsNull(f.Filter("*leans back."));
            Assert.AreEqual("[laugh] Really?", f.Filter("Then giggles* Really?"));
        }

        [Test]
        public void SoundActionWordsMapToTags()
        {
            Assert.AreEqual("[laugh]", SpeechFilter.SoundTagFor("laughs nervously"));
            Assert.AreEqual("[laugh]", SpeechFilter.SoundTagFor("Giggling"));
            Assert.AreEqual("[clear throat]", SpeechFilter.SoundTagFor("clears her throat"));
            Assert.AreEqual("[cough]", SpeechFilter.SoundTagFor("coughs"));
            Assert.AreEqual("[gasp]", SpeechFilter.SoundTagFor("gasps"));
            Assert.AreEqual("[sniff]", SpeechFilter.SoundTagFor("sniffles"));
            Assert.AreEqual("[groan]", SpeechFilter.SoundTagFor("groans"));
            Assert.AreEqual("[shush]", SpeechFilter.SoundTagFor("shhh"));
            Assert.AreEqual("[chuckle]", SpeechFilter.SoundTagFor("smiles and chuckles, then laughs"), "earliest sound wins");
            Assert.IsNull(SpeechFilter.SoundTagFor("looks at the sights"));
            Assert.IsNull(SpeechFilter.SoundTagFor("slaughters a dragon"));
            Assert.IsNull(SpeechFilter.SoundTagFor("winks"));
        }

        [Test]
        public void KeepsKnownTagsOnlyWhenAsked()
        {
            Assert.AreEqual("[laugh] Yay!", new SpeechFilter(true, true).Filter("[Laugh] [emotion:happy] Yay!"));
            Assert.AreEqual("Ahem. [clear throat] Hi.", new SpeechFilter(true, true).Filter("Ahem. [clear  throat] Hi."));
            Assert.AreEqual("Yay!", new SpeechFilter(true).Filter("[laugh] Yay!"));
            Assert.AreEqual("Hi.", new SpeechFilter(true).Filter("*chuckles* Hi."), "off by default");
        }
    }

    public class TtsSoundTagSettingsTests
    {
        [Test]
        public void FollowsPresetUnlessSet()
        {
            var t = new MateEngineQoL.Settings.TtsSettings { Preset = "chatterbox" };
            Assert.IsTrue(t.UsesSoundTags());
            t.Preset = "kokoro";
            Assert.IsFalse(t.UsesSoundTags());
            t.Preset = "custom";
            t.SoundTags = true;
            Assert.IsTrue(t.UsesSoundTags());
            t.Preset = "chatterbox";
            t.SoundTags = false;
            Assert.IsFalse(t.UsesSoundTags());
        }
    }

    public class PcmTests
    {
        [Test]
        public void RingBufferWrapsAndReportsPartialWrites()
        {
            var b = new PcmRingBuffer(5);
            Assert.AreEqual(3, b.Write(new[] { 1f, 2f, 3f }, 0, 3));
            var out2 = new float[2];
            Assert.AreEqual(2, b.Read(out2, 0, 2));
            Assert.AreEqual(new[] { 1f, 2f }, out2);
            Assert.AreEqual(4, b.Write(new[] { 4f, 5f, 6f, 7f, 8f }, 0, 5), "only 4 free");
            var out5 = new float[6];
            Assert.AreEqual(5, b.Read(out5, 0, 6));
            Assert.AreEqual(new[] { 3f, 4f, 5f, 6f, 7f, 0f }, out5);
            Assert.AreEqual(0, b.Count);
        }

        [Test]
        public void RingBufferClear()
        {
            var b = new PcmRingBuffer(4);
            b.Write(new[] { 1f, 2f }, 0, 2);
            b.Clear();
            Assert.AreEqual(0, b.Count);
            Assert.AreEqual(4, b.FreeSpace);
        }

        [Test]
        public void RingBufferIsSafeAcrossThreads()
        {
            var b = new PcmRingBuffer(256);
            const int total = 200_000;
            var writer = Task.Run(() =>
            {
                var chunk = new float[37];
                int next = 0;
                while (next < total)
                {
                    int n = Math.Min(chunk.Length, total - next);
                    for (int i = 0; i < n; i++) chunk[i] = next + i;
                    int written = b.Write(chunk, 0, n);
                    next += written;
                    if (written == 0) Thread.Yield();
                }
            });
            var read = new float[53];
            int expected = 0;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (expected < total && DateTime.UtcNow < deadline)
            {
                int n = b.Read(read, 0, read.Length);
                for (int i = 0; i < n; i++) Assert.AreEqual(expected++, read[i]);
                if (n == 0) Thread.Yield();
            }
            writer.Wait();
            Assert.AreEqual(total, expected);
        }

        [Test]
        public void ResamplerDoublesAndHalvesLength()
        {
            var input = Enumerable.Range(0, 2400).Select(i => (float)Math.Sin(i * 0.01)).ToArray();
            var up = new Resampler(24000, 48000);
            int upCount = 0;
            for (int i = 0; i < input.Length; i += 100) upCount += up.Process(input.Skip(i).Take(100).ToArray()).Length;
            Assert.That(upCount, Is.InRange(4797, 4800));

            var down = new Resampler(48000, 24000);
            Assert.That(down.Process(input).Length, Is.InRange(1199, 1200));
        }

        [Test]
        public void ResamplerIsContinuousAcrossChunks()
        {
            // A ramp stays a ramp: no repeated or skipped values at chunk edges.
            var r = new Resampler(22050, 48000);
            var output = new List<float>();
            for (int start = 0; start < 2205; start += 77)
            {
                int n = Math.Min(77, 2205 - start);
                output.AddRange(r.Process(Enumerable.Range(start, n).Select(i => (float)i).ToArray()));
            }
            double step = 22050.0 / 48000;
            for (int i = 0; i < output.Count; i++) Assert.AreEqual(i * step, output[i], 1e-3);
        }

        [Test]
        public void SameRateIsACopy()
        {
            var r = new Resampler(24000, 24000);
            Assert.AreEqual(new[] { 0.5f, -0.5f }, r.Process(new[] { 0.5f, -0.5f, 9f }, 2));
        }

        [Test]
        public void Pcm16DecodesAcrossOddChunks()
        {
            short[] values = { 0, 16384, -16384, short.MaxValue, short.MinValue };
            byte[] bytes = values.SelectMany(v => BitConverter.GetBytes(v)).ToArray();
            var d = new Pcm16Decoder();
            var samples = new List<float>();
            samples.AddRange(d.Decode(bytes.Take(3).ToArray(), 3));
            samples.AddRange(d.Decode(bytes.Skip(3).Take(1).ToArray(), 1));
            samples.AddRange(d.Decode(bytes.Skip(4).ToArray(), 6));
            Assert.AreEqual(new[] { 0f, 0.5f, -0.5f, 32767f / 32768f, -1f }, samples);
        }
    }

    public class WavTests
    {
        /// <summary>A 16-bit PCM WAV with a LIST chunk (odd size, padded) before the data, like many encoders write.</summary>
        internal static byte[] Wav(int rate, int channels, short[] interleaved, int format = 1, int bits = 16, bool streamed = false)
        {
            var ms = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(streamed ? -1 : 0); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)format); w.Write((short)channels);
            w.Write(rate); w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
            w.Write(System.Text.Encoding.ASCII.GetBytes("LIST")); w.Write(3); w.Write(new byte[] { 1, 2, 3, 0 });
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(streamed ? -1 : interleaved.Length * 2);
            foreach (short v in interleaved) w.Write(v);
            return ms.ToArray();
        }

        static float[] DecodeInPieces(byte[] bytes, int pieceSize, WavStreamDecoder d)
        {
            var all = new List<float>();
            for (int i = 0; i < bytes.Length; i += pieceSize)
            {
                byte[] piece = bytes.Skip(i).Take(pieceSize).ToArray();
                all.AddRange(d.Decode(piece, piece.Length));
            }
            return all.ToArray();
        }

        [Test]
        public void DecodesHeaderAndSamplesSplitAnywhere()
        {
            byte[] wav = Wav(22050, 1, new short[] { 0, 16384, -16384, short.MinValue }, streamed: true);
            foreach (int piece in new[] { 1, 3, 7, 44, 4096 })
            {
                var d = new WavStreamDecoder();
                Assert.AreEqual(new[] { 0f, 0.5f, -0.5f, -1f }, DecodeInPieces(wav, piece, d), "piece size " + piece);
                Assert.AreEqual(22050, d.SampleRate);
            }
        }

        [Test]
        public void MixesChannelsToMono()
        {
            var d = new WavStreamDecoder();
            byte[] wav = Wav(24000, 2, new short[] { 16384, 0, -16384, -16384 });
            Assert.AreEqual(new[] { 0.25f, -0.5f }, DecodeInPieces(wav, 5, d));
        }

        [Test]
        public void RefusesFloatAnd8BitAndNonWav()
        {
            Assert.Throws<ProviderException>(() => new WavStreamDecoder().Decode(Wav(24000, 1, new short[2], format: 3, bits: 32), 60));
            Assert.Throws<ProviderException>(() => new WavStreamDecoder().Decode(Wav(24000, 1, new short[2], bits: 8), 60));
            byte[] notWav = System.Text.Encoding.ASCII.GetBytes("RIFFxxxxAVI LIST");
            Assert.Throws<ProviderException>(() => new WavStreamDecoder().Decode(notWav, notWav.Length));
        }

        [Test]
        public void RefusesEndlessHeaders()
        {
            var d = new WavStreamDecoder();
            byte[] head = Wav(24000, 1, new short[0]).Take(36).ToArray(); // RIFF, WAVE, fmt
            d.Decode(head, head.Length);
            byte[] junk = new byte[70 * 1024];
            byte[] chunk = System.Text.Encoding.ASCII.GetBytes("junk").Concat(BitConverter.GetBytes(int.MaxValue)).Concat(junk).ToArray();
            Assert.Throws<ProviderException>(() => d.Decode(chunk, chunk.Length));
        }
    }

    public class TtsProviderTests
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

        static HttpResponseMessage Pcm(byte[] bytes, string type = "audio/pcm")
        {
            var content = new ByteArrayContent(bytes);
            if (type != null) content.Headers.ContentType = new MediaTypeHeaderValue(type);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        static float[] Collect(IAsyncEnumerable<float[]> stream) =>
            stream.ToListAsync().GetAwaiter().GetResult().SelectMany(c => c).ToArray();

        static readonly VoiceSettings Voice = new VoiceSettings { Model = "kokoro", Voice = "af_heart", Speed = 1.25f };

        [Test]
        public void OpenAICompatiblePostsSpeechRequestAndDecodesPcm()
        {
            var handler = new FakeHandler(_ => Pcm(new byte[] { 0x00, 0x40, 0x00, 0xC0 }));
            var tts = new OpenAICompatibleTtsProvider(new HttpClient(handler), "http://localhost:8880/v1", null);

            Assert.AreEqual(new[] { 0.5f, -0.5f }, Collect(tts.StreamSpeechAsync("Hi there.", Voice)));
            Assert.AreEqual(24000, tts.SampleRate);
            Assert.AreEqual("http://127.0.0.1:8880/v1/audio/speech", handler.LastRequest.RequestUri.ToString());
            Assert.IsNull(handler.LastRequest.Headers.Authorization);
            JObject body = JObject.Parse(handler.LastBody);
            Assert.AreEqual("kokoro", (string)body["model"]);
            Assert.AreEqual("Hi there.", (string)body["input"]);
            Assert.AreEqual("af_heart", (string)body["voice"]);
            Assert.AreEqual("wav", (string)body["response_format"]);
            Assert.AreEqual(1.25f, (float)body["speed"]);
        }

        [Test]
        public void OpenAICompatibleSendsKeyAndOmitsDefaultSpeed()
        {
            var handler = new FakeHandler(_ => Pcm(new byte[2], null));
            var tts = new OpenAICompatibleTtsProvider(new HttpClient(handler), "https://api.openai.com/v1", "sk-test");
            Collect(tts.StreamSpeechAsync("Hi.", new VoiceSettings { Model = "gpt-4o-mini-tts", Voice = "alloy" }));
            Assert.AreEqual("Bearer sk-test", handler.LastRequest.Headers.Authorization.ToString());
            Assert.IsNull(JObject.Parse(handler.LastBody)["speed"]);
        }

        [Test]
        public void RejectsEncodedAudio()
        {
            var mp3 = new OpenAICompatibleTtsProvider(new HttpClient(new FakeHandler(_ => Pcm(new byte[8], "audio/mpeg"))), "http://localhost:8880/v1", null);
            var e = Assert.Throws<ProviderException>(() => Collect(mp3.StreamSpeechAsync("Hi.", Voice)));
            StringAssert.Contains("audio/mpeg", e.Message);
        }

        [Test]
        public void ReadsWavRepliesAndResamplesThem()
        {
            // Chatterbox-style reply: 16-bit mono WAV at 24 kHz.
            byte[] wav24 = WavTests.Wav(24000, 1, new short[] { 16384, -16384 });
            var tts = new OpenAICompatibleTtsProvider(new HttpClient(new FakeHandler(_ => Pcm(wav24, "audio/wav"))), "http://localhost:8004/v1", null);
            Assert.AreEqual(new[] { 0.5f, -0.5f }, Collect(tts.StreamSpeechAsync("Hi.", Voice)));

            // 48 kHz stereo comes out as 24 kHz mono.
            var frames = new short[4800 * 2];
            byte[] wav48 = WavTests.Wav(48000, 2, frames);
            tts = new OpenAICompatibleTtsProvider(new HttpClient(new FakeHandler(_ => Pcm(wav48, "audio/x-wav"))), "http://localhost:8004/v1", null);
            Assert.That(Collect(tts.StreamSpeechAsync("Hi.", Voice)).Length, Is.InRange(2398, 2400));
        }

        [Test]
        public void HttpErrorsAreOneLineWithoutSecrets()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Invalid key\\nprovided\"}}"),
            });
            var tts = new OpenAICompatibleTtsProvider(new HttpClient(handler), "https://api.openai.com/v1", "sk-secret");
            var e = Assert.Throws<ProviderException>(() => Collect(tts.StreamSpeechAsync("Hi.", Voice)));
            Assert.AreEqual(401, e.StatusCode);
            StringAssert.Contains("Invalid key provided", e.Message);
            StringAssert.Contains("Check the API key", e.Message);
            StringAssert.DoesNotContain("sk-secret", e.Message);
        }

        [Test]
        public void EmptyTextSendsNothing()
        {
            var handler = new FakeHandler(_ => throw new InvalidOperationException("should not be called"));
            var tts = new OpenAICompatibleTtsProvider(new HttpClient(handler), "http://localhost:8880/v1", null);
            Assert.AreEqual(0, Collect(tts.StreamSpeechAsync("  ", Voice)).Length);
        }

        [Test]
        public void FishPostsTtsWithModelHeaderAndReferenceId()
        {
            var handler = new FakeHandler(_ => Pcm(new byte[] { 0x00, 0x40 }, "audio/pcm"));
            var tts = new FishAudioTtsProvider(new HttpClient(handler), null, "fish-key");

            Assert.AreEqual(new[] { 0.5f }, Collect(tts.StreamSpeechAsync("Hello.", new VoiceSettings { Model = "s1", Voice = "abc123", Speed = 1.5f })));
            Assert.AreEqual("https://api.fish.audio/v1/tts", handler.LastRequest.RequestUri.ToString());
            Assert.AreEqual("Bearer fish-key", handler.LastRequest.Headers.Authorization.ToString());
            Assert.AreEqual("s1", handler.LastRequest.Headers.GetValues("model").Single());
            JObject body = JObject.Parse(handler.LastBody);
            Assert.AreEqual("Hello.", (string)body["text"]);
            Assert.AreEqual("abc123", (string)body["reference_id"]);
            Assert.AreEqual("pcm", (string)body["format"]);
            Assert.AreEqual(24000, (int)body["sample_rate"]);
            Assert.AreEqual(1.5f, (float)body["prosody"]["speed"]);
        }

        [Test]
        public void FishNeedsKeyAndHttpsAndSkipsOddModelNames()
        {
            Assert.Throws<ProviderException>(() => new FishAudioTtsProvider(new HttpClient(), null, ""));
            Assert.Throws<ProviderException>(() => new FishAudioTtsProvider(new HttpClient(), "http://api.fish.audio", "key"));
            Assert.IsNull(FishAudioTtsProvider.ModelHeader("s1\r\nX-Evil: 1"));
            Assert.IsNull(FishAudioTtsProvider.ModelHeader(""));
            Assert.AreEqual("s2.1-pro", FishAudioTtsProvider.ModelHeader(" s2.1-pro "));
            Assert.IsNull(JObject.Parse(FishAudioTtsProvider.BuildBody("Hi", new VoiceSettings()))["reference_id"]);
        }

        [Test]
        public void RegistryBuildsTtsFromSettings()
        {
            var registry = new ProviderRegistry();
            registry.RegisterTts(TtsProviderIds.FishAudio, (s, key) => new FishAudioTtsProvider(new HttpClient(), s.BaseUrl, key));
            var settings = new QolSettings();

            registry.Rebuild(settings, _ => null);
            Assert.IsNull(registry.Tts);
            Assert.IsNull(registry.TtsError, "voice is off by default");

            settings.Tts.Provider = TtsProviderIds.FishAudio;
            settings.Tts.BaseUrl = "https://api.fish.audio";
            registry.Rebuild(settings, _ => null);
            Assert.IsNull(registry.Tts);
            StringAssert.Contains("API key", registry.TtsError);

            registry.Rebuild(settings, slot => slot == QolSecrets.TtsSlot ? "k" : null);
            Assert.IsInstanceOf<FishAudioTtsProvider>(registry.Tts);

            settings.Tts.Provider = "nope";
            registry.Rebuild(settings, _ => null);
            StringAssert.Contains("Unknown voice provider", registry.TtsError);
        }
    }
}
