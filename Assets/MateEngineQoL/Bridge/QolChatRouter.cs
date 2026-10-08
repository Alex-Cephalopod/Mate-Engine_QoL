using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLMUnity;
using MateEngineQoL.AI;
using MateEngineQoL.Characters;
using UnityEngine;
using ChatMessage = MateEngineQoL.AI.ChatMessage; // LLMUnity has its own ChatMessage and ChatRequest
using ChatRequest = MateEngineQoL.AI.ChatRequest;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Where ChatBot sends a message. Slash commands (/char, /chars, /new) are handled here and never reach a model.
    /// With the "upstream-local" provider it calls LLMCharacter.Chat after trimming its chat to the context budget.
    /// Otherwise it streams from the configured remote provider. Finished exchanges go to the active character's
    /// session log (CharacterManager); if characters failed to load, upstream's prompt, history and save are used.
    /// </summary>
    public static class QolChatRouter
    {
        static CancellationTokenSource _cts;

        /// <summary>Raised on the main thread for every piece of reply text, for any provider (voice pipeline hook).</summary>
        public static event Action<string> ReplyDelta;

        /// <summary>Raised on the main thread with the full reply once it ends (not raised on errors or cancel).</summary>
        public static event Action<string> ReplyCompleted;

        /// <summary>True when chat goes to a QoL provider, so the local model's warm-up doesn't gate input.</summary>
        public static bool UsesRemoteProvider
        {
            get
            {
                QolServices.EnsureInitialized();
                return QolServices.Registry.Chat != null || QolServices.Registry.ChatError != null;
            }
        }

        /// <param name="onPartial">Receives the cumulative reply text, like LLMCharacter's callback.</param>
        public static void Send(LLMCharacter llm, string message, Action<string> onPartial, Action onDone)
        {
            QolServices.EnsureInitialized();
            Cancel();

            if (CharacterManager.IsReady && ChatCommand.TryParse(message, out ChatCommand command))
            {
                RunCommand(command, onPartial, onDone);
                return;
            }

            _cts = new CancellationTokenSource();
            int generation = CharacterManager.IsReady ? CharacterManager.Instance.Generation : -1;

            IChatProvider provider = QolServices.Registry.Chat;
            if (provider == null && QolServices.Registry.ChatError == null)
            {
                SendUpstream(llm, message, generation, onPartial, onDone);
                return;
            }

            if (provider == null)
            {
                onPartial("[!] " + QolServices.Registry.ChatError);
                onDone();
                return;
            }

            _ = SendRemoteAsync(provider, llm, message, generation, onPartial, onDone, _cts.Token);
        }

        public static void Cancel()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        static void RunCommand(ChatCommand command, Action<string> onPartial, Action onDone)
        {
            CharacterManager characters = CharacterManager.Instance;
            switch (command.Kind)
            {
                case ChatCommandKind.SwitchCharacter:
                    CharacterProfile target = characters.Find(command.Argument);
                    if (target == null)
                    {
                        onPartial(QolText.Format("QOL_CMD_NO_CHARACTER", command.Argument, CharacterNames(characters)));
                        onDone();
                        return;
                    }
                    onDone(); // finish the command's bubbles before the switch re-renders the chat
                    characters.Switch(target.Id);
                    return;

                case ChatCommandKind.ListCharacters:
                    onPartial(QolText.Format("QOL_CMD_CHARACTERS", characters.Active.DisplayName, CharacterNames(characters)));
                    onDone();
                    return;

                case ChatCommandKind.NewSession:
                    onDone();
                    characters.StartNewSession();
                    return;
            }
        }

        static string CharacterNames(CharacterManager characters)
        {
            var names = new List<string>();
            foreach (CharacterProfile p in characters.All()) names.Add(p.DisplayName);
            return string.Join(", ", names);
        }

        static void SendUpstream(LLMCharacter llm, string message, int generation, Action<string> onPartial, Action onDone)
        {
            if (CharacterManager.IsReady)
            {
                // LLMCharacter sends its whole chat list; cut it down to the context budget first.
                int contextSize = llm.llm != null ? llm.llm.contextSize : 0;
                LoadContextInto(llm, CharacterManager.Instance.BuildContext(message, contextSize));
            }
            int countBefore = llm.chat.Count;

            int emitted = 0;
            string last = "";
            _ = llm.Chat(message,
                partial =>
                {
                    partial = partial ?? "";
                    // LLMUnity reports cumulative text; turn it into deltas for listeners.
                    if (partial.Length > emitted && partial.StartsWith(last, StringComparison.Ordinal))
                        ReplyDelta?.Invoke(partial.Substring(emitted));
                    emitted = partial.Length;
                    last = partial;
                    onPartial(partial);
                },
                () =>
                {
                    // LLMCharacter appends the exchange to its chat when it has a result.
                    if (llm.chat.Count >= countBefore + 2) Record(message, llm.chat[llm.chat.Count - 1].content, generation);
                    ReplyCompleted?.Invoke(last);
                    onDone();
                });
        }

        /// <summary>Replaces LLMCharacter's chat with the built context, minus the new message (Chat adds that).</summary>
        static void LoadContextInto(LLMCharacter llm, ContextResult context)
        {
            string system = context.SystemText;
            if (llm.prompt != system) llm.SetPrompt(system, true);
            else llm.ClearChat();
            int from = system.Length > 0 ? 1 : 0;
            for (int i = from; i < context.Messages.Count - 1; i++)
            {
                ChatMessage m = context.Messages[i];
                llm.AddMessage(m.Role == ChatMessage.User ? llm.playerName : llm.AIName, m.Content);
            }
        }

        /// <summary>Saves a finished exchange, unless the chat was reset (character switch, /new) while it ran.</summary>
        static void Record(string message, string reply, int generation)
        {
            if (!CharacterManager.IsReady) return;
            CharacterManager characters = CharacterManager.Instance;
            if (characters.Generation == generation) characters.RecordExchange(message, reply);
            else characters.ResyncLlm(); // a stale local reply was appended to the new conversation's chat
        }

        static async Task SendRemoteAsync(IChatProvider provider, LLMCharacter llm, string message, int generation,
            Action<string> onPartial, Action onDone, CancellationToken ct)
        {
            var chat = QolServices.Settings.Chat;
            ContextResult context = CharacterManager.IsReady
                ? CharacterManager.Instance.BuildContext(message)
                : ContextBuilder.Build(new ContextInput
                {
                    Prompt = llm.prompt,
                    History = FromUpstreamChat(llm),
                    NewMessage = message,
                }, QolServices.Settings.Context);

            var request = new ChatRequest
            {
                Messages = context.Messages,
                Model = chat.Model,
                Temperature = chat.Temperature,
                MaxTokens = chat.MaxTokens,
            };

            var reply = new StringBuilder();
            bool completed = false;
            try
            {
                // Awaited on the main thread: Unity's SynchronizationContext resumes here, so callbacks are safe.
                await foreach (string delta in provider.StreamReplyAsync(request, ct))
                {
                    reply.Append(delta);
                    ReplyDelta?.Invoke(delta);
                    onPartial(reply.ToString());
                }
                completed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // User cancelled or sent a new message; keep whatever arrived, don't save it.
            }
            catch (OperationCanceledException)
            {
                onPartial(WithError(reply, "The provider stopped responding."));
            }
            catch (ProviderException e)
            {
                Debug.LogWarning("[QoL Chat] " + e.Message);
                onPartial(WithError(reply, e.Message));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                onPartial(WithError(reply, "Unexpected error, see Player.log."));
            }

            bool current = !CharacterManager.IsReady || CharacterManager.Instance.Generation == generation;
            if (completed && reply.Length > 0 && current)
            {
                string text = reply.ToString().Trim();
                llm.AddPlayerMessage(message);
                llm.AddAIMessage(text);
                if (CharacterManager.IsReady) CharacterManager.Instance.RecordExchange(message, text);
                else SaveHistory(llm);
                ReplyCompleted?.Invoke(text);
            }
            onDone();
        }

        /// <summary>Upstream's history (roles by position after the system prompt) as session messages.</summary>
        static List<SessionMessage> FromUpstreamChat(LLMCharacter llm)
        {
            var list = new List<SessionMessage>();
            for (int i = 1; i < llm.chat.Count; i++)
                list.Add(new SessionMessage(i % 2 == 1 ? ChatMessage.User : ChatMessage.Assistant, llm.chat[i].content, DateTime.UtcNow));
            return list;
        }

        static string WithError(StringBuilder reply, string error) =>
            (reply.Length > 0 ? reply + "\n\n" : "") + "[!] " + error;

        /// <summary>
        /// Fallback when characters are unavailable. Writes the history JSON exactly like LLMCharacter.Save, without its KV-cache step, which needs the
        /// local model running and would be stale for remote replies anyway.
        /// </summary>
        static void SaveHistory(LLMCharacter llm)
        {
            if (string.IsNullOrEmpty(llm.save) || llm.chat.Count < 1) return;
            try
            {
                string path = llm.GetJsonSavePath(llm.save);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string json = JsonUtility.ToJson(new ChatListWrapper { chat = llm.chat.GetRange(1, llm.chat.Count - 1) });
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[QoL Chat] Could not save chat history: " + e.Message);
            }
        }
    }
}
