# Provider compatibility

Verified on **2026-10-09** against official documentation pages (listed per row). No live provider call was
made from this environment: no real keys were supplied, so **real-provider behaviour is not verified** — only
the request/response formats below, through offline contract tests (`tests/AiGateway.Tests/AdapterContractTests.cs`).

## SDK choice

Typed `HttpClient` adapters (`src/AiGateway.Infrastructure/Providers.cs`), not vendor SDKs:

- one retry layer: the gateway's `InferenceService` owns retry/fallback, so SDK retries cannot multiply attempts;
- fixed official hosts only (`https://api.openai.com/`, `https://api.anthropic.com/`), no endpoint override (SSRF);
- raw provider error bodies are never surfaced; cancellation flows through the `HttpClient` call.

## Endpoints and mapping

| | OpenAI | Anthropic |
|---|---|---|
| Endpoint | `POST /v1/responses` (Responses API) | `POST /v1/messages`, header `anthropic-version: 2023-06-01` |
| Auth | `Authorization: Bearer` | `x-api-key` |
| System prompt | `input[]` item with `role: system` | top-level `system` string |
| Text | `input_text` (assistant turns: `output_text`) | `{type: text}` |
| Image | `input_image` with `data:<mime>;base64,` URL | `{type: image, source: {type: base64, media_type, data}}` |
| Structured output | `text.format = {type: json_schema, name, schema, strict: true}` | `output_config.format = {type: json_schema, schema}` (GA; no beta header) |
| Output limit | `max_output_tokens` | `max_tokens` |
| Stream events used | `response.output_text.delta`, `response.completed`/`incomplete`, `response.failed`/`error` | `message_start` (input usage), `content_block_delta/text_delta`, `message_delta` (stop_reason, output usage), `error` |
| Usage | `usage.input_tokens/output_tokens`, `input_tokens_details.cached_tokens` | `input_tokens` + `cache_read_input_tokens` + `cache_creation_input_tokens`, `output_tokens` |
| Finish mapping | `completed→stop`, `incomplete/max_output_tokens→length` | `end_turn/stop_sequence→stop`, `max_tokens→length`, `refusal→content_filter` |
| Key verification | `GET /v1/models` (free) | `GET /v1/models` (free) |
| Storage | `store: false` sent | — |

## Verified models (seeded in `ModelCapabilities`)

| Provider | Model ID | text | image | streaming | jsonSchema | Source |
|---|---|---|---|---|---|---|
| openai | `gpt-6-astra` | ✓ | ✓ | ✓ | ✓ | https://developers.openai.com/api/docs/models/gpt-6-astra |
| openai | `gpt-6.1-sol` | ✓ | ✓ | ✓ | ✓ | https://developers.openai.com/api/docs/models/gpt-6.1-sol |
| openai | `gpt-6-luna` | ✓ | ✓ | ✓ | ✓ | https://developers.openai.com/api/docs/models/gpt-6-luna |
| anthropic | `claude-opus-5-5` | ✓ | ✓ | ✓ | ✓ | https://platform.claude.com/docs/en/build-with-claude/structured-outputs , …/vision |
| anthropic | `claude-sonnet-5-5` | ✓ | ✓ | ✓ | ✓ | same |
| anthropic | `claude-haiku-5-5` | ✓ | ✓ | ✓ | ✓ | same |
| demo | `demo-model` | ✓ | ✓ | ✓ | ✓ | local, Development/Testing only |

Adding a model = a new migration with its source URL and date. Unlisted models are rejected with `model_not_allowed`.

## Prices

**No prices are shipped.** Owners enter price versions (`POST /pricing`) with a source. Without a price the cost
state is `unknown` (never zero) and USD-budgeted requests fail with `pricing_unavailable`. Image tariffs are
entered as a conservative per-image token ceiling; Anthropic documents `⌈w/28⌉×⌈h/28⌉` visual tokens capped at
4784 for Claude 4.7+ (vision page). Without it, budgeted image requests are rejected.

## JSON Schema subset

Accepted keywords: `type, properties, required, additionalProperties(false), items, enum, description, title, anyOf, format`.
Root must be `type: object`; every object with `properties` must set `additionalProperties: false`; depth ≤ 10,
≤ 300 properties. OpenAI strict mode additionally requires every property to be listed in `required` (checked
only for OpenAI). Anthropic documents numeric/string length constraints, `$ref` recursion and non-false
`additionalProperties` as unsupported — all excluded by the subset. Output is always re-validated locally
(`422 output_validation_failed` on failure). `schemaMode: validation` sends no schema to the provider and gives
no hard guarantee.

## Limits of this verification

- Event names / field names come from the docs pages above; they are exercised only with recorded-format fixtures.
- Image limits: Anthropic 10 MB base64 per image, 8000×8000 px; OpenAI PNG/JPEG/WEBP/non-animated GIF. The gateway
  is stricter (JPEG/PNG/WebP, 10 MiB, 20 MP, 4 images).
- Real-provider smoke test is manual and opt-in (see README) and has **not** been run here.
