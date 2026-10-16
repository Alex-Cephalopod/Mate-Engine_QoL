# Upstream hook points

Where QoL code attaches to upstream Mate Engine. Line numbers are as of upstream 3.3.0 Hotfix-1 (`03074b11`) and drift on rebase; search for the symbol if a number is off.

Rule: QoL code lives in `Assets/MateEngineQoL/`. Edits to upstream files stay at the call sites listed here and are marked with a `// QoL:` comment so they are easy to find on rebase.

## Assemblies

| Folder | Assembly | May reference |
| --- | --- | --- |
| `Assets/MateEngineQoL/Core/` | `MateEngineQoL.Core` (asmdef, auto-referenced, `noEngineReferences`) | .NET and precompiled DLLs (Newtonsoft). No `UnityEngine`, no upstream types. |
| `Assets/MateEngineQoL/Core.Tests/` | `MateEngineQoL.Core.Tests` (Editor only) | Core, NUnit. Also built by `Tools/CoreTests` for `dotnet test`. |
| `Assets/MateEngineQoL/Bridge/` | Assembly-CSharp (no asmdef) | Core and all upstream code |
| `Assets/MateEngineQoL/Bridge/Editor/` | Assembly-CSharp-Editor | as above, plus `UnityEditor` |

An asmdef cannot reference Assembly-CSharp, which is why anything touching `ChatBot`, `SaveLoadHandler`, `VRMLoader` etc. goes in Bridge. `IAsyncEnumerable` / `await foreach` compile in Core at this project's API level (verified 2026-10-06).

## Chat

- **Entry point:** `Assets/LLMUnity/Samples/ChatBot/ChatBot.cs`. The LLMUnity sample was modified by upstream and has no asmdef, so it compiles into Assembly-CSharp.
  - `onInputFieldSubmit` (line 251) calls `llmCharacter.Chat(message, onPartial, onDone)` at line 270. `onPartial` receives the *cumulative* reply text, not a delta.
  - `CancelRequests()` (line 316) calls `llmCharacter.CancelRequests()`.
  - `WarmUpCallback()` (line 303) unblocks input after the local model warms up; `Start()` calls `llmCharacter.Warmup`.
  - `ShowLoadedMessages()` (line 237) renders `llmCharacter.chat` as bubbles.
- **Local LLM:** LLMUnity 2.5.1. Scene `LLMCharacter` has `save: ZomeAI`, `saveCache: 1`, `remote: 0`. Useful API: `AddPlayerMessage`, `AddAIMessage`, `AddMessage(role, content)`, `Save(filename)`, `Load(filename)`, `ClearChat`, `SetPrompt(prompt, clearChat)`.
- **Fork hooks (`// QoL:`):** `onInputFieldSubmit` calls `QolChatRouter.Send(llmCharacter, ...)` instead of `llmCharacter.Chat(...)`; `CancelRequests` also calls `QolChatRouter.Cancel()`; `Start` calls `WarmUpCallback()` immediately when a remote provider is active so input isn't blocked on the local model. `QolReloadMessages()` re-renders the bubbles after a character switch; `ShowLoadedMessages` adds the character's greeting bubble to an empty session. `Bubble.GetText`/`SetText` tolerate a destroyed bubble, since a switch can destroy bubbles while a cancelled reply still calls back.
- **Router (`Bridge/QolChatRouter.cs`):** `/char <name>`, `/chars` and `/new` are handled here and never reach a model (`Core/Characters/ChatCommand.cs`; any other text starting with `/` is sent as usual). With provider `upstream-local` it loads the budgeted context into `llmCharacter.chat` and calls `LLMCharacter.Chat`. Otherwise it sends the budgeted context to the provider, then `AddPlayerMessage`/`AddAIMessage`. Finished exchanges go to `CharacterManager.RecordExchange`; a reply that started before a switch or `/new` (checked with `CharacterManager.Generation`) is dropped. Errors show in the AI bubble as `[!] ...` and are not saved. `ReplyDelta`/`ReplyCompleted` events fire for both paths (voice pipeline hook); `Cancelled` fires on every `Cancel()`, including the one at the start of each `Send`; `IsReplying` is true from `Send` until the newest reply's done callback. If characters failed to load, it falls back to `llmCharacter.prompt` + `llmCharacter.chat` and upstream's `ZomeAI.json` save.
- **Context (`Core/AI/ContextBuilder.cs`):** system message = prompt + facts + summary (facts and summary are empty until Phase 8b), then the newest whole user/assistant pairs that fit `qol_settings.json` → `Context` (`TotalTokens` minus `ReplyReserveTokens`, at most `MaxHistoryMessages`). Tokens are estimated at 4 characters each. For the built-in model the total is also capped by `LLM.contextSize`. The prompt is never trimmed; if it alone is over budget, no history is sent and a warning is logged.
- **Context size:** `SettingsHandlerDropdowns` sets `LLM.contextSize`.
- **`localhost` is rewritten to `127.0.0.1`** by `EndpointPolicy`: Unity's Mono tries `::1` first and waits ~2 s per request before falling back, and Ollama, LM Studio and Kokoro listen on IPv4 only (measured 2,040 ms vs 4 ms per request). An IPv6-only server needs `[::1]` in the URL.
- **Model files are gitignored** (`Assets/StreamingAssets/undreamai-*-llamacpp`, `*.gguf`). A fresh clone has no local model.

