# Mate Engine QoL plan

The plan lives in a shared doc and is the source of truth for scope, architecture and progress:
[Mate Engine QoL — Fork Plan](https://claude.ai/artifact/ETNpSqHqQ5JFPNa2RbsUpV)

- **Fork Plan** tab: goals, the six features, settings design, risks, references.
- **Phase plan** tab: Phase 0 findings, accepted plan changes, build order, and a checklist per phase with the files each task touches.

Tick checkboxes in the doc, not here. Code-level hook points are in [HOOKS.md](HOOKS.md).

## Features

1. AI provider layer: swappable local and remote chat, TTS and STT.
2. Voice pipeline: streamed TTS with uLipSync on VRM 0.x and 1.0.
3. Animation library: runtime `.vrma` and `.me` clips from a folder.
4. Emotion and action tags in replies drive expressions and gestures.
5. Character profiles and loadouts.
6. Memory and conversation resume.

## Build order

| Step | Phase | Needs |
| --- | --- | --- |
| 1 | 0 Fork, build, assemblies, compile spike | — |
| 2 | 1 Provider layer and remote chat (with minimal QoL settings tab) | 0 |
| 3 | 7a Profiles + 8a session log and `ContextBuilder` | 1 |
| 4 | 2 Voice out | 1 |
| 5 | 3 Local voice and voice in | 2 |
| 6 | 4 Animation library (starts with the VRMA retarget spike) | 0 |
| 7 | 5 Emotion and action tags | 1, 4 |
| 8 | 6 Anchored sitting and polish | 4 |
| 9 | 7b Full loadouts | 2, 4 |
| 10 | 8b Long-term memory | 8a |
| later | Linux port of the fork | all |

## Accepted decisions (2026-10-06)

- Base is the Windows upstream (shinyflvre/Mate-Engine, Unity 6000.2.6f2). Not contributing back for now, so upstream files may be changed where it helps; keep those edits deliberate and marked `// QoL:` so upstream merges stay manageable. A Linux port of the finished fork comes after; keep `#if UNITY_STANDALONE_WIN` guards.
- Code split: `MateEngineQoL.Core` asmdef (no upstream types, EditMode-tested) and `Bridge` in Assembly-CSharp.
- Minimal QoL settings tab ships in Phase 1 and grows per phase.
- Sitting reuses `AvatarWindowHandler` and `AvatarTaskbarController`.
- VRMA plays through a `HumanPoseHandler` pose retarget, gated by a Phase 4 spike.
- `isTalking` stays on while TTS audio plays; only the gibberish `streamAudioSource` is muted.
