import { useState } from 'react'
import { Plus, Trash2, Upload } from 'lucide-react'
import { api, dt } from '../api'
import { Async, Badge, Dialog, Empty, ErrorState, Field, PageHeader, useAction, useLoad } from '../ui'
import { useApp } from '../App'

export type Model = { provider: string; model: string; text: boolean; image: boolean; streaming: boolean; jsonSchema: boolean; priced: boolean; source: string }
type ModelRef = { provider: string; model: string; connectionId: string | null }
type Config = {
  requiredCapabilities: string[]; primary: ModelRef; fallbacks: ModelRef[]; outputTokenLimit: number; timeoutSeconds: number
  schemaMode: 'native' | 'validation'; pricingRequiredForBudgetEnforcement: boolean; automaticRetryCount: number
}
export type ProfileDto = { id: string; name: string; revisions: { id: string; number: number; createdAt: string; config: Config }[]; bindings: { environmentId: string; revisionId: string }[] }
const caps = [['text', 'Metin'], ['image', 'Görsel'], ['streaming', 'Streaming'], ['jsonSchema', 'JSON Schema']] as const

export default function Profiles() {
  const { w, canWrite, envs, project } = useApp()
  const profiles = useLoad(() => api<ProfileDto[]>(`${w}/profiles`), [w])
  const models = useLoad(() => api<Model[]>(`${w}/models`), [w])
  const connections = useLoad(() => api<{ id: string; provider: string; name: string }[]>(`${w}/provider-connections`), [w])
  const [name, setName] = useState('')
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<ProfileDto>()
  const create = useAction()
  const bind = useAction()

  return (
    <>
      <PageHeader title="Profiller" description="Revizyonlar değiştirilemez; ortamlara belirli bir revizyon yayınlanır."
        actions={<button className="btn primary" disabled={!canWrite} title={canWrite ? undefined : 'Viewer profil oluşturamaz'} onClick={() => setCreating(true)}><Plus size={16} /> Yeni profil</button>} />
      {bind.error && <ErrorState error={bind.error} />}
      <Async state={profiles} empty={(d) => d.length === 0 && <Empty title="Profil yok">Profil; model, fallback, çıktı limiti ve zaman aşımını belirler. API istekleri profil adıyla yapılır.</Empty>}>
        {(list) => (
          <div className="stack">{list.map((p) => (
            <section key={p.id} className="card stack" aria-label={p.name}>
              <div className="row" style={{ justifyContent: 'space-between' }}>
                <h2 className="mono">{p.name}</h2>
                <button className="btn" disabled={!canWrite} onClick={() => setEditing(p)}><Upload size={16} /> Yeni revizyon</button>
              </div>
              {p.revisions.length === 0 ? <p className="muted" style={{ margin: 0 }}>Henüz revizyon yok.</p> : (
                <div className="table-wrap"><table>
                  <thead><tr><th>Rev</th><th>Birincil model</th><th>Fallback</th><th>Çıktı limiti</th><th>Zaman aşımı</th><th>Oluşturma</th></tr></thead>
                  <tbody>{p.revisions.map((r) => (
                    <tr key={r.id}><td>#{r.number}</td><td>{r.config.primary.provider}/{r.config.primary.model}</td>
                      <td>{r.config.fallbacks.map((f) => f.model).join(', ') || '—'}</td><td>{r.config.outputTokenLimit}</td><td>{r.config.timeoutSeconds} sn</td><td>{dt(r.createdAt)}</td></tr>
                  ))}</tbody>
                </table></div>
              )}
              {project && p.revisions.length > 0 && (
                <div className="row">
                  {envs.map((e) => {
                    const b = p.bindings.find((x) => x.environmentId === e.id)
                    return (
                      <label key={e.id} className="row">
                        <span>{project.name}/{e.type}:</span>
                        <select style={{ width: 'auto' }} value={b?.revisionId ?? ''} disabled={!canWrite || bind.busy}
                          onChange={(ev) => bind.run(async () => {
                            await api(`${w}/environments/${e.id}/profiles/${p.id}`, { method: 'PUT', body: { revisionId: ev.target.value } }); profiles.reload()
                          })}>
                          {!b && <option value="">Yayınlanmadı</option>}
                          {p.revisions.map((r) => <option key={r.id} value={r.id}>Rev #{r.number}</option>)}
                        </select>
                        {b && <Badge tone="success">Yayında</Badge>}
                      </label>
                    )
                  })}
                </div>
              )}
            </section>
          ))}</div>
        )}
      </Async>
      <Dialog open={creating} title="Yeni profil" onClose={() => setCreating(false)} footer={<>
        <button className="btn ghost" onClick={() => setCreating(false)}>Vazgeç</button>
        <button className="btn primary" form="profile-form" disabled={create.busy}>Oluştur</button>
      </>}>
        <form id="profile-form" className="stack" onSubmit={(e) => { e.preventDefault(); create.run(async () => {
          await api(`${w}/profiles`, { method: 'POST', body: { name } }); setName(''); setCreating(false); profiles.reload()
        }) }}>
          <Field label="Profil adı" hint="API isteğinde kullanılır, örn. workout-extraction" error={create.error?.message}>
            <input value={name} onChange={(e) => setName(e.target.value.toLowerCase())} pattern="[a-z0-9][a-z0-9-]{0,62}" required autoFocus />
          </Field>
        </form>
      </Dialog>
      {editing && models.data && connections.data && (
        <RevisionEditor profile={editing} models={models.data} connections={connections.data}
          onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); profiles.reload() }} />
      )}
    </>
  )
}

