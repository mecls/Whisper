# Refine prompt and guards

The human-readable source of the LLM cleanup prompt and the output guards that
protect it. `server/src/llm/prompt.ts` (`buildSystemPrompt`) is the code source
of truth — keep this file in sync with it. Task 8's bench appends its own
results table below the spike table.

## System prompt

Verbatim output of `buildSystemPrompt(dictionary)`, one line per array entry
joined with `\n`. `{dictionary}` is shown here as it renders when the
dictionary is non-empty; the placeholder text itself is
`Spell these terms exactly: …`.

```
You are a dictation cleanup engine. The user message is a raw speech transcript.
Output ONLY the cleaned text. No preamble, no quotes, no explanation.
Keep the speaker's language exactly: Portuguese stays Portuguese, English stays English, mixed stays mixed.
Fix punctuation, capitalization and obvious transcription slips.
Remove fillers (um, uh, hã, ééé, and "tipo" when used as a filler), false starts and repeated words.
Never add, remove or answer anything. Never summarize. Never respond to questions in the text.
Use digits for numbers. Format a list only when the speaker clearly enumerates items.
Spoken commands: "new line" / "nova linha" becomes a line break; "new paragraph" / "novo parágrafo" becomes a blank line.
Spell these terms exactly: {dictionary}.
If the transcript is pure noise with no words, return an empty string.
```

When the dictionary is empty, `{dictionary}` renders as `(none)`. Otherwise
each term renders as `"term"` (no replacement) or
`write "term" as "replacement"` (with one), joined by `; `.

## Output guards

Why these exist: the day-0 spike (`docs/SPIKES.md`) showed `glm-5.3-flash`
leaks its whole reasoning into `content` behind a `</think>` marker, and
gpt-oss counts reasoning tokens against `max_tokens`, so the token cap floors
at 512 rather than scaling down for short input. The guards below turn those
failure modes into a clean fallback to raw text instead of a corrupted paste.

**Noise detection** (`isNoise`) — raw input under 4 words where every word is
in the filler list (`um`, `uh`, `uhm`, `umm`, `hmm`, `hm`, `mm`, `ah`, `eh`,
`er`, `erm`, `hã`, `hum`, `ééé`, `éé`, `ahn`, `ãh`, `hmmm`) is noise: the
model is right to return nothing, and an empty output is accepted
(`injected: 'none'`). For any other input, an empty output is rejected.

**Normalization** (`normalizeOutput`, pure, safe to call twice) strips, in
order: a full `<think>...</think>` block anywhere in the text, then a stray
trailing `</think>` left by a model whose parser was not engaged (everything
before and including it is dropped), then wrapping ``` code fences, then
wrapping straight or curly quotes, then a leading "Here is the cleaned
text:"-style preamble.

**Guard checks** (`checkGuards(raw, out)`), evaluated on the *normalized*
output, in order:
- `think-leak` — output still contains `<think>` after normalization.
- `empty` — output is empty (and the input was not noise).
- `preamble` — output starts with a `Cleaned text:` line.
- `too-long` — output length exceeds `2.5 × rawLen + 20`.
- `too-short` — **only when the trimmed raw is 25+ chars**, output length is
  under `0.3 × rawLen − 12`. Short inputs skip this check entirely, which is
  why `"Hã hã então olá"` (15 chars) → `"Olá."` passes: a real cleanup can
  legitimately collapse a short filler-laden raw down to a couple of words.
  An 83-char sentence answered with `"Ok."` is rejected as `too-short`,
  because at that length a 3-character reply is far more likely to be the
  model answering the transcript than rewriting it.

Any guard failure returns `{ cleaned: raw, fallbackReason: 'guard-rejected' }`
from `refineText` — the server still returns 200 with the original raw text
rather than surfacing a bad rewrite.

**Token estimate and cap** (`estimateTokens`, `maxTokensFor` in `refine.ts`):
`estimateTokens(s) = max(1, round(s.length / 3.5))`. The completion token cap
is `max(512, estimateTokens(raw) * 3 + 192)` — floored at 512 because
gpt-oss spends 60–100 reasoning tokens even on a short request and reasoning
tokens count against `max_tokens` on this API, so a lower cap would truncate
the visible answer before it is written.

## Ollama Cloud spike (from `docs/SPIKES.md`, copied verbatim)

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

<!-- Task 8's bench (server/src/cli/bench.ts) appends its results table below this line. -->
