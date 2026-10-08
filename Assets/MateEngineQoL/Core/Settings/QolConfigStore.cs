using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngineQoL.Settings
{
    /// <summary>Encrypts secrets at rest (Windows: DPAPI, scoped to the current user).</summary>
    public interface ISecretProtector
    {
        string Name { get; }
        byte[] Protect(byte[] plain);
        byte[] Unprotect(byte[] cipher);
    }

    /// <summary>
    /// Loads and saves qol_settings.json and qol_secrets.json in the fork's data folder.
    /// Writes are atomic (temp file + replace). Environment variables MEQOL_CHAT_API_KEY,
    /// MEQOL_TTS_API_KEY and MEQOL_STT_API_KEY override stored keys and are never written to disk.
    /// </summary>
    public sealed class QolConfigStore
    {
        public const string SettingsFile = "qol_settings.json";
        public const string SecretsFile = "qol_secrets.json";

        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            // Explicit: never let file contents choose .NET types (see docs/SECURITY.md).
            TypeNameHandling = TypeNameHandling.None,
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Formatting = Formatting.Indented,
        };

        readonly string _dir;
        readonly string _secretsDir;
        readonly ISecretProtector _protector;
        readonly Func<string, string> _getEnv;

        public string Directory => _dir;

        /// <summary>Last load problem (corrupt file, decryption failure) for the UI to show; null if none.</summary>
        public string LastWarning { get; private set; }

        /// <param name="dir">Folder for qol_settings.json (per instance).</param>
        /// <param name="secretsDir">Folder for qol_secrets.json; defaults to <paramref name="dir"/>. Pass the data root
        /// so extra instances share keys instead of asking for them again.</param>
        public QolConfigStore(string dir, ISecretProtector protector, Func<string, string> getEnv = null, string secretsDir = null)
        {
            _dir = dir ?? throw new ArgumentNullException(nameof(dir));
            _secretsDir = secretsDir ?? dir;
            _protector = protector;
            _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        }

        public QolSettings LoadSettings()
        {
            string path = Path.Combine(_dir, SettingsFile);
            if (!File.Exists(path)) return new QolSettings();
            try
            {
                QolSettings s = JsonConvert.DeserializeObject<QolSettings>(File.ReadAllText(path), JsonSettings) ?? new QolSettings();
                if (s.Chat == null) s.Chat = new ChatSettings();
                if (s.Characters == null) s.Characters = new CharacterSettings();
                if (s.Context == null) s.Context = new ContextSettings();
                return s;
            }
            catch (JsonException e)
            {
                LastWarning = SettingsFile + " is unreadable and was ignored: " + e.Message;
                return new QolSettings();
            }
        }

        public void SaveSettings(QolSettings settings) =>
            WriteAtomic(Path.Combine(_dir, SettingsFile), JsonConvert.SerializeObject(settings, JsonSettings));

        public QolSecrets LoadSecrets()
        {
            string path = Path.Combine(_secretsDir, SecretsFile);
            if (!File.Exists(path)) return new QolSecrets();
            try
            {
                JObject file = JObject.Parse(File.ReadAllText(path));
                string scheme = (string)file["scheme"] ?? "plain";
                string payload = (string)file["data"] ?? "";

                string json;
                if (scheme == "plain")
                {
                    json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                }
                else if (_protector != null && scheme == _protector.Name)
                {
                    json = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(payload)));
                }
                else
                {
                    LastWarning = "API keys were saved with '" + scheme + "' protection that isn't available here; please re-enter them.";
                    return new QolSecrets();
                }
                return JsonConvert.DeserializeObject<QolSecrets>(json, JsonSettings) ?? new QolSecrets();
            }
            catch (Exception e) when (e is JsonException || e is FormatException || e is System.Security.Cryptography.CryptographicException)
            {
                // Typical cause: the file was copied from another Windows account or PC. Don't crash, ask for keys again.
                LastWarning = "Stored API keys couldn't be read (" + e.GetType().Name + "); please re-enter them.";
                return new QolSecrets();
            }
        }

        public void SaveSecrets(QolSecrets secrets)
        {
            byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(secrets, JsonSettings));
            var file = new JObject
            {
                ["scheme"] = _protector?.Name ?? "plain",
                ["data"] = Convert.ToBase64String(_protector != null ? _protector.Protect(plain) : plain),
            };
            WriteAtomic(Path.Combine(_secretsDir, SecretsFile), file.ToString(Formatting.Indented));
        }

        /// <summary>The key for a slot: environment variable first, then the stored value. Null if none.</summary>
        public string GetApiKey(QolSecrets secrets, string slot)
        {
            string env = _getEnv("MEQOL_" + slot.ToUpperInvariant() + "_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return secrets != null && secrets.ApiKeys.TryGetValue(slot, out string key) && !string.IsNullOrWhiteSpace(key) ? key : null;
        }

        public bool IsKeyFromEnvironment(string slot) =>
            !string.IsNullOrWhiteSpace(_getEnv("MEQOL_" + slot.ToUpperInvariant() + "_API_KEY"));

        static void WriteAtomic(string path, string contents)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
