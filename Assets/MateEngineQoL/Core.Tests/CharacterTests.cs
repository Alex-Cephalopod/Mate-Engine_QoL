using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MateEngineQoL.AI;
using MateEngineQoL.Characters;
using MateEngineQoL.Settings;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class CharacterTests
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "meqol-chars-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        static SessionMessage U(string text) => new SessionMessage(ChatMessage.User, text, T0);
        static SessionMessage A(string text) => new SessionMessage(ChatMessage.Assistant, text, T0);

        // ---- ids and store -------------------------------------------------------

        [TestCase("Miku Hatsune!", "miku-hatsune")]
        [TestCase("  ../../etc  ", "etc")]
        [TestCase("ゼロ", "character")]
        [TestCase("A__B", "a-b")]
        public void IdsFromNamesAreFolderSafe(string name, string expected)
        {
            Assert.AreEqual(expected, CharacterIds.FromName(name));
            Assert.IsTrue(CharacterIds.IsValid(CharacterIds.FromName(name)));
        }

        [TestCase("..")]
        [TestCase("a/b")]
        [TestCase("a\\b")]
        [TestCase("Upper")]
        [TestCase("")]
        [TestCase(null)]
        public void UnsafeIdsAreRejected(string id)
        {
            Assert.IsFalse(CharacterIds.IsValid(id));
            var store = new CharacterStore(_dir);
            Assert.IsNull(store.Load(id));
            if (id != null) Assert.Throws<ArgumentException>(() => store.DirectoryOf(id));
        }

        [Test]
        public void CreateMakesUniqueIdsAndRoundTrips()
        {
            var store = new CharacterStore(_dir);
            CharacterProfile a = store.Create("Aqua", "prompt a", "hi");
            CharacterProfile b = store.Create("aqua");
            Assert.AreEqual("aqua", a.Id);
            Assert.AreEqual("aqua-2", b.Id);

            CharacterProfile loaded = store.Load("aqua");
            Assert.AreEqual("Aqua", loaded.Name);
            Assert.AreEqual("prompt a", loaded.Prompt);
            Assert.AreEqual("hi", loaded.Greeting);
            Assert.AreEqual(2, store.LoadAll().Count);
        }

        [Test]
        public void FolderNameWinsOverIdInFile()
        {
            var store = new CharacterStore(_dir);
            Directory.CreateDirectory(Path.Combine(_dir, "real"));
            File.WriteAllText(Path.Combine(_dir, "real", CharacterStore.ProfileFile), "{\"Id\":\"../evil\",\"Name\":\"R\"}");
            Assert.AreEqual("real", store.Load("real").Id);
        }

        [Test]
        public void UnreadableProfileIsSkippedWithWarning()
        {
            var store = new CharacterStore(_dir);
            store.Create("Good");
            Directory.CreateDirectory(Path.Combine(_dir, "bad"));
            File.WriteAllText(Path.Combine(_dir, "bad", CharacterStore.ProfileFile), "{ not json");
            Assert.AreEqual(1, store.LoadAll().Count);
            StringAssert.Contains("bad", store.LastWarning);
        }

        [Test]
        public void FindMatchesIdNameThenUniquePrefix()
        {
            var all = new List<CharacterProfile>
            {
                new CharacterProfile { Id = "zome", Name = "Zome" },
                new CharacterProfile { Id = "miku", Name = "Hatsune Miku" },
                new CharacterProfile { Id = "mikasa", Name = "Mikasa" },
            };
            Assert.AreEqual("zome", CharacterStore.Find(all, "zome").Id);
            Assert.AreEqual("miku", CharacterStore.Find(all, "hatsune miku").Id);
            Assert.AreEqual("miku", CharacterStore.Find(all, "Hats").Id);
            Assert.IsNull(CharacterStore.Find(all, "mik"), "ambiguous prefix");
            Assert.IsNull(CharacterStore.Find(all, "nobody"));
            Assert.IsNull(CharacterStore.Find(all, "  "));
        }

        // ---- seeding ---------------------------------------------------------------

        [Test]
        public void SeedCreatesDefaultAndImportsUpstreamHistory()
        {
            var store = new CharacterStore(_dir);
            string upstream = "{\"chat\":[{\"role\":\"\",\"content\":\"hi\"},{\"role\":\"\",\"content\":\"hello!\"},{\"role\":\"\",\"content\":\"bye\"}]}";

            Assert.IsTrue(CharacterSeeder.SeedIfEmpty(store, "You are Zome.", upstream, T0));
            CharacterProfile p = store.Load(CharacterIds.Default);
            Assert.AreEqual("You are Zome.", p.Prompt);
            Assert.AreEqual(CharacterSeeder.DefaultName, p.Name, "no Name: line in this prompt");

            var log = new SessionLog(store.SessionsDirectoryOf(CharacterIds.Default));
            List<SessionMessage> msgs = log.Read(log.Latest());
            Assert.AreEqual(new[] { "user", "assistant", "user" }, msgs.Select(m => m.Role).ToArray());
            Assert.AreEqual(new[] { "hi", "hello!", "bye" }, msgs.Select(m => m.Text).ToArray());

            Assert.IsFalse(CharacterSeeder.SeedIfEmpty(store, "other", null, T0), "seeds only once");
        }

        [TestCase("Who you are:\n\n- Name: Zome\n- Age: 21", "Zome")]
        [TestCase("Character:\n\nName: Roxanne Wolf, usually called Roxy\nAge: Adult", "Roxanne Wolf")]
        [TestCase("You are a helpful cat. Your name is Mochi.", CharacterSeeder.DefaultName)]
        [TestCase("Username: bob\n", CharacterSeeder.DefaultName)]
        [TestCase("", CharacterSeeder.DefaultName)]
        public void SeedNameComesFromNameLine(string prompt, string expected)
        {
            Assert.AreEqual(expected, CharacterSeeder.NameFromPrompt(prompt));
        }

        [Test]
        public void SeedWithoutHistoryOrWithCorruptHistoryHasNoSession()
        {
            var store = new CharacterStore(_dir);
            Assert.IsTrue(CharacterSeeder.SeedIfEmpty(store, "p", "{ corrupt", T0));
            Assert.IsNull(new SessionLog(store.SessionsDirectoryOf(CharacterIds.Default)).Latest());
        }

        // ---- session log -------------------------------------------------------------

        [Test]
        public void SessionLogAppendsAndReadsWithEscapes()
        {
            var log = new SessionLog(_dir, () => T0);
            string id = log.Create();
            Assert.AreEqual("20261007-120000", id);
            log.Append(id, U("line1\nline2 \"quoted\""));
            log.Append(id, new[] { A("ok"), U("ünïcødé ✓") });

            List<SessionMessage> msgs = log.Read(id);
            Assert.AreEqual(3, msgs.Count);
            Assert.AreEqual("line1\nline2 \"quoted\"", msgs[0].Text);
            Assert.AreEqual("ünïcødé ✓", msgs[2].Text);
            Assert.AreEqual(T0, msgs[1].TimeUtc);
            Assert.AreEqual(3, File.ReadAllLines(Path.Combine(_dir, id + SessionLog.Extension)).Length, "one message per line");
        }

        [Test]
        public void SessionIdsInSameSecondGetSuffixesAndSortNumerically()
        {
            var log = new SessionLog(_dir, () => T0);
            var ids = Enumerable.Range(0, 11).Select(_ => log.Create()).ToList();
            Assert.AreEqual("20261007-120000-2", ids[1]);
            Assert.AreEqual("20261007-120000-11", log.Latest());
            CollectionAssert.AreEqual(ids, log.List());
        }

        [Test]
        public void TornAndForeignLinesAreSkipped()
        {
            var log = new SessionLog(_dir, () => T0);
            string id = log.Create();
            log.Append(id, U("kept"));
            File.AppendAllText(Path.Combine(_dir, id + SessionLog.Extension),
                "{\"t\":\"x\",\"role\":\"system\",\"text\":\"injected\"}\n{\"t\":\"2026-10-07T12:00:00Z\",\"role\":\"assis");
            List<SessionMessage> msgs = log.Read(id);
            Assert.AreEqual(1, msgs.Count);
            Assert.AreEqual(2, log.LastSkipped);
        }

        [Test]
        public void SessionLogIgnoresOtherFilesAndRejectsBadIds()
        {
            File.WriteAllText(Path.Combine(_dir, "notes.jsonl"), "");
            File.WriteAllText(Path.Combine(_dir, "20261007-120000.txt"), "");
            var log = new SessionLog(_dir);
            Assert.IsNull(log.Latest());
            Assert.Throws<ArgumentException>(() => log.Read("../x"));
        }

        [Test]
        public void DeleteAllRemovesSessions()
        {
            var log = new SessionLog(_dir, () => T0);
            log.Create();
            log.Create();
            log.DeleteAll();
            Assert.IsEmpty(log.List());
        }

        [Test]
        public void AlternatingDropsLeadingAssistantAndMergesRepeats()
        {
            List<SessionMessage> m = SessionLog.Alternating(new[] { A("greet"), U("a"), U("b"), A("c"), A("d"), U("e") });
            Assert.AreEqual(new[] { "user", "assistant", "user" }, m.Select(x => x.Role).ToArray());
            Assert.AreEqual(new[] { "a\n\nb", "c\n\nd", "e" }, m.Select(x => x.Text).ToArray());
        }

        // ---- commands ----------------------------------------------------------------

        [Test]
        public void CommandsParse()
        {
            Assert.IsTrue(ChatCommand.TryParse("/char  Hatsune Miku ", out ChatCommand c));
            Assert.AreEqual(ChatCommandKind.SwitchCharacter, c.Kind);
            Assert.AreEqual("Hatsune Miku", c.Argument);

            Assert.IsTrue(ChatCommand.TryParse("/CHAR", out c));
            Assert.AreEqual(ChatCommandKind.ListCharacters, c.Kind);
            Assert.IsTrue(ChatCommand.TryParse("/chars", out c));
            Assert.AreEqual(ChatCommandKind.ListCharacters, c.Kind);
            Assert.IsTrue(ChatCommand.TryParse("/new", out c));
            Assert.AreEqual(ChatCommandKind.NewSession, c.Kind);

            Assert.IsFalse(ChatCommand.TryParse("/shrug", out _));
            Assert.IsFalse(ChatCommand.TryParse("/charming", out _));
            Assert.IsFalse(ChatCommand.TryParse("hello /char x", out _));
            Assert.IsFalse(ChatCommand.TryParse("/", out _));
        }

        // ---- context builder -----------------------------------------------------------

        // One token per word keeps the arithmetic in these tests readable.
        static int Words(string s) => string.IsNullOrWhiteSpace(s) ? 0 : s.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

        static ContextSettings Budget(int total, int maxMessages = 100) => new ContextSettings
        {
            TotalTokens = total, ReplyReserveTokens = 0, FactsTokens = 10, SummaryTokens = 10, MaxHistoryMessages = maxMessages,
        };

        static List<SessionMessage> History(int pairs) =>
            Enumerable.Range(1, pairs).SelectMany(i => new[] { U("u" + i), A("a" + i) }).ToList();

        [Test]
        public void ContextKeepsNewestPairsWithinMessageCap()
        {
            ContextResult r = ContextBuilder.Build(new ContextInput { Prompt = "sys", History = History(5), NewMessage = "now" },
                Budget(1000, maxMessages: 5), Words);
            // 5 messages would start on an assistant turn; whole pairs only, so 4.
            Assert.AreEqual(new[] { "sys", "u4", "a4", "u5", "a5", "now" }, r.Messages.Select(m => m.Content).ToArray());
            Assert.AreEqual(4, r.HistoryIncluded);
        }

        [Test]
        public void ContextTrimsOldTurnsToTokenBudget()
        {
            // Costs: system 1+4, new 1+4, each pair 2+8 = 10. Total 30 leaves room for exactly two pairs.
            ContextResult r = ContextBuilder.Build(new ContextInput { Prompt = "sys", History = History(5), NewMessage = "now" },
                Budget(30), Words);
            Assert.AreEqual(new[] { "sys", "u4", "a4", "u5", "a5", "now" }, r.Messages.Select(m => m.Content).ToArray());
            Assert.AreEqual(30, r.EstimatedTokens);

            r = ContextBuilder.Build(new ContextInput { Prompt = "sys", History = History(5), NewMessage = "now" }, Budget(29), Words);
            Assert.AreEqual(2, r.HistoryIncluded);
        }

        [Test]
        public void ReplyReserveComesOffTheTotal()
        {
            ContextSettings b = Budget(30);
            b.ReplyReserveTokens = 10;
            ContextResult r = ContextBuilder.Build(new ContextInput { Prompt = "sys", History = History(5), NewMessage = "now" }, b, Words);
            Assert.AreEqual(2, r.HistoryIncluded);
        }

        [Test]
        public void TrailingUnansweredUserTurnIsLeftOut()
        {
            var h = History(2);
            h.Add(U("cancelled"));
            ContextResult r = ContextBuilder.Build(new ContextInput { History = h, NewMessage = "now" }, Budget(1000), Words);
            Assert.AreEqual(new[] { "u1", "a1", "u2", "a2", "now" }, r.Messages.Select(m => m.Content).ToArray());
            Assert.AreEqual("", r.SystemText, "no system message without a prompt");
        }

        [Test]
        public void FactsAndSummaryAreLayeredAndTrimmedToTheirBudgets()
        {
            var input = new ContextInput
            {
                Prompt = "You are Zome.",
                Facts = new[] { "likes tea", "", "has a cat named Mochi", "lives in a very very very big city" },
                Summary = "one two three four five six seven eight nine ten eleven twelve",
                NewMessage = "hi",
            };
            ContextResult r = ContextBuilder.Build(input, Budget(1000), Words);
            string sys = r.SystemText;
            StringAssert.StartsWith("You are Zome.", sys);
            // Facts budget 10 words: "- likes tea" (3) + "- has a cat named Mochi" (6) fit, the third (8) doesn't.
            StringAssert.Contains("- likes tea\n- has a cat named Mochi", sys);
            StringAssert.DoesNotContain("big city", sys);
            // Summary keeps its newest part.
            StringAssert.EndsWith("eleven twelve", sys);
            StringAssert.DoesNotContain("one two", sys);
        }

        [Test]
        public void OversizedPromptIsKeptAndFlagged()
        {
            string prompt = string.Join(" ", Enumerable.Repeat("w", 50));
            ContextResult r = ContextBuilder.Build(new ContextInput { Prompt = prompt, History = History(3), NewMessage = "now" },
                Budget(20), Words);
            Assert.IsTrue(r.PromptOverBudget);
            Assert.AreEqual(prompt, r.SystemText);
            Assert.AreEqual(0, r.HistoryIncluded);
            Assert.AreEqual("now", r.Messages.Last().Content);
        }

        [Test]
        public void DefaultEstimatorRoundsUp()
        {
            Assert.AreEqual(0, ContextBuilder.EstimateTokens(""));
            Assert.AreEqual(1, ContextBuilder.EstimateTokens("a"));
            Assert.AreEqual(2, ContextBuilder.EstimateTokens("abcde"));
        }
    }
}
