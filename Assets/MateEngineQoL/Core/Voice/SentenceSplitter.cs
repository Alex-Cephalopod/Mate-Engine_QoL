using System;
using System.Collections.Generic;
using System.Text;

namespace MateEngineQoL.Voice
{
    /// <summary>
    /// Cuts streamed reply text into sentences for TTS, so speech can start before the reply is finished.
    /// A sentence ends at . ! ? or … followed by whitespace, or at a newline. Text that runs past
    /// <see cref="MaxChars"/> without an end is cut at the last space (or comma) instead.
    /// </summary>
    public sealed class SentenceSplitter
    {
        static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "st", "prof", "sr", "jr", "vs", "etc", "e.g", "i.e", "no",
        };

        readonly StringBuilder _buffer = new StringBuilder();

        public int MaxChars { get; }

        public SentenceSplitter(int maxChars = 220)
        {
            if (maxChars < 20) throw new ArgumentOutOfRangeException(nameof(maxChars));
            MaxChars = maxChars;
        }

        /// <summary>Adds reply text and returns the sentences it completed (possibly none).</summary>
        public List<string> Push(string delta)
        {
            var sentences = new List<string>();
            if (string.IsNullOrEmpty(delta)) return sentences;
            _buffer.Append(delta);

            int start = 0;
            int i = 0;
            while (i < _buffer.Length)
            {
                int end = BoundaryAt(i);
                if (end < 0)
                {
                    i++;
                    if (i - start > MaxChars)
                    {
                        int cut = SoftCut(start, i);
                        Emit(sentences, start, cut);
                        start = i = cut;
                    }
                    continue;
                }
                if (end == int.MaxValue) break; // need more text to decide
                Emit(sentences, start, end);
                start = i = end;
            }

            _buffer.Remove(0, start);
            return sentences;
        }

        /// <summary>Returns whatever is left once the reply has ended (null if nothing), and empties the buffer.</summary>
        public string Flush()
        {
            string rest = Clean(_buffer.ToString());
            _buffer.Clear();
            return rest;
        }

        public void Reset() => _buffer.Clear();

        /// <summary>
        /// -1: no sentence end at <paramref name="i"/>. int.MaxValue: maybe, but the text after it hasn't arrived.
        /// Otherwise the index just past the end (terminators and closing quotes included).
        /// </summary>
        int BoundaryAt(int i)
        {
            char c = _buffer[i];
            if (c == '\n') return i + 1;
            if (c != '.' && c != '!' && c != '?' && c != '…') return -1;

            int j = i + 1;
            while (j < _buffer.Length && IsTerminatorOrCloser(_buffer[j])) j++;
            if (j >= _buffer.Length) return int.MaxValue;
            if (!char.IsWhiteSpace(_buffer[j])) return -1; // 3.5, e.g.x, file.txt
            if (c == '.' && j == i + 1 && EndsWithAbbreviationOrNumber(i)) return -1;
            return j;
        }

        static bool IsTerminatorOrCloser(char c) =>
            c == '.' || c == '!' || c == '?' || c == '…' || c == '"' || c == '\'' || c == '”' || c == '’' || c == ')' || c == ']' || c == '*';

        /// <summary>True for "Dr." and for list numbers like "1." where a period isn't a sentence end.</summary>
        bool EndsWithAbbreviationOrNumber(int dot)
        {
            int k = dot - 1;
            while (k >= 0 && (char.IsLetterOrDigit(_buffer[k]) || _buffer[k] == '.')) k--;
            string word = _buffer.ToString(k + 1, dot - k - 1);
            if (word.Length == 0) return false;
            bool allDigits = true;
            foreach (char ch in word) if (!char.IsDigit(ch)) { allDigits = false; break; }
            return allDigits || Abbreviations.Contains(word);
        }

        int SoftCut(int start, int end)
        {
            for (int k = end - 1; k > start + MaxChars / 2; k--)
                if (_buffer[k] == ',' || _buffer[k] == ';' || _buffer[k] == ':') return k + 1;
            for (int k = end - 1; k > start; k--)
                if (char.IsWhiteSpace(_buffer[k])) return k + 1;
            return end;
        }

        void Emit(List<string> sentences, int start, int end)
        {
            string s = Clean(_buffer.ToString(start, end - start));
            if (s != null) sentences.Add(s);
        }

        static string Clean(string s)
        {
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
