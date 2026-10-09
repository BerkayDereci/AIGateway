import { useState } from 'react'
import { PlugZap, ShieldCheck, Trash2 } from 'lucide-react'
import { api, dt } from '../api'
import { Async, Badge, Confirm, Dialog, ErrorState, Field, PageHeader, useAction, useLoad } from '../ui'
import { useApp } from '../App'

type Connection = { id: string; provider: string; name: string; maskedSuffix: string; status: string; createdAt: string; verifiedAt: string | null }
const providers = [{ id: 'openai', label: 'OpenAI' }, { id: 'anthropic', label: 'Anthropic' }]

export default function Connections() {
  const { w, isOwner, meta } = useApp()
  const list = useLoad(() => api<Connection[]>(`${w}/provider-connections`), [w])
  const [editing, setEditing] = useState<{ provider: string; connection?: Connection }>()
  const [form, setForm] = useState({ name: '', apiKey: '' })
  const [verify, setVerify] = useState<Connection>()
  const [remove, setRemove] = useState<Connection>()
  const save = useAction()
  const reason = isOwner ? undefined : 'Yalnızca owner sağlayıcı bağlantılarını yönetebilir'

  return (
    <>
      <PageHeader title="Sağlayıcı bağlantıları" description="Kendi sağlayıcı anahtarınız (BYOK). Anahtar şifrelenerek saklanır ve bir daha gösterilmez." />
      {meta.demo && <div className="card tight muted">DEMO modunda “demo” sağlayıcısı anahtarsız çalışır; gerçek çağrı için aşağıdan anahtar ekleyin.</div>}
      <Async state={list}>
        {(connections) => (
          <div className="grid-2">
            {providers.map((p) => {
              const items = connections.filter((c) => c.provider === p.id)
              return (
                <section key={p.id} className="card stack" aria-label={p.label}>
                  <div className="row" style={{ justifyContent: 'space-between' }}>
                    <h2 className="row"><PlugZap size={18} aria-hidden /> {p.label}</h2>
                    <button className="btn" disabled={!isOwner} title={reason} onClick={() => { setForm({ name: p.label, apiKey: '' }); setEditing({ provider: p.id }) }}>Anahtar ekle</button>
                  </div>
                  {items.length === 0 ? <p className="muted" style={{ margin: 0 }}>Bağlantı yok. Bu sağlayıcıyı kullanan profiller çalışmaz.</p> : items.map((c) => (
                    <div key={c.id} className="card tight stack">
                      <div className="row" style={{ justifyContent: 'space-between' }}>
                        <strong>{c.name}</strong>
                        <Badge tone={c.status === 'verified' ? 'success' : c.status === 'failed' ? 'danger' : 'neutral'}>
                          {c.status === 'verified' ? 'Doğrulandı' : c.status === 'failed' ? 'Doğrulama başarısız' : 'Doğrulanmadı'}</Badge>
                      </div>
                      <div className="muted">Anahtar: <span className="mono">{c.maskedSuffix}</span> · Eklenme {dt(c.createdAt)}{c.verifiedAt && ` · Doğrulama ${dt(c.verifiedAt)}`}</div>
                      <div className="row">
                        <button className="btn ghost" disabled={!isOwner} title={reason} onClick={() => { setForm({ name: c.name, apiKey: '' }); setEditing({ provider: p.id, connection: c }) }}>Anahtarı güncelle</button>
                        <button className="btn ghost" disabled={!isOwner} title={reason} onClick={() => setVerify(c)}><ShieldCheck size={16} /> Doğrula</button>
                        <button className="btn ghost" disabled={!isOwner} title={reason} onClick={() => setRemove(c)}><Trash2 size={16} /> Sil</button>
                      </div>
                    </div>
                  ))}
                </section>
              )
            })}
          </div>
        )}
      </Async>
      <Dialog open={!!editing} title={editing?.connection ? 'Anahtarı güncelle' : 'Yeni bağlantı'} onClose={() => setEditing(undefined)} footer={<>
        <button className="btn ghost" onClick={() => setEditing(undefined)}>Vazgeç</button>
        <button className="btn primary" form="conn-form" disabled={save.busy}>Kaydet</button>
      </>}>
        <form id="conn-form" className="stack" autoComplete="off" onSubmit={(e) => { e.preventDefault(); save.run(async () => {
          const c = editing!.connection
          await api(c ? `${w}/provider-connections/${c.id}` : `${w}/provider-connections`,
            { method: c ? 'PATCH' : 'POST', body: c ? { name: form.name, apiKey: form.apiKey || null } : { provider: editing!.provider, ...form } })
          setForm({ name: '', apiKey: '' }); setEditing(undefined); list.reload()
        }) }}>
          <Field label="Ad"><input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required /></Field>
          <Field label="API anahtarı" hint="Kaydedildikten sonra yalnızca son 4 karakteri gösterilir.">
            <input type="password" autoComplete="new-password" value={form.apiKey} onChange={(e) => setForm({ ...form, apiKey: e.target.value })} required={!editing?.connection} />
          </Field>
          {save.error && <ErrorState error={save.error} />}
        </form>
      </Dialog>
      <Confirm open={!!verify} title="Bağlantıyı doğrula" onClose={() => setVerify(undefined)}
        message="Bu işlem sağlayıcının model listesi uç noktasına anahtarınızla gerçek bir istek gönderir. Genellikle ücretsizdir ancak sağlayıcı hesabınızda kayıt oluşturur ve kotanızı etkileyebilir."
        onConfirm={async () => { await api(`${w}/provider-connections/${verify!.id}/verify`, { method: 'POST' }); list.reload() }} />
      <Confirm open={!!remove} title="Bağlantıyı sil" danger onClose={() => setRemove(undefined)}
        message="Şifreli anahtar silinir. Bu bağlantıyı kullanan profiller istek alamaz."
        onConfirm={async () => { await api(`${w}/provider-connections/${remove!.id}`, { method: 'DELETE' }); list.reload() }} />
    </>
  )
}
