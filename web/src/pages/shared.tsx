import { useEffect, useRef, useState } from 'react'
import { ImagePlus, X } from 'lucide-react'
import { api, ApiError, stream, usd } from '../api'
import { ErrorState, useLoad } from '../ui'
import { useApp } from '../App'
import type { ProfileDto } from './Profiles'

export type Asset = { assetId: string; preview: string; name: string }

/** Profiles published to the selected environment. */
export function usePublishedProfiles() {
  const { w, env } = useApp()
  return useLoad(async () => (await api<ProfileDto[]>(`${w}/profiles`)).filter((p) => p.bindings.some((b) => b.environmentId === env?.id)), [w, env?.id])
}

/** Uploads through the admin playground route; images stay in memory only (object URLs), never browser storage. */
export function ImagePicker({ assets, onChange, max = 4 }: { assets: Asset[]; onChange: (a: Asset[]) => void; max?: number }) {
  const { w, env } = useApp()
  const input = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  const [error, setError] = useState<Error>()
  const [busy, setBusy] = useState(false)
  useEffect(() => () => assets.forEach((a) => URL.revokeObjectURL(a.preview)), []) // eslint-disable-line react-hooks/exhaustive-deps

  const add = async (files: FileList | null) => {
    if (!files || !env) return
    setError(undefined); setBusy(true)
    const next = [...assets]
    try {
      for (const file of Array.from(files).slice(0, max - assets.length)) {
        const form = new FormData()
        form.append('file', file)
        const r = await api(`${w}/environments/${env.id}/uploads`, { method: 'POST', form })
        next.push({ assetId: r.assetId, preview: URL.createObjectURL(file), name: file.name })
      }
    } catch (e) { setError(e as Error) } finally { setBusy(false); onChange(next) }
  }

  return (
    <div className="stack">
      <div className={`dropzone ${over ? 'over' : ''}`} role="button" tabIndex={0} aria-label="Görsel seç veya sürükle bırak"
        onClick={() => input.current?.click()} onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), input.current?.click())}
        onDragOver={(e) => { e.preventDefault(); setOver(true) }} onDragLeave={() => setOver(false)}
        onDrop={(e) => { e.preventDefault(); setOver(false); add(e.dataTransfer.files) }}>
        <ImagePlus aria-hidden /> <div>{busy ? 'Yükleniyor…' : `JPEG, PNG veya WebP sürükleyin ya da seçin (en fazla ${max}, 10 MiB, 15 dk geçerli)`}</div>
        <input ref={input} type="file" accept="image/jpeg,image/png,image/webp" multiple hidden onChange={(e) => { add(e.target.files); e.target.value = '' }} />
      </div>
      {error && <ErrorState error={error} />}
      {assets.length > 0 && (
        <div className="thumbs">{assets.map((a) => (
          <figure key={a.assetId}>
            <img src={a.preview} alt={a.name} />
            <button className="icon-btn" aria-label={`${a.name} kaldır`} onClick={() => { URL.revokeObjectURL(a.preview); onChange(assets.filter((x) => x !== a)) }}><X size={14} /></button>
          </figure>
        ))}</div>
      )}
    </div>
  )
}

export type GenerationResult = {
  requestId: string; provider: string; model: string; finishReason: string
  output: { text: string | null; json: unknown }
  usage: { inputTokens: number | null; outputTokens: number | null; state: string }
  cost: { estimatedAmount: number | null; state: string }
}

export function useGenerate() {
  const { w, env } = useApp()
  const [text, setText] = useState('')
  const [result, setResult] = useState<GenerationResult>()
  const [error, setError] = useState<{ message: string; code: string; partial?: boolean }>()
  const [busy, setBusy] = useState(false)
  const abort = useRef<AbortController>(null)

  const run = async (body: Record<string, unknown>) => {
    if (!env) return
    abort.current = new AbortController()
    setText(''); setResult(undefined); setError(undefined); setBusy(true)
    const url = `${w}/environments/${env.id}/generations`
    try {
      if (!body.stream) setResult(await api(url, { method: 'POST', body, signal: abort.current.signal }))
      else for await (const ev of stream(url, body, abort.current.signal)) {
        if (ev.event === 'text.delta') setText((t) => t + ev.data.text)
        else if (ev.event === 'completed') setResult(ev.data)
        else if (ev.event === 'error') setError(ev.data)
      }
    } catch (e) {
      if ((e as Error).name === 'AbortError') setError({ code: 'client_cancelled', message: 'İstek iptal edildi; sağlayıcı çağrısı durduruldu.', partial: true })
      else setError({ code: (e as ApiError).code ?? 'error', message: (e as Error).message })
    } finally { setBusy(false) }
  }
  return { run, cancel: () => abort.current?.abort(), text, result, error, busy }
}

export const UsageSummary = ({ r }: { r: GenerationResult }) => (
  <div className="row muted" style={{ fontSize: 13 }}>
    <span>{r.provider}/{r.model}</span>·<span>bitiş: {r.finishReason}</span>·
    <span>token: {r.usage.inputTokens ?? '?'} giriş / {r.usage.outputTokens ?? '?'} çıkış ({r.usage.state})</span>·
    <span>maliyet: {usd(r.cost.estimatedAmount)} ({r.cost.state})</span>
  </div>
)
