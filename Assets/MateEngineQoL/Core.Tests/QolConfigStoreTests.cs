using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MateEngineQoL.AI;
using MateEngineQoL.Settings;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class QolConfigStoreTests
    {
        sealed class XorProtector : ISecretProtector
        {
            public string Name => "test-xor";
            public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
            public byte[] Unprotect(byte[] cipher) => Protect(cipher);
        }

        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "meqol-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void MissingFilesGiveDefaults()
        {
            var store = new QolConfigStore(_dir, null, _ => null);
            Assert.AreEqual(ChatProviderIds.UpstreamLocal, store.LoadSettings().Chat.Provider);
            Assert.IsEmpty(store.LoadSecrets().ApiKeys);
        }

        [Test]
        public void SettingsRoundTripAndOverwrite()
        {
            var store = new QolConfigStore(_dir, null, _ => null);
            var s = new QolSettings();
            s.Chat.Provider = ChatProviderIds.OpenAICompatible;
            s.Chat.Model = "llama3";
            store.SaveSettings(s);
            s.Chat.Model = "qwen3";
            store.SaveSettings(s); // second save exercises the File.Replace path

            QolSettings loaded = store.LoadSettings();
            Assert.AreEqual(ChatProviderIds.OpenAICompatible, loaded.Chat.Provider);
            Assert.AreEqual("qwen3", loaded.Chat.Model);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, QolConfigStore.SettingsFile + ".tmp")));
        }

        [Test]
        public void SecretsAreProtectedOnDisk()
        {
            var store = new QolConfigStore(_dir, new XorProtector(), _ => null);
            var secrets = new QolSecrets();
            secrets.ApiKeys[QolSecrets.ChatSlot] = "sk-live-abcdef";
            store.SaveSecrets(secrets);

            string onDisk = File.ReadAllText(Path.Combine(_dir, QolConfigStore.SecretsFile));
            StringAssert.DoesNotContain("sk-live-abcdef", onDisk);
            StringAssert.Contains("test-xor", onDisk);
            Assert.AreEqual("sk-live-abcdef", store.GetApiKey(store.LoadSecrets(), QolSecrets.ChatSlot));
        }

        [Test]
        public void UnreadableSecretsAskForKeysAgainInsteadOfCrashing()
        {
            new QolConfigStore(_dir, new XorProtector(), _ => null).SaveSecrets(new QolSecrets { ApiKeys = { ["chat"] = "k" } });
            var otherMachine = new QolConfigStore(_dir, null, _ => null); // protector not available
            Assert.IsEmpty(otherMachine.LoadSecrets().ApiKeys);
            StringAssert.Contains("re-enter", otherMachine.LastWarning);
        }

        [Test]
        public void CorruptSettingsFallBackToDefaultsWithWarning()
        {
            File.WriteAllText(Path.Combine(_dir, QolConfigStore.SettingsFile), "{ not json");
            var store = new QolConfigStore(_dir, null, _ => null);
            Assert.AreEqual(ChatProviderIds.UpstreamLocal, store.LoadSettings().Chat.Provider);
            Assert.IsNotNull(store.LastWarning);
        }

        [Test]
        public void EnvironmentVariableOverridesStoredKey()
        {
            var env = new Dictionary<string, string> { ["MEQOL_CHAT_API_KEY"] = " sk-env " };
            var store = new QolConfigStore(_dir, null, n => env.TryGetValue(n, out string v) ? v : null);
            var secrets = new QolSecrets { ApiKeys = { ["chat"] = "sk-file" } };
            Assert.AreEqual("sk-env", store.GetApiKey(secrets, QolSecrets.ChatSlot));
            Assert.IsTrue(store.IsKeyFromEnvironment(QolSecrets.ChatSlot));
            Assert.IsNull(store.GetApiKey(secrets, QolSecrets.TtsSlot));
        }

        [Test]
        public void RegistryBuildsConfiguredProviderOrReportsError()
        {
            var registry = new ProviderRegistry();
            registry.RegisterChat(ChatProviderIds.OpenAICompatible, (s, key) =>
                new AI.OpenAI.OpenAICompatibleChatProvider(new System.Net.Http.HttpClient(), s.BaseUrl, key));

            var settings = new QolSettings();
            registry.Rebuild(settings, _ => null);
            Assert.IsNull(registry.Chat, "upstream-local means no QoL provider");

            settings.Chat.Provider = ChatProviderIds.OpenAICompatible;
            settings.Chat.BaseUrl = "http://localhost:11434/v1";
            registry.Rebuild(settings, _ => null);
            Assert.IsNotNull(registry.Chat);

            settings.Chat.BaseUrl = "http://10.0.0.5/v1";
            registry.Rebuild(settings, _ => "sk-x");
            Assert.IsNull(registry.Chat);
            StringAssert.Contains("https", registry.ChatError);
        }
    }
}
