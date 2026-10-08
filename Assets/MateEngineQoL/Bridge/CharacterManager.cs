using System;
using System.Collections.Generic;
using System.IO;
using LLMUnity;
using LLMUnitySamples;
using MateEngineQoL.AI;
using MateEngineQoL.Characters;
using MateEngineQoL.Settings;
using UnityEngine;
using ChatMessage = MateEngineQoL.AI.ChatMessage; // LLMUnity has its own ChatMessage

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Owns the active character and its session. Characters live in &lt;BaseDir&gt;/Characters/&lt;id&gt;/ (per
    /// instance); each message is appended to sessions/&lt;session-id&gt;.jsonl as it lands, and the newest session is
    /// resumed on launch. LLMCharacter keeps working as upstream's in-memory chat, but its own ZomeAI save is turned
    /// off: the session log is the only store.
    /// </summary>
    // Runs after every other Start in the first frame (LLMCharacter.Start applies upstream's prompt file, ChatBot.Start
    // renders bubbles), so applying the character here wins without editing those Start methods.
    [DefaultExecutionOrder(10000)]
    public sealed class CharacterManager : MonoBehaviour
    {
        public static CharacterManager Instance { get; private set; }

        /// <summary>Raised after a switch (or the first load) with the new active character.</summary>
        public static event Action<CharacterProfile> CharacterChanged;

        CharacterStore _store;
        SessionLog _log;
        LLMCharacter _llm;
        readonly List<SessionMessage> _history = new List<SessionMessage>();

        public CharacterProfile Active { get; private set; }
        public string SessionId { get; private set; }
        public IReadOnlyList<SessionMessage> History => _history;

        public static bool IsReady => Instance != null && Instance.Active != null;

        /// <summary>Greeting of the active character, or null. ChatBot shows it in an empty session.</summary>
        public static string ActiveGreeting => IsReady ? Instance.Active.Greeting : null;

        /// <summary>
        /// Changes whenever the chat is reset (switch, new session, delete). A reply that started under an older
        /// generation belongs to a conversation that is gone and must not be recorded.
        /// </summary>
        public int Generation { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Instance != null) return;
            var go = new GameObject("QoL Characters");
            DontDestroyOnLoad(go);
            go.AddComponent<CharacterManager>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            // LLMCharacter sits in the chat window, which may first open after startup; its Awake and Start would then
            // clear the chat and load upstream's prompt file over the active character.
            LLMCharacter.QolLoadCharacter = OnLlmStarting;
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            LLMCharacter.QolLoadCharacter = null;
        }

        static bool OnLlmStarting(LLMCharacter llm)
        {
            if (!IsReady || llm != Instance._llm) return false;
            Instance.ResyncLlm();
            return true;
        }

        void Start()
        {
            try
            {
                QolServices.EnsureInitialized();
                _llm = FindFirstObjectByType<LLMCharacter>(FindObjectsInactive.Include);
                _store = new CharacterStore(Path.Combine(SaveLoadHandler.DataDirectory, CharacterStore.FolderName));

                if (CharacterSeeder.SeedIfEmpty(_store, ReadUpstreamPrompt(), ReadUpstreamHistory(), DateTime.UtcNow))
                    Debug.Log("[QoL] Created the default character from the existing prompt and chat history.");

                string id = QolServices.Settings.Characters.ActiveId;
                if (!_store.Exists(id)) id = _store.Exists(CharacterIds.Default) ? CharacterIds.Default : _store.LoadAll()[0].Id;
                Switch(id);
            }
            catch (Exception e)
            {
                // Characters are optional: without them chat keeps upstream's single prompt and ZomeAI history.
                Debug.LogWarning("[QoL] Characters disabled: " + e.Message);
                Debug.LogException(e);
                Active = null;
            }
        }

        // ---- queries ---------------------------------------------------------------

        public List<CharacterProfile> All()
        {
            List<CharacterProfile> all = _store?.LoadAll() ?? new List<CharacterProfile>();
            if (_store?.LastWarning != null) Debug.LogWarning("[QoL] " + _store.LastWarning);
            return all;
        }

        public CharacterProfile Find(string query) => CharacterStore.Find(All(), query);

        // ---- switching -------------------------------------------------------------

        /// <summary>Makes <paramref name="id"/> active: resumes its newest session, updates the model and the chat window.</summary>
        public bool Switch(string id)
        {
            CharacterProfile profile = _store?.Load(id);
            if (profile == null) return false;

            CancelChat();
            Active = profile;
            _log = new SessionLog(_store.SessionsDirectoryOf(id));
            SessionId = _log.Latest();
            _history.Clear();
            if (SessionId != null)
            {
                _history.AddRange(_log.Read(SessionId));
                if (_log.LastSkipped > 0) Debug.LogWarning($"[QoL] Skipped {_log.LastSkipped} unreadable line(s) in session {SessionId}.");
            }

            if (QolServices.Settings.Characters.ActiveId != id)
            {
                QolServices.Settings.Characters.ActiveId = id;
                QolServices.SaveSettings(QolServices.Settings);
            }

            ApplyToChat();
            CharacterChanged?.Invoke(profile);
            Debug.Log($"[QoL] Character: {profile.DisplayName} ({id}), session {SessionId ?? "new"}, {_history.Count} message(s).");
            return true;
        }

        /// <summary>Creates a character and switches to it.</summary>
        public CharacterProfile CreateAndSwitch(string name)
        {
            if (_store == null) return null;
            CharacterProfile p = _store.Create(string.IsNullOrWhiteSpace(name) ? "New character" : name.Trim());
            Switch(p.Id);
            return p;
        }

        /// <summary>Starts an empty session for the active character; earlier sessions stay on disk.</summary>
        public void StartNewSession()
        {
            if (Active == null) return;
            CancelChat();
            SessionId = null; // created on the first message, so empty sessions don't pile up
            _history.Clear();
            ApplyToChat();
        }

        /// <summary>Deletes every session of the active character (upstream's "delete AI history" button).</summary>
        public void DeleteHistory()
        {
            if (Active == null) return;
            CancelChat();
            try { _log.DeleteAll(); }
            catch (IOException e) { Debug.LogWarning("[QoL] Could not delete chat history: " + e.Message); }
            SessionId = null;
            _history.Clear();
            ApplyToChat();
        }

        // ---- edits -----------------------------------------------------------------

        public void UpdateActive(string name = null, string prompt = null, string greeting = null)
        {
            if (Active == null) return;
            if (name != null) Active.Name = name.Trim();
            if (prompt != null) Active.Prompt = prompt;
            if (greeting != null) Active.Greeting = greeting;
            try { _store.Save(Active); }
            catch (IOException e) { Debug.LogWarning("[QoL] Could not save character: " + e.Message); }
            if (prompt != null && _llm != null) _llm.SetPrompt(prompt, false);
        }

        /// <summary>Called by AISystemPromptBinder (QoL hook): the prompt box edits the active character.</summary>
        public static bool TrySetActivePrompt(string prompt)
        {
            if (!IsReady) return false;
            Instance.UpdateActive(prompt: prompt);
            return true;
        }

        public static bool TryGetActivePrompt(out string prompt)
        {
            prompt = IsReady ? Instance.Active.Prompt : null;
            return prompt != null;
        }

        /// <summary>Appends a finished exchange to the session log and the in-memory history.</summary>
        public void RecordExchange(string userMessage, string reply)
        {
            if (Active == null) return;
            DateTime now = DateTime.UtcNow;
            var pair = new[]
            {
                new SessionMessage(ChatMessage.User, userMessage, now),
                new SessionMessage(ChatMessage.Assistant, reply, now),
            };
            _history.AddRange(pair);
            try
            {
                if (SessionId == null) SessionId = _log.Create();
                _log.Append(SessionId, pair);
            }
            catch (IOException e)
            {
                Debug.LogWarning("[QoL] Could not write the session log: " + e.Message);
            }
        }

        // ---- context ---------------------------------------------------------------

        /// <summary>Context for the next message, within the configured token budgets.</summary>
        public ContextResult BuildContext(string newMessage, int? contextLimit = null)
        {
            ContextSettings budget = QolServices.Settings.Context;
            if (contextLimit.HasValue && contextLimit.Value > 0 && contextLimit.Value < budget.TotalTokens)
            {
                budget = new ContextSettings
                {
                    TotalTokens = contextLimit.Value,
                    ReplyReserveTokens = Math.Min(budget.ReplyReserveTokens, contextLimit.Value / 4),
                    FactsTokens = budget.FactsTokens,
                    SummaryTokens = budget.SummaryTokens,
                    MaxHistoryMessages = budget.MaxHistoryMessages,
                };
            }
            ContextResult result = ContextBuilder.Build(new ContextInput
            {
                Prompt = Active?.Prompt ?? "",
                History = _history,
                NewMessage = newMessage,
            }, budget);
            if (result.PromptOverBudget)
                Debug.LogWarning($"[QoL] The character prompt alone is larger than the context budget ({budget.TotalTokens} tokens); no history fits.");
            return result;
        }

        // ---- upstream glue -----------------------------------------------------------

        void CancelChat()
        {
            // Not ChatBot.CancelRequests: it needs ChatBot.Start to have run (the chat window may be closed) and throws when
            // no local model is running. A cancelled reply still calls ChatBot's done callback, which re-enables input.
            QolChatRouter.Cancel();
            if (_llm == null) return;
            try { _llm.CancelRequests(); }
            catch (Exception) { /* the local model isn't running, so nothing to cancel */ }
        }

        /// <summary>Pushes the active character and session into LLMCharacter, the chat window and the prompt box.</summary>
        void ApplyToChat()
        {
            Generation++;
            ResyncLlm();

            ChatBot chatBot = FindFirstObjectByType<ChatBot>(FindObjectsInactive.Include);
            if (chatBot != null) chatBot.QolReloadMessages();

            foreach (AISystemPromptBinder binder in FindObjectsByType<AISystemPromptBinder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (binder.input != null) binder.input.SetTextWithoutNotify(Active.Prompt);
        }

        /// <summary>Rebuilds LLMCharacter's in-memory chat from the session, e.g. after a stale reply was added to it.</summary>
        public void ResyncLlm()
        {
            if (_llm == null || Active == null) return;
            _llm.save = ""; // the session log replaces upstream's ZomeAI.json
            _llm.SetPrompt(Active.Prompt, true);
            foreach (SessionMessage m in SessionLog.Alternating(_history))
                _llm.AddMessage(m.Role == ChatMessage.User ? _llm.playerName : _llm.AIName, m.Text);
        }

        static string ReadUpstreamPrompt()
        {
            // Same file AISystemPromptBinder and LLMCharacter.Start use; falls back to the scene's built-in prompt.
            string path = Path.Combine(Application.persistentDataPath, "ZomeAI_prompt.txt");
            try { if (File.Exists(path)) return File.ReadAllText(path); }
            catch (IOException e) { Debug.LogWarning("[QoL] Could not read the prompt file: " + e.Message); }
            LLMCharacter llm = FindFirstObjectByType<LLMCharacter>(FindObjectsInactive.Include);
            return llm != null ? llm.prompt : "";
        }

        static string ReadUpstreamHistory()
        {
            LLMCharacter llm = FindFirstObjectByType<LLMCharacter>(FindObjectsInactive.Include);
            string save = llm != null && !string.IsNullOrEmpty(llm.save) ? llm.save : "ZomeAI";
            string path = Path.Combine(Application.persistentDataPath, save + ".json");
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (IOException e)
            {
                Debug.LogWarning("[QoL] Could not read the old chat history: " + e.Message);
                return null;
            }
        }
    }
}
