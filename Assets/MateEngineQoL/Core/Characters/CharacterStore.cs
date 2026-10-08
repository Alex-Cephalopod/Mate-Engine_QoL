using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace MateEngineQoL.Characters
{
    /// <summary>
    /// Reads and writes characters under &lt;BaseDir&gt;/Characters/&lt;id&gt;/character.json. Ids are validated
    /// before they touch a path, so a typed name or a hand-edited file can't escape the folder.
    /// </summary>
    public sealed class CharacterStore
    {
        public const string FolderName = "Characters";
        public const string ProfileFile = "character.json";
        public const string SessionsFolder = "sessions";

        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None, // file contents never choose .NET types (docs/SECURITY.md)
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Formatting = Formatting.Indented,
        };

        public string Root { get; }

        /// <summary>Last unreadable character file seen by <see cref="Load"/> or <see cref="LoadAll"/>, for the log; null if none.</summary>
        public string LastWarning { get; private set; }

        /// <param name="root">The Characters folder itself, e.g. Path.Combine(BaseDir, FolderName).</param>
        public CharacterStore(string root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public string DirectoryOf(string id)
        {
            if (!CharacterIds.IsValid(id)) throw new ArgumentException("Invalid character id: " + id, nameof(id));
            return Path.Combine(Root, id);
        }

        public string SessionsDirectoryOf(string id) => Path.Combine(DirectoryOf(id), SessionsFolder);

        public bool Exists(string id) =>
            CharacterIds.IsValid(id) && File.Exists(Path.Combine(DirectoryOf(id), ProfileFile));

        /// <summary>The character, or null if it doesn't exist or its file is unreadable.</summary>
        public CharacterProfile Load(string id)
        {
            if (!Exists(id)) return null;
            string path = Path.Combine(DirectoryOf(id), ProfileFile);
            try
            {
                CharacterProfile p = JsonConvert.DeserializeObject<CharacterProfile>(File.ReadAllText(path), JsonSettings);
                if (p == null) return null;
                p.Id = id;
                p.Name = p.Name ?? "";
                p.Prompt = p.Prompt ?? "";
                p.Greeting = p.Greeting ?? "";
                return p;
            }
            catch (Exception e) when (e is JsonException || e is IOException)
            {
                LastWarning = path + " is unreadable and was skipped: " + e.Message;
                return null;
            }
        }

        /// <summary>Every readable character, sorted by display name.</summary>
        public List<CharacterProfile> LoadAll()
        {
            var list = new List<CharacterProfile>();
            LastWarning = null;
            if (!Directory.Exists(Root)) return list;
            foreach (string dir in Directory.GetDirectories(Root))
            {
                CharacterProfile p = Load(Path.GetFileName(dir));
                if (p != null) list.Add(p);
            }
            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public void Save(CharacterProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string dir = DirectoryOf(profile.Id);
            Directory.CreateDirectory(dir);
            WriteAtomic(Path.Combine(dir, ProfileFile), JsonConvert.SerializeObject(profile, JsonSettings));
        }

        /// <summary>Creates and saves a new character with an id derived from <paramref name="name"/>, made unique.</summary>
        public CharacterProfile Create(string name, string prompt = "", string greeting = "")
        {
            string baseId = CharacterIds.FromName(name);
            string id = baseId;
            for (int n = 2; Directory.Exists(DirectoryOf(id)); n++) id = baseId + "-" + n;
            var p = new CharacterProfile { Id = id, Name = name ?? "", Prompt = prompt ?? "", Greeting = greeting ?? "" };
            Save(p);
            return p;
        }

        /// <summary>
        /// Looks a character up by what a user typed: exact id, then display name (case-insensitive), then a unique
        /// name or id prefix. Null if nothing or more than one character matches.
        /// </summary>
        public static CharacterProfile Find(IReadOnlyList<CharacterProfile> all, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0 || all == null) return null;

            foreach (CharacterProfile p in all)
                if (p.Id == query) return p;
            foreach (CharacterProfile p in all)
                if (string.Equals(p.DisplayName, query, StringComparison.OrdinalIgnoreCase)) return p;

            CharacterProfile match = null;
            foreach (CharacterProfile p in all)
            {
                if (!p.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                    && !p.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) return null; // ambiguous
                match = p;
            }
            return match;
        }

        internal static void WriteAtomic(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
