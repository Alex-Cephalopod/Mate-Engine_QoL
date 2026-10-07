# Security rules for the QoL fork

This fork adds network clients, API keys, external processes and user-supplied files to an app that runs all day on the desktop. These rules apply to every phase. If a task seems to need breaking one, stop and decide explicitly; don't work around it.

## Secrets

- API keys live only in `qol_secrets.json` (in `SaveLoadHandler`'s `BaseDir`, gitignored) or environment variables (`MEQOL_*`). Never in `qol_settings.json`, character loadouts, exported `.zip` files, chat history, logs or the repo.
- Never log a key, an `Authorization` header or a full request body. Log provider id, URL host, status code and timing only.
- Key fields in the UI are masked and never echoed into chat bubbles or error messages.
- Sending a key over plain `http://` is refused unless the host is `localhost` / `127.0.0.1` (Ollama, LM Studio). Credentials embedded in a URL are refused. (`Core/AI/Http/EndpointPolicy.cs`, tested.)
- Implemented: `qol_secrets.json` is encrypted with Windows DPAPI for the current user plus app-specific entropy. A copied file is unreadable for another account or PC; a file that can't be decrypted is ignored with a "re-enter your keys" warning instead of crashing. This does not protect against malware running as you. The Linux port stores keys as plain base64 until a keyring integration exists. Stored keys are never shown again in the UI; it only shows "Key saved".

## Network

- The app opens **no listening sockets reachable from other machines**. Anything that must listen binds `IPAddress.Loopback`.
  - Fixed: `AvatarMinecraftMessages` bound UDP 32145 on all interfaces; now loopback with a capped queue.
  - Keep LLMUnity's `LLM.remote` off. When on, it serves on `0.0.0.0:13333`.
- Outbound calls go only to the base URL the user configured for each provider. No telemetry, no auto-update, no other endpoints.
- HTTPS for every remote provider; certificate validation is never disabled.
- Every request has a timeout and a cancellation token. Response bodies and SSE streams have a size cap so a bad server can't exhaust memory. Implemented for chat: 30 s connect, 60 s idle between chunks, 1 MB per SSE line, 200,000 characters per reply, 4 KB error bodies (shown as one trimmed line).

## Model output is untrusted input

- LLM replies, STT transcripts and remote JSON are data. They never become file paths, process arguments, URLs, shell text or code.
- Tag parsing (Phase 5) is an allow-list: `emotion` maps to a fixed set of expressions; `action` only looks up clips already in the loaded library. Unknown tags are dropped.
- Text shown in bubbles has Unity rich-text tags stripped or escaped.

## External processes (Piper, whisper.cpp)

- Run only binaries at a path the user chose in settings. Never download and run binaries automatically.
- Start with `ProcessStartInfo` using `UseShellExecute = false`. Pass arguments built from settings values, never from model or network text. No `cmd.exe /c`, no PowerShell.
- Temp files go in a per-run folder under `Application.temporaryCachePath` and are deleted afterwards.
- If binaries are ever shipped or auto-fetched, they come from the project's official release page with a pinned SHA-256 checked before first run.

## Files from users and mods

- AssetBundles (`.me`, `.unity3d`) cannot carry new code, but they can instantiate any existing component with crafted data. Only load them from the user's own folders, which is upstream's current behaviour; don't add remote fetching.
- Character `.zip` import: reject absolute paths and `..` entries (zip-slip), cap total extracted size and file count, extract only the expected file types (`.json`, `.vrm`, `.png`, `.jpg`).
- JSON from disk or network is parsed with Newtonsoft using default `TypeNameHandling.None`. Never enable `TypeNameHandling` or binary serializers.

## Dependencies

- Add packages only from their official source (Unity registry, NuGet.org, the author's GitHub), pinned to a version or tag, not a branch.
- Note every new dependency, its licence and source in the PR or commit message.
- Prefer code already in the project (Newtonsoft, NAudio, UnityWebRequest/HttpClient) over adding a new library.

## Working rules for Claude Code sessions

- Never commit or push without the user's go-ahead. Never force-push.
- Stage files explicitly; never `git add -A` (editor side effects, see `HOOKS.md`).
- Don't run downloaded binaries or scripts. Untrusted downloads go in their own empty folder, outside the repo.
- Don't paste secrets into the plan doc, commits, logs or chat.
