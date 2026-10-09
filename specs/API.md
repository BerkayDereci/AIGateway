# API v1 — normatif sözleşme

JSON camelCase. UTC ISO-8601 dates. UUID IDs. Money decimal USD. requestId everywhere.
Admin endpoints cookie+CSRF; inference/upload Bearer project key. Do not mix authentication schemes.

## Yönetim
POST /api/v1/auth/register {email,password,workspaceName}
POST /api/v1/auth/login {email,password}
POST /api/v1/auth/logout
GET /api/v1/auth/me
GET /api/v1/auth/csrf
GET/POST /api/v1/workspaces
GET/POST /api/v1/workspaces/{workspaceId}/members
PATCH /api/v1/workspaces/{workspaceId}/members/{userId} {role}
GET/POST /api/v1/workspaces/{workspaceId}/projects
GET/PATCH /api/v1/workspaces/{workspaceId}/projects/{projectId}
GET /api/v1/workspaces/{workspaceId}/projects/{projectId}/environments
GET/POST /api/v1/workspaces/{workspaceId}/provider-connections
PATCH/DELETE /api/v1/workspaces/{workspaceId}/provider-connections/{connectionId}
POST /api/v1/workspaces/{workspaceId}/provider-connections/{connectionId}/verify (explicit potentially paid action; UI discloses)
GET/POST /api/v1/workspaces/{workspaceId}/environments/{environmentId}/keys
DELETE /api/v1/workspaces/{workspaceId}/environments/{environmentId}/keys/{keyId}
GET/POST /api/v1/workspaces/{workspaceId}/profiles
POST /api/v1/workspaces/{workspaceId}/profiles/{profileId}/revisions
PUT /api/v1/workspaces/{workspaceId}/environments/{environmentId}/profiles/{profileId} {revisionId}
GET/PATCH /api/v1/workspaces/{workspaceId}/limits
GET /api/v1/workspaces/{workspaceId}/usage?projectId=&from=&to=&cursor=
GET /api/v1/workspaces/{workspaceId}/requests/{requestId}
GET /api/v1/workspaces/{workspaceId}/models
GET /api/v1/workspaces/{workspaceId}/pricing
POST /api/v1/workspaces/{workspaceId}/pricing (owner manual verified price version)

All workspace IDs membership checked. List paging default 25 max 100, opaque cursor. Filters validated and tenant-scoped. Key responses masked except create.
Admin playground generation/upload routes scoped to workspace/environment, role owner/admin; same inference service and budget rules, without exposing project API key to UI.

## Görsel
POST /api/v1/uploads multipart field file
201 {assetId,expiresAt,mimeType,width,height}
DELETE /api/v1/uploads/{assetId}
All IDs bound to calling environment.

## Generation
POST /api/v1/generations
Headers: Authorization: Bearer <key>; optional Idempotency-Key <=128 chars.
Body:
{
  "profile":"structured-default",
  "messages":[{"role":"user","content":[{"type":"text","text":"Programı oku"},{"type":"image","assetId":"<uuid>"}]}],
  "output":{"format":"json_schema","name":"workout_program","schema":{}},
  "stream":false,
  "endUserRef":"opaque-user-42",
  "metadata":{"feature":"workout-import"}
}

roles: system/user/assistant. content block text or image. Profile supplies outputTokenLimit; caller may only lower it. Metadata max 10 entries/256 chars values, no sensitive data. Messages/text/body size bounded; reject tool/audio/video inputs.
Output formats text or json_schema. For structured streaming, deltas are provisional text; JSON validation only at completion, completed event contains validated output. Never promise parseable JSON on every delta.

200:
{
 "requestId":"uuid","status":"completed","profileRevisionId":"uuid",
 "provider":"openai","model":"configured-model",
 "output":{"text":null,"json":{}},
 "usage":{"inputTokens":123,"outputTokens":45,"cachedInputTokens":0,"state":"reported"},
 "cost":{"currency":"USD","estimatedAmount":0.001,"state":"estimated","priceVersionId":"uuid"},
 "finishReason":"stop"
}
Unknown token counts/amount null, never fabricated zero.

## SSE
Content-Type text/event-stream; Cache-Control no-cache; no proxy buffering.
event: started; data: {requestId,profileRevisionId}
event: text.delta; data: {requestId,sequence,text}
event: completed; data: complete response object
event: error; data: {requestId,code,message,retryable,partial:true|false}
Heartbeat comment at 15s; monotonic sequence; one terminal event. HTTP validation errors before headers use ProblemDetails. Disconnect cancels provider and records accounting state. No Last-Event-ID replay support V1.

## Hatalar
ProblemDetails {type,title,status,detail,requestId,code,retryable}
400 invalid_request / unsupported_capability / unsupported_schema
401 invalid_key
403 model_not_allowed / plan_not_allowed
404 not_found
409 idempotency_conflict / request_in_progress
413 upload_too_large
415 unsupported_media_type
422 output_validation_failed
429 rate_limit_exceeded / concurrency_limit_exceeded / budget_exceeded (Retry-After where meaningful)
502 provider_error
503 provider_unavailable / pricing_unavailable
504 provider_timeout
No raw provider body or credentials in response.
