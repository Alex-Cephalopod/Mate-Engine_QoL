using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using MateEngineQoL.AI;
using MateEngineQoL.Characters;
using MateEngineQoL.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// The "AI PROVIDERS" page in the settings menu. Built at runtime by cloning upstream's own widgets
    /// (Color Menu page, context-length dropdown, system-prompt field) so it matches the menu without
    /// editing the scene. A "PROVIDERS" button next to the AI section header opens it. Below the provider
    /// rows come the voice section, the character section (pick, rename, greeting, new chat; the prompt itself
    /// stays in upstream's box) and the animation library (QolAiSettingsPage.Animations.cs).
    /// </summary>
    public sealed partial class QolAiSettingsPage : MonoBehaviour
    {
        const float RowPadding = 4f;

        GameObject _mainMenu;
        TMP_Dropdown _provider;
        InputField _baseUrl, _model, _apiKey;
        Button _removeKey, _test;
        TMP_Text _status, _presetHint;
        readonly List<CanvasGroup> _remoteOnly = new List<CanvasGroup>();
        CancellationTokenSource _testCts;

        TMP_Dropdown _ttsProvider;
        InputField _ttsUrl, _ttsModel, _ttsVoice, _ttsKey;
        Button _ttsRemoveKey, _ttsTest;
        Slider _ttsSpeed, _ttsVolume;
        TMP_Text _ttsHint, _ttsStatus, _ttsSpeedLabel, _ttsVolumeLabel;
        readonly List<CanvasGroup> _ttsRows = new List<CanvasGroup>();
        float _ttsSaveAt = -1f;
        bool _ttsTesting;
        float _ttsTestUntil;
        int _ttsTestSentences, _ttsTestErrors;

        TMP_Dropdown _character;
        InputField _characterName, _greeting;
        readonly List<string> _characterIds = new List<string>();
        readonly List<CanvasGroup> _characterRows = new List<CanvasGroup>();

        // Index 0 is the built-in model; 1..N map to ChatPresets.All.
        static int PresetToIndex(ChatSettings s)
        {
            if (s.Provider == ChatProviderIds.UpstreamLocal) return 0;
            for (int i = 0; i < ChatPresets.All.Count; i++)
                if (ChatPresets.All[i].Id == s.Preset) return i + 1;
            return ChatPresets.All.Count; // custom
        }

        // Index 0 is "Off"; 1..N map to TtsPresets.All.
        static int TtsPresetToIndex(TtsSettings s)
        {
            if (string.IsNullOrEmpty(s.Provider) || s.Provider == TtsProviderIds.None) return 0;
            for (int i = 0; i < TtsPresets.All.Count; i++)
                if (TtsPresets.All[i].Id == s.Preset) return i + 1;
            return TtsPresets.All.Count; // custom
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            try { Build(); }
            catch (Exception e) { Debug.LogWarning("[QoL] AI providers page not installed: " + e.Message); }
        }

        static void Build()
        {
            if (SaveLoadHandler.Instance == null) throw new InvalidOperationException("SaveLoadHandler not found");
            Transform canvas = SaveLoadHandler.Instance.transform.Find("SettingsMenuCanvas");
            _canvas = canvas;
            Transform mainMenu = Find(canvas, "Main Menu");
            Transform colorMenu = Find(canvas, "Color Menu");
            Transform mainContent = mainMenu.GetComponent<ScrollRect>().content;
            Transform aiHeader = Find(mainContent, "MenuPanel/Main Menu/= AI");
            Transform aiInput = Find(aiHeader, "AI INPUT");

            // Page: a copy of the Color Menu page with its rows removed.
            GameObject pageGo = Instantiate(colorMenu.gameObject, colorMenu.parent);
            pageGo.name = "QoL AI Providers";
            pageGo.SetActive(false);
            var page = pageGo.AddComponent<QolAiSettingsPage>();
            page._mainMenu = mainMenu.gameObject;
            page.CollectOtherPages(canvas);
            SaveLoadHandler.Instance.gameObject.AddComponent<PageWatcher>().Page = page;

            Transform content = pageGo.GetComponent<ScrollRect>().content;
            TMP_Text titleTemplate = colorMenu.GetComponent<ScrollRect>().content.GetComponentInChildren<TMP_Text>(true);
            for (int i = content.childCount - 1; i >= 0; i--) DestroyImmediate(content.GetChild(i).gameObject);

            // The Color page's layout is tuned for two tall sections; use row-sized spacing with the menu's side insets.
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(66, 74, 16, 24);
            layout.spacing = 6f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Button back = Find(pageGo.transform, "Back").GetComponent<Button>();
            Rewire(back.gameObject);
            back.onClick.AddListener(page.Close);

            page.BuildRows(content, titleTemplate,
                Find(aiInput, "Context Length").GetComponent<TMP_Dropdown>(),
                Find(aiInput, "AiSystemPrompt").GetComponent<InputField>(),
                Find(aiInput, "Text").GetComponent<TMP_Text>(),
                Find(aiInput, "Text (1)").GetComponent<TMP_Text>(),
                back,
                Find(mainContent, "MenuPanel/Main Menu/= AUDIO/Menu Volumen").GetComponent<Slider>());

            // Entry button at the right end of the AI section header.
            GameObject open = Instantiate(back.gameObject, aiHeader);
            open.name = "QoL Providers Button";
            Rewire(open);
            var openRect = (RectTransform)open.transform;
            openRect.anchorMin = openRect.anchorMax = new Vector2(1f, 0.5f);
            openRect.pivot = new Vector2(1f, 0.5f);
            openRect.anchoredPosition = Vector2.zero;
            openRect.sizeDelta = new Vector2(170f, 34f);
            SetButtonLabel(open, "QOL_AI_PROVIDERS_BUTTON", 14f);
            open.GetComponent<Button>().onClick.AddListener(page.Open);
        }

        void BuildRows(Transform content, TMP_Text titleTemplate, TMP_Dropdown dropdownTemplate, InputField inputTemplate,
            TMP_Text labelTemplate, TMP_Text noteTemplate, Button buttonTemplate, Slider sliderTemplate)
        {
            QolServices.EnsureInitialized();

            if (titleTemplate != null) AddText(content, titleTemplate, QolText.Get("QOL_AI_PROVIDERS_TITLE"), 60f);

            _provider = AddDropdown(content, dropdownTemplate, "QOL_CHAT_PROVIDER");
            _provider.ClearOptions();
            var options = new List<string> { QolText.Get("QOL_CHAT_PROVIDER_BUILTIN") };
            foreach (ChatPreset p in ChatPresets.All) options.Add(p.DisplayName);
            _provider.AddOptions(options);
            _provider.onValueChanged.AddListener(OnProviderChanged);
            _presetHint = AddText(content, noteTemplate, "", 22f);

            _baseUrl = AddInput(content, inputTemplate, labelTemplate, "QOL_BASE_URL", false);
            _baseUrl.onEndEdit.AddListener(_ => SaveFields());
            _model = AddInput(content, inputTemplate, labelTemplate, "QOL_MODEL", false);
            _model.onEndEdit.AddListener(_ => SaveFields());
            SetPlaceholder(_model, QolText.Get("QOL_MODEL_HINT"));
            _apiKey = AddInput(content, inputTemplate, labelTemplate, "QOL_API_KEY", true);
            _apiKey.onEndEdit.AddListener(OnKeyEntered);

            _removeKey = AddButton(content, buttonTemplate, "QOL_REMOVE_KEY", RemoveKey);
            _remoteOnly.Add(_removeKey.gameObject.AddComponent<CanvasGroup>());
            _test = AddButton(content, buttonTemplate, "QOL_TEST", RunTest);

            _status = AddText(content, noteTemplate, "", 48f);
            AddText(content, noteTemplate, QolText.Get("QOL_NOTE_PRIVACY"), 40f);

            BuildVoiceRows(content, titleTemplate, dropdownTemplate, inputTemplate, labelTemplate, noteTemplate, buttonTemplate, sliderTemplate);

            // Characters (Phase 7a).
            if (titleTemplate != null) AddText(content, titleTemplate, QolText.Get("QOL_CHARACTERS_TITLE"), 60f);
            _character = AddDropdown(content, dropdownTemplate, "QOL_CHARACTER");
            _characterRows.Add(_character.transform.parent.gameObject.AddComponent<CanvasGroup>());
            _character.onValueChanged.AddListener(OnCharacterChanged);
            _characterName = AddInput(content, inputTemplate, labelTemplate, "QOL_CHARACTER_NAME", false, _characterRows);
            _characterName.characterLimit = 60;
            _characterName.onEndEdit.AddListener(_ => SaveCharacterFields());
            _greeting = AddInput(content, inputTemplate, labelTemplate, "QOL_GREETING", false, _characterRows);
            SetPlaceholder(_greeting, QolText.Get("QOL_GREETING_HINT"));
            _greeting.onEndEdit.AddListener(_ => SaveCharacterFields());
            Button newChat = AddButton(content, buttonTemplate, "QOL_NEW_CHAT", () => CharacterManager.Instance?.StartNewSession());
            _characterRows.Add(newChat.gameObject.AddComponent<CanvasGroup>());
            AddText(content, noteTemplate, QolText.Get("QOL_CHARACTER_NOTE"), 40f);

            BuildAnimationRows(content, titleTemplate, dropdownTemplate, noteTemplate, buttonTemplate);
        }

        // ---- voice (Phase 2) ---------------------------------------------------

        void BuildVoiceRows(Transform content, TMP_Text titleTemplate, TMP_Dropdown dropdownTemplate, InputField inputTemplate,
            TMP_Text labelTemplate, TMP_Text noteTemplate, Button buttonTemplate, Slider sliderTemplate)
        {
            if (titleTemplate != null) AddText(content, titleTemplate, QolText.Get("QOL_VOICE_TITLE"), 60f);

            _ttsProvider = AddDropdown(content, dropdownTemplate, "QOL_TTS_PROVIDER");
            _ttsProvider.ClearOptions();
            var options = new List<string> { QolText.Get("QOL_TTS_OFF") };
            foreach (TtsPreset p in TtsPresets.All) options.Add(p.DisplayName);
            _ttsProvider.AddOptions(options);
            _ttsProvider.onValueChanged.AddListener(OnTtsProviderChanged);
            _ttsHint = AddText(content, noteTemplate, "", 40f);

            _ttsUrl = AddInput(content, inputTemplate, labelTemplate, "QOL_BASE_URL", false, _ttsRows);
            _ttsUrl.onEndEdit.AddListener(_ => SaveTtsFields());
            _ttsModel = AddInput(content, inputTemplate, labelTemplate, "QOL_MODEL", false, _ttsRows);
            SetPlaceholder(_ttsModel, QolText.Get("QOL_TTS_MODEL_HINT"));
            _ttsModel.onEndEdit.AddListener(_ => SaveTtsFields());
            _ttsVoice = AddInput(content, inputTemplate, labelTemplate, "QOL_TTS_VOICE", false, _ttsRows);
            SetPlaceholder(_ttsVoice, QolText.Get("QOL_TTS_VOICE_HINT"));
            _ttsVoice.onEndEdit.AddListener(_ => SaveTtsFields());
            _ttsKey = AddInput(content, inputTemplate, labelTemplate, "QOL_API_KEY", true, _ttsRows);
            _ttsKey.onEndEdit.AddListener(OnTtsKeyEntered);
            _ttsRemoveKey = AddButton(content, buttonTemplate, "QOL_REMOVE_KEY", () =>
            {
                QolServices.SetApiKey(QolSecrets.TtsSlot, null);
                RefreshVoice();
            });
            _ttsRows.Add(_ttsRemoveKey.gameObject.AddComponent<CanvasGroup>());

            _ttsSpeed = AddSlider(content, sliderTemplate, 0.5f, 2f, out _ttsSpeedLabel);
            _ttsSpeed.onValueChanged.AddListener(v =>
            {
                QolServices.Settings.Tts.Speed = Mathf.Round(v * 20f) / 20f;
                ShowSliderLabels();
                _ttsSaveAt = Time.unscaledTime + 0.5f;
            });
            _ttsVolume = AddSlider(content, sliderTemplate, 0f, 1f, out _ttsVolumeLabel);
            _ttsVolume.onValueChanged.AddListener(v =>
            {
                QolServices.Settings.Tts.Volume = Mathf.Round(v * 100f) / 100f;
                ShowSliderLabels();
                _ttsSaveAt = Time.unscaledTime + 0.5f;
            });

            _ttsTest = AddButton(content, buttonTemplate, "QOL_TTS_TEST", RunVoiceTest);
            _ttsRows.Add(_ttsTest.gameObject.AddComponent<CanvasGroup>());
            _ttsStatus = AddText(content, noteTemplate, "", 40f);
            AddText(content, noteTemplate, QolText.Get("QOL_TTS_NOTE"), 40f);
        }

        void RefreshVoice()
        {
            TtsSettings tts = QolServices.Settings.Tts;
            _ttsProvider.SetValueWithoutNotify(TtsPresetToIndex(tts));
            _ttsProvider.RefreshShownValue();
            _ttsUrl.SetTextWithoutNotify(tts.BaseUrl ?? "");
            _ttsModel.SetTextWithoutNotify(tts.Model ?? "");
            _ttsVoice.SetTextWithoutNotify(tts.Voice ?? "");
            _ttsKey.SetTextWithoutNotify(""); // stored keys are never shown again
            _ttsSpeed.SetValueWithoutNotify(tts.Speed);
            _ttsVolume.SetValueWithoutNotify(tts.Volume);
            ShowSliderLabels();

            bool on = !string.IsNullOrEmpty(tts.Provider) && tts.Provider != TtsProviderIds.None;
            foreach (CanvasGroup g in _ttsRows)
            {
                g.interactable = on;
                g.alpha = on ? 1f : 0.4f;
            }

            TtsPreset preset = TtsPresets.Find(tts.Preset);
            bool fromEnv = QolServices.Store.IsKeyFromEnvironment(QolSecrets.TtsSlot);
            bool hasKey = QolServices.HasApiKey(QolSecrets.TtsSlot);
            string keyHint = fromEnv ? "QOL_TTS_KEY_ENV"
                : hasKey ? "QOL_API_KEY_SAVED"
                : preset != null && !preset.NeedsKey && preset.Id != "custom" ? "QOL_API_KEY_NOT_NEEDED"
                : "QOL_API_KEY_HINT";
            SetPlaceholder(_ttsKey, QolText.Get(keyHint));
            _ttsKey.interactable = on && !fromEnv;
            _ttsRemoveKey.interactable = on && hasKey && !fromEnv;

            _ttsHint.text = QolText.Get(!on ? "QOL_TTS_HINT_OFF"
                : preset == null || preset.Id == "custom" ? "QOL_TTS_HINT_CUSTOM"
                : "QOL_TTS_HINT_" + preset.Id.ToUpperInvariant());
            ShowVoiceStatus();
        }

        void ShowSliderLabels()
        {
            TtsSettings tts = QolServices.Settings.Tts;
            if (_ttsSpeedLabel != null) _ttsSpeedLabel.text = QolText.Format("QOL_TTS_SPEED", tts.Speed.ToString("0.00"));
            if (_ttsVolumeLabel != null) _ttsVolumeLabel.text = QolText.Format("QOL_TTS_VOLUME", Mathf.RoundToInt(tts.Volume * 100f));
        }

        void ShowVoiceStatus()
        {
            if (_ttsTesting) return;
            ProviderRegistry r = QolServices.Registry;
            QolVoicePipeline voice = QolVoicePipeline.Instance;
            if (r.TtsError != null) _ttsStatus.text = QolText.Format("QOL_STATUS_ERROR", r.TtsError);
            else if (r.Tts == null) _ttsStatus.text = QolText.Get("QOL_TTS_STATUS_OFF");
            else if (voice != null && voice.LastError != null) _ttsStatus.text = QolText.Format("QOL_TTS_STATUS_LAST_ERROR", voice.LastError);
            else _ttsStatus.text = QolText.Format("QOL_TTS_STATUS_READY", HostOf(QolServices.Settings.Tts.BaseUrl));
        }

        void OnTtsProviderChanged(int index)
        {
            QolSettings s = QolServices.Settings;
            if (index == 0)
            {
                s.Tts.Provider = TtsProviderIds.None;
            }
            else
            {
                TtsPreset preset = TtsPresets.All[index - 1];
                bool samePreset = preset.Id == s.Tts.Preset && s.Tts.Provider != TtsProviderIds.None;
                s.Tts.Provider = preset.Provider;
                s.Tts.Preset = preset.Id;
                if (!samePreset && preset.Id != "custom")
                {
                    s.Tts.BaseUrl = preset.BaseUrl;
                    s.Tts.Model = preset.Model;
                    s.Tts.Voice = preset.Voice;
                }
                if (!samePreset) s.Tts.SoundTags = null; // follow the new preset
            }
            QolServices.SaveSettings(s);
            RefreshVoice();
        }

        void SaveTtsFields()
        {
            QolSettings s = QolServices.Settings;
            string url = _ttsUrl.text.Trim();
            TtsPreset preset = TtsPresets.Find(s.Tts.Preset);
            // Editing an OpenAI-compatible preset's URL makes it a custom server.
            if (url != s.Tts.BaseUrl && preset != null && s.Tts.Provider == TtsProviderIds.OpenAICompatible
                && !string.Equals(preset.BaseUrl, url, StringComparison.OrdinalIgnoreCase))
                s.Tts.Preset = "custom";
            s.Tts.BaseUrl = url;
            s.Tts.Model = _ttsModel.text.Trim();
            s.Tts.Voice = _ttsVoice.text.Trim();
            QolServices.SaveSettings(s);
            RefreshVoice();
        }

        void OnTtsKeyEntered(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            QolServices.SetApiKey(QolSecrets.TtsSlot, key);
            _ttsKey.SetTextWithoutNotify("");
            RefreshVoice();
        }

        /// <summary>Slider moves change the live settings at once and are written to disk shortly after the last move.</summary>
        void FlushVoiceSliders()
        {
            if (_ttsSaveAt < 0f) return;
            _ttsSaveAt = -1f;
            QolServices.SaveSettings(QolServices.Settings);
        }

        void RunVoiceTest()
        {
            FlushVoiceSliders();
            QolVoicePipeline voice = QolVoicePipeline.Instance;
            if (voice == null || QolServices.Registry.Tts == null)
            {
                ShowVoiceStatus();
                return;
            }
            _ttsTesting = true;
            _ttsTestUntil = Time.unscaledTime + 45f;
            _ttsTestSentences = voice.SentencesStarted;
            _ttsTestErrors = voice.ErrorCount;
            _ttsStatus.text = QolText.Get("QOL_TESTING");
            voice.Speak(QolText.Get("QOL_TTS_SAMPLE"));
        }

        void Update()
        {
            if (_ttsSaveAt >= 0f && Time.unscaledTime >= _ttsSaveAt) FlushVoiceSliders();
            if (!_ttsTesting) return;
            QolVoicePipeline voice = QolVoicePipeline.Instance;
            if (voice == null || Time.unscaledTime > _ttsTestUntil)
            {
                _ttsTesting = false;
                ShowVoiceStatus();
            }
            else if (voice.ErrorCount != _ttsTestErrors)
            {
                _ttsTesting = false;
                _ttsStatus.text = QolText.Format("QOL_TEST_FAILED", voice.LastError);
            }
            else if (voice.SentencesStarted != _ttsTestSentences)
            {
                _ttsTesting = false;
                _ttsStatus.text = QolText.Format("QOL_TTS_TEST_OK", voice.LastLatencyMs);
            }
        }

        // ---- page open/close -------------------------------------------------
        //
        // Upstream's page buttons (e.g. the lighting toggle) flip pages with UISetOnOff.SetOnOff, which
        // toggles activeSelf. If this page deactivated Main Menu, such a flip would turn it back on and
        // both pages would overlap. So Main Menu stays active but hidden while this page is open, and
        // PageWatcher closes this page as soon as upstream switches pages or the settings window closes.

        CanvasGroup _mainMenuGroup;
        float _mainAlpha;
        bool _mainInteractable, _mainBlocksRaycasts, _mainHidden;
        readonly List<GameObject> _otherPages = new List<GameObject>();
        readonly HashSet<GameObject> _pagesOpenAtStart = new HashSet<GameObject>();

        public void Open()
        {
            Refresh();
            _pagesOpenAtStart.Clear();
            foreach (GameObject p in _otherPages)
                if (p != null && p.activeInHierarchy) _pagesOpenAtStart.Add(p);
            HideMainMenu();
            gameObject.SetActive(true);
        }

        public void Close()
        {
            gameObject.SetActive(false);
            ShowMainMenu();
        }

        void OnDisable()
        {
            _testCts?.Cancel();
            _ttsTesting = false;
            FlushVoiceSliders();
        }

        /// <summary>Upstream pages that can open over this one: the canvas's top-level objects and every scroll page
        /// (models list, mod list, ...), except Main Menu and this page. Objects already open on Open() are ignored.</summary>
        void CollectOtherPages(Transform canvas)
        {
            var found = new HashSet<GameObject>();
            foreach (Transform child in canvas) found.Add(child.gameObject);
            foreach (ScrollRect s in canvas.GetComponentsInChildren<ScrollRect>(true)) found.Add(s.gameObject);
            foreach (GameObject go in found)
                if (!go.transform.IsChildOf(transform) && !go.transform.IsChildOf(_mainMenu.transform))
                    _otherPages.Add(go);
        }

        void HideMainMenu()
        {
            if (_mainHidden) return;
            if (_mainMenuGroup == null) _mainMenuGroup = _mainMenu.GetComponent<CanvasGroup>();
            if (_mainMenuGroup == null) _mainMenuGroup = _mainMenu.AddComponent<CanvasGroup>();
            _mainAlpha = _mainMenuGroup.alpha;
            _mainInteractable = _mainMenuGroup.interactable;
            _mainBlocksRaycasts = _mainMenuGroup.blocksRaycasts;
            _mainMenuGroup.alpha = 0f;
            _mainMenuGroup.interactable = false;
            _mainMenuGroup.blocksRaycasts = false;
            _mainHidden = true;
        }

        void ShowMainMenu()
        {
            if (!_mainHidden || _mainMenuGroup == null) return;
            _mainMenuGroup.alpha = _mainAlpha;
            _mainMenuGroup.interactable = _mainInteractable;
            _mainMenuGroup.blocksRaycasts = _mainBlocksRaycasts;
            _mainHidden = false;
        }

        /// <summary>Called every frame by <see cref="PageWatcher"/> while this page is open.</summary>
        void Watch()
        {
            if (!_canvas.gameObject.activeInHierarchy || !_mainMenu.activeSelf)
            {
                Close(); // settings window closed, or upstream switched away from Main Menu
                return;
            }
            foreach (GameObject p in _otherPages)
            {
                if (p != null && p.activeInHierarchy && !_pagesOpenAtStart.Contains(p))
                {
                    Close(); // another page opened on top
                    return;
                }
            }
        }

        /// <summary>Lives on SaveLoadHandler's object, which stays active while the settings canvas is closed.</summary>
        sealed class PageWatcher : MonoBehaviour
        {
            public QolAiSettingsPage Page;

            void Update()
            {
                if (Page != null && Page.gameObject.activeSelf) Page.Watch();
            }
        }

        void Refresh()
        {
            ChatSettings chat = QolServices.Settings.Chat;
            _provider.SetValueWithoutNotify(PresetToIndex(chat));
            _provider.RefreshShownValue();
            _baseUrl.SetTextWithoutNotify(chat.BaseUrl ?? "");
            _model.SetTextWithoutNotify(chat.Model ?? "");
            _apiKey.SetTextWithoutNotify(""); // stored keys are never shown again

            bool remote = chat.Provider != ChatProviderIds.UpstreamLocal;
            foreach (CanvasGroup g in _remoteOnly)
            {
                g.interactable = remote;
                g.alpha = remote ? 1f : 0.4f;
            }

            bool fromEnv = QolServices.Store.IsKeyFromEnvironment(QolSecrets.ChatSlot);
            bool hasKey = QolServices.HasApiKey(QolSecrets.ChatSlot);
            ChatPreset preset = ChatPresets.Find(chat.Preset);
            string keyHint = fromEnv ? "QOL_API_KEY_ENV"
                : hasKey ? "QOL_API_KEY_SAVED"
                : preset != null && !preset.NeedsKey && preset.Id != "custom" ? "QOL_API_KEY_NOT_NEEDED"
                : "QOL_API_KEY_HINT";
            SetPlaceholder(_apiKey, QolText.Get(keyHint));
            _apiKey.interactable = remote && !fromEnv;
            _removeKey.interactable = remote && hasKey && !fromEnv;

            RefreshCharacters();
            RefreshVoice();
            RefreshAnimations();

            _presetHint.text = QolText.Get(!remote ? "QOL_HINT_BUILTIN"
                : preset == null || preset.Id == "custom" ? "QOL_HINT_CUSTOM"
                : preset.Free ? "QOL_HINT_FREE_LOCAL"
                : preset.Id == "openrouter" ? "QOL_HINT_OPENROUTER"
                : "QOL_HINT_PAID");

            ShowStatus();
        }

        void ShowStatus()
        {
            ProviderRegistry r = QolServices.Registry;
            if (r.ChatError != null) _status.text = QolText.Format("QOL_STATUS_ERROR", r.ChatError);
            else if (r.Chat == null) _status.text = QolText.Get("QOL_TEST_BUILTIN");
            else _status.text = QolText.Format("QOL_STATUS_READY", HostOf(QolServices.Settings.Chat.BaseUrl));
        }

        void RefreshCharacters()
        {
            bool ready = CharacterManager.IsReady;
            foreach (CanvasGroup g in _characterRows)
            {
                g.interactable = ready;
                g.alpha = ready ? 1f : 0.4f;
            }
            _characterIds.Clear();
            var names = new List<string>();
            int selected = 0;
            if (ready)
            {
                foreach (CharacterProfile p in CharacterManager.Instance.All())
                {
                    if (p.Id == CharacterManager.Instance.Active.Id) selected = _characterIds.Count;
                    _characterIds.Add(p.Id);
                    names.Add(p.DisplayName);
                }
            }
            names.Add(QolText.Get("QOL_CHARACTER_NEW"));
            _character.ClearOptions();
            _character.AddOptions(names);
            _character.SetValueWithoutNotify(selected);
            _character.RefreshShownValue();

            CharacterProfile active = ready ? CharacterManager.Instance.Active : null;
            _characterName.SetTextWithoutNotify(active?.Name ?? "");
            _greeting.SetTextWithoutNotify(active?.Greeting ?? "");
        }

        // ---- edits -------------------------------------------------------------

        void OnCharacterChanged(int index)
        {
            CharacterManager characters = CharacterManager.Instance;
            if (characters == null) return;
            if (index >= _characterIds.Count) characters.CreateAndSwitch(QolText.Get("QOL_CHARACTER_NEW_NAME"));
            else characters.Switch(_characterIds[index]);
            RefreshCharacters();
        }

        void SaveCharacterFields()
        {
            if (!CharacterManager.IsReady) return;
            CharacterManager.Instance.UpdateActive(name: _characterName.text.Trim(), greeting: _greeting.text.Trim());
            RefreshCharacters();
        }

        void OnProviderChanged(int index)
        {
            QolSettings s = QolServices.Settings;
            if (index == 0)
            {
                s.Chat.Provider = ChatProviderIds.UpstreamLocal;
            }
            else
            {
                ChatPreset preset = ChatPresets.All[index - 1];
                s.Chat.Provider = ChatProviderIds.OpenAICompatible;
                s.Chat.Preset = preset.Id;
                if (!string.IsNullOrEmpty(preset.BaseUrl)) s.Chat.BaseUrl = preset.BaseUrl;
            }
            QolServices.SaveSettings(s);
            Refresh();
        }

        void SaveFields()
        {
            QolSettings s = QolServices.Settings;
            string url = _baseUrl.text.Trim();
            if (url != s.Chat.BaseUrl)
            {
                s.Chat.BaseUrl = url;
                ChatPreset match = null;
                foreach (ChatPreset p in ChatPresets.All)
                    if (string.Equals(p.BaseUrl, url, StringComparison.OrdinalIgnoreCase)) match = p;
                s.Chat.Preset = match?.Id ?? "custom";
            }
            s.Chat.Model = _model.text.Trim();
            QolServices.SaveSettings(s);
            Refresh();
        }

        void OnKeyEntered(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            QolServices.SetApiKey(QolSecrets.ChatSlot, key);
            _apiKey.SetTextWithoutNotify("");
            Refresh();
        }

        void RemoveKey()
        {
            QolServices.SetApiKey(QolSecrets.ChatSlot, null);
            Refresh();
        }

        async void RunTest()
        {
            IChatProvider provider = QolServices.Registry.Chat;
            if (provider == null)
            {
                ShowStatus();
                return;
            }

            _testCts?.Cancel();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            CancellationToken ct = _testCts.Token;
            _test.interactable = false;
            _status.text = QolText.Get("QOL_TESTING");

            var request = new ChatRequest
            {
                Model = QolServices.Settings.Chat.Model,
                MaxTokens = 40,
                Messages = { new ChatMessage(ChatMessage.User, "Reply with a short friendly greeting, five words at most.") },
            };
            var timer = Stopwatch.StartNew();
            var reply = new StringBuilder();
            try
            {
                await foreach (string delta in provider.StreamReplyAsync(request, ct))
                    reply.Append(delta);
                string text = reply.ToString().Trim();
                if (text.Length > 80) text = text.Substring(0, 80) + "...";
                _status.text = QolText.Format("QOL_TEST_OK", timer.ElapsedMilliseconds, text);
            }
            catch (OperationCanceledException)
            {
                if (_status != null) _status.text = QolText.Format("QOL_TEST_FAILED", "timed out");
            }
            catch (ProviderException e)
            {
                if (_status != null) _status.text = QolText.Format("QOL_TEST_FAILED", e.Message);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (_status != null) _status.text = QolText.Format("QOL_TEST_FAILED", e.GetType().Name);
            }
            finally
            {
                if (_test != null) _test.interactable = true;
            }
        }

        // ---- widget helpers ----------------------------------------------------

        static Transform _canvas;

        /// <summary>Product of local scales from <paramref name="t"/> up to (not including) <paramref name="ancestor"/>.</summary>
        static float ScaleUnder(Transform t, Transform ancestor)
        {
            float s = 1f;
            for (; t != null && t != ancestor; t = t.parent) s *= t.localScale.x;
            return s;
        }

        static Transform Find(Transform parent, string path)
        {
            Transform t = parent != null ? parent.Find(path) : null;
            if (t == null) throw new InvalidOperationException("UI element not found: " + path);
            return t;
        }

        static RectTransform Row(Transform content, float height)
        {
            var go = new GameObject("QoL Row", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(content, false);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height + RowPadding * 2;
            return (RectTransform)go.transform;
        }

        static GameObject Clone(GameObject template, Transform parent)
        {
            GameObject go = Instantiate(template, parent);
            // Upstream authors some widgets large and scales them down; keep the template's on-screen scale.
            // Measured against the shared canvas, not lossyScale: the canvas is inactive (scale 0) at startup.
            float scale = ScaleUnder(template.transform, _canvas) / ScaleUnder(parent, _canvas);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) scale = 1f;
            if (Mathf.Abs(scale - 1f) < 0.001f) scale = 1f;
            go.transform.localScale = new Vector3(scale, scale, 1f);
            Rewire(go);
            go.SetActive(true);
            return go;
        }

        /// <summary>Strips upstream behaviour from a cloned widget: inspector-wired events, localizers, page linkers.</summary>
        static void Rewire(GameObject go)
        {
            foreach (LocalizeStringEvent l in go.GetComponentsInChildren<LocalizeStringEvent>(true)) DestroyImmediate(l);
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string n = mb.GetType().Name;
                if (n == "ButtonLinker" || n == "UiTooltip" || n == "AISystemPromptBinder") DestroyImmediate(mb);
            }
            foreach (Button b in go.GetComponentsInChildren<Button>(true)) b.onClick = new Button.ButtonClickedEvent();
            foreach (InputField f in go.GetComponentsInChildren<InputField>(true))
            {
                f.onValueChanged = new InputField.OnChangeEvent();
                f.onEndEdit = new InputField.EndEditEvent();
                f.onSubmit = new InputField.SubmitEvent();
            }
            foreach (TMP_Dropdown d in go.GetComponentsInChildren<TMP_Dropdown>(true)) d.onValueChanged = new TMP_Dropdown.DropdownEvent();
            foreach (Slider s in go.GetComponentsInChildren<Slider>(true)) s.onValueChanged = new Slider.SliderEvent();
        }

        static void Stretch(RectTransform rt, float top, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -top);
            rt.sizeDelta = new Vector2(0f, height);
        }

        TMP_Text AddText(Transform content, TMP_Text template, string text, float height)
        {
            RectTransform row = Row(content, height);
            var label = Clone(template.gameObject, row).GetComponent<TMP_Text>();
            Stretch((RectTransform)label.transform, RowPadding, height);
            label.text = text;
            return label;
        }

        /// <summary>A dropdown laid out like upstream's context-length row (its "Title" label sits on the left).</summary>
        TMP_Dropdown AddDropdown(Transform content, TMP_Dropdown template, string titleKey)
        {
            RectTransform row = Row(content, 50f);
            var dropdown = Clone(template.gameObject, row).GetComponent<TMP_Dropdown>();
            var rect = (RectTransform)dropdown.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = ((RectTransform)template.transform).rect.size;
            dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
            Transform title = dropdown.transform.Find("Title");
            if (title != null) SetText(title.gameObject, QolText.Get(titleKey));
            return dropdown;
        }

        /// <param name="group">Rows to grey out together; defaults to the remote-provider rows.</param>
        InputField AddInput(Transform content, InputField template, TMP_Text labelTemplate, string labelKey, bool secret,
            List<CanvasGroup> group = null)
        {
            const float labelHeight = 22f, fieldHeight = 34f;
            RectTransform row = Row(content, labelHeight + fieldHeight + 4f);

            var label = Clone(labelTemplate.gameObject, row).GetComponent<TMP_Text>();
            Stretch((RectTransform)label.transform, RowPadding, labelHeight);
            label.text = QolText.Get(labelKey);

            var field = Clone(template.gameObject, row).GetComponent<InputField>();
            Stretch((RectTransform)field.transform, RowPadding + labelHeight + 4f, fieldHeight);
            // The template is a tall multi-line box; refit its text and placeholder to a single line.
            foreach (Graphic g in new Graphic[] { field.textComponent, field.placeholder })
            {
                if (g == null) continue;
                var r = (RectTransform)g.transform;
                r.anchorMin = Vector2.zero;
                r.anchorMax = Vector2.one;
                r.offsetMin = new Vector2(10f, 2f);
                r.offsetMax = new Vector2(-10f, -2f);
                if (g is Text t)
                {
                    t.fontSize = 18;
                    t.alignment = TextAnchor.MiddleLeft;
                    t.horizontalOverflow = HorizontalWrapMode.Overflow;
                    t.verticalOverflow = VerticalWrapMode.Truncate;
                }
            }
            field.lineType = InputField.LineType.SingleLine;
            field.contentType = secret ? InputField.ContentType.Password : InputField.ContentType.Standard;
            field.characterLimit = secret ? 512 : 300;
            field.SetTextWithoutNotify("");

            (group ?? _remoteOnly).Add(row.gameObject.AddComponent<CanvasGroup>());
            return field;
        }

        /// <summary>A slider laid out like upstream's volume rows; its "Text" child is the label, returned for live values.</summary>
        Slider AddSlider(Transform content, Slider template, float min, float max, out TMP_Text label)
        {
            RectTransform row = Row(content, 50f);
            var slider = Clone(template.gameObject, row).GetComponent<Slider>();
            var rect = (RectTransform)slider.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, RowPadding + 4f);
            rect.sizeDelta = ((RectTransform)template.transform).rect.size;
            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;
            Transform text = slider.transform.Find("Text");
            label = text != null ? text.GetComponent<TMP_Text>() : null;
            _ttsRows.Add(row.gameObject.AddComponent<CanvasGroup>());
            return slider;
        }

        Button AddButton(Transform content, Button template, string labelKey, UnityEngine.Events.UnityAction onClick)
        {
            RectTransform row = Row(content, 44f);
            GameObject go = Clone(template.gameObject, row);
            Stretch((RectTransform)go.transform, RowPadding, 44f);
            SetButtonLabel(go, labelKey, 0f);
            var button = go.GetComponent<Button>();
            button.onClick.AddListener(onClick);
            return button;
        }

        static void SetButtonLabel(GameObject button, string key, float fontSize)
        {
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label == null) return;
            label.text = QolText.Get(key);
            if (fontSize > 0f)
            {
                label.enableAutoSizing = false;
                label.fontSize = fontSize;
            }
        }

        static void SetText(GameObject go, string text)
        {
            TMP_Text t = go.GetComponent<TMP_Text>();
            if (t != null) t.text = text;
        }

        static void SetPlaceholder(InputField field, string text)
        {
            if (field.placeholder is Text t) t.text = text;
            else if (field.placeholder != null) SetText(field.placeholder.gameObject, text);
        }

        static string HostOf(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.Host : url;
    }
}
