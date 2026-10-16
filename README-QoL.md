# Mate Engine QoL

An unofficial fork of [Mate Engine](https://github.com/shinyflvre/Mate-Engine) (Windows, Unity 6000.2.6f2) that turns the desktop mascot into a character you can talk to. It adds swappable AI providers, spoken replies with lip sync, an animation library, and replies that change the avatar's face and gestures. Everything upstream does still works.

Not affiliated with or endorsed by the Mate Engine authors. See [Licence](#licence) before sharing anything built from this repo.

## Features

| Feature | What it does |
| --- | --- |
| AI providers | Chat through any OpenAI-compatible server (Ollama, LM Studio, OpenRouter, OpenAI, DeepSeek, ...) or keep the built-in local model. |
| Characters | Each character has its own prompt, greeting and saved conversation. Type `/chars` to list them, `/char <name>` to switch, `/new` to start over. |
| Voice | Replies are spoken sentence by sentence while they stream, with lip sync on VRM 0.x and 1.0. Kokoro and Chatterbox run on your PC; OpenAI and Fish Audio are online. |
| Animation library | Drop `.vrma` clips into a folder: idles rotate while the avatar stands, gestures play on request, sit clips replace the pose on the taskbar or a window edge. |
| Reply tags | The AI can begin a reply with `[emotion:happy]` and add `[action:wave]`. The face and gesture change; the tags are never shown or spoken. |

All of this is set up under **Settings > AI > PROVIDERS**.

## Setting up providers

Every server below that runs on your PC listens on `127.0.0.1` only. API keys are stored encrypted for your Windows user in `qol_secrets.json`, or read from `MEQOL_CHAT_API_KEY` / `MEQOL_TTS_API_KEY`.

### Chat

| Provider | SERVER URL | Key |
| --- | --- | --- |
| Ollama | `http://127.0.0.1:11434/v1` | none |
| LM Studio | `http://127.0.0.1:1234/v1` | none |
| OpenRouter | `https://openrouter.ai/api/v1` | required |
| OpenAI | `https://api.openai.com/v1` | required |
| DeepSeek | `https://api.deepseek.com/v1` | required |
| Built-in | (none) | none; uses upstream's bundled model |

Pick a preset, set MODEL to a model the server has (for Ollama, a name from `ollama list`), then press **TEST CONNECTION**. If the test fails, the message says what to check: a local server that isn't running, a missing or wrong key, or a wrong URL or model name.

### Voice

| Preset | Runs | Notes |
| --- | --- | --- |
| Kokoro (local) | [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) on port 8880 | Fast, many stock voices. |
| Chatterbox (local) | [Chatterbox-TTS-Server](https://github.com/devnen/Chatterbox-TTS-Server) on port 8004 | Copies a voice from a 10-20 s clip; VOICE is the clip's file name in its `reference_audio` folder. With the Turbo model, `[laugh]`, `[chuckle]`, `[sigh]`, `[gasp]`, `[cough]`, `[clear throat]`, `[sniff]`, `[groan]` and `[shush]` play as sounds. |
| OpenAI | online | Paid per use. |
| Fish Audio | online | `s2.1-pro-free` costs nothing; VOICE takes a voice model id from fish.audio. |
| Custom | any OpenAI-compatible `/audio/speech` server that returns WAV | |

Text between `*asterisks*` is shown but not spoken (set `Tts.SkipActions` to `false` in `qol_settings.json` to hear it). With Chatterbox, an action that names a sound (`*chuckles*`, `*sighs softly*`) plays that sound, and the AI is told to put sounds in brackets and keep actions in asterisks.

Speech-to-text and push-to-talk are not in this release.

## Animation library

Clips live in `%USERPROFILE%\AppData\LocalLow\Shinymoon\MateEngineQoL\Animations` (**OPEN FOLDER** in the ANIMATIONS section). New and changed files are picked up without a restart. A clip may have a `.json` file with the same name next to it:

```json
{
  "name": "Edge Sit",
  "category": "sit",
  "anchor": "windowTopEdge",
  "loop": true,
  "blendIn": 0.4,
  "blendOut": 0.4,
  "tags": ["relaxed"]
}
```

| Field | Meaning | Default |
| --- | --- | --- |
| `category` | `idle` (plays while standing), `gesture` (the AI can play it with `[action:name]`), `sit` (replaces the sit pose) | `idle` |
| `anchor` | For sit clips: `taskbar` or `windowTopEdge`. Leave it out to use the clip on both. | none |
| `idleWeight` | How often an idle is picked, relative to the others; 0 never | 1 for idles, else 0 |
| `loop` | Loop the clip, or play it once | `true` |
| `blendIn`, `blendOut` | Seconds to fade in and out (0 to 5) | 0.4 |
| `tags` | Extra names a gesture answers to | none |

Upstream still decides where the avatar sits; a sit clip only changes the pose, and the hips stay where upstream put them. Without a sit clip, upstream's own sit animations play. Dragging and dancing always take over.

## Your data

The fork keeps its data in `LocalLow\Shinymoon\MateEngineQoL`, apart from the Steam version's `MateEngineX` folder. On first start it copies your Steam settings, avatars and mods across once; the Steam folder is never written.

## Building from source

1. Install Unity 6000.2.6f2 and open the project.
2. Run **MateEngine > QoL > Build Windows Player**, or from a shell with the Editor closed:
   `Unity.exe -batchmode -quit -projectPath . -executeMethod MateEngineQoL.Build.QolBuild.BuildWindows`
3. The build lands in `Builds/Windows/MateEngineX.exe`.

The fork's own tests run without Unity: `dotnet test Tools/CoreTests`. Code layout, hook points and known editor side effects are in [docs/HOOKS.md](docs/HOOKS.md); security rules are in [docs/SECURITY.md](docs/SECURITY.md).

## Licence

Mate Engine is under the [MateEngine Pro License v2.1](LICENSE.md), and so is this fork. In short:

- The complete source of any modified version must be public, under the same licence, on a free code-hosting site such as GitHub.
- No commercial use, and no distribution on Steam, itch.io, the Microsoft Store or any other store or binary platform, free or paid, without written permission from the copyright holder.
- Keep the copyright notice and licence text with every copy.
- Upstream's audio, visual and UI assets are © Shiny; see [NOTICE.txt](NOTICE.txt).

Third-party parts added by this fork:

| Component | Licence |
| --- | --- |
| [uLipSync](https://github.com/hecomi/uLipSync) 3.1.5 (lip sync) | MIT |
| UniVRM's VRM Animation importer (already in upstream) | MIT |

The voice servers (Kokoro-FastAPI, Chatterbox-TTS-Server) and chat servers are separate programs you install yourself; they are not part of this repo or its builds, and each has its own licence. Voice clips you clone with Chatterbox must be ones you have the right to use.
