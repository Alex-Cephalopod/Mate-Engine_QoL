using System;
using System.IO;
using MateEngineQoL.Import;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class DataFolderImportTests
    {
        const string Src = "C:/Users/me/AppData/LocalLow/Shinymoon/MateEngineX";
        const string Dst = "C:/Users/me/AppData/LocalLow/Shinymoon/MateEngineQoL";

        [Test]
        public void RewritePaths_MovesMixedSeparatorPathsUnderSource()
        {
            string json = "{\"filePath\":\"C:/Users/me/AppData/LocalLow/Shinymoon/MateEngineX\\\\Steam Workshop\\\\Kermit.vrm\"}";
            string result = DataFolderImport.RewritePaths(json, Src, Dst);
            Assert.AreEqual(Dst + "\\Steam Workshop\\Kermit.vrm", (string)JObject.Parse(result)["filePath"]);
        }

        [Test]
        public void RewritePaths_HandlesBackslashSourceRootAndNestedArrays()
        {
            string json = "{\"avatars\":[{\"p\":\"C:\\\\Users\\\\me\\\\AppData\\\\LocalLow\\\\Shinymoon\\\\MateEngineX\\\\a.vrm\"}]}";
            string result = DataFolderImport.RewritePaths(json, Src.Replace('/', '\\'), Dst);
            Assert.AreEqual(Dst + "\\a.vrm", (string)JObject.Parse(result)["avatars"][0]["p"]);
        }

        [Test]
        public void RewritePaths_IgnoresSiblingFoldersWithSamePrefix()
        {
            string json = "{\"p\":\"C:/Users/me/AppData/LocalLow/Shinymoon/MateEngineXtra/a.vrm\"}";
            Assert.AreEqual(json, DataFolderImport.RewritePaths(json, Src, Dst));
        }

        [Test]
        public void RewritePaths_LeavesOtherValuesAlone()
        {
            string json = "{\"name\":\"Kermit\",\"scale\":1.5,\"path\":\"D:/Models/other.vrm\"}";
            Assert.AreEqual(json, DataFolderImport.RewritePaths(json, Src, Dst));
        }

        [Test]
        public void RewritePaths_ReturnsNullForInvalidJson()
        {
            Assert.IsNull(DataFolderImport.RewritePaths("not json {", Src, Dst));
        }

        string _tmp;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "meqol-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tmp, true); } catch { }
        }

        [Test]
        public void Run_CopiesRewritesSkipsAndNeverOverwrites()
        {
            string src = Path.Combine(_tmp, "MateEngineX");
            string dst = Path.Combine(_tmp, "MateEngineQoL");
            Directory.CreateDirectory(Path.Combine(src, "Steam Workshop"));
            Directory.CreateDirectory(dst);

            File.WriteAllText(Path.Combine(src, "Steam Workshop", "a.vrm"), "model");
            File.WriteAllText(Path.Combine(src, "avatars.json"), "{\"p\":\"" + src.Replace('\\', '/') + "/Steam Workshop/a.vrm\"}");
            File.WriteAllText(Path.Combine(src, "ZomeAI_prompt.txt"), "You are Zome.");
            File.WriteAllText(Path.Combine(src, "ZomeAI.cache"), "big");
            File.WriteAllText(Path.Combine(src, "settings.json"), "{\"from\":\"steam\"}");
            File.WriteAllText(Path.Combine(dst, "settings.json"), "{\"from\":\"fork\"}");

            DataFolderImport.Result r = DataFolderImport.Run(src, dst);

            Assert.AreEqual("model", File.ReadAllText(Path.Combine(dst, "Steam Workshop", "a.vrm")));
            Assert.AreEqual("You are Zome.", File.ReadAllText(Path.Combine(dst, "ZomeAI_prompt.txt")));
            StringAssert.Contains("MateEngineQoL", (string)JObject.Parse(File.ReadAllText(Path.Combine(dst, "avatars.json")))["p"]);
            Assert.IsFalse(File.Exists(Path.Combine(dst, "ZomeAI.cache")), "excluded file was copied");
            Assert.AreEqual("{\"from\":\"fork\"}", File.ReadAllText(Path.Combine(dst, "settings.json")), "existing file was overwritten");

            Assert.AreEqual(3, r.Copied);
            Assert.AreEqual(1, r.Rewritten);
            Assert.AreEqual(1, r.SkippedExisting);
            Assert.AreEqual(1, r.SkippedExcluded);
            Assert.IsEmpty(r.Errors);
            Assert.AreEqual("{\"from\":\"steam\"}", File.ReadAllText(Path.Combine(src, "settings.json")), "source was modified");
        }

        [Test]
        public void Run_RefusesNestedFolders()
        {
            string src = Path.Combine(_tmp, "MateEngineX");
            Directory.CreateDirectory(src);
            Assert.Throws<ArgumentException>(() => DataFolderImport.Run(src, Path.Combine(src, "inner")));
            Assert.Throws<ArgumentException>(() => DataFolderImport.Run(src, src));
        }
    }
}
