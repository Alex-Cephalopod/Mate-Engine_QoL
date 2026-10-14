using System;
using System.Collections.Generic;
using System.IO;
using MateEngineQoL.Animations;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class AnimationSidecarTests
    {
        [Test]
        public void MissingSidecarIsALoopingIdleNamedAfterTheFile()
        {
            AnimationSidecar s = AnimationSidecar.Parse(null, "Stretch", out string error);
            Assert.IsNull(error);
            Assert.AreEqual("Stretch", s.Name);
            Assert.AreEqual(AnimationCategories.Idle, s.Category);
            Assert.IsTrue(s.Loop);
            Assert.AreEqual(1f, s.IdleWeight);
            Assert.IsNull(s.Anchor);
            Assert.AreEqual(0.4f, s.BlendIn);
            Assert.AreEqual(0.4f, s.BlendOut);
            Assert.IsEmpty(s.Tags);
        }

        [Test]
        public void ReadsEveryField()
        {
            const string json = "{\"name\":\"Sit on edge\",\"category\":\"Sit\",\"loop\":false,\"idleWeight\":0,\"anchor\":\"windowTopEdge\","
                + "\"blendIn\":0.25,\"blendOut\":1,\"tags\":[\"Relaxed\",\"\",3]}";
            AnimationSidecar s = AnimationSidecar.Parse(json, "file", out string error);
            Assert.IsNull(error);
            Assert.AreEqual("Sit on edge", s.Name);
            Assert.AreEqual("sit", s.Category);
            Assert.IsFalse(s.Loop);
            Assert.AreEqual(0f, s.IdleWeight);
            Assert.AreEqual("windowTopEdge", s.Anchor);
            Assert.AreEqual(0.25f, s.BlendIn);
            Assert.AreEqual(1f, s.BlendOut);
            Assert.AreEqual(new[] { "relaxed" }, s.Tags);
        }

        [Test]
        public void NonIdleCategoriesAreNotPickedAsIdlesUnlessWeighted()
        {
            Assert.AreEqual(0f, AnimationSidecar.Parse("{\"category\":\"gesture\"}", "wave", out _).IdleWeight);
            Assert.AreEqual(2f, AnimationSidecar.Parse("{\"category\":\"gesture\",\"idleWeight\":2}", "wave", out _).IdleWeight);
        }

        [Test]
        public void ClampsNumbersAndIgnoresWrongTypes()
        {
            AnimationSidecar s = AnimationSidecar.Parse("{\"idleWeight\":-3,\"blendIn\":99,\"blendOut\":\"slow\",\"loop\":\"yes\",\"name\":5}", "f", out string error);
            Assert.IsNull(error);
            Assert.AreEqual(0f, s.IdleWeight);
            Assert.AreEqual(AnimationSidecar.MaxBlend, s.BlendIn);
            Assert.AreEqual(0.4f, s.BlendOut);
            Assert.IsTrue(s.Loop);
            Assert.AreEqual("f", s.Name);
        }

        [Test]
        public void BrokenJsonFallsBackToDefaultsWithAnError()
        {
            AnimationSidecar s = AnimationSidecar.Parse("{\"name\": ", "f", out string error);
            Assert.IsNotNull(error);
            Assert.AreEqual("f", s.Name);
            Assert.AreEqual(1f, s.IdleWeight);

            AnimationSidecar.Parse("[1,2]", "f", out error);
            Assert.AreEqual("not a JSON object", error);
        }
    }

    public class AnimationCatalogTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "meqol-anim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        void Write(string rel, string text = "x")
        {
            string path = Path.Combine(_dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        [Test]
        public void ScansClipsWithSidecarsAndSubfolders()
        {
            Write("Stretch.vrma");
            Write("Gestures/Wave.vrma");
            Write("Gestures/Wave.json", "{\"category\":\"gesture\"}");
            Write("notes.json", "{}");
            Write("readme.txt");
            Write("odd.vrmax");

            using var c = new AnimationCatalog(_dir);
            Assert.IsTrue(c.Rescan());
            CollectionAssert.AreEqual(new[] { "Gestures/Wave.vrma", "Stretch.vrma" }, Ids(c));
            Assert.AreEqual("gesture", c.Find("gestures/wave.vrma").Info.Category);
            Assert.IsTrue(c.Find("Stretch.vrma").IsIdle);
            Assert.IsFalse(c.Rescan(), "nothing changed");
        }

        [Test]
        public void RescanNoticesNewClipsAndSidecarEdits()
        {
            Write("A.vrma");
            using var c = new AnimationCatalog(_dir);
            c.Rescan();

            Write("B.vrma");
            Assert.IsTrue(c.Rescan());
            Assert.AreEqual(2, c.Entries.Count);

            Write("A.json", "{\"name\":\"Renamed\",\"idleWeight\":3}");
            Assert.IsTrue(c.Rescan());
            Assert.AreEqual("Renamed", c.Find("A.vrma").Info.Name);

            File.Delete(Path.Combine(_dir, "B.vrma"));
            Assert.IsTrue(c.Rescan());
            Assert.AreEqual(1, c.Entries.Count);
        }

        [Test]
        public void BadSidecarKeepsTheClip()
        {
            Write("A.vrma");
            Write("A.json", "{oops");
            using var c = new AnimationCatalog(_dir);
            c.Rescan();
            Assert.AreEqual(1, c.Entries.Count);
            Assert.IsNotNull(c.Entries[0].SidecarError);
            Assert.IsTrue(c.Entries[0].IsIdle);
        }

        [Test]
        public void MissingFolderIsEmpty()
        {
            using var c = new AnimationCatalog(Path.Combine(_dir, "nope"));
            Assert.IsFalse(c.Rescan());
            Assert.IsEmpty(c.Entries);
        }

        [Test]
        public void PollWaitsForChangesToSettle()
        {
            Write("A.vrma");
            using var c = new AnimationCatalog(_dir);
            c.Rescan();
            Write("B.vrma");
            c.MarkDirty();
            DateTime now = DateTime.UtcNow;
            Assert.IsFalse(c.PollChanges(now), "too soon");
            Assert.IsTrue(c.PollChanges(now.AddSeconds(2)));
            Assert.AreEqual(2, c.Entries.Count);
            Assert.IsFalse(c.PollChanges(now.AddSeconds(4)), "nothing new");
        }

        static string[] Ids(AnimationCatalog c)
        {
            var ids = new List<string>();
            foreach (AnimationEntry e in c.Entries) ids.Add(e.Id);
            return ids.ToArray();
        }
    }

    public class IdlePickerTests
    {
        static AnimationEntry Clip(string id, float weight) =>
            new AnimationEntry { Id = id, Info = new AnimationSidecar { Name = id, IdleWeight = weight } };

        [Test]
        public void NoIdlesGivesNull()
        {
            Assert.IsNull(IdlePicker.Pick(new[] { Clip("wave", 0f) }, null, new Random(1)));
            Assert.IsNull(IdlePicker.Pick(new AnimationEntry[0], null, new Random(1)));
        }

        [Test]
        public void SingleIdleRepeats()
        {
            Assert.AreEqual("a", IdlePicker.Pick(new[] { Clip("a", 1f), Clip("wave", 0f) }, "a", new Random(1)).Id);
        }

        [Test]
        public void AvoidsTheCurrentClipAndFollowsWeights()
        {
            var clips = new[] { Clip("a", 1f), Clip("b", 3f), Clip("c", 1f) };
            var rng = new Random(42);
            var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 0 };
            for (int i = 0; i < 4000; i++) counts[IdlePicker.Pick(clips, "c", rng).Id]++;
            Assert.AreEqual(0, counts["c"]);
            Assert.That(counts["b"] / (double)counts["a"], Is.InRange(2.5, 3.5));
        }
    }
}
