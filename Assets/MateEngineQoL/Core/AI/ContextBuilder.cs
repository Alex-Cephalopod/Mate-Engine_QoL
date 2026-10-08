using System;
using System.Collections.Generic;
using System.Text;
using MateEngineQoL.Characters;
using MateEngineQoL.Settings;

namespace MateEngineQoL.AI
{
    public sealed class ContextInput
    {
        /// <summary>The character prompt. Never trimmed; if it alone exceeds the budget, <see cref="ContextResult.PromptOverBudget"/> is set.</summary>
        public string Prompt = "";
        /// <summary>Long-term facts, most important first (Phase 8b). Trimmed from the end to the facts budget.</summary>
        public IReadOnlyList<string> Facts;
        /// <summary>Rolling summary of older turns (Phase 8b). Trimmed from the start to the summary budget, keeping the newest part.</summary>
        public string Summary = "";
        /// <summary>The session so far, oldest first.</summary>
        public IReadOnlyList<SessionMessage> History;
        public string NewMessage = "";
    }

    public sealed class ContextResult
    {
        /// <summary>System message (prompt + facts + summary) if non-empty, recent turns, then the new user message.</summary>
        public List<ChatMessage> Messages = new List<ChatMessage>();
        public int EstimatedTokens;
        /// <summary>How many history messages made it in.</summary>
        public int HistoryIncluded;
        public bool PromptOverBudget;

        public string SystemText => Messages.Count > 0 && Messages[0].Role == ChatMessage.System ? Messages[0].Content : "";
    }

    /// <summary>
    /// Builds the model's context from layers, each with its own token budget: prompt, facts, summary, recent turns.
    /// Recent turns get whatever the other layers leave of the total, newest first, in whole user/assistant pairs.
    /// Tokens are estimated (no tokenizer for remote models); the estimate errs high.
    /// </summary>
    public static class ContextBuilder
    {
        /// <summary>Per-message overhead for role markers and separators in chat templates.</summary>
        public const int MessageOverhead = 4;

        /// <summary>About four characters per token for English, rounded up; CJK text is closer to one per character, so this underestimates it.</summary>
        public static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

        public static ContextResult Build(ContextInput input, ContextSettings budget, Func<string, int> countTokens = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            budget = budget ?? new ContextSettings();
            countTokens = countTokens ?? EstimateTokens;
            var result = new ContextResult();

            // Layer 1-3: system text.
            string prompt = (input.Prompt ?? "").Trim();
            string facts = TrimFacts(input.Facts, Math.Max(0, budget.FactsTokens), countTokens);
            string summary = TrimSummary(input.Summary, Math.Max(0, budget.SummaryTokens), countTokens);

            var system = new StringBuilder(prompt);
            if (facts.Length > 0) Section(system, "Things you remember about the user:\n" + facts);
            if (summary.Length > 0) Section(system, "Summary of the earlier conversation:\n" + summary);
            string systemText = system.ToString();

            int available = Math.Max(0, budget.TotalTokens - Math.Max(0, budget.ReplyReserveTokens));
            int used = 0;
            if (systemText.Length > 0)
            {
                result.Messages.Add(new ChatMessage(ChatMessage.System, systemText));
                used += countTokens(systemText) + MessageOverhead;
            }
            result.PromptOverBudget = countTokens(prompt) + MessageOverhead > available;

            string newMessage = input.NewMessage ?? "";
            used += countTokens(newMessage) + MessageOverhead;

            // Layer 4: recent turns, newest pair first, within what's left and the message-count cap.
            // Alternating() starts on a user turn, so pairs are (even, odd) indices. A trailing user message without
            // a reply can't pair up and is left out.
            List<SessionMessage> history = SessionLog.Alternating(input.History);
            int maxMessages = Math.Max(0, budget.MaxHistoryMessages);
            int end = history.Count;
            if (end > 0 && history[end - 1].Role == ChatMessage.User) end--;
            int start = end;
            while (start >= 2 && end - (start - 2) <= maxMessages)
            {
                int cost = countTokens(history[start - 2].Text) + countTokens(history[start - 1].Text) + 2 * MessageOverhead;
                if (used + cost > available) break;
                used += cost;
                start -= 2;
            }

            for (int k = start; k < end; k++)
                result.Messages.Add(new ChatMessage(history[k].Role, history[k].Text));
            result.HistoryIncluded = end - start;

            result.Messages.Add(new ChatMessage(ChatMessage.User, newMessage));
            result.EstimatedTokens = used;
            return result;
        }

        static void Section(StringBuilder sb, string text)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(text);
        }

        static string TrimFacts(IReadOnlyList<string> facts, int budget, Func<string, int> count)
        {
            if (facts == null || budget <= 0) return "";
            var sb = new StringBuilder();
            int used = 0;
            foreach (string f in facts)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                string line = "- " + f.Trim();
                int cost = count(line);
                if (used + cost > budget) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                used += cost;
            }
            return sb.ToString();
        }

        static string TrimSummary(string summary, int budget, Func<string, int> count)
        {
            summary = (summary ?? "").Trim();
            if (summary.Length == 0 || budget <= 0) return "";
            if (count(summary) <= budget) return summary;

            // Keep the newest part: drop leading words until it fits.
            string[] words = summary.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int from = 0;
            string kept = summary;
            while (from < words.Length && count(kept) > budget)
            {
                from++;
                kept = "..." + string.Join(" ", words, from, words.Length - from);
            }
            return from < words.Length ? kept : "";
        }
    }
}
