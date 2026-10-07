using System.Collections.Generic;

namespace MateEngineQoL.AI
{
    /// <summary>
    /// Converts upstream's chat history into a request. Upstream stores every role as an empty string and
    /// tells speakers apart by position (after the system prompt: user, assistant, user, ...), the same way
    /// ChatBot.ShowLoadedMessages renders bubbles.
    /// </summary>
    public static class HistoryMapper
    {
        /// <param name="systemPrompt">The character prompt; skipped if empty.</param>
        /// <param name="turns">History contents after the system prompt, oldest first, alternating user/assistant.</param>
        /// <param name="newUserMessage">The message being sent now.</param>
        /// <param name="maxHistoryMessages">Most recent history entries to include (rounded down to start on a user turn).</param>
        public static List<ChatMessage> Build(string systemPrompt, IReadOnlyList<string> turns, string newUserMessage, int maxHistoryMessages)
        {
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new ChatMessage(ChatMessage.System, systemPrompt));

            int count = turns?.Count ?? 0;
            int take = maxHistoryMessages < 0 ? 0 : (maxHistoryMessages < count ? maxHistoryMessages : count);
            int start = count - take;
            if (start % 2 == 1) start++; // keep user/assistant pairs aligned: index 0, 2, 4... are user turns

            for (int i = start; i < count; i++)
                messages.Add(new ChatMessage(i % 2 == 0 ? ChatMessage.User : ChatMessage.Assistant, turns[i] ?? ""));

            messages.Add(new ChatMessage(ChatMessage.User, newUserMessage ?? ""));
            return messages;
        }
    }
}
