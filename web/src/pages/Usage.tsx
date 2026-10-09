import { useEffect, useState } from 'react'
import { X } from 'lucide-react'
import { api, dt, usd } from '../api'
import { Async, Badge, Empty, ErrorState, PageHeader, useLoad } from '../ui'
import { useApp } from '../App'
import { statusTone } from './Overview'

type Row = { requestId: string; createdAt: string; status: string; feature: string | null; provider: string; model: string; durationMs: number | null
  inputTokens: number | null; outputTokens: number | null; estimatedCost: number | null; costState: string; attempts: number; errorCode: string | null }
const columns: [keyof Row, string][] = [['createdAt', 'Zaman'], ['status', 'Durum'], ['feature', 'Özellik'], ['model', 'Sağlayıcı/model'],
  ['durationMs', 'Süre'], ['inputTokens', 'Token'], ['estimatedCost', 'Maliyet']]

export default function Usage() {
  const { w, projects } = useApp()
  const [filters, setFilters] = useState({ projectId: '', from: '', to: '' })
  const [rows, setRows] = useState<Row[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [sort, setSort] = useState<{ key: keyof Row; asc: boolean }>({ key: 'createdAt', asc: false })
  const [open, setOpen] = useState<string>()
  const qs = (c?: string | null) => new URLSearchParams(Object.entries({ ...filters, from: filters.from && new Date(filters.from).toISOString(), to: filters.to && new Date(filters.to).toISOString(), cursor: c ?? '' })
    .filter(([, v]) => v)).toString()
  const first = useLoad(() => api(`${w}/usage?${qs()}`), [w, filters])
  useEffect(() => { if (first.data) { setRows(first.data.items); setCursor(first.data.nextCursor) } }, [first.data])
  const [moreError, setMoreError] = useState<Error>()
  const more = async () => {
    try { const r = await api(`${w}/usage?${qs(cursor)}`); setRows((x) => [...x, ...r.items]); setCursor(r.nextCursor) } catch (e) { setMoreError(e as Error) }
  }
  const sorted = [...rows].sort((a, b) => {
    const [x, y] = [a[sort.key] ?? '', b[sort.key] ?? '']
    return (x < y ? -1 : x > y ? 1 : 0) * (sort.asc ? 1 : -1)
  })

  return (
    <>
      <PageHeader title="Kullanım" description="Her satır bir istek; retry ve fallback denemelerinin maliyeti dahildir. Fiyatı bilinmeyen maliyet sıfır sayılmaz." />
      <div className="row">
        <label className="row">Proje <select style={{ width: 'auto' }} value={filters.projectId} onChange={(e) => setFilters({ ...filters, projectId: e.target.value })}>
          <option value="">Tümü</option>{projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        <label className="row">Başlangıç <input type="date" style={{ width: 'auto' }} value={filters.from} onChange={(e) => setFilters({ ...filters, from: e.target.value })} /></label>
        <label className="row">Bitiş <input type="date" style={{ width: 'auto' }} value={filters.to} onChange={(e) => setFilters({ ...filters, to: e.target.value })} /></label>
      </div>
      <Async state={first} empty={(d) => d.items.length === 0 && <Empty title="İstek yok">Seçili filtrelerle kayıt bulunamadı.</Empty>}>
        {(d) => (<>
          <div className="row muted">Toplam {d.summary.requests} istek · tahmini {usd(d.summary.estimatedUsd)} · fiyatı bilinmeyen {d.summary.unknownCostRequests}</div>
          <div className="table-wrap"><table>
            <thead><tr>{columns.map(([k, label]) => (
              <th key={k} aria-sort={sort.key === k ? (sort.asc ? 'ascending' : 'descending') : undefined}>
                <button onClick={() => setSort({ key: k, asc: sort.key === k ? !sort.asc : true })}>{label}{sort.key === k ? (sort.asc ? ' ↑' : ' ↓') : ''}</button>
              </th>))}</tr></thead>
            <tbody>{sorted.map((r) => (
              <tr key={r.requestId} className="clickable" tabIndex={0} onClick={() => setOpen(r.requestId)} onKeyDown={(e) => e.key === 'Enter' && setOpen(r.requestId)}>
                <td>{dt(r.createdAt)}</td>
                <td><Badge tone={statusTone(r.status)}>{r.status}</Badge>{r.errorCode && <div className="muted mono">{r.errorCode}</div>}</td>
                <td>{r.feature ?? '—'}</td><td>{r.provider}/{r.model}{r.attempts > 1 && <div className="muted">{r.attempts} deneme</div>}</td>
                <td>{r.durationMs ?? '—'} ms</td><td>{r.inputTokens ?? '?'} / {r.outputTokens ?? '?'}</td>
                <td>{usd(r.estimatedCost)} {r.costState === 'unknown' && <Badge tone="warning">bilinmiyor</Badge>}</td>
              </tr>))}</tbody>
          </table></div>
          {moreError && <ErrorState error={moreError} />}
          {cursor && <button className="btn ghost" onClick={more}>Daha fazla yükle</button>}
        </>)}
      </Async>
      {open && <RequestDrawer id={open} onClose={() => setOpen(undefined)} />}
    </>
  )
}

function RequestDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { w } = useApp()
  const r = useLoad(() => api(`${w}/requests/${id}`), [id])
  useEffect(() => { const k = (e: KeyboardEvent) => e.key === 'Escape' && onClose(); addEventListener('keydown', k); return () => removeEventListener('keydown', k) }, [onClose])
  return (
    <aside className="drawer" role="dialog" aria-modal="true" aria-label="İstek detayı">
      <div className="row" style={{ justifyContent: 'space-between' }}><h2>İstek detayı</h2><button className="icon-btn" aria-label="Kapat" onClick={onClose} autoFocus><X size={18} /></button></div>
      <Async state={r}>{(d) => (<>
        <dl className="stack" style={{ margin: 0 }}>
          <div><dt className="muted">Request ID</dt><dd className="mono" style={{ margin: 0 }}>{d.requestId}</dd></div>
          <div><dt className="muted">Durum</dt><dd style={{ margin: 0 }}><Badge tone={statusTone(d.status)}>{d.status}</Badge> {d.errorCode}</dd></div>
          <div><dt className="muted">Rezervasyon</dt><dd style={{ margin: 0 }}>{d.reservation ? `${usd(d.reservation.amount)} — ${d.reservation.state}` : '—'}</dd></div>
          <div><dt className="muted">Son kullanıcı</dt><dd style={{ margin: 0 }}>{d.endUserRef ?? '—'}</dd></div>
        </dl>
        <h3>Denemeler</h3>
        {d.attempts.length === 0 ? <p className="muted">Sağlayıcıya istek gönderilmedi.</p> : d.attempts.map((a: any) => (
          <div key={a.attemptNumber} className="card tight stack">
            <div className="row" style={{ justifyContent: 'space-between' }}><strong>#{a.attemptNumber} {a.provider}/{a.model}</strong><Badge tone={a.status === 'succeeded' ? 'success' : 'warning'}>{a.status}</Badge></div>
            <div className="muted">Token {a.inputTokens ?? '?'} / {a.outputTokens ?? '?'} ({a.usageState}) · maliyet {usd(a.estimatedCost)} ({a.costState}){a.ttftMs != null && ` · TTFT ${a.ttftMs} ms`}</div>
            {a.errorCode && <div className="mono">{a.errorCode}</div>}
            {a.providerRequestId && <div className="muted mono">{a.providerRequestId}</div>}
          </div>
        ))}
      </>)}</Async>
    </aside>
  )
}
