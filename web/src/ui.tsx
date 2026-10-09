import { createContext, useCallback, useContext, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { AlertTriangle, Inbox, Loader2, RefreshCw, X } from 'lucide-react'
import { ApiError } from './api'

export function useLoad<T>(fn: () => Promise<T>, deps: unknown[]) {
  const [state, setState] = useState<{ data?: T; error?: ApiError; loading: boolean }>({ loading: true })
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let live = true
    setState((s) => ({ ...s, loading: true, error: undefined }))
    fn().then(
      (data) => live && setState({ data, loading: false }),
      (error) => live && setState({ error, loading: false }),
    )
    return () => { live = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick])
  return { ...state, reload: useCallback(() => setTick((t) => t + 1), []) }
}

/** Renders loading / error / empty states around real data. */
export function Async<T>({ state, empty, children }: { state: { data?: T; error?: ApiError; loading: boolean; reload: () => void }; empty?: (d: T) => ReactNode | false; children: (d: T) => ReactNode }) {
  if (state.error) return <ErrorState error={state.error} retry={state.reload} />
  if (state.data === undefined) return <div className="state" role="status"><Loader2 className="spin" aria-hidden /> Yükleniyor…</div>
  const e = empty?.(state.data)
  return <>{e || children(state.data)}</>
}

export const ErrorState = ({ error, retry }: { error: ApiError | Error; retry?: () => void }) => (
  <div className="state error" role="alert">
    <AlertTriangle aria-hidden /> <span>{error.message}</span>
    {retry && <button className="btn ghost" onClick={retry}><RefreshCw size={16} aria-hidden /> Tekrar dene</button>}
  </div>
)

export const Empty = ({ title, children }: { title: string; children?: ReactNode }) => (
  <div className="state empty"><Inbox aria-hidden /><div><strong>{title}</strong>{children && <div className="muted">{children}</div>}</div></div>
)

export const Badge = ({ tone = 'neutral', children }: { tone?: 'neutral' | 'success' | 'warning' | 'danger' | 'accent'; children: ReactNode }) =>
  <span className={`badge ${tone}`}>{children}</span>

export const PageHeader = ({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) => (
  <header className="page-header">
    <div><h1>{title}</h1>{description && <p className="muted">{description}</p>}</div>
    {actions && <div className="row">{actions}</div>}
  </header>
)

export function Field({ label, error, hint, children }: { label: string; error?: string; hint?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
      {hint && !error && <small className="muted">{hint}</small>}
      {error && <small className="field-error" role="alert">{error}</small>}
    </label>
  )
}

/** Native modal dialog: focus is trapped and Escape closes it. */
export function Dialog({ open, title, onClose, children, footer }: { open: boolean; title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  useEffect(() => {
    const d = ref.current
    if (!d) return
    if (open && !d.open) d.showModal()
    if (!open && d.open) d.close()
  }, [open])
  return (
    <dialog ref={ref} onClose={onClose} aria-labelledby={titleId}>
      <div className="dialog-head">
        <h2 id={titleId}>{title}</h2>
        <button className="icon-btn" onClick={onClose} aria-label="Kapat"><X size={18} /></button>
      </div>
      <div className="dialog-body">{children}</div>
      {footer && <div className="dialog-foot">{footer}</div>}
    </dialog>
  )
}

export function Confirm({ open, title, message, danger, onConfirm, onClose }: { open: boolean; title: string; message: ReactNode; danger?: boolean; onConfirm: () => Promise<void>; onClose: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<Error>()
  return (
    <Dialog open={open} title={title} onClose={onClose} footer={<>
      <button className="btn ghost" onClick={onClose}>Vazgeç</button>
      <button className={`btn ${danger ? 'danger' : 'primary'}`} disabled={busy} onClick={async () => {
        setBusy(true); setError(undefined)
        try { await onConfirm(); onClose() } catch (e) { setError(e as Error) } finally { setBusy(false) }
      }}>Onayla</button>
    </>}>
      <p>{message}</p>
      {error && <ErrorState error={error} />}
    </Dialog>
  )
}

const ToastCtx = createContext<(msg: string) => void>(() => {})
export const useToast = () => useContext(ToastCtx)
export function ToastProvider({ children }: { children: ReactNode }) {
  const [msg, setMsg] = useState<string>()
  useEffect(() => { if (msg) { const t = setTimeout(() => setMsg(undefined), 3000); return () => clearTimeout(t) } }, [msg])
  return (
    <ToastCtx.Provider value={setMsg}>
      {children}
      <div className="toast" role="status" aria-live="polite">{msg}</div>
    </ToastCtx.Provider>
  )
}

/** Submits an async action and exposes busy/error for forms. */
export function useAction() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<ApiError>()
  const run = async (fn: () => Promise<void>) => {
    setBusy(true); setError(undefined)
    try { await fn() } catch (e) { setError(e as ApiError) } finally { setBusy(false) }
  }
  return { busy, error, run, setError }
}

export function JsonTree({ value }: { value: unknown }) {
  if (value === null) return <span className="json-null">null</span>
  if (Array.isArray(value)) return value.length === 0 ? <span>[]</span> : (
    <ol className="json-list" start={0}>{value.map((v, i) => <li key={i}><JsonTree value={v} /></li>)}</ol>
  )
  if (typeof value === 'object') return (
    <ul className="json-list">{Object.entries(value as object).map(([k, v]) => <li key={k}><span className="json-key">{k}:</span> <JsonTree value={v} /></li>)}</ul>
  )
  return <span className={`json-${typeof value}`}>{JSON.stringify(value)}</span>
}

export function download(name: string, data: unknown) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
  const a = Object.assign(document.createElement('a'), { href: url, download: name })
  a.click()
  URL.revokeObjectURL(url)
}
