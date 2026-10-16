using System.Collections.Generic;
using System.Text;
using MateEngineQoL.AI;
using MateEngineQoL.Animations;
using MateEngineQoL.Settings;
using MateEngineQoL.Tags;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class TagParserTests
    {
        static string Run(IEnumerable<string> chunks, List<ReplyTag> tags)
        {
            var parser = new TagParser();
            var sb = new StringBuilder();
            foreach (string c in chunks) sb.Append(parser.Push(c, tags));
            sb.Append(parser.Flush());
            return sb.ToString();
        }

        [Test]
        public void LeadingTagsAreRemovedAndReported()
        {
            var tags = new List<ReplyTag>();
            string text = Run(new[] { "[emotion:happy][action:wave] Hi there!" }, tags);
            Assert.AreEqual("Hi there!", text);
            Assert.AreEqual(2, tags.Count);
            Assert.AreEqual(ReplyTagKind.Emotion, tags[0].Kind);
            Assert.AreEqual("happy", tags[0].Name);
            Assert.AreEqual(ReplyTagKind.Action, tags[1].Kind);
            Assert.AreEqual("wave", tags[1].Name);
        }

        [Test]
        public void TagSplitAcrossEveryChunkBoundaryIsHeldBack()
        {
            const string reply = "[emotion:happy] Hello [action:wave] friend.";
            for (int cut = 1; cut < reply.Length; cut++)
            {
                var tags = new List<ReplyTag>();
                var parser = new TagParser();
                string first = parser.Push(reply.Substring(0, cut), tags);
                Assert.IsFalse(first.Contains("["), "cut at " + cut + " leaked: " + first);
                string text = first + parser.Push(reply.Substring(cut), tags) + parser.Flush();
                Assert.AreEqual("Hello friend.", text, "cut at " + cut);
                Assert.AreEqual(2, tags.Count, "cut at " + cut);
            }
        }

        [Test]
        public void OneCharacterAtATime()
        {
            var tags = new List<ReplyTag>();
            var chunks = new List<string>();
            foreach (char c in "[ Emotion : Very Happy ]\nOkay.") chunks.Add(c.ToString());
            Assert.AreEqual("Okay.", Run(chunks, tags));
            Assert.AreEqual("very_happy", tags[0].Name);
        }

        [Test]
        public void UnknownKeyValueTagsAreDroppedSilently()
        {
            var tags = new List<ReplyTag>();
            Assert.AreEqual("Sure.", Run(new[] { "[pose:sit] Sure." }, tags));
            Assert.IsEmpty(tags);
        }

        [Test]
        public void BracketsThatAreNotTagsStayAsText()
        {
            var tags = new List<ReplyTag>();
            Assert.AreEqual("See [1] and [laugh] ok", Run(new[] { "See [1", "] and [lau", "gh] ok" }, tags));
            Assert.AreEqual("[!] error", Run(new[] { "[!] error" }, tags));
            Assert.AreEqual("a [b c", Run(new[] { "a [b c" }, tags)); // unclosed at the end
            Assert.AreEqual("[x:\ny]", Run(new[] { "[x:\ny]" }, tags)); // line break inside
            Assert.AreEqual("[:x]", Run(new[] { "[:x]" }, tags));
            Assert.AreEqual("[emotion:]", Run(new[] { "[emotion:]" }, tags));
            Assert.IsEmpty(tags);
        }

        [Test]
        public void LongBracketIsReleasedInsteadOfHeldForever()
        {
            var tags = new List<ReplyTag>();
            string note = "[note: " + new string('x', 80) + "]";
            var parser = new TagParser();
            string shown = parser.Push(note.Substring(0, 70), tags);
            Assert.IsTrue(shown.StartsWith("[note: "), "held past the limit");
            Assert.AreEqual(note, shown + parser.Push(note.Substring(70), tags) + parser.Flush());
        }

        [Test]
        public void NestedBracketReleasesTheFirst()
        {
            var tags = new List<ReplyTag>();
            Assert.AreEqual("[a [b] ok", Run(new[] { "[a [b] [emotion:sad] ok" }, tags));
            Assert.AreEqual("sad", tags[0].Name);
        }

        [Test]
        public void MidReplyTagLeavesOneSpace()
        {
            var tags = new List<ReplyTag>();
            Assert.AreEqual("I can do that! Look.", Run(new[] { "I can do that! [action:nod] Look." }, tags));
            Assert.AreEqual("End.", Run(new[] { "End.[action:bow]" }, tags).TrimEnd());
            Assert.AreEqual("Line one\nLine two", Run(new[] { "Line one\n[emotion:happy] Line two" }, tags));
        }

        [Test]
        public void KeySynonymsAndHostileNames()
        {
            var tags = new List<ReplyTag>();
            Run(new[] { "[mood:Joy][gesture: big-wave!][emotion:<script>]x" }, tags);
            Assert.AreEqual(3, tags.Count);
            Assert.AreEqual("joy", tags[0].Name);
            Assert.AreEqual("big_wave", tags[1].Name);
            Assert.AreEqual(ReplyTagKind.Action, tags[1].Kind);
            Assert.AreEqual("script", tags[2].Name);

            tags.Clear();
            Run(new[] { "[emotion:!!!]x" }, tags);
            Assert.IsEmpty(tags); // nothing usable left in the name
        }

        [Test]
        public void StripHandlesWholeText()
        {
            var tags = new List<ReplyTag>();
            Assert.AreEqual("Hi.", TagParser.Strip("[emotion:happy] Hi.", tags));
            Assert.AreEqual(1, tags.Count);
            Assert.AreEqual("", TagParser.Strip(""));
        }

        [Test]
        public void NormalizeName()
        {
            Assert.AreEqual("look_around", TagParser.NormalizeName("  Look Around! "));
            Assert.AreEqual("a_b_c", TagParser.NormalizeName("a--b__c"));
            Assert.IsNull(TagParser.NormalizeName("?!"));
            Assert.AreEqual(TagParser.MaxNameLength, TagParser.NormalizeName(new string('a', 100)).Length);
        }
    }

    public class ReplyTagVocabularyTests
    {
        static AnimationEntry Clip(string id, string json)
        {
            return new AnimationEntry { Id = id, Info = AnimationSidecar.Parse(json, id, out _) };
        }

        static readonly List<AnimationEntry> Library = new List<AnimationEntry>
        {
            Clip("Sway", null), // idle
            Clip("Wave", "{\"category\":\"gesture\",\"tags\":[\"hello\",\"greet\"]}"),
            Clip("Bow", "{\"name\":\"Deep Bow\",\"category\":\"gesture\",\"tags\":[\"greet\"]}"),
            Clip("Sit", "{\"category\":\"sit\",\"tags\":[\"hello\"]}"),
        };

        [Test]
        public void FindsGesturesByNameThenTag()
        {
            Assert.AreEqual("Wave", ReplyTagVocabulary.FindAction(Library, "wave").Id);
            Assert.AreEqual("Bow", ReplyTagVocabulary.FindAction(Library, "deep_bow").Id);
            Assert.AreEqual("Wave", ReplyTagVocabulary.FindAction(Library, "greet").Id); // first match by tag
            Assert.AreEqual("Wave", ReplyTagVocabulary.FindAction(Library, "hello").Id); // the sit clip isn't a gesture
            Assert.IsNull(ReplyTagVocabulary.FindAction(Library, "sway")); // idles aren't actions
            Assert.IsNull(ReplyTagVocabulary.FindAction(Library, "dance"));
        }

        [Test]
        public void PromptListsEmotionsAndGestures()
        {
            List<string> actions = ReplyTagVocabulary.ActionNames(Library);
            CollectionAssert.AreEqual(new[] { "wave", "deep_bow" }, actions);
            string prompt = ReplyTagVocabulary.PromptText(actions);
            StringAssert.Contains("[emotion:NAME]", prompt);
            StringAssert.Contains("happy, sad, angry, surprised, relaxed, neutral", prompt);
            StringAssert.Contains("wave, deep_bow", prompt);
            StringAssert.DoesNotContain("[action:", ReplyTagVocabulary.PromptText(new List<string>()));
        }

        [Test]
        public void EmotionAliases()
        {
            Assert.AreEqual("happy", ReplyTagVocabulary.CanonicalEmotion("joy"));
            Assert.AreEqual("sad", ReplyTagVocabulary.CanonicalEmotion("sorrow"));
            Assert.AreEqual("neutral", ReplyTagVocabulary.CanonicalEmotion("normal"));
            Assert.AreEqual("smug", ReplyTagVocabulary.CanonicalEmotion("smug")); // custom expression name
        }

        [Test]
        public void InstructionsFollowThePromptAndCountAgainstTheBudget()
        {
            var input = new ContextInput { Prompt = "You are Ami.", Instructions = "Use tags.", NewMessage = "hi" };
            ContextResult r = ContextBuilder.Build(input, new ContextSettings());
            Assert.AreEqual("You are Ami.\n\nUse tags.", r.SystemText);

            var tiny = new ContextSettings { TotalTokens = 20, ReplyReserveTokens = 0 };
            input.Instructions = new string('x', 100);
            Assert.IsTrue(ContextBuilder.Build(input, tiny).PromptOverBudget);
        }
    }
}
