using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MateEngineQoL.Voice
{
    /// <summary>
    /// Turns a reply sentence into text worth speaking: drops *roleplay actions* (optionally, and across sentences),
    /// [tags], &lt;rich text&gt;, URLs, markdown symbols and emoji. Returns null when nothing speakable is left.
    /// With sound tags on (Chatterbox Turbo), a skipped action that names a sound (*chuckles*, *sighs*) becomes the
    /// server's tag ([chuckle], [sigh]) and those tags written by the model are kept; silent actions (*winks*) stay silent.
    /// One instance per reply, since an action can open in one sentence and close in the next.
    /// </summary>
    public sealed class SpeechFilter
    {
        /// <summary>The sound tags Chatterbox Turbo plays instead of reading aloud.</summary>
        public static readonly string[] SoundTagNames = { "laugh", "chuckle", "sigh", "gasp", "cough", "clear throat", "sniff", "groan", "shush" };

        static readonly Regex Url = new Regex(@"\bhttps?://\S+|\bwww\.\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Tag = new Regex(@"\[[A-Za-z_]+(?: [A-Za-z_]+)?(:[^\]\r\n]{0,40})?\]|<[^<>\r\n]{1,60}>", RegexOptions.CultureInvariant);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);

        /// <summary>Verbs in an action, each with the tag it plays. The earliest match in the action wins.</summary>
        static readonly Regex SoundAction = new Regex(
            @"\b(?:(?<chuckle>chuckl\w*)|(?<laugh>laugh\w*|giggl\w*|snicker\w*|cackl\w*)|(?<sigh>sigh(?:s|ed|ing)?\b)|(?<gasp>gasp\w*)" +
            @"|(?<clear>clear\w*\s+(?:\w+\s+)?throat)|(?<cough>cough\w*)|(?<sniff>snif\w*)|(?<groan>groan\w*)|(?<shush>shh+\b|shush\w*))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        readonly bool _skipActions;
        readonly bool _soundTags;
        readonly StringBuilder _action = new StringBuilder();
        bool _inAction;

        public SpeechFilter(bool skipActions, bool soundTags = false)
        {
            _skipActions = skipActions;
            _soundTags = soundTags;
        }

        public void Reset()
        {
            _inAction = false;
            _action.Clear();
        }

        public string Filter(string sentence)
        {
            if (string.IsNullOrEmpty(sentence)) return null;
            string s = Tag.Replace(Url.Replace(sentence, " "), m => KeepTag(m.Value) ?? " ");

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '*')
                {
                    if (!_skipActions) continue;
                    _inAction = !_inAction;
                    if (_inAction) _action.Clear();
                    else if (_soundTags)
                    {
                        string tag = SoundTagFor(_action.ToString());
                        if (tag != null) sb.Append(' ').Append(tag).Append(' ');
                    }
                    continue;
                }
                if (_inAction)
                {
                    if (_soundTags && _action.Length < 200) _action.Append(c);
                    continue;
                }
                if (char.IsSurrogate(c)) continue; // emoji and other astral symbols
                if (c == '_' || c == '~' || c == '#' || c == '`' || c == '>' || c == '|') { sb.Append(' '); continue; }
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherSymbol) continue;
                sb.Append(c);
            }
            if (_inAction && _soundTags) _action.Append(' '); // the action goes on in the next sentence

            string result = Spaces.Replace(sb.ToString(), " ").Trim();
            foreach (char c in result)
                if (char.IsLetterOrDigit(c)) return result;
            return null;
        }

        /// <summary>The Turbo tag for an action's text ("chuckles softly" → "[chuckle]"), or null for a silent action.</summary>
        public static string SoundTagFor(string action)
        {
            if (string.IsNullOrEmpty(action)) return null;
            Match m = SoundAction.Match(action);
            if (!m.Success) return null;
            if (m.Groups["chuckle"].Success) return "[chuckle]";
            if (m.Groups["laugh"].Success) return "[laugh]";
            if (m.Groups["sigh"].Success) return "[sigh]";
            if (m.Groups["gasp"].Success) return "[gasp]";
            if (m.Groups["clear"].Success) return "[clear throat]";
            if (m.Groups["cough"].Success) return "[cough]";
            if (m.Groups["sniff"].Success) return "[sniff]";
            if (m.Groups["groan"].Success) return "[groan]";
            return "[shush]";
        }

        /// <summary>A model-written [tag] that Turbo plays, normalised; null when the tag should be dropped.</summary>
        string KeepTag(string tag)
        {
            if (!_soundTags || tag[0] != '[') return null;
            string name = Spaces.Replace(tag.Substring(1, tag.Length - 2), " ").Trim();
            foreach (string known in SoundTagNames)
                if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) return "[" + known + "]";
            return null;
        }
    }
}
