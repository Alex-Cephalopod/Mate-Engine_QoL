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
- **Context size:** `SettingsHandlerDropdowns` sets `LLM.contextSize`.
- **Model files are gitignored** (`Assets/StreamingAssets/undreamai-*-llamacpp`, `*.gguf`). A fresh clone has no local model.

## System prompt

- `Assets/MATE ENGINE - Scripts/AvatarHandlers/AISystemPromptBinder.cs` reads and writes the prompt file and pushes it into `LLMCharacter.prompt` (`ApplyToLLM`).
- `GetFixedPromptPath()` (line 86) is hardcoded to `%LOCALAPPDATA%\..\LocalLow\Shinymoon\MateEngineX\ZomeAI_prompt.txt`. It ignores `--datadir` and is Windows-only. Character profiles (Phase 7) must write the same prompt; the Linux port will need to fix this path.

## Talking state

- Animator bool `isTalking` drives state `Talk` in `Assets/MATE ENGINE - Animations/AvatarAnimatorController.controller`.
- Set by `ChatBot` (lines 178, 268, 275), `AvatarRandomMessages` (190, 229, 254), `AvatarMinecraftMessages` (317, 383, 399).
- `ChatBot.streamAudioSource` plays a gibberish voice loop while a reply streams (line 267) and fades out in `FadeOutStreamAudio`.

## Settings and data paths

- `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs`: `BaseDir` (line 17) is `persistentDataPath`, or `persistentDataPath/<--datadir>` for extra instances. It is a **private** property; Phase 1 makes it readable (one-line change) so `qol_settings.json` lands in the same folder.
- `settings.json` holds `SettingsData`; QoL keeps its own `qol_settings.json` / `qol_secrets.json` next to it to avoid touching `SettingsData`.

## Avatar loading

- `Assets/MATE ENGINE - Scripts/VRMLoader/VRMLoader.cs`: `LoadVRM(string path)` (line 117, `async void`). `FinalizeLoadedModel(GameObject, string, AssetBundle)` (line 240) ends with `SettingsHandlerUtility.ReloadAllSettingsHandlers()` (line 309). A second reload at line 482 covers another load path.
- Phase 2 adds one `QolEvents.RaiseAvatarLoaded(model)` line after the reload so Bridge can attach lip sync and animation components without editing the template prefab.

## Animation

- UniVRM is 0.128.3 (`Assets/MATE ENGINE - Packages/VRM10`). Runtime VRMA support: `Runtime/Components/VrmAnimationInstance/Vrm10AnimationInstance.cs`, `Vrm10PoseLoader.cs`. A VRMA is a pose source for a VRM 1.0 runtime, not a Mecanim `AnimationClip`.
- `AvatarDancePlayer` loads `AnimationClip`s from AssetBundles (`AssetBundle.LoadFromFile`, line 747) and swaps them into a placeholder clip found by `FindPlaceholderClip` (line 710). Reuse this for `.me` animation clips.
- Animator parameters other scripts rely on: `isIdle`, `isDragging`, `isDancing`, `isTalking`, `IdleIndex`, `DanceIndex`, `isMale`, `isFemale`.

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

## Linux port (later)

Keep new platform code behind `#if UNITY_STANDALONE_WIN` with a stub `#else`. Known Windows-only spots to revisit: `AISystemPromptBinder.GetFixedPromptPath`, `WinApi`/`DwmApi`, push-to-talk hotkey.
