import { useState } from 'react'
import { Triangle } from 'lucide-react'
import { api, resetCsrf } from '../api'
import { Badge, ErrorState, Field, useAction } from '../ui'
import { ThemeToggle } from '../App'

export default function Auth({ demo, onDone }: { demo?: boolean; onDone: () => void }) {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [form, setForm] = useState({ email: '', password: '', workspaceName: '' })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const { busy, error, run } = useAction()
  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    const errs: Record<string, string> = {}
    if (!/^\S+@\S+\.\S+$/.test(form.email)) errs.email = 'Geçerli bir e-posta girin.'
    if (mode === 'register' && form.password.length < 10) errs.password = 'Parola en az 10 karakter olmalı (büyük/küçük harf, rakam ve sembol).'
    if (mode === 'register' && !form.workspaceName.trim()) errs.workspaceName = 'Workspace adı zorunlu.'
    setErrors(errs)
    if (Object.keys(errs).length) return
    run(async () => {
      await api(`/api/v1/auth/${mode}`, { method: 'POST', body: mode === 'login' ? { email: form.email, password: form.password } : form })
      resetCsrf()
      onDone()
    })
  }

  return (
    <div className="auth">
      <form className="card" onSubmit={submit} noValidate>
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <h1 className="row"><Triangle size={22} color="var(--accent)" aria-hidden /> PrismGate</h1>
          <div className="row">{demo && <Badge tone="warning">DEMO</Badge>}<ThemeToggle /></div>
        </div>
        <div className="tabs" role="tablist">
          <button type="button" role="tab" aria-selected={mode === 'login'} onClick={() => setMode('login')}>Giriş</button>
          <button type="button" role="tab" aria-selected={mode === 'register'} onClick={() => setMode('register')}>Kayıt ol</button>
        </div>
        <Field label="E-posta" error={errors.email}><input type="email" autoComplete="email" value={form.email} onChange={set('email')} required /></Field>
        <Field label="Parola" error={errors.password}>
          <input type="password" autoComplete={mode === 'login' ? 'current-password' : 'new-password'} value={form.password} onChange={set('password')} required />
        </Field>
        {mode === 'register' && <Field label="Workspace adı" error={errors.workspaceName}><input value={form.workspaceName} onChange={set('workspaceName')} /></Field>}
        {error && <ErrorState error={error} />}
        <button className="btn primary" disabled={busy}>{mode === 'login' ? 'Giriş yap' : 'Hesap oluştur'}</button>
        <p className="muted" style={{ margin: 0, fontSize: 12 }}>Parola sıfırlama ve e-posta doğrulaması pilot sürümde yok (production gereksinimi).</p>
      </form>
    </div>
  )
}
