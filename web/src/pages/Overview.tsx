import { useState } from 'react'
import { CheckCircle2, Circle } from 'lucide-react'
import { api, dt, usd } from '../api'
import { Async, Badge, Empty, PageHeader, useLoad } from '../ui'
import { useApp } from '../App'

export const statusTone = (s: string) => (s === 'completed' ? 'success' : s === 'in_progress' ? 'accent' : s === 'unknown' || s === 'cancelled' ? 'warning' : 'danger')

export default function Overview() {
  const { w, project, projects, env, meta } = useApp()
  const [days, setDays] = useState(7)
  const [projectFilter, setProjectFilter] = useState('')
  const from = new Date(Date.now() - days * 86400_000).toISOString()
  const usage = useLoad(() => api(`${w}/usage?limit=5&from=${from}${projectFilter ? `&projectId=${projectFilter}` : ''}`), [w, days, projectFilter])
  const setup = useLoad(async () => {
    const [connections, profiles, keys] = await Promise.all([
      api<any[]>(`${w}/provider-connections`),
      api<any[]>(`${w}/profiles`),
      env ? api<any[]>(`${w}/environments/${env.id}/keys`) : Promise.resolve([]),
    ])
    return [
      { done: connections.length > 0 || meta.demo, label: meta.demo && !connections.length ? 'Sağlayıcı bağlantısı (DEMO modunda isteğe bağlı)' : 'Sağlayıcı bağlantısı ekle', href: '#/connections' },
      { done: projects.length > 0, label: 'Proje oluştur', href: '#/projects' },
      { done: profiles.some((p) => p.bindings.length > 0), label: 'Profil revizyonu yayınla', href: '#/profiles' },
      { done: keys.some((k) => !k.revokedAt), label: `${env?.type ?? 'ortam'} için API anahtarı oluştur`, href: '#/keys' },
    ]
  }, [w, env?.id, projects.length])

  return (
    <>
      <PageHeader title="Genel bakış" description="Gerçek istek kayıtlarından hesaplanır. Maliyetler tahmindir; sağlayıcı faturası değildir." actions={<>
        <label className="sr-only" htmlFor="range">Zaman aralığı</label>
        <select id="range" value={days} onChange={(e) => setDays(+e.target.value)} style={{ width: 'auto' }}>
          <option value={1}>Son 24 saat</option><option value={7}>Son 7 gün</option><option value={30}>Son 30 gün</option>
        </select>
        <label className="sr-only" htmlFor="pf">Proje filtresi</label>
        <select id="pf" value={projectFilter} onChange={(e) => setProjectFilter(e.target.value)} style={{ width: 'auto' }}>
          <option value="">Tüm projeler</option>
          {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </>} />
      <Async state={setup}>
        {(steps) => steps.every((s) => s.done) ? null : (
          <section className="card stack" aria-labelledby="setup-title">
            <h2 id="setup-title">Kurulum</h2>
            <p className="muted" style={{ margin: 0 }}>İlk isteği göndermek için şu adımları tamamlayın{project ? ` (proje: ${project.name})` : ''}.</p>
            <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'grid', gap: 8 }}>
              {steps.map((s) => (
                <li key={s.label} className="row">
                  {s.done ? <CheckCircle2 size={18} color="var(--success)" aria-label="Tamam" /> : <Circle size={18} aria-label="Bekliyor" />}
                  {s.done ? s.label : <a href={s.href}>{s.label}</a>}
                </li>
              ))}
            </ul>
          </section>
        )}
      </Async>
      <Async state={usage}>
        {(u) => (
          <>
            <div className="grid">
              <div className="card stat"><span className="muted">Çağrı</span><strong>{u.summary.requests}</strong></div>
              <div className="card stat"><span className="muted">Tahmini maliyet</span><strong>{usd(u.summary.estimatedUsd)}</strong></div>
              <div className="card stat"><span className="muted">p95 gecikme</span><strong>{u.summary.p95LatencyMs == null ? '—' : `${u.summary.p95LatencyMs} ms`}</strong></div>
              <div className="card stat"><span className="muted">Hata oranı</span><strong>{u.summary.requests ? `${((u.summary.errors / u.summary.requests) * 100).toFixed(1)}%` : '—'}</strong></div>
              <div className="card stat"><span className="muted">Fiyatı bilinmeyen çağrı</span><strong>{u.summary.unknownCostRequests}</strong>
                {u.summary.unknownCostRequests > 0 && <small className="muted">Tahmini toplama dahil değil</small>}</div>
            </div>
            <section className="stack">
              <h2>Son istekler</h2>
              {u.items.length === 0 ? <Empty title="Bu aralıkta istek yok">Playground'dan veya API anahtarıyla ilk isteği gönderin.</Empty> : (
                <div className="table-wrap"><table>
                  <thead><tr><th>Zaman</th><th>Durum</th><th>Model</th><th>Süre</th><th>Maliyet</th></tr></thead>
                  <tbody>{u.items.map((r: any) => (
                    <tr key={r.requestId}><td>{dt(r.createdAt)}</td><td><Badge tone={statusTone(r.status)}>{r.status}</Badge></td>
                      <td>{r.provider}/{r.model}</td><td>{r.durationMs ?? '—'} ms</td><td>{usd(r.estimatedCost)}</td></tr>
                  ))}</tbody>
                </table></div>
              )}
            </section>
          </>
        )}
      </Async>
    </>
  )
}
