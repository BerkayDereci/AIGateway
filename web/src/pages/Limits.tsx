import { useState } from 'react'
import { api, dt, usd } from '../api'
import { Async, Badge, Dialog, Empty, ErrorState, Field, PageHeader, useAction, useLoad } from '../ui'
import { useApp } from '../App'
import type { Model } from './Profiles'

type Limit = { scope: string; scopeKey: string; monthlyUsd: number | null; monthlyRequests: number | null; monthlyTokens: number | null; concurrency: number | null }
type Bucket = { scope: string; scopeKey: string; reserved: number; settled: number; unknown: number; requests: number; tokens: number }

export default function Limits() {
  const { w, project, projects } = useApp()
  const data = useLoad(() => api<{ period: string; limits: Limit[]; buckets: Bucket[]; reviewRequired: number }>(`${w}/limits`), [w])
  const scopes = [
    { scope: 'workspace', scopeKey: '', label: 'Workspace' },
    ...(project ? [{ scope: 'project', scopeKey: project.id, label: `Proje: ${project.name}` }] : []),
    { scope: 'endUser', scopeKey: '*', label: 'Her son kullanıcı (varsayılan)' },
  ]
  const name = (b: Bucket) => b.scope === 'project' ? `Proje: ${projects.find((p) => p.id === b.scopeKey)?.name ?? b.scopeKey}` : b.scope === 'endUser' ? `Son kullanıcı: ${b.scopeKey}` : 'Workspace'

  return (
    <>
      <PageHeader title="Limitler ve fiyatlar" description="Aylık limitler UTC takvim ayına göre işler. BYOK limitleri yalnızca gateway trafiğini kontrol eder; sağlayıcı hesabı genelinde garanti değildir." />
      <Async state={data}>{(d) => (<>
        {d.reviewRequired > 0 && <div className="state error" role="alert" style={{ background: 'var(--warning-soft)', color: 'var(--warning)' }}>
          {d.reviewRequired} isteğin sonucu belirsiz (zaman aşımı/iptal). Tutarları sağlayıcı panelinden kontrol edin; bu yükümlülük bütçede “bilinmeyen” olarak tutulur.</div>}
        <div className="grid-2">{scopes.map((s) => (
          <LimitForm key={s.scope + s.scopeKey} {...s} limit={d.limits.find((l) => l.scope === s.scope && l.scopeKey === s.scopeKey)} onSaved={data.reload} />
        ))}</div>
        <section className="stack">
          <h2>{d.period} dönemi kullanımı</h2>
          {d.buckets.length === 0 ? <Empty title="Bu ay henüz kayıt yok" /> : (
            <div className="table-wrap"><table>
              <thead><tr><th>Kapsam</th><th>Rezerve</th><th>Kesinleşen (tahmini)</th><th>Bilinmeyen yükümlülük</th><th>İstek</th><th>Token</th></tr></thead>
              <tbody>{d.buckets.map((b) => (
                <tr key={b.scope + b.scopeKey}><td>{name(b)}</td><td>{usd(b.reserved)}</td><td>{usd(b.settled)}</td>
                  <td>{b.unknown > 0 ? <Badge tone="warning">{usd(b.unknown)}</Badge> : usd(0)}</td><td>{b.requests}</td><td>{b.tokens}</td></tr>
              ))}</tbody>
            </table></div>
          )}
        </section>
      </>)}</Async>
      <Prices />
    </>
  )
}

function LimitForm({ scope, scopeKey, label, limit, onSaved }: { scope: string; scopeKey: string; label: string; limit?: Limit; onSaved: () => void }) {
  const { w, canWrite } = useApp()
  const toForm = (l?: Limit) => ({ monthlyUsd: l?.monthlyUsd?.toString() ?? '', monthlyRequests: l?.monthlyRequests?.toString() ?? '', monthlyTokens: l?.monthlyTokens?.toString() ?? '', concurrency: l?.concurrency?.toString() ?? '' })
  const [form, setForm] = useState(toForm(limit))
  const save = useAction()
  const n = (v: string) => (v === '' ? null : Number(v))
  const input = (k: keyof typeof form, l: string, step = '1') => (
    <Field label={l}><input type="number" min={0} step={step} placeholder="Sınırsız" value={form[k]} disabled={!canWrite} onChange={(e) => setForm({ ...form, [k]: e.target.value })} /></Field>
  )
  return (
    <form className="card stack" onSubmit={(e) => { e.preventDefault(); save.run(async () => {
      await api(`${w}/limits`, { method: 'PATCH', body: { scope, scopeKey, monthlyUsd: n(form.monthlyUsd), monthlyRequests: n(form.monthlyRequests), monthlyTokens: n(form.monthlyTokens), concurrency: n(form.concurrency) } })
      onSaved()
    }) }}>
      <h2>{label}</h2>
      <div className="grid-2">
        {input('monthlyUsd', 'Aylık USD (tahmini)', '0.01')}{input('monthlyRequests', 'Aylık istek')}{input('monthlyTokens', 'Aylık token')}{input('concurrency', 'Eşzamanlı istek')}
      </div>
      {save.error && <ErrorState error={save.error} />}
      <button className="btn" disabled={!canWrite || save.busy}>Kaydet</button>
    </form>
  )
}

