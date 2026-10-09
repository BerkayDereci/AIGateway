import { Fragment, useState } from 'react'
import { Archive, Plus, RotateCcw } from 'lucide-react'
import { api, dt } from '../api'
import { Async, Badge, Confirm, Dialog, Empty, ErrorState, Field, PageHeader, useAction, useLoad } from '../ui'
import { useApp, type Env, type Project } from '../App'

export default function Projects() {
  const { w, canWrite, reloadProjects } = useApp()
  const list = useLoad(() => api(`${w}/projects?limit=100`).then((r) => r.items as Project[]), [w])
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [archive, setArchive] = useState<Project>()
  const [selected, setSelected] = useState<string>()
  const create = useAction()
  const refresh = () => { list.reload(); reloadProjects() }

  return (
    <>
      <PageHeader title="Projeler" description="Her proje development ve production ortamıyla oluşturulur."
        actions={<button className="btn primary" disabled={!canWrite} title={canWrite ? undefined : 'Viewer rolü proje oluşturamaz'} onClick={() => setCreating(true)}><Plus size={16} /> Yeni proje</button>} />
      <Async state={list} empty={(d) => d.length === 0 && <Empty title="Henüz proje yok">Uygulamanız için ilk projeyi oluşturun.</Empty>}>
        {(projects) => (
          <div className="table-wrap"><table>
            <thead><tr><th>Ad</th><th>Oluşturma</th><th>Durum</th><th><span className="sr-only">İşlemler</span></th></tr></thead>
            <tbody>{projects.map((p) => (<Fragment key={p.id}>
              <tr className="clickable" onClick={() => setSelected(selected === p.id ? undefined : p.id)}>
                <td><button className="icon-btn" aria-expanded={selected === p.id} onClick={(e) => { e.stopPropagation(); setSelected(selected === p.id ? undefined : p.id) }}>{p.name}</button></td>
                <td>{dt(p.createdAt)}</td>
                <td>{p.archivedAt ? <Badge tone="warning">Arşivlendi</Badge> : <Badge tone="success">Aktif</Badge>}</td>
                <td style={{ textAlign: 'right' }}>{canWrite && (p.archivedAt
                  ? <button className="btn ghost" onClick={async (e) => { e.stopPropagation(); await api(`${w}/projects/${p.id}`, { method: 'PATCH', body: { archived: false } }); refresh() }}><RotateCcw size={16} /> Geri al</button>
                  : <button className="btn ghost" onClick={(e) => { e.stopPropagation(); setArchive(p) }}><Archive size={16} /> Arşivle</button>)}</td>
              </tr>
              {selected === p.id && <tr><td colSpan={4}><Environments projectId={p.id} /></td></tr>}
            </Fragment>))}</tbody>
          </table></div>
        )}
      </Async>
      <Dialog open={creating} title="Yeni proje" onClose={() => setCreating(false)} footer={<>
        <button className="btn ghost" onClick={() => setCreating(false)}>Vazgeç</button>
        <button className="btn primary" form="project-form" disabled={create.busy}>Oluştur</button>
      </>}>
        <form id="project-form" className="stack" onSubmit={(e) => { e.preventDefault(); create.run(async () => {
          await api(`${w}/projects`, { method: 'POST', body: { name } }); setName(''); setCreating(false); refresh()
        }) }}>
          <Field label="Proje adı" error={create.error?.message}><input value={name} onChange={(e) => setName(e.target.value)} required autoFocus /></Field>
        </form>
      </Dialog>
      <Confirm open={!!archive} title="Projeyi arşivle" danger onClose={() => setArchive(undefined)}
        message={<>“{archive?.name}” arşivlenince bu projenin API anahtarları istek yapamaz. Geri alınabilir.</>}
        onConfirm={async () => { await api(`${w}/projects/${archive!.id}`, { method: 'PATCH', body: { archived: true } }); refresh() }} />
    </>
  )
}

function Environments({ projectId }: { projectId: string }) {
  const { w } = useApp()
  const envs = useLoad(() => api<Env[]>(`${w}/projects/${projectId}/environments`), [projectId])
  const [tab, setTab] = useState(0)
  if (envs.error) return <ErrorState error={envs.error} retry={envs.reload} />
  const list = envs.data ?? []
  const env = list[tab]
  return (
    <div className="stack">
      <div className="tabs" role="tablist" style={{ maxWidth: 320 }}>
        {list.map((e, i) => <button key={e.id} role="tab" aria-selected={i === tab} onClick={() => setTab(i)}>{e.type}</button>)}
      </div>
      {env && <div className="muted">Ortam kimliği: <code>{env.id}</code> — anahtar ve profil yayınlamayı üst bardaki ortam seçiciyle yapın.</div>}
    </div>
  )
}
