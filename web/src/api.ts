export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public retryable = false) {
    super(message)
  }
}

let csrf: string | null = null
export const resetCsrf = () => { csrf = null }

async function csrfToken() {
  if (!csrf) csrf = (await (await fetch('/api/v1/auth/csrf', { credentials: 'same-origin' })).json()).token
  return csrf!
}

async function send(path: string, method: string, body?: unknown, form?: FormData, signal?: AbortSignal) {
  const headers: Record<string, string> = {}
  if (method !== 'GET') headers['X-CSRF-TOKEN'] = await csrfToken()
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  let res: Response
  try {
    res = await fetch(path, { method, headers, credentials: 'same-origin', signal, body: form ?? (body === undefined ? undefined : JSON.stringify(body)) })
  } catch (e) {
    if ((e as Error).name === 'AbortError') throw e
    throw new ApiError(0, 'offline', 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edin.')
  }
  if (!res.ok) {
    const p = await res.json().catch(() => ({}))
    throw new ApiError(res.status, p.code ?? 'error', p.detail ?? `İstek başarısız (HTTP ${res.status}).`, p.retryable)
  }
  return res
}

export async function api<T = any>(path: string, opts: { method?: string; body?: unknown; form?: FormData; signal?: AbortSignal } = {}): Promise<T> {
  const res = await send(path, opts.method ?? 'GET', opts.body, opts.form, opts.signal)
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export type SseEvent = { event: string; data: any }

/** Splits complete SSE frames off the buffer; partial frames stay in `rest`. Comments (heartbeats) are dropped. */
export function parseSse(buffer: string): { events: SseEvent[]; rest: string } {
  const frames = buffer.split('\n\n')
  const rest = frames.pop() ?? ''
  const events: SseEvent[] = []
  for (const frame of frames) {
    let event = 'message'
    const data: string[] = []
    for (const line of frame.split('\n')) {
      if (line.startsWith('event:')) event = line.slice(6).trim()
      else if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
    }
    if (data.length) events.push({ event, data: JSON.parse(data.join('\n')) })
  }
  return { events, rest }
}

/** POSTs and yields SSE events; aborting the signal closes the connection, which cancels the provider call server-side. */
export async function* stream(path: string, body: unknown, signal: AbortSignal): AsyncGenerator<SseEvent> {
  const res = await send(path, 'POST', body, undefined, signal)
  const reader = res.body!.getReader()
  const decoder = new TextDecoder() // stream mode keeps multi-byte UTF-8 split across chunks intact
  let buffer = ''
  for (;;) {
    const { done, value } = await reader.read()
    if (done) return
    const parsed = parseSse(buffer + decoder.decode(value, { stream: true }))
    buffer = parsed.rest
    yield* parsed.events
  }
}

export const usd = (v: number | null | undefined) =>
  v == null ? 'bilinmiyor' : new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'USD', maximumFractionDigits: 6 }).format(v)
export const dt = (v: string | null | undefined) => (v ? new Date(v).toLocaleString('tr-TR') : '—')