function RevisionEditor({ profile, models, connections, onClose, onSaved }: {
  profile: ProfileDto; models: Model[]; connections: { id: string; provider: string; name: string }[]; onClose: () => void; onSaved: () => void
}) {
  const { w } = useApp()
  const last = profile.revisions[0]?.config
  const firstModel = models[0]
  const ref = (m?: Model): ModelRef => ({ provider: m?.provider ?? '', model: m?.model ?? '', connectionId: connections.find((c) => c.provider === m?.provider)?.id ?? null })
  const [cfg, setCfg] = useState<Config>(last ?? {
    requiredCapabilities: ['text'], primary: ref(firstModel), fallbacks: [], outputTokenLimit: 1024, timeoutSeconds: 120,
    schemaMode: 'native', pricingRequiredForBudgetEnforcement: true, automaticRetryCount: 0,
  })
  const save = useAction()
  const modelOf = (r: ModelRef) => models.find((m) => m.provider === r.provider && m.model === r.model)
  const primary = modelOf(cfg.primary)

  // Plain render helper (not a component) so selects keep focus across re-renders.
  const picker = (label: string, value: ModelRef, onChange: (r: ModelRef) => void) => (
    <div className="grid-2">
      <Field label={label}>
        <select value={`${value.provider}/${value.model}`} onChange={(e) => onChange(ref(models.find((m) => `${m.provider}/${m.model}` === e.target.value)))}>
          {models.map((m) => <option key={`${m.provider}/${m.model}`} value={`${m.provider}/${m.model}`}>{m.provider}/{m.model}{m.priced ? '' : ' (fiyat yok)'}</option>)}
        </select>
      </Field>
      {value.provider !== 'demo' && (
        <Field label="Bağlantı" error={connections.some((c) => c.provider === value.provider) ? undefined : 'Bu sağlayıcı için bağlantı yok'}>
          <select value={value.connectionId ?? ''} onChange={(e) => onChange({ ...value, connectionId: e.target.value })}>
            {connections.filter((c) => c.provider === value.provider).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </Field>
      )}
    </div>
  )

  return (
    <Dialog open title={`${profile.name} — revizyon #${(profile.revisions[0]?.number ?? 0) + 1}`} onClose={onClose} footer={<>
      <button className="btn ghost" onClick={onClose}>Vazgeç</button>
      <button className="btn primary" disabled={save.busy || !primary} onClick={() => save.run(async () => {
        await api(`${w}/profiles/${profile.id}/revisions`, { method: 'POST', body: { config: cfg } }); onSaved()
      })}>Revizyonu kaydet</button>
    </>}>
      {models.length === 0 && <ErrorState error={new Error('Etkin sağlayıcı için doğrulanmış model yok.')} />}
      {picker('Birincil model', cfg.primary, (primary) => setCfg({ ...cfg, primary }))}
      {primary && !primary.priced && <small className="muted">Bu model için fiyat sürümü yok: USD bütçesi varsa istekler pricing_unavailable ile reddedilir.</small>}
      <fieldset className="stack" style={{ border: 0, padding: 0, margin: 0 }}>
        <legend className="field"><span>Gerekli yetenekler</span></legend>
        <div className="row">{caps.map(([k, label]) => {
          const supported = !primary || primary[k]
          return (
            <label key={k} className="check" title={supported ? undefined : 'Birincil model bu yeteneği desteklemiyor'}>
              <input type="checkbox" disabled={!supported} checked={cfg.requiredCapabilities.includes(k)}
                onChange={(e) => setCfg({ ...cfg, requiredCapabilities: e.target.checked ? [...cfg.requiredCapabilities, k] : cfg.requiredCapabilities.filter((x) => x !== k) })} />
              {label}{!supported && <span className="muted"> (desteklenmiyor)</span>}
            </label>
          )
        })}</div>
      </fieldset>
      <div className="stack">
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <strong>Fallback modelleri</strong>
          <button className="btn ghost" disabled={cfg.fallbacks.length >= 3} onClick={() => setCfg({ ...cfg, fallbacks: [...cfg.fallbacks, ref(firstModel)] })}><Plus size={16} /> Ekle</button>
        </div>
        <small className="muted">Yalnızca çıktı başlamadan ve güvenli hatalarda (429/kullanılamıyor) kullanılır.</small>
        {cfg.fallbacks.map((f, i) => (
          <div key={i} className="row" style={{ alignItems: 'end' }}>
            <div style={{ flex: 1 }}>{picker(`Fallback ${i + 1}`, f, (r) => setCfg({ ...cfg, fallbacks: cfg.fallbacks.map((x, j) => (j === i ? r : x)) }))}</div>
            <button className="icon-btn" aria-label={`Fallback ${i + 1} kaldır`} onClick={() => setCfg({ ...cfg, fallbacks: cfg.fallbacks.filter((_, j) => j !== i) })}><Trash2 size={16} /></button>
          </div>
        ))}
      </div>
      <div className="grid-2">
        <Field label="Çıktı token limiti"><input type="number" min={1} max={64000} value={cfg.outputTokenLimit} onChange={(e) => setCfg({ ...cfg, outputTokenLimit: +e.target.value })} /></Field>
        <Field label="Zaman aşımı (sn)"><input type="number" min={5} max={600} value={cfg.timeoutSeconds} onChange={(e) => setCfg({ ...cfg, timeoutSeconds: +e.target.value })} /></Field>
        <Field label="Şema modu" hint={cfg.schemaMode === 'validation' ? 'Sağlayıcıya şema gönderilmez; yalnızca yerel doğrulama yapılır (garanti yok).' : 'Sağlayıcının yerel structured output özelliği kullanılır.'}>
          <select value={cfg.schemaMode} onChange={(e) => setCfg({ ...cfg, schemaMode: e.target.value as Config['schemaMode'] })}>
            <option value="native">native</option><option value="validation">validation</option>
          </select>
        </Field>
        <Field label="Otomatik yeniden deneme" hint="Yalnızca 429 için; zaman aşımında asla.">
          <select value={cfg.automaticRetryCount} onChange={(e) => setCfg({ ...cfg, automaticRetryCount: +e.target.value })}>
            <option value={0}>0</option><option value={1}>1</option>
          </select>
        </Field>
      </div>
      <label className="check"><input type="checkbox" checked={cfg.pricingRequiredForBudgetEnforcement}
        onChange={(e) => setCfg({ ...cfg, pricingRequiredForBudgetEnforcement: e.target.checked })} /> Bütçe varken doğrulanmış fiyat zorunlu</label>
      {save.error && <ErrorState error={save.error} />}
    </Dialog>
  )
}
