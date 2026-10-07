using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLMUnity;
using MateEngineQoL.AI;
using UnityEngine;
using ChatRequest = MateEngineQoL.AI.ChatRequest; // LLMUnity has its own ChatRequest

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Where ChatBot sends a message. With the "upstream-local" provider it calls LLMCharacter.Chat exactly as
    /// upstream did. Otherwise it streams from the configured remote provider, using LLMCharacter's prompt and
    /// history as the source of truth and writing the reply back in upstream's save format.
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
            _cts = new CancellationTokenSource();

            IChatProvider provider = QolServices.Registry.Chat;
            if (provider == null && QolServices.Registry.ChatError == null)
            {
                SendUpstream(llm, message, onPartial, onDone);
                return;
            }

            if (provider == null)
            {
                onPartial("[!] " + QolServices.Registry.ChatError);
                onDone();
                return;
            }

            _ = SendRemoteAsync(provider, llm, message, onPartial, onDone, _cts.Token);
        }

        public static void Cancel()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        static void SendUpstream(LLMCharacter llm, string message, Action<string> onPartial, Action onDone)
        {
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
                    ReplyCompleted?.Invoke(last);
                    onDone();
                });
        }

        static async Task SendRemoteAsync(IChatProvider provider, LLMCharacter llm, string message,
            Action<string> onPartial, Action onDone, CancellationToken ct)
        {
            var chat = QolServices.Settings.Chat;
            var turns = new List<string>();
            for (int i = 1; i < llm.chat.Count; i++) turns.Add(llm.chat[i].content); // chat[0] is the system prompt

            var request = new ChatRequest
            {
                Messages = HistoryMapper.Build(llm.prompt, turns, message, chat.HistoryMessages),
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

            if (completed && reply.Length > 0)
            {
                string text = reply.ToString().Trim();
                llm.AddPlayerMessage(message);
                llm.AddAIMessage(text);
                SaveHistory(llm);
                ReplyCompleted?.Invoke(text);
            }
            onDone();
        }

        static string WithError(StringBuilder reply, string error) =>
            (reply.Length > 0 ? reply + "\n\n" : "") + "[!] " + error;

        /// <summary>
        /// Writes the history JSON exactly like LLMCharacter.Save, without its KV-cache step, which needs the
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
