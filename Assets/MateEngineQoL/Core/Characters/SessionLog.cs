using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MateEngineQoL.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngineQoL.Characters
{
    public sealed class SessionMessage
    {
        /// <summary><see cref="ChatMessage.User"/> or <see cref="ChatMessage.Assistant"/>.</summary>
        public string Role;
        public string Text;
        public DateTime TimeUtc;

        public SessionMessage(string role, string text, DateTime timeUtc)
        {
            Role = role;
            Text = text ?? "";
            TimeUtc = timeUtc;
        }
    }

    /// <summary>
    /// One character's conversation log: Characters/&lt;id&gt;/sessions/&lt;session-id&gt;.jsonl, one JSON object per
    /// line ({"t","role","text"}), appended as messages land. Session ids are UTC timestamps (yyyyMMdd-HHmmss), so
    /// sorting by name sorts by start time. A torn last line (crash mid-write) is skipped on read.
    /// </summary>
    public sealed class SessionLog
    {
        public const string Extension = ".jsonl";

        static readonly Regex ValidId = new Regex("^[0-9]{8}-[0-9]{6}(-[0-9]{1,4})?$");

        readonly Func<DateTime> _utcNow;

        public string Directory { get; }

        /// <summary>Lines skipped by the last <see cref="Read"/> (unreadable or unknown role).</summary>
        public int LastSkipped { get; private set; }

        public SessionLog(string sessionsDirectory, Func<DateTime> utcNow = null)
        {
            Directory = sessionsDirectory ?? throw new ArgumentNullException(nameof(sessionsDirectory));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public static bool IsValidId(string id) => id != null && ValidId.IsMatch(id);

        string PathOf(string sessionId)
        {
            if (!IsValidId(sessionId)) throw new ArgumentException("Invalid session id: " + sessionId, nameof(sessionId));
            return Path.Combine(Directory, sessionId + Extension);
        }

        /// <summary>Session ids, oldest first.</summary>
        public List<string> List()
        {
            var ids = new List<string>();
            if (!System.IO.Directory.Exists(Directory)) return ids;
            foreach (string file in System.IO.Directory.GetFiles(Directory, "*" + Extension))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (IsValidId(id)) ids.Add(id);
            }
            ids.Sort(CompareIds);
            return ids;
        }

        /// <summary>The newest session id, or null if there are none.</summary>
        public string Latest()
        {
            List<string> ids = List();
            return ids.Count > 0 ? ids[ids.Count - 1] : null;
        }

        /// <summary>Creates an empty session file and returns its id.</summary>
        public string Create()
        {
            System.IO.Directory.CreateDirectory(Directory);
            string baseId = _utcNow().ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string id = baseId;
            for (int n = 2; File.Exists(PathOf(id)); n++) id = baseId + "-" + n;
            File.WriteAllText(PathOf(id), "");
            return id;
        }

        public void Append(string sessionId, SessionMessage message) => Append(sessionId, new[] { message });

        public void Append(string sessionId, IEnumerable<SessionMessage> messages)
        {
            var sb = new StringBuilder();
            foreach (SessionMessage m in messages)
            {
                var line = new JObject
                {
                    ["t"] = m.TimeUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["role"] = m.Role,
                    ["text"] = m.Text ?? "",
                };
                sb.Append(line.ToString(Formatting.None)).Append('\n');
            }
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(PathOf(sessionId), sb.ToString(), new UTF8Encoding(false));
        }

        public List<SessionMessage> Read(string sessionId)
        {
            var list = new List<SessionMessage>();
            LastSkipped = 0;
            string path = PathOf(sessionId);
            if (!File.Exists(path)) return list;

            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    JObject o = JObject.Parse(raw);
                    string role = (string)o["role"];
                    if (role != ChatMessage.User && role != ChatMessage.Assistant) { LastSkipped++; continue; }
                    DateTime t = DateTime.TryParse((string)o["t"], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
                        ? parsed : DateTime.MinValue;
                    list.Add(new SessionMessage(role, (string)o["text"] ?? "", t));
                }
                catch (JsonException)
                {
                    LastSkipped++;
                }
            }
            return list;
        }

        /// <summary>Deletes every session of this character.</summary>
        public void DeleteAll()
        {
            foreach (string id in List()) File.Delete(PathOf(id));
        }

        static int CompareIds(string a, string b)
        {
            // "20261007-120000-10" must sort after "...-2": compare the timestamp, then the numeric suffix.
            int c = string.CompareOrdinal(a.Substring(0, 15), b.Substring(0, 15));
            return c != 0 ? c : Suffix(a).CompareTo(Suffix(b));
        }

        static int Suffix(string id) => id.Length > 16 ? int.Parse(id.Substring(16), CultureInfo.InvariantCulture) : 1;

        /// <summary>
        /// Makes a message list strictly alternate user/assistant starting with a user turn, which upstream's
        /// position-based history and some local chat templates rely on: a leading assistant message is dropped and
        /// consecutive messages from the same speaker are merged.
        /// </summary>
        public static List<SessionMessage> Alternating(IReadOnlyList<SessionMessage> messages)
        {
            var result = new List<SessionMessage>();
            if (messages == null) return result;
            foreach (SessionMessage m in messages)
            {
                if (result.Count == 0 && m.Role != ChatMessage.User) continue;
                if (result.Count > 0 && result[result.Count - 1].Role == m.Role)
                {
                    SessionMessage last = result[result.Count - 1];
                    result[result.Count - 1] = new SessionMessage(last.Role, last.Text + "\n\n" + m.Text, m.TimeUtc);
                    continue;
                }
                result.Add(m);
            }
            return result;
        }
    }
}
