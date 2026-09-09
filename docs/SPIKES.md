# Day-0 spikes

Measurements that set the plan's numbers. Throwaway code lived in the session scratchpad; only the results are kept.

## Ollama Cloud (2026-09-09, house key from hub/miraside/.env.local)

19 models listed on the account, including `gpt-oss:120b`, `gpt-oss:20b`, `gemma4:31b`, `qwen3.5:397b`, `glm-5.3-flash`.

Five short dictation fixtures (pt, en, mixed, fillers, one word), the v1 cleanup system prompt, `temperature 0.1`, `max_tokens 512`, sequential:

| model | reasoning_effort | p50 | max | completion tokens (5 fixtures) | reasoning in `message.reasoning` | quality |
|---|---|---|---|---|---|---|
| `gemma4` (31b) | `none` | **706 ms** | 910 ms | 2–20 | none | clean on all 5; digits for numbers; honoured "new line" |
| `gpt-oss:120b` | `low` | 837 ms | 1099 ms | 30–104 (60–100 of them reasoning) | 76–336 chars | clean on all 5 |
| `gpt-oss:120b` | `none` | 779 ms | 1085 ms | 95–184 | still reasons (`none` is ignored) | clean |
| `qwen3.5` (397b) | `none` | 1498 ms | 1877 ms | 2–22 | none | clean, slower |
| `gpt-oss:20b` | `low` | 1806 ms | 2201 ms | 29–118 | yes | left "hã" in place once; slower than 120b |
| `glm-5.3-flash` | `none` | 2308 ms | 2782 ms | 55–225 | **leaked into `content` with a `</think>` marker** | unusable without the strip guard |

Consequences:
- **Default `LLM_MODEL=gemma4`, `LLM_REASONING=none`.** Fastest, smallest output, no reasoning overhead; `gpt-oss:120b` at `low` is the documented alternative. The M1 bench re-checks on 20 fixtures.
- The `<think>` strip + reject guard is not theoretical: `glm-5.3-flash` puts its whole reasoning in `content`.
- gpt-oss ignores `reasoning_effort: none` and emits 60–100 reasoning tokens per short request, which is why the token cap floors at 512.

Concurrency probe on `gpt-oss:120b`: 3 parallel → all ok, wall 1.2 s; **5 parallel → all ok, wall 1.15 s**, no rate-limit headers. The account tier allows at least 5 in flight, so `LLM_CONCURRENCY=5`.

## WhisperKit on the M2 (2026-09-09, `argmax-oss-swift` 0.18.0, model `large-v3-v20240930_turbo_632MB`)

Throwaway SwiftPM executable; `DecodingOptions(temperature: 0, temperatureFallbackCount: 0, usePrefillPrompt: true, detectLanguage: true, withoutTimestamps: true)`, default compute units, no prompt tokens.

| step | time |
|---|---|
| first run: download 624 MB + CoreML specialization + prewarm | **262 s** (one-time; the onboarding must show progress) |
| `en.wav` 12.5 s, first transcription after load | 1.72 s |
| `en.wav` 12.5 s, second transcription | **0.92 s** |
| `pt-synthetic.wav` 11.3 s (English TTS voice reading Portuguese) | 0.88 s |
| `silence.wav` 6 s | 0.46 s → hallucinated `you` |

Consequences:
- Steady-state ASR on this M2 is **~0.9 s** for a 10–12 s clip, better than the 1.2–2 s the plan assumed. M3 acceptance: an 8 s clip pasted within **1.3 s** of release.
- The first transcription after launch is ~2× slower even after `prewarm`; the coordinator should run one throwaway transcription of 1 s of zeros right after `prepare()`.
- Silence produced `you`: the energy gate before Whisper is required, not optional.
- Without prompt tokens, "Miraside" came out as "Miracyte" and "Ollama key" as "Olamaki" — the dictionary-as-prompt and the server-side dictionary earn their place.
- No pt-PT TTS voice is installed on this Mac, so real Portuguese detection/accuracy is verified in M3 with a live recording, not a fixture.
- WhisperKit's default `downloadBase` is `~/Documents/huggingface`, layout `models/<repo>/openai_whisper-<variant>`; the app passes its own Application Support base. The spike's copy at `~/Documents/huggingface/models/argmaxinc/whisperkit-coreml/` (624 MB) can be deleted or moved into the app's models folder to skip the first download.

## Event tap under secure input (pending — needs a permission grant, run by Miguel)

## VPS (2026-09-09, `ssh -i ~/.ssh/<SSH_KEY> vps`)

`vps` = <VPS_IP>, Ubuntu 24.04.4, **2 vCPU / 7.8 GB RAM** (not the 4 vCPU / 16 GB assumed during planning), 73 GB free, Docker 29.4, root shell, passwordless sudo.

| container | ports | note |
|---|---|---|
| `<proxy-container>` | `0.0.0.0:80`, `0.0.0.0:443` | **the public reverse proxy** — docker provider, `exposedbydefault=false`, entrypoints `web` (redirects to https) / `websecure`, certresolver `mytlschallenge` (TLS-ALPN), network `<proxy-network>` |
| `n8n-n8n-1` | `127.0.0.1:5678` | routed as `n8n.miraside.co` via labels |
| `arwatches-openwa` | `127.0.0.1:2785` | loopback only, as its README says |
| `arwatches-worker` | exposed 4000 only | not published |
| `deal-pipeline` | none | `/opt/miraside/demos/deal-pipeline` |

ufw allows 22, 80, 443, 8642, 9119. Nothing needs opening. The ARwatches docs' "no inbound path" describes the ARwatches containers, not the box.

Consequences: **Caddy dropped**; `voice-api` joins `<proxy-network>` with Traefik labels; deploy dir `/opt/miraside/voice`; memory limit 512 MB is generous on 7.8 GB but keep it. The 2 vCPU figure makes the on-device Whisper decision final for this box.

Known: a `speaches` container (`arwatches-speaches`, faster-whisper CPU) is defined in the ARwatches compose but was not running at audit time.