## System prompt and characters

- `Assets/MATE ENGINE - Scripts/AvatarHandlers/AISystemPromptBinder.cs` reads and writes the prompt file and pushes it into `LLMCharacter.prompt` (`ApplyToLLM`). Upstream's `ApplyToLLM` calls `SetPrompt(s, true)`, which clears the in-memory chat on every keystroke.
- `GetFixedPromptPath()` (line 86) was hardcoded to `%LOCALAPPDATA%\..\LocalLow\Shinymoon\MateEngineX\ZomeAI_prompt.txt`; the fork changed it (`// QoL:`) to `Application.persistentDataPath/ZomeAI_prompt.txt`. It still ignores `--datadir`, so all instances share one prompt file.
- `LLMCharacter.Start` (upstream edit in vendored LLMUnity) also reads `ZomeAI_prompt.txt` into `chat[0]`.
- **Characters (Phase 7a/8a):** `Bridge/CharacterManager.cs` owns the active character. Files: `<DataDirectory>/Characters/<id>/character.json` (name, prompt, greeting) and `Characters/<id>/sessions/<yyyyMMdd-HHmmss>.jsonl` (one message per line, appended as each exchange finishes). The newest session is resumed on launch; `ActiveId` in `qol_settings.json` picks the character. Characters are per instance (`--datadir`).
  - First run: if `Characters/` has no character, `default` is created from `ZomeAI_prompt.txt` (name from its `Name:` line) and `ZomeAI.json` is imported as its first session. Upstream files are only read.
  - `LLMCharacter.save` is set to `""`: the session log replaces `ZomeAI.json` and the KV cache.
  - It runs `Start` at `DefaultExecutionOrder(10000)`, after `LLMCharacter.Start` and `ChatBot.Start`.
  - `LLMCharacter` sits in the chat window, which may first open after startup; its `Awake`/`Start` would then clear the chat and load the prompt file. LLMUnity Runtime is its own asmdef and can't reference Bridge, so `LLMCharacter` got a static hook, `QolLoadCharacter` (`// QoL:`), called at the end of `Awake` and the start of `Start`; `CharacterManager` sets it and reloads the active character there.
  - Hooks (`// QoL:`): `AISystemPromptBinder.Awake` shows the active character's prompt and `Save` writes it to `character.json` with `SetPrompt(s, false)`, so the chat is kept. `DeleteAIHistory.DeleteHistoryFiles` deletes the active character's sessions.
  - `LLMCharacter.CancelRequests` (and upstream's `ChatBot.CancelRequests`) throws when no local model is running; `CharacterManager` wraps it.
  - UI: CHARACTERS section on the AI PROVIDERS page (pick or add, name, greeting, NEW CHAT). No delete or duplicate yet (7b); remove a character by deleting its folder.

## Talking state

- Animator bool `isTalking` drives state `Talk` in `Assets/MATE ENGINE - Animations/AvatarAnimatorController.controller`.
- Set by `ChatBot` (lines 178, 268, 275), `AvatarRandomMessages` (190, 229, 254), `AvatarMinecraftMessages` (317, 383, 399).
- `ChatBot.streamAudioSource` plays a gibberish voice loop while a reply streams (line 267) and fades out in `FadeOutStreamAudio`.
- The Talk clip moves the mouth through `UniversalBlendshapes` (animated `A`..`O` floats), which writes all mouth shapes every `LateUpdate`.
- **Fork hooks (`// QoL:`):** `ChatBot.onInputFieldSubmit` skips `streamAudioSource.Play()` while a voice provider is on, and its done callback leaves `isTalking` on while `QolVoicePipeline.IsSpeaking`; the pipeline clears it when the audio drains.

## Voice (Phase 2)

- **Pipeline (`Bridge/QolVoicePipeline.cs`):** one `DontDestroyOnLoad` object "QoL Voice" (AudioSource + pipeline + `uLipSync`, in that order). Reply deltas go through `Core/Voice/SentenceSplitter` and `SpeechFilter` (drops `*actions*`, `[tags]`, rich text, URLs, emoji; with `Tts.SoundTags` on, sound actions like `*chuckles*` become Chatterbox Turbo tags like `[chuckle]` and those tags are kept), then one TTS request per sentence in order, `Resampler` to `AudioSettings.outputSampleRate`, and a 30 s `PcmRingBuffer`. The AudioSource loops a silent clip; `OnAudioFilterRead` replaces it with the buffer (audio thread), so uLipSync, the next component, analyses the real speech. Not `AudioClip.Create(stream)`: the filter has lower latency and tells exactly when audio drains. Audio is 2D, so it isn't parented to the head.
- **Stops speech:** `QolChatRouter.Cancelled` (new message, `/char`, character switch), grabbing the avatar (`AvatarAnimatorController.isDragging` rising edge, which also cancels the reply), voice turned off, audio device change. A provider error clears the queue so a dead server doesn't time out once per sentence.
- **Lip sync (`Bridge/QolLipSync.cs`):** added to every `AvatarAnimatorController` object at start and on `QolEvents.AvatarLoaded`. Maps uLipSync's A/I/U/E/O ratios × volume to `VRMBlendShapeProxy.ImmediatelySetValue` (VRM 0.x) or `Vrm10RuntimeExpression.SetWeight` (`aa ih ou ee oh`, VRM 1.0). `DefaultExecutionOrder(10000)`: after `UniversalBlendshapes` (0), before `Vrm10Instance` (11000). It writes only while speaking, or while a reply streams with a voice on (keeps the Talk clip from flapping the mouth silently), then releases the mouth once. If the VRM 0.x vowel clips bind nothing (converted models such as Roxanne) or the model has no VRM expressions, mouth blendshapes on its meshes are set directly (0..100): VRChat visemes `vrc.v_aa ih ou e oh` first, then `Aa`/`Ih`/`Ou`/`Ee`/`Oh`, `Mouth_A`.., single letters. Verified 2026-10-16 on Roxanne (Body: `vrc.v_*`, Shoulder Pad: `Aa`, `Oh`). A model with neither gets no lip sync.
- **uLipSync** 3.1.5 (MIT, hecomi) from `https://github.com/hecomi/uLipSync.git?path=Assets/uLipSync#v3.1.5`, locked to commit `65f87c1`; pulls `com.unity.burst` and `com.unity.mathematics` (already in the project). The runtime profile is `Assets/MateEngineQoL/Resources/QolLipSyncProfile.asset`, a copy of the package's sample profile made with **MateEngine → QoL → Create Lip Sync Profile** (package assets aren't in a build unless referenced).
- **Providers:** `Core/AI/OpenAI/OpenAICompatibleTtsProvider` (`POST {base}/audio/speech`, `response_format: wav`, which every compatible server has; Chatterbox-TTS-Server has no `pcm`. Replies may be 16-bit WAV at any rate and channel count (`Core/Voice/WavStreamDecoder`, mixed to mono and resampled to 24 kHz) or raw s16le 24 kHz. Kokoro-FastAPI `:8880/v1`, Chatterbox-TTS-Server `:8004/v1` (VOICE = a clip's file name in its `reference_audio`), OpenAI) and `Core/AI/Fish/FishAudioTtsProvider` (`POST https://api.fish.audio/v1/tts`, `format: pcm`, `sample_rate: 24000`, voice id → `reference_id`, TTS model → `model` header, only plain ids). Shared HTTP handling is `Core/AI/Http/ProviderHttp.cs`: connect/idle timeouts, one-line errors, 10 minutes of audio max per sentence, rejects MP3/Ogg/JSON bodies and non-16-bit WAV. Key slot `tts` (`MEQOL_TTS_API_KEY`).
- **Kokoro-FastAPI** (Windows, no Docker): its lockfile markers say `x86_64`, but Windows reports `AMD64`, so `uv sync --extra gpu-cu128` installs CPU torch. Install `torch==2.8.0+cu128` from `https://download.pytorch.org/whl/cu128` on top and start uvicorn with the venv's Python (not `uv run`, which re-syncs). Bind `--host 127.0.0.1`; the project's `start-gpu.ps1` uses `0.0.0.0`. Measured on an RTX 5070: about 180 ms from a sentence being ready to its first audio.
- **Chatterbox-TTS-Server** v2.0.0 (Windows, no Docker): needs Python 3.10 (`uv python install 3.10`), `requirements-nvidia-cu128.txt` (torch 2.9.0+cu128), then the engine with `--no-deps` from `devnen/chatterbox-v2` pinned to a commit, then `protobuf==4.25.8 --no-deps` (descript-audiotools pins <3.20, onnx 1.16 needs newer). Set `server.host: 127.0.0.1` in its `config.yaml` before the first start; the shipped file says `0.0.0.0`. First request after start takes ~19 s (warm-up), then ~1-1.5 s per sentence on an RTX 5070 with the Turbo model; the reply isn't streamed, so the first sentence's whole synthesis is the latency.
- **Testing without a TTS server:** a loopback PowerShell `HttpListener` on `http://localhost:8880/` that answers `/v1/audio/speech` with Windows SAPI speech as 24 kHz s16le gives real speech for lip sync. Back up `settings.json` before loading another avatar in a test; `LoadVRM` saves the selection.

## Settings and data paths

- **The fork has its own data folder.** `productName` is `MateEngineQoL` (upstream: `MateEngineX`), so `persistentDataPath` is `LocalLow\Shinymoon\MateEngineQoL` and the Steam install's `LocalLow\Shinymoon\MateEngineX` is never written. The exe keeps the name `MateEngineX.exe` because `LaunchMateEngineInstance.executableName` looks for it.
- **First-run import:** `Bridge/SteamDataImporter.cs` runs at `BeforeSceneLoad`. If the fork folder has no `settings.json` and no `qol_import.json`, it copies the Steam folder in (`Core/Import/DataFolderImport.cs`): read-only on the source, never overwrites, skips `Player*.log`, `SteamDRM.token`, `ZomeAI.cache`, rewrites absolute paths inside `.json` files, records the result in `qol_import.json`. Re-run without overwriting via **MateEngine → QoL → Import Steam Data**; to redo it from scratch, delete the fork folder.
- `PlayerPrefs` (registry, keyed by company/product) is not imported: FPS limit, a legacy model-path key, blendshape preset paths, LLMUnity debug flags.

- `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs`: `BaseDir` (line 17) is `persistentDataPath`, or `persistentDataPath/<--datadir>` for extra instances. The fork adds `public static DataDirectory` (`// QoL:`) with the same value, valid after `Awake`.
- `settings.json` holds `SettingsData`; the fork keeps its own files instead of touching `SettingsData`:
  - `qol_settings.json` in `DataDirectory` (per instance): chat provider, preset, base URL, model; active character; context budgets; voice (`Tts`: provider `none`/`openai-compatible`/`fish-audio`, preset, base URL, model, voice, speed, volume, `SkipActions`, `SoundTags` (null = the preset's default: on for Chatterbox, off elsewhere; choosing another preset resets it)); animation library (`Animation`: `LibraryIdles`, `IdleSwitchSeconds`, `Favourites`, `PinnedIdle`); reply tags (`Tags`: `Enabled`, `ExpressionIntensity`, `HoldSeconds`). Written with defaults on first use; voice is off by default.
  - `Characters/` in `DataDirectory` (per instance): see System prompt and characters.
  - `qol_secrets.json` in `persistentDataPath` (shared by all instances): API keys, DPAPI-encrypted for the Windows user (`Bridge/DpapiSecretProtector.cs`). `MEQOL_CHAT_API_KEY` / `MEQOL_TTS_API_KEY` / `MEQOL_STT_API_KEY` override it.
- `Bridge/QolServices.cs` owns the store, settings and `ProviderRegistry`; it initializes lazily on first use.

## Settings UI

- `Bridge/QolAiSettingsPage.cs` builds the **AI PROVIDERS** page at `AfterSceneLoad` by cloning upstream widgets, without scene edits: the `Color Menu` page (layout overridden), the AI section's `Context Length` dropdown, `AiSystemPrompt` input field and labels, and the page's `Back` button. A **PROVIDERS** button is added to the right of the `= AI` section header.
- Clones are stripped of upstream behaviour (`Rewire`: inspector events, `LocalizeStringEvent`, `ButtonLinker`, `UiTooltip`) and keep the template's on-screen scale. Upstream authors some widgets ~4x and scales them down; the scale is computed by multiplying local scales up to `SettingsMenuCanvas`, because the canvas is inactive (scale 0) when the page is built.
- A **VOICE** section (provider: Off / Kokoro (local) / OpenAI / Fish Audio / Custom, server URL, model, voice, key, speed and volume sliders cloned from `= AUDIO/Menu Volumen`, TEST VOICE) sits between the provider rows and CHARACTERS. Slider moves apply live and are saved 0.5 s after the last move.
- An **ANIMATIONS** section (`Bridge/QolAiSettingsPage.Animations.cs`) follows CHARACTERS: LIBRARY IDLES on/off, a CLIP dropdown (favourites first, then by category and name), PREVIEW / FAVOURITE / SET AS IDLE (pins; ROTATE IDLES unpins), RELOAD FOLDER / OPEN FOLDER (`explorer.exe` with `UseShellExecute = false` on Windows), and a status line.
- **Page switching:** upstream's page toggles call `UISetOnOff.SetOnOff`, which *flips* `activeSelf` (the lighting toggle flips `Color Menu` and `Main Menu`). So the QoL page never deactivates `Main Menu`; it hides it with a `CanvasGroup`, and a `PageWatcher` on `SaveLoadHandler`'s object closes the QoL page when `Main Menu` turns off, another page opens, or the settings window closes.
- If upstream renames any of those objects, the page logs `[QoL] AI providers page not installed: UI element not found: <path>` and the rest of the app is unaffected.
- Strings come from the fork's own **`QoL` string table** (`Assets/MateEngineQoL/Localization`) via `QolText.Get`, falling back to English. Edit the English source in `Bridge/Editor/QolLocalizationSetup.cs`, then run **MateEngine → QoL → Update QoL String Table**. A separate collection keeps upstream's `Languages (UI)` tables untouched; creating it added the tables to the `Localization-String-Tables-*` Addressables groups.

## Avatar loading

- `Assets/MATE ENGINE - Scripts/VRMLoader/VRMLoader.cs`: `LoadVRM(string path)` (line 117, `async void`). `FinalizeLoadedModel(GameObject, string, AssetBundle)` (line 240) ends with `SettingsHandlerUtility.ReloadAllSettingsHandlers()` (line 309). A second reload at line 482 covers another load path.
- **Fork hooks (`// QoL:`):** `QolEvents.RaiseAvatarLoaded(model)` at the end of `FinalizeLoadedModel`, `ActivateDefaultModel` and `ResetModel`, so Bridge attaches per-avatar components (lip sync, later the animation player) without editing the template prefab. Listener exceptions are caught.

## Animation

- UniVRM is 0.128.3 (`Assets/MATE ENGINE - Packages/VRM10`). Runtime VRMA support: `Runtime/Components/VrmAnimationInstance/Vrm10AnimationInstance.cs`, `Vrm10PoseLoader.cs`. A VRMA is a pose source for a VRM 1.0 runtime, not a Mecanim `AnimationClip`.
- **VRMA retarget (Phase 4 spike, passed 2026-10-14):** `Bridge/QolVrmaClip` loads a `.vrma` with `VrmAnimationImporter` (`GlbFileParser` → `LoadAsync(ImmediateCaller)`). UniVRM builds a hidden humanoid rig (generated `Avatar` on an `Animator`, plus a legacy `Animation` holding the clip); the rig's Animator is disabled, the box-man mesh hidden, and the legacy `Animation` samples the clip. `Bridge/QolPoseRetarget` on the avatar reads the rig with `HumanPoseHandler.GetHumanPose` and writes the avatar with `SetHumanPose`, blending muscles and body position/rotation (both relative to each rig's root) by a weight that eases in and out.
  - **Execution order:** `QolPoseRetarget` runs in `LateUpdate` at `DefaultExecutionOrder(-200)`: after the Animator and `IKFix.OnAnimatorIK` (the clip overrides the feet while it plays), before `AvatarMouseTracking.LateUpdate` (0), which adds head, spine and eye turns on top of the posed bones, and before `QolLipSync` (10000) and `Vrm10Instance` (11000).
  - Verified in play mode with three-vrm's `test.vrma` (T-pose, right arm drops 90° at 0.5 s, 3 s loop): the arm follows the clip on a VRM 0.x avatar and on "Shadow" (VRM 1.0, `Vrm10Instance` with its default control rig); head tracking still turns the head; `Release()` blends back to upstream's idle.
- **Library (`Bridge/QolAnimationLibrary.cs`, "QoL Animation" object, `DontDestroyOnLoad`):** `Core/Animations/AnimationCatalog` scans `<DataDirectory>/Animations/` (subfolders up to 4 deep, no junctions; at most 500 clips of 64 MB, sidecars 64 KB) and watches it with a `FileSystemWatcher`; changes are applied on the main thread once the folder has been quiet for 0.75 s. Sidecar `<clip>.json` (`Core/Animations/AnimationSidecar`): `name`, `category` (`idle` default, `gesture`, `sit`, ...), `loop` (true), `idleWeight` (1 for idles, else 0), `anchor` (Phase 6), `blendIn`/`blendOut` (0.4 s, max 5), `tags`; bad fields fall back to defaults. Clip ids are paths relative to the folder.
  - **Idle scheduler:** while `AvatarAnimatorController.isIdle && !isDragging && !isDancing` (upstream's `isIdle` is true only in the base layer's `Idle` state, so sitting, sleeping, eating, hover reactions and big-screen states all count as busy), it plays a weighted idle (`IdlePicker`, never the same one twice in a row) on the base layer and switches every `IdleSwitchSeconds` (45 s) or at the end of a one-shot clip; `PinnedIdle` keeps one. Drag and dance release at once; other states blend out. Talking is on the separate "Speak Layer", so the idle keeps playing under speech. Up to 8 loaded clips are cached; a clip that fails to load is skipped until the folder changes.
  - **Gestures:** `PlayGesture(id)` plays a clip once on the upper-body layer (`QolPoseRetarget`: muscles of the spine and everything above; legs, hips and body placement stay with the base). `PlayAction(name)` finds the gesture for an `[action:name]` tag; refused while dragged or dancing.
  - **Test clips:** `Tools/VrmaGen/make_vrma.py <rig.vrma> <out>` writes procedural clips (Relaxed Sway, Look Around, Wave) with sidecars, using a T-pose rig such as three-vrm's `examples/models/test.vrma` (MIT). Rotations are authored in world space and converted to the rig's local axes.
- `AvatarDancePlayer` loads `AnimationClip`s from AssetBundles (`AssetBundle.LoadFromFile`, line 747) and swaps them into a placeholder clip found by `FindPlaceholderClip` (line 710). Reuse this for `.me` animation clips.
- Animator parameters other scripts rely on: `isIdle`, `isDragging`, `isDancing`, `isTalking`, `IdleIndex`, `DanceIndex`, `isMale`, `isFemale`.

## Reply tags (Phase 5)

- **Parser (`Core/Tags/TagParser.cs`):** `QolChatRouter` runs every reply (remote and `upstream-local`) through one parser per reply before the bubble, `ReplyDelta` (voice) and the saved history. Text after `[` is held until `]`, a line break, a second `[`, 64 characters, or a character that can't be in a key (before the colon only letters, `_`, spaces), then released as text. `[key:value]` with key `emotion`/`mood`/`face`/`expression`/`feeling` or `action`/`gesture`/`anim`/`animation`/`motion` raises `QolChatRouter.TagReceived`; other `[key:value]` tags are dropped; brackets without a colon (`[laugh]` for Chatterbox, `[1]`) stay. Names are normalised to `[a-z0-9_]`, max 40. Spaces left by a tag are collapsed. For the local model the stored `llm.chat` reply is stripped too.
- **Prompt:** `QolReplyTags.PromptInstructions()` (`Core/Tags/ReplyTagVocabulary.PromptText`) goes into `ContextInput.Instructions`, appended after the character prompt at request time and counted with it; the character file and `AISystemPromptBinder`'s prompt file are untouched. It lists the emotions (`happy sad angry surprised relaxed neutral`) and up to 30 gestures (clips with `category: gesture`, by normalised `name`). Empty when tags are off.
- **Expressions (`Bridge/QolExpression.cs`):** added on first use to the active `AvatarAnimatorController`'s object, `DefaultExecutionOrder(9990)` (after `UniversalBlendshapes`, before `QolLipSync`). VRM 0.x: preset clip (`Joy Angry Sorrow Fun`, `surprised` and custom names by clip name) via `ImmediatelySetValue`; VRM 1.0: `Vrm10RuntimeExpression.SetWeight` (presets incl. `surprised`, custom by name). For keys `UniversalBlendshapes` also drives (Face Layer hover faces), it writes `max(ours, upstream field)`. If the VRM clip is missing or binds nothing (converted models such as Roxanne, whose 17 preset clips are all empty), same-named mesh blendshapes (`Joy`/`Happy`/`Smile`, `Sorrow`/`Sad`, `Surprised`, ...) are set directly (0..100). Blend in ~0.3 s, hold while `IsReplying` or `IsSpeaking` plus `Tags.HoldSeconds` (4), fade ~1 s; `neutral` and `QolChatRouter.Cancelled` release. Aliases (joy→happy, sorrow→sad, ...) in `ReplyTagVocabulary.CanonicalEmotion`.
- **Actions** play as soon as the tag streams in, which is usually before the voice starts.
- **Settings:** `Tags` in `qol_settings.json`: `Enabled` (true), `ExpressionIntensity` (0.8), `HoldSeconds` (4). EMOTION TAGS on/off in the ANIMATIONS section; the status line lists the gesture names the AI can use.
- Verified in play mode (2026-10-16) on Roxanne (VRM 0.x): `[emotion:joy]` → `Body.Joy` 0 → 80 → back to 0 after the hold; `[action:greeting]` found Wave by its sidecar tag and played it.

## Sitting

- `AvatarWindowHandler` sits on other windows, `AvatarTaskbarController` on the taskbar. Phase 6 anchored clips plug into these instead of a new desktop query layer.

## Input

- Old Input Manager (`activeInputHandler: 0`). Unity `Input` only sees keys while the mascot window has focus.
- Global input precedent: `APIs/WinApi.cs` `SetWindowsHookEx`; `AvatarBigScreenScreenSaver` uses `GetAsyncKeyState`. Push-to-talk (Phase 3) uses Win32, guarded by `#if UNITY_STANDALONE_WIN`.

## Build and tests

- Mono backend, .NET Framework API level, Windows x64.
- Upstream ships `EditorBuildSettings` with no scenes. **MateEngine → QoL → Add Main Scene To Build** adds `Mate Engine Main.unity`; **Build Windows Player** calls it first.
- CLI build: `Unity.exe -batchmode -quit -projectPath . -executeMethod MateEngineQoL.Build.QolBuild.BuildWindows` (optional `-qolBuildOutput <path>`, default `Builds/Windows/MateEngineX.exe`).
- **Core tests: `dotnet test Tools/CoreTests`** (seconds, no Unity needed). Core's asmdef has `noEngineReferences: true`, so it stays plain C# and the same sources build under .NET 8 at C# 9 (Unity 6's language version). Keep Core engine-free; anything needing `UnityEngine` belongs in Bridge.
- Unity's own test runner hangs in this project: `Unity.PerformanceTesting.Editor.TestRunBuilder` (`IPrebuildSetup`, from `com.unity.test-framework.performance`) blocks the main thread on every run, whether batchmode `-runTests`, `unity test`, or `unity command run_tests` in a live Editor. The `Core.Tests` asmdef stays so the tests also show in Unity's Test Runner window, but don't rely on it.
- **Unity CLI** (`unity`, installed at `%LOCALAPPDATA%\Unity\bin`): the project has `com.unity.pipeline` installed, so `unity open .` starts an Editor that the CLI can drive (`unity status`, `unity command <name>`, `unity recompile`, `unity list`). Its server listens on `127.0.0.1` only. `unity test` / `unity build` / `-executeMethod` need the Editor closed.
- `Assets/Editor/PostBuildCopy.cs` copies `steam_api64.dll` and `steam_appid.txt` next to the exe.
- Batchmode fails if the project is open in the Editor.

## Known editor side effects

Do not commit these; they are environment noise, not changes.

- **Fresh `Library`:** UniVRM imports `.vrm` files before the MToon shaders exist and fails with `ArgumentNullException: Parameter name: Shader`. Run **MateEngine → QoL → Reimport VRM Assets**. The import can also rewrite extracted VRM assets under `Assets/MATE ENGINE - Avatar/` (mostly line endings, some texture `.meta` settings).
- **Every Editor open:** `Assets/AddressableAssetsData/link.xml` (+ `.meta`) gets deleted. Addressables generates it during builds and removes it afterwards; upstream committed a stale copy.
- **Poiyomi/Thry:** writes a `Thry/` cache folder at the repo root (gitignored).
- `ProjectSettings/ProjectSettings.asset` and `UserSettings/` can show line-ending-only diffs.
- **Play mode:** `ThemeManager` tints the shared `Assets/MATE ENGINE - Scripts/ThemeManager/*.mat` materials at runtime, so they show as modified after playing in the Editor.
- `unity command capture_game_view --save_path Temp/x.png` actually writes to `Assets/Temp/x.png`; delete it afterwards.
- **Tracked but ignored:** upstream commits `UserSettings/Layouts/default-6000.dwlt` (Editor layout) and `Assets/AddressableAssetsData/Windows/addressables_content_state.bin` (rewritten by every build). `.gitignore` lists them, but that has no effect on tracked files, so hide local changes per clone with `git update-index --skip-worktree <both paths>`. If a pull or rebase stops on one of them, run `git update-index --no-skip-worktree <path>`, discard the local copy, pull, then set it again.

## Testing in the live Editor

With the Editor open via `unity open .`: `unity command editor_play`, then `unity command eval_file --file <script.cs>` to drive the app (e.g. open `SettingsMenuCanvas`, invoke buttons, call `QolChatRouter.Send`), and `unity command capture_game_view` for screenshots. For remote-provider tests without a real API, run a fake OpenAI-compatible SSE server bound to `127.0.0.1` and point a Custom provider at it. Back up the fork's `ZomeAI.json` first; remote replies are saved to chat history.

## Linux port (later)

Keep new platform code behind `#if UNITY_STANDALONE_WIN` with a stub `#else`. Known Windows-only spots to revisit: `WinApi`/`DwmApi`, push-to-talk hotkey, DPAPI secrets. The voice pipeline is portable (Unity audio, uLipSync with Burst). (`AISystemPromptBinder.GetFixedPromptPath` is fixed.) The Steam import finds the Steam folder as a sibling under the company folder, which also works for `~/.config/unity3d/Shinymoon/` on Linux.
