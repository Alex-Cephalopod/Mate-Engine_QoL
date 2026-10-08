using System.Text;
using System.Text.RegularExpressions;

namespace MateEngineQoL.Characters
{
    /// <summary>
    /// Contents of Characters/&lt;id&gt;/character.json. The folder name is the id; the file's own copy is ignored
    /// on load so a renamed or copied folder can't point somewhere else. New fields need defaults that keep old
    /// files working. Never holds API keys (docs/SECURITY.md).
    /// </summary>
    public sealed class CharacterProfile
    {
        public int Version = 1;
        public string Id = "";
        public string Name = "";
        /// <summary>System prompt: who the character is.</summary>
        public string Prompt = "";
        /// <summary>Shown as the character's first bubble in a new, empty session. Not sent to the model.</summary>
        public string Greeting = "";

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();
    }

    public static class CharacterIds
    {
        public const string Default = "default";

        static readonly Regex Valid = new Regex("^[a-z0-9][a-z0-9_-]{0,63}$");

        /// <summary>True for ids that are safe as a folder name: lowercase ASCII, digits, '-' and '_', 64 chars max.</summary>
        public static bool IsValid(string id) => id != null && Valid.IsMatch(id);

        /// <summary>A valid id derived from a display name, e.g. "Miku Hatsune!" → "miku-hatsune".</summary>
        public static string FromName(string name)
        {
            var sb = new StringBuilder();
            bool dash = false;
            foreach (char c in (name ?? "").Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    dash = false;
                }
                else if (sb.Length > 0 && !dash)
                {
                    sb.Append('-');
                    dash = true;
                }
                if (sb.Length >= 48) break;
            }
            string id = sb.ToString().TrimEnd('-');
            return id.Length > 0 ? id : "character";
        }
    }
}
