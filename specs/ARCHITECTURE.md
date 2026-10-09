# Mimari ve veri davranışı

## Modüler monolit
Access, Inference, Routing, Providers, Usage, Administration modülleri.
API host: auth, DTO validation, middleware, SSE.
Core: contracts/policies; Infrastructure: EF, provider SDK, storage, cryptography.
React same-origin reverse proxy ile /api'ye gider. Local dev proxy; production CORS allowlist.
PostgreSQL source of truth. İlk sürümde limit/reservation işlemleri DB transaction/row locking ile tüm instance'larda tutarlı. Redis yok.

## Veri modeli
Users (ASP.NET Core Identity), Workspaces, WorkspaceMembers.
Projects(workspaceId), Environments(workspaceId,projectId,type).
ProviderConnections(workspaceId,provider,ciphertext,keyVersion,maskedSuffix).
ApiKeys(workspaceId,environmentId,keyHash,prefix,permissions,expiresAt,revokedAt).
Profiles, ProfileRevisions(immutable config), EnvironmentProfileBindings.
ModelCapabilities(provider,model,version,source,verifiedAt).
PriceVersions(provider,model,effectiveAt,currency,input/output/cached/image pricing metadata).
GenerationRequests(workspace/project/environment,key,userRef,revision,status,requestId).
ProviderAttempts(requestId,attemptNumber,status,providerRequestId,usage,costState).
UsageRecords(append-only measured tokens,cost estimate,priceVersionId).
BudgetBuckets(scope,period,limit,settled,reserved), Reservations(requestId,amount,state).
UploadAssets(workspace/environment,objectKey,state,expiresAt,dimensions).
AuditEvents(workspace,actor,action,target,time; no secrets/content).

Tenant-owned entities have composite keys/unique indexes and composite FKs preventing cross-workspace associations. Tenant filters alone insufficient: verify at command boundary too. Tenant context from authenticated user membership or API key; never request body. Background jobs explicitly acquire tenant scope. Cross-tenant lookup returns 404.

## Kimlik ve secret'lar
Identity cookie HttpOnly/Secure/SameSite; write endpoints CSRF protection; login/register rate limit; password hashing Identity default. Local dev Secure exception documented. No JWT/localStorage session.
Registration creates workspace transactionally. Generic login errors. Password reset/email verification external SMTP setup production requirement; never ship default admin password.
Gateway key: >=256 bit random; store SHA-256 digest of high-entropy secret, prefix for lookup; constant-time compare where relevant. Return full key once.
Provider keys: IDataProtection backed encrypted storage with persistent keyring and rotation. Local volume keyring private. Production keyring protected using KMS/Key Vault certificate; startup fail closed if production protection/config missing. Database+unencrypted keyring is not acceptable production encryption. Exportable plaintext never returned.
Only fixed official provider hosts; endpoint customization disabled. Prevent SSRF through remote image URLs.

## Provider contract
Capabilities explicit: text, image, streaming, jsonSchema; per-model supported schema subset documented.
Own IProviderAdapter with GenerateAsync and StreamAsync plus capability check. Use verified official SDKs or typed HttpClient; Microsoft.Extensions.AI optional internal helper.
Map text/image blocks correctly. No pretending OpenAI message format equals Anthropic. Normalize finish reasons, usage and errors; keep redacted diagnostics.
Structured output native when verified; otherwise explicitly configured local-validation mode. Do not advertise hard schema guarantee in validation-only mode. Return output_validation_failed for invalid JSON/schema. Validate schema complexity and supported keywords before invoking.
MVP tools rejected explicitly. Provider prompt caches accounted when usage exposes them; application semantic caching off.

## Routing/retry
Published revision lists primary and optional allowed fallbacks. Check model permissions, required capabilities, provider credentials, data-sharing consent and price availability.
Single retry layer: SDK retry disabled where supported. Default zero automatic retry for ambiguous network timeout; at most one retry for explicitly safe pre-generation rejection/429 within total deadline; obey Retry-After. No retry for auth/invalid request. Attempts recorded even failed.
Fallback only before client-visible output and only if eligible error/explicit policy; no fallback after delta. An HTTP timeout may still incur provider charge: classify unknown and preserve reservation.
Request deadline 120s default; connection 10s; streaming idle 30s configurable. Propagate cancellation. Reverse proxy buffering off; no compression buffering SSE.

## Budget/concurrency
Before network call: authenticate, validate, resolve revision, estimate conservative charge using verified pricing/known token or image estimator, atomically reserve all budget scopes and concurrency slots.
Sort scope locks deterministically; transaction with SELECT FOR UPDATE (or equivalent proven mechanism). If any limit rejects, roll back all reservations. No network I/O while holding DB lock.
Limits use UTC calendar months; records display local timezone. Money decimal, USD explicit; no float arithmetic.
Budget-limited profiles lacking trustworthy upper estimation rejected with pricing_unavailable. Unknown image tariff never silently charged as zero.
Completion: append usage per attempt and settle reservations atomically/idempotently.
Actual provider usage means measured tokens, not authoritative invoice amount. Derived currency remains estimated.
Missing usage or ambiguous outcome → unknown liability. Do not auto-release solely by time-to-live. Reconciler marks review_required if provider reconciliation unavailable. Concurrency slot lease may expire independently; financial liability does not disappear.
Failed requests before provider dispatch release reservation. Retain retry/fallback attempt charges. Budget can still be exceeded if provider pricing/usage exceeds estimates; surface discrepancy and halt later requests. BYOK limits are controls on gateway traffic, not provider-account-wide guarantees.
Gateway crash after response: durable request lifecycle and reconciliation. Do not stream completed until usage persistence attempt recorded; failure produces explicit accounting issue without inventing totals.

## Idempotency
Unique(workspace,environment,keyId,idempotencyKey). Store canonical request hash; changed body returns 409.
Completed non-stream response replay without provider call. In-flight returns 409 with Retry-After. Unknown prior outcome never silently retried.
Stream reconnect replay V1 unsupported: duplicate key returns documented conflict; no regeneration.

## Upload
Authenticated multipart upload to gateway; decode/re-encode sanitized image to private MinIO/S3. Return opaque asset ID. Asset lookup tenant/environment scoped and reject expired/deleted.
Presigned download only if necessary, short lived; never record URLs. Gateway reads stored bytes for provider transfer. Cleanup and inference coordinate asset claims to avoid deletion while reading.

## Operations
Compose Postgres/MinIO/API/web with healthchecks and persistent volumes; migration separate idempotent initialization step.
Development demo provider explicit, production never accepts it.
OpenTelemetry request metrics/TTFT/duration/error/usage; avoid unbounded user/request labels and prompt traces.
Readiness checks DB/storage; liveness process only. Redacted audit logs, retention config. No default prompt/image retention.
