import { useState } from 'react'
import { api, dt } from '../api'
import { Async, ErrorState, Field, PageHeader, useAction, useLoad } from '../ui'
import { useApp } from '../App'

export default function Settings() {
  const { w, ws, isOwner, canWrite } = useApp()
  const members = useLoad(() => api<{ userId: string; email: string; role: string }[]>(`${w}/members`), [w])
  const audit = useLoad(() => (canWrite ? api(`${w}/audit?limit=50`) : Promise.resolve({ items: [] })), [w])
  const [form, setForm] = useState({ email: '', role: 'viewer' })
  const add = useAction()
  const change = useAction()

  return (
    <>
      <PageHeader title="Ayarlar" />
      <section className="card stack">
        <h2>Plan</h2>
        <p style={{ margin: 0 }}><strong>{ws.plan}</strong> — pilot sürümde plan manuel atanır. Online ödeme, fatura ve vergi işlemleri henüz yok.</p>
      </section>
      <section className="card stack">
        <h2>Üyeler</h2>
        <Async state={members}>{(list) => (
          <div className="table-wrap"><table>
            <thead><tr><th>E-posta</th><th>Rol</th></tr></thead>
            <tbody>{list.map((m) => (
              <tr key={m.userId}><td>{m.email}</td><td>
                <label className="sr-only" htmlFor={`role-${m.userId}`}>{m.email} rolü</label>
                <select id={`role-${m.userId}`} style={{ width: 'auto' }} value={m.role} disabled={!isOwner || change.busy}
                  onChange={(e) => change.run(async () => { await api(`${w}/members/${m.userId}`, { method: 'PATCH', body: { role: e.target.value } }); members.reload() })}>
                  <option value="owner">owner</option><option value="admin">admin</option><option value="viewer">viewer</option>
                </select></td></tr>
            ))}</tbody>
          </table></div>
        )}</Async>
        {change.error && <ErrorState error={change.error} />}
        {isOwner && (
          <form className="row" style={{ alignItems: 'end' }} onSubmit={(e) => { e.preventDefault(); add.run(async () => {
            await api(`${w}/members`, { method: 'POST', body: form }); setForm({ email: '', role: 'viewer' }); members.reload()
          }) }}>
            <div style={{ flex: 1, minWidth: 200 }}><Field label="Kayıtlı kullanıcının e-postası" hint="E-posta daveti V2'de; kullanıcı önce kayıt olmalı."><input type="email" required value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} /></Field></div>
            <Field label="Rol"><select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value })}><option>viewer</option><option>admin</option><option>owner</option></select></Field>
            <button className="btn primary" disabled={add.busy}>Üye ekle</button>
          </form>
        )}
        {add.error && <ErrorState error={add.error} />}
      </section>
      {canWrite && (
        <section className="card stack">
          <h2>Denetim kaydı</h2>
          <Async state={audit}>{(a) => a.items.length === 0 ? <p className="muted">Kayıt yok.</p> : (
            <div className="table-wrap"><table>
              <thead><tr><th>Zaman</th><th>İşlem</th><th>Hedef</th></tr></thead>
              <tbody>{a.items.map((x: any) => <tr key={x.id}><td>{dt(x.at)}</td><td className="mono">{x.action}</td><td className="mono muted">{x.target}</td></tr>)}</tbody>
            </table></div>
          )}</Async>
        </section>
      )}
    </>
  )
}
