using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MateEngineQoL.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngineQoL.Characters
{
    /// <summary>
    /// First run of the character system: turns upstream's single prompt file and ZomeAI history into the
    /// "default" character and its first session. Upstream files are only read, never changed.
    /// </summary>
    public static class CharacterSeeder
    {
        public const string DefaultName = "Zome";

        /// <summary>
        /// Creates the default character when no character exists yet. Returns true if it seeded.
        /// </summary>
        /// <param name="prompt">Contents of upstream's prompt file (or the scene's default prompt).</param>
        /// <param name="upstreamHistoryJson">Contents of upstream's ZomeAI.json, or null if there is none.</param>
        /// <param name="importTimeUtc">Timestamp for imported messages; upstream history has none.</param>
        public static bool SeedIfEmpty(CharacterStore store, string prompt, string upstreamHistoryJson, DateTime importTimeUtc)
        {
            if (store.LoadAll().Count > 0) return false;

            var profile = new CharacterProfile { Id = CharacterIds.Default, Name = NameFromPrompt(prompt), Prompt = prompt ?? "" };
            store.Save(profile);

            List<SessionMessage> imported = ParseUpstreamHistory(upstreamHistoryJson, importTimeUtc);
            if (imported.Count > 0)
            {
                var log = new SessionLog(store.SessionsDirectoryOf(profile.Id), () => importTimeUtc);
                log.Append(log.Create(), imported);
            }
            return true;
        }

        static readonly Regex NameLine = new Regex(@"^\s*[-*]?\s*name\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>
        /// The character's name from a "Name: ..." line, as upstream's default prompt and most character cards have
        /// ("- Name: Zome", "Name: Roxanne Wolf, usually called Roxy" → "Roxanne Wolf"); <see cref="DefaultName"/> otherwise.
        /// </summary>
        public static string NameFromPrompt(string prompt)
        {
            Match m = NameLine.Match(prompt ?? "");
            if (!m.Success) return DefaultName;
            string name = m.Groups[1].Value;
            int cut = name.IndexOfAny(new[] { ',', '(', ';', '/' });
            if (cut >= 0) name = name.Substring(0, cut);
            name = name.Trim().TrimEnd('.');
            return name.Length > 0 && name.Length <= 40 ? name : DefaultName;
        }

        /// <summary>
        /// Reads upstream's history format, {"chat":[{"role":"","content":"..."}]} without the system prompt. Upstream
        /// leaves roles empty and tells speakers apart by position: user, assistant, user, ...
        /// Returns an empty list for missing or unreadable input.
        /// </summary>
        public static List<SessionMessage> ParseUpstreamHistory(string json, DateTime timeUtc)
        {
            var list = new List<SessionMessage>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                if (!(JObject.Parse(json)["chat"] is JArray chat)) return list;
                for (int i = 0; i < chat.Count; i++)
                {
                    string content = (string)chat[i]?["content"] ?? "";
                    list.Add(new SessionMessage(i % 2 == 0 ? ChatMessage.User : ChatMessage.Assistant, content, timeUtc));
                }
            }
            catch (JsonException)
            {
                list.Clear();
            }
            return list;
        }
    }
}
