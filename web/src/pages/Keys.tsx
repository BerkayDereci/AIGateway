import { useState } from 'react'
import { Copy, KeyRound, Plus } from 'lucide-react'
import { api, dt } from '../api'
import { Async, Badge, Confirm, Dialog, Empty, Field, PageHeader, useAction, useLoad, useToast } from '../ui'
import { useApp } from '../App'

type Key = { id: string; name: string; prefix: string; createdAt: string; expiresAt: string | null; revokedAt: string | null }

export default function Keys() {
  const { w, env, project, canWrite } = useApp()
  const toast = useToast()
  const keys = useLoad(() => (env ? api<Key[]>(`${w}/environments/${env.id}/keys`) : Promise.resolve([])), [w, env?.id])
  const [creating, setCreating] = useState(false)
  const [form, setForm] = useState({ name: '', expiresAt: '' })
  const [secret, setSecret] = useState<string>()
  const [revoke, setRevoke] = useState<Key>()
  const create = useAction()

  if (!env) return <><PageHeader title="API anahtarları" /><Empty title="Önce bir proje seçin veya oluşturun"><a href="#/projects">Projeler</a></Empty></>
  const state = (k: Key) => k.revokedAt ? <Badge tone="danger">İptal</Badge>
    : k.expiresAt && new Date(k.expiresAt) < new Date() ? <Badge tone="warning">Süresi doldu</Badge> : <Badge tone="success">Aktif</Badge>

  return (
    <>
      <PageHeader title="API anahtarları" description={`${project?.name} / ${env.type}. Anahtarlar yalnızca bu ortamda inference ve upload yapabilir; yönetim API'sine erişemez.`}
        actions={<button className="btn primary" disabled={!canWrite} title={canWrite ? undefined : 'Viewer anahtar oluşturamaz'} onClick={() => setCreating(true)}><Plus size={16} /> Yeni anahtar</button>} />
      <Async state={keys} empty={(d) => d.length === 0 && <Empty title="Bu ortamda anahtar yok">Sunucu tarafı uygulamanız için bir anahtar oluşturun. Mobil uygulamaya gömmeyin.</Empty>}>
        {(list) => (
          <div className="table-wrap"><table>
            <thead><tr><th>Ad</th><th>Önek</th><th>Oluşturma</th><th>Bitiş</th><th>Durum</th><th><span className="sr-only">İşlem</span></th></tr></thead>
            <tbody>{list.map((k) => (
              <tr key={k.id}><td>{k.name}</td><td className="mono">{k.prefix}…</td><td>{dt(k.createdAt)}</td><td>{dt(k.expiresAt)}</td><td>{state(k)}</td>
                <td style={{ textAlign: 'right' }}>{canWrite && !k.revokedAt && <button className="btn ghost" onClick={() => setRevoke(k)}>İptal et</button>}</td></tr>
            ))}</tbody>
          </table></div>
        )}
      </Async>
      <Dialog open={creating} title="Yeni API anahtarı" onClose={() => setCreating(false)} footer={<>
        <button className="btn ghost" onClick={() => setCreating(false)}>Vazgeç</button>
        <button className="btn primary" form="key-form" disabled={create.busy}><KeyRound size={16} /> Oluştur</button>
      </>}>
        <form id="key-form" className="stack" onSubmit={(e) => { e.preventDefault(); create.run(async () => {
          const r = await api(`${w}/environments/${env.id}/keys`, { method: 'POST', body: { name: form.name, expiresAt: form.expiresAt ? new Date(form.expiresAt).toISOString() : null } })
          setCreating(false); setForm({ name: '', expiresAt: '' }); setSecret(r.key); keys.reload()
        }) }}>
          <Field label="Ad"><input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required autoFocus /></Field>
          <Field label="Bitiş tarihi (isteğe bağlı)"><input type="datetime-local" value={form.expiresAt} onChange={(e) => setForm({ ...form, expiresAt: e.target.value })} /></Field>
          {create.error && <small className="field-error" role="alert">{create.error.message}</small>}
        </form>
      </Dialog>
      <Dialog open={!!secret} title="Anahtarınız (yalnızca bir kez gösterilir)" onClose={() => setSecret(undefined)} footer={
        <button className="btn primary" onClick={() => setSecret(undefined)}>Kaydettim, kapat</button>}>
        <p>Bu anahtarı şimdi secret manager'ınıza kaydedin. Pencere kapandıktan sonra tekrar görüntülenemez.</p>
        <label className="field"><span>API anahtarı</span><input readOnly value={secret ?? ''} className="mono" onFocus={(e) => e.target.select()} /></label>
        <button className="btn" onClick={async () => { await navigator.clipboard.writeText(secret!); toast('Anahtar panoya kopyalandı') }}><Copy size={16} /> Panoya kopyala</button>
      </Dialog>
      <Confirm open={!!revoke} title="Anahtarı iptal et" danger onClose={() => setRevoke(undefined)}
        message={<>“{revoke?.name}” ({revoke?.prefix}…) iptal edilince bu anahtarla yapılan istekler 401 alır. Bu işlem geri alınamaz.</>}
        onConfirm={async () => { await api(`${w}/environments/${env.id}/keys/${revoke!.id}`, { method: 'DELETE' }); keys.reload() }} />
    </>
  )
}
