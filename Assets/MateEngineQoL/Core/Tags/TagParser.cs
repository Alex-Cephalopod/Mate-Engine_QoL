using System.Collections.Generic;
using System.Text;

namespace MateEngineQoL.Tags
{
    public enum ReplyTagKind { Emotion, Action }

    /// <summary>A tag found in a reply: [emotion:happy] or [action:wave]. <see cref="Name"/> is normalised.</summary>
    public readonly struct ReplyTag
    {
        public readonly ReplyTagKind Kind;
        public readonly string Name;

        public ReplyTag(ReplyTagKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public override string ToString() => "[" + (Kind == ReplyTagKind.Emotion ? "emotion" : "action") + ":" + Name + "]";
    }

    /// <summary>
    /// Takes [key:value] tags out of a streamed reply. Text after a '[' is held back until the bracket closes or can't
    /// be a tag any more, so a tag split across chunks never reaches the bubble or the voice. Emotion and action tags
    /// are reported; any other [key:value] tag is dropped silently. Brackets without a colon ([laugh], [1]) are text.
    /// Model output is untrusted: names are reduced to [a-z0-9_] and capped; nothing here acts on them.
    /// One instance per reply.
    /// </summary>
    public sealed class TagParser
    {
        /// <summary>Longest bracket that can be a tag, brackets included; longer ones are released as text.</summary>
        public const int MaxTagLength = 64;
        const int MaxKeyLength = 20;
        public const int MaxNameLength = 40;

        readonly StringBuilder _held = new StringBuilder();
        bool _holding;
        bool _sawColon;
        bool _emittedAny;
        bool _skipSpaces;
        char _last;

        /// <summary>Feeds a piece of reply text; returns the text to show (possibly empty) and adds any tags found.</summary>
        public string Push(string delta, List<ReplyTag> tags)
        {
            var output = new StringBuilder(delta?.Length ?? 0);
            if (string.IsNullOrEmpty(delta)) return "";
            foreach (char c in delta) Feed(c, output, tags);
            return output.ToString();
        }

        /// <summary>Ends the reply: an unclosed bracket is released as text.</summary>
        public string Flush()
        {
            var output = new StringBuilder();
            if (_holding) Release(output);
            return output.ToString();
        }

        /// <summary>The whole text with tags removed (for text that wasn't streamed through a parser).</summary>
        public static string Strip(string text, List<ReplyTag> tags = null)
        {
            var parser = new TagParser();
            var list = tags ?? new List<ReplyTag>();
            return parser.Push(text, list) + parser.Flush();
        }

        void Feed(char c, StringBuilder output, List<ReplyTag> tags)
        {
            if (!_holding)
            {
                if (c == '[')
                {
                    _holding = true;
                    _sawColon = false;
                    _held.Clear().Append(c);
                    return;
                }
                Emit(c, output);
                return;
            }

            if (c == '[')
            {
                // A new bracket: the held one wasn't a tag.
                Release(output);
                Feed(c, output, tags);
                return;
            }
            _held.Append(c);
            if (c == ']')
            {
                _holding = false;
                if (TryReadTag(_held.ToString(), out string key, out string value))
                {
                    ReplyTag? tag = ToTag(key, value);
                    if (tag.HasValue) tags.Add(tag.Value);
                    // Don't leave a double space (or leading space at the start of the reply) where the tag was.
                    _skipSpaces = !_emittedAny || char.IsWhiteSpace(_last);
                }
                else
                {
                    EmitHeld(output);
                }
                return;
            }
            if (!CanContinue(c)) Release(output);
        }

        bool CanContinue(char c)
        {
            if (c == '\r' || c == '\n' || _held.Length >= MaxTagLength) return false;
            if (c == ':')
            {
                if (_sawColon) return true;
                _sawColon = true;
                return _held.Length > 2; // "[:" has no key
            }
            if (_sawColon) return true;
            // Before the colon only a key fits: letters, '_' and spaces.
            return (char.IsLetter(c) || c == '_' || c == ' ') && _held.Length <= MaxKeyLength + 3;
        }

        void Release(StringBuilder output)
        {
            _holding = false;
            EmitHeld(output);
        }

        void EmitHeld(StringBuilder output)
        {
            string held = _held.ToString();
            _held.Clear();
            foreach (char h in held) Emit(h, output);
        }

        void Emit(char c, StringBuilder output)
        {
            if (_skipSpaces)
            {
                // At the start of the reply skip line breaks too; mid-reply only spaces and tabs.
                if (c == ' ' || c == '\t' || (!_emittedAny && (c == '\r' || c == '\n'))) return;
                _skipSpaces = false;
            }
            output.Append(c);
            _emittedAny = true;
            _last = c;
        }

        /// <summary>Splits "[key:value]"; false unless the key is a word and the value isn't empty.</summary>
        static bool TryReadTag(string bracket, out string key, out string value)
        {
            key = value = null;
            if (bracket.Length < 4 || bracket[0] != '[' || bracket[bracket.Length - 1] != ']') return false;
            string inner = bracket.Substring(1, bracket.Length - 2);
            int colon = inner.IndexOf(':');
            if (colon <= 0) return false;
            key = inner.Substring(0, colon).Trim().ToLowerInvariant();
            value = inner.Substring(colon + 1).Trim();
            if (key.Length == 0 || key.Length > MaxKeyLength || value.Length == 0) return false;
            foreach (char k in key)
                if (!char.IsLetter(k) && k != '_' && k != ' ') return false;
            return true;
        }

        static ReplyTag? ToTag(string key, string value)
        {
            ReplyTagKind kind;
            switch (key)
            {
                case "emotion": case "emote": case "mood": case "face": case "expression": case "feeling":
                    kind = ReplyTagKind.Emotion;
                    break;
                case "action": case "gesture": case "anim": case "animation": case "motion":
                    kind = ReplyTagKind.Action;
                    break;
                default:
                    return null;
            }
            string name = NormalizeName(value);
            return name == null ? (ReplyTag?)null : new ReplyTag(kind, name);
        }

        /// <summary>"Look Around!" → "look_around": lower case, words joined by '_', only [a-z0-9_]; null when nothing is left.</summary>
        public static string NormalizeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var sb = new StringBuilder(value.Length);
            bool gap = false;
            foreach (char raw in value)
            {
                char c = char.ToLowerInvariant(raw);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    if (gap && sb.Length > 0) sb.Append('_');
                    gap = false;
                    sb.Append(c);
                    if (sb.Length >= MaxNameLength) break;
                }
                else if (c == ' ' || c == '_' || c == '-' || c == '.')
                {
                    gap = true;
                }
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}
