using System.Text;
using System.Text.RegularExpressions;

namespace MateEngineQoL.Voice
{
    /// <summary>
    /// Turns a reply sentence into text worth speaking: drops *roleplay actions* (optionally, and across sentences),
    /// [tags], &lt;rich text&gt;, URLs, markdown symbols and emoji. Returns null when nothing speakable is left.
    /// One instance per reply, since an action can open in one sentence and close in the next.
    /// </summary>
    public sealed class SpeechFilter
    {
        static readonly Regex Url = new Regex(@"\bhttps?://\S+|\bwww\.\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Tag = new Regex(@"\[[A-Za-z_]+(:[^\]\r\n]{0,40})?\]|<[^<>\r\n]{1,60}>", RegexOptions.CultureInvariant);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);

        readonly bool _skipActions;
        bool _inAction;

        public SpeechFilter(bool skipActions) => _skipActions = skipActions;

        public void Reset() => _inAction = false;

        public string Filter(string sentence)
        {
            if (string.IsNullOrEmpty(sentence)) return null;
            string s = Tag.Replace(Url.Replace(sentence, " "), " ");

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '*')
                {
                    if (_skipActions) _inAction = !_inAction;
                    continue;
                }
                if (_inAction) continue;
                if (char.IsSurrogate(c)) continue; // emoji and other astral symbols
                if (c == '_' || c == '~' || c == '#' || c == '`' || c == '>' || c == '|') { sb.Append(' '); continue; }
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherSymbol) continue;
                sb.Append(c);
            }

            string result = Spaces.Replace(sb.ToString(), " ").Trim();
            foreach (char c in result)
                if (char.IsLetterOrDigit(c)) return result;
            return null;
        }
    }
}
