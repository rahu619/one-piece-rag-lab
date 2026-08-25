# AI Safety & Ethics Card

> [!NOTE]
> This document is the model card and safety policy for the One Piece RAG assistant.
> It follows the "safety & ethics" pillar of the AI Engineer roadmap: documented intent,
> layered guardrails, privacy protection, transparency, and a regression gate so safety
> behavior cannot silently degrade.

## What is this system?

A local, interactive question-answering assistant over a dataset of 958 One Piece episode
records (titles, seasons, episode numbers, release years, community ratings). It routes each
question to one of three paths — structured SQL over SQLite, semantic vector search over
Qdrant, or general conversation — and answers with a small local LLM (`qwen2.5-coder:1.5b`),
with embeddings from `nomic-embed-text`.

**Intended use.** Answering factual questions about One Piece episodes for learning and
demonstration purposes.

**Out-of-scope use.** Anything that requires plot-accurate knowledge beyond episode titles
(the corpus stores titles and metadata, not full scripts), advice of any kind (medical,
legal, financial), or any deployment facing untrusted users without additional hardening.

## Data provenance & IP

The episode dataset is community-sourced metadata (titles, air dates, ratings). One Piece
is the intellectual property of Eiichiro Oda, Shueisha, and Toei Animation. This project
uses factual episode metadata for educational purposes and does not reproduce copyrighted
script content, video, or artwork. If you are a rights holder with concerns, open an issue.

## Guardrails

Every query passes through layered defenses. Deterministic checks run **before** any LLM
call, so attacks are rejected without spending tokens or touching the models.

```text
user query
   │
   ▼
┌────────────────────────── INPUT GUARDRAILS ──────────────────────────┐
│ 1. Length limit (default 2000 chars)                                 │
│ 2. Prompt-injection patterns (instruction overrides, jailbreaks)     │
│ 3. Personal data detection (email, phone, national ID, card numbers) │
│ 4. Harmful content (weapon synthesis, violence, self-harm)           │
└──────────────────────────────┬───────────────────────────────────────┘
                               │ allowed
                               ▼
        exact cache → semantic cache → LLM router → SQL / VECTOR / GENERAL
                               │
                               ▼
┌────────────────────────── OUTPUT GUARDRAILS ─────────────────────────┐
│ 5. Harmful content scan on the generated answer → refusal            │
│ 6. PII redaction before the answer is shown or cached                │
└──────────────────────────────┬───────────────────────────────────────┘
                               │
                               ▼
                            response
```

| Threat | Defense | Where |
| --- | --- | --- |
| Prompt injection / jailbreak | Pattern-based detection, blocked before any LLM call | `Safety/InputGuardrails.cs` |
| PII leakage (input) | Queries containing emails/phones/IDs/cards are rejected | `Safety/InputGuardrails.cs` + `Safety/PiiRedactor.cs` |
| PII leakage (output/traces) | Redaction before display, caching, and trace logging | `Safety/OutputGuardrails.cs`, `Observability/TraceStore.cs` |
| Harmful requests & outputs | Phrase-based detection on both directions; self-harm gets a supportive message with helpline pointer instead of a blunt refusal | `Safety/` |
| SQL injection via generated SQL | Read-only SQLite connection (engine-enforced), single-statement check, SELECT prefix check, row cap | `Retrieval/SqliteDatabaseService.cs` |

> [!IMPORTANT]
> The input filters are deterministic pattern matchers, which is the right tool for a
> small local deployment: they are cheap, auditable, and cannot be talked out of their
> decisions. They are **not** a substitute for a trained moderation model in a public
> product — the honest limitation is documented here rather than implied away.

## Privacy

- The assistant is fully local: queries and answers never leave the machine.
- Queries containing personal data are rejected, not processed.
- Detected PII is redacted from observability traces (`Safety:RedactPiiInTraces`).
- No accounts, no profiling, no personalization, no retention beyond the trace files you
  can delete at any time (`observability/` directory).

## Transparency

- Every response shows its trace id and latency; `/stats` shows session-wide metrics.
- VECTOR answers list their source episodes; trace files record the full prompt/response
  of every LLM call (toggleable via `Observability:LogPrompts`).
- The system identifies itself as a One Piece episode assistant and refuses to pretend
  otherwise when its instructions are probed.

## Fairness & limitations (candidly)

- The small chat model makes mistakes: wrong episode attributions and arithmetic errors
  are possible. The eval suite measures this so it is a known quantity, not a surprise.
- The VECTOR route retrieves by episode-title similarity; the corpus does not contain
  plot text, so storyline questions get title-level answers only.
- Pattern-based safety filters trade recall for precision deliberately; a moderation
  model would be the next layer for a production deployment.

## Enforcement: the regression gate

Safety behavior is pinned by tests and evaluations so it cannot silently regress:

1. **Unit tests** (`dotnet test`) cover the guardrails, PII redaction, and route parsing
   against attack and benign-case tables.
2. **The safety eval suite** (`tests/one-piece-api.Evals/datasets/safety.json`) runs
   adversarial queries through the live pipeline and requires a **100% block rate** with
   **zero false positives** on benign One Piece questions. The baseline is enforced by a
   non-zero exit code.

## Incident response

If a query slips past the guardrails: add it to `safety.json`, add the pattern to the
matching guardrail, re-run `dotnet test` and the eval suite, and the regression is fixed
with a permanent gate against recurrence.