function Prices() {
  const { w, isOwner } = useApp()
  const prices = useLoad(() => api<any[]>(`${w}/pricing`), [w])
  const models = useLoad(() => api<Model[]>(`${w}/models`), [w])
  const [open, setOpen] = useState(false)
  const blank = { model: '', effectiveAt: '', inputPerMillion: '', outputPerMillion: '', cachedInputPerMillion: '', imageInputTokens: '', source: '' }
  const [form, setForm] = useState(blank)
  const save = useAction()
  const set = (k: keyof typeof blank) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setForm({ ...form, [k]: e.target.value })

  return (
    <section className="stack">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h2>Fiyat sürümleri</h2>
        <button className="btn" disabled={!isOwner} title={isOwner ? undefined : 'Yalnızca owner fiyat ekleyebilir'} onClick={() => setOpen(true)}>Fiyat ekle</button>
      </div>
      <p className="muted" style={{ margin: 0 }}>Gateway fiyat uydurmaz. Sağlayıcının güncel fiyat sayfasından doğruladığınız değerleri kaynağıyla girin. Yeni sürüm geçmiş maliyetleri değiştirmez.</p>
      <Async state={prices} empty={(d) => d.length === 0 && <Empty title="Fiyat yok">Fiyatı olmayan modellerin maliyeti “bilinmiyor” görünür ve USD bütçeli isteklerde reddedilir.</Empty>}>
        {(list) => (
          <div className="table-wrap"><table>
            <thead><tr><th>Model</th><th>Geçerlilik</th><th>Giriş / 1M</th><th>Çıkış / 1M</th><th>Önbellek / 1M</th><th>Görsel token</th><th>Kaynak</th></tr></thead>
            <tbody>{list.map((p) => (
              <tr key={p.id}><td>{p.provider}/{p.model}</td><td>{dt(p.effectiveAt)}</td><td>{usd(p.inputPerMillion)}</td><td>{usd(p.outputPerMillion)}</td>
                <td>{p.cachedInputPerMillion == null ? '—' : usd(p.cachedInputPerMillion)}</td><td>{p.imageInputTokens ?? 'bilinmiyor'}</td><td className="muted">{p.source}</td></tr>
            ))}</tbody>
          </table></div>
        )}
      </Async>
      <Dialog open={open} title="Fiyat sürümü ekle" onClose={() => setOpen(false)} footer={<>
        <button className="btn ghost" onClick={() => setOpen(false)}>Vazgeç</button>
        <button className="btn primary" form="price-form" disabled={save.busy}>Kaydet</button>
      </>}>
        <form id="price-form" className="stack" onSubmit={(e) => { e.preventDefault(); save.run(async () => {
          const [provider, model] = (form.model || `${models.data![0].provider}/${models.data![0].model}`).split(/\/(.+)/)
          const n = (v: string) => (v === '' ? null : Number(v))
          await api(`${w}/pricing`, { method: 'POST', body: {
            provider, model, effectiveAt: form.effectiveAt ? new Date(form.effectiveAt).toISOString() : new Date().toISOString(),
            inputPerMillion: Number(form.inputPerMillion), outputPerMillion: Number(form.outputPerMillion),
            cachedInputPerMillion: n(form.cachedInputPerMillion), imageInputTokens: n(form.imageInputTokens), source: form.source,
          } })
          setForm(blank); setOpen(false); prices.reload(); models.reload()
        }) }}>
          <Field label="Model"><select value={form.model} onChange={set('model')}>{models.data?.map((m) => <option key={m.provider + m.model} value={`${m.provider}/${m.model}`}>{m.provider}/{m.model}</option>)}</select></Field>
          <div className="grid-2">
            <Field label="Giriş USD / 1M token"><input type="number" step="0.0001" min={0} required value={form.inputPerMillion} onChange={set('inputPerMillion')} /></Field>
            <Field label="Çıkış USD / 1M token"><input type="number" step="0.0001" min={0} required value={form.outputPerMillion} onChange={set('outputPerMillion')} /></Field>
            <Field label="Önbellekli giriş / 1M (isteğe bağlı)"><input type="number" step="0.0001" min={0} value={form.cachedInputPerMillion} onChange={set('cachedInputPerMillion')} /></Field>
            <Field label="Görsel başına üst sınır token" hint="Boşsa görselli bütçeli istekler reddedilir."><input type="number" min={0} value={form.imageInputTokens} onChange={set('imageInputTokens')} /></Field>
          </div>
          <Field label="Geçerlilik başlangıcı"><input type="datetime-local" value={form.effectiveAt} onChange={set('effectiveAt')} /></Field>
          <Field label="Kaynak (URL ve doğrulama tarihi)"><input required value={form.source} onChange={set('source')} /></Field>
          {save.error && <ErrorState error={save.error} />}
        </form>
      </Dialog>
    </section>
  )
}
