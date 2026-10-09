import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { Activity, Boxes, Dumbbell, FlaskConical, Gauge, KeyRound, LayoutDashboard, LogOut, Menu, Moon, PlugZap, Settings as SettingsIcon, SlidersHorizontal, Sun, Triangle } from 'lucide-react'
import { api, resetCsrf } from './api'
import { Async, Badge, useLoad } from './ui'
import Auth from './pages/Auth'
import Overview from './pages/Overview'
import Projects from './pages/Projects'
import Keys from './pages/Keys'
import Connections from './pages/Connections'
import Profiles from './pages/Profiles'
import Playground from './pages/Playground'
import Workout from './pages/Workout'
import Usage from './pages/Usage'
import Limits from './pages/Limits'
import Settings from './pages/Settings'

export type Workspace = { id: string; name: string; plan: string; role: 'owner' | 'admin' | 'viewer' }
export type Project = { id: string; name: string; archivedAt: string | null; createdAt: string }
export type Env = { id: string; projectId: string; type: 'development' | 'production' }
export type Meta = { demo: boolean; environment: string; providers: string[] }

export type Ctx = {
  ws: Workspace; w: string; meta: Meta
  projects: Project[]; project?: Project; env?: Env; envs: Env[]
  setProjectId: (id: string) => void; reloadProjects: () => void
  canWrite: boolean; isOwner: boolean
}
const AppCtx = createContext<Ctx>(null!)
export const useApp = () => useContext(AppCtx)

const pref = (k: string) => { try { return localStorage.getItem(k) } catch { return null } }
const savePref = (k: string, v: string) => { try { localStorage.setItem(k, v) } catch { /* private mode */ } }

const routes: { path: string; label: string; icon: ReactNode; page: () => ReactNode }[] = [
  { path: 'overview', label: 'Genel bakış', icon: <LayoutDashboard size={18} />, page: Overview },
  { path: 'projects', label: 'Projeler', icon: <Boxes size={18} />, page: Projects },
  { path: 'keys', label: 'API anahtarları', icon: <KeyRound size={18} />, page: Keys },
  { path: 'connections', label: 'Sağlayıcı bağlantıları', icon: <PlugZap size={18} />, page: Connections },
  { path: 'profiles', label: 'Profiller', icon: <SlidersHorizontal size={18} />, page: Profiles },
  { path: 'playground', label: 'Playground', icon: <FlaskConical size={18} />, page: Playground },
  { path: 'workout', label: 'Antrenman içe aktarma', icon: <Dumbbell size={18} />, page: Workout },
  { path: 'usage', label: 'Kullanım', icon: <Activity size={18} />, page: Usage },
  { path: 'limits', label: 'Limitler ve fiyatlar', icon: <Gauge size={18} />, page: Limits },
  { path: 'settings', label: 'Ayarlar', icon: <SettingsIcon size={18} />, page: Settings },
]

function useHash() {
  const [hash, setHash] = useState(location.hash.slice(2) || 'overview')
  useEffect(() => {
    const on = () => setHash(location.hash.slice(2) || 'overview')
    addEventListener('hashchange', on)
    return () => removeEventListener('hashchange', on)
  }, [])
  return hash
}

export function ThemeToggle() {
  const [theme, setTheme] = useState(document.documentElement.dataset.theme ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'))
  const next = theme === 'dark' ? 'light' : 'dark'
  return (
    <button className="icon-btn" aria-label={next === 'dark' ? 'Koyu temaya geç' : 'Açık temaya geç'} onClick={() => {
      document.documentElement.dataset.theme = next; savePref('theme', next); setTheme(next)
    }}>{theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}</button>
  )
}

export default function App() {
  const [session, setSession] = useState(0)
  const me = useLoad(() => api('/api/v1/auth/me'), [session])
  const meta = useLoad<Meta>(() => api('/api/v1/meta'), [])
  if (me.error?.status === 401) return <Auth demo={meta.data?.demo} onDone={() => { resetCsrf(); setSession((s) => s + 1) }} />
  return (
    <Async state={me}>
      {(m) => <Async state={meta}>{(mt) => <Shell me={m} meta={mt} onLogout={() => { resetCsrf(); setSession((s) => s + 1) }} />}</Async>}
    </Async>
  )
}

function Shell({ me, meta, onLogout }: { me: { email: string; workspaces: Workspace[] }; meta: Meta; onLogout: () => void }) {
  const route = useHash()
  const [open, setOpen] = useState(false)
  const [wsId, setWsId] = useState(me.workspaces.find((w) => w.id === pref('ws'))?.id ?? me.workspaces[0]?.id)
  const ws = me.workspaces.find((w) => w.id === wsId)!
  const w = `/api/v1/workspaces/${ws.id}`
  const projects = useLoad(() => api(`${w}/projects?limit=100`).then((r) => r.items as Project[]), [w])
  const active = (projects.data ?? []).filter((p) => !p.archivedAt)
  const [projectId, setProjectId] = useState(pref('project') ?? '')
  const project = active.find((p) => p.id === projectId) ?? active[0]
  const envs = useLoad(() => (project ? api<Env[]>(`${w}/projects/${project.id}/environments`) : Promise.resolve([])), [w, project?.id])
  const [envType, setEnvType] = useState(pref('env') ?? 'development')
  const env = envs.data?.find((e) => e.type === envType)
  const current = routes.find((r) => r.path === route) ?? routes[0]
  const Page = current.page

  const ctx: Ctx = {
    ws, w, meta, projects: projects.data ?? [], project, env, envs: envs.data ?? [],
    setProjectId: (id) => { setProjectId(id); savePref('project', id) }, reloadProjects: projects.reload,
    canWrite: ws.role !== 'viewer', isOwner: ws.role === 'owner',
  }

  return (
    <AppCtx.Provider value={ctx}>
      <div className="shell">
        <nav className={`sidebar ${open ? 'open' : ''}`} aria-label="Ana menü">
          <div className="brand"><Triangle size={20} color="var(--accent)" aria-hidden /> PrismGate</div>
          {routes.map((r) => (
            <a key={r.path} href={`#/${r.path}`} className="nav-link" aria-current={r === current ? 'page' : undefined} onClick={() => setOpen(false)}>
              {r.icon}{r.label}
            </a>
          ))}
          <div style={{ flex: 1 }} />
          <div className="muted" style={{ padding: 8, fontSize: 12 }}>{me.email}<br />Rol: {ws.role}</div>
          <button className="nav-link icon-btn" onClick={async () => { await api('/api/v1/auth/logout', { method: 'POST' }); onLogout() }}>
            <LogOut size={18} /> Çıkış yap
          </button>
        </nav>
        <div>
          {meta.demo && <div className="demo-banner" role="note">DEMO modu: “demo” sağlayıcısı gerçek model çağırmaz, çıktılar örnektir.</div>}
          <div className="topbar">
            <button className="icon-btn menu-btn" aria-label="Menüyü aç" aria-expanded={open} onClick={() => setOpen(!open)}><Menu size={20} /></button>
            <label className="sr-only" htmlFor="ws-select">Workspace</label>
            <select id="ws-select" value={ws.id} onChange={(e) => { setWsId(e.target.value); savePref('ws', e.target.value) }}>
              {me.workspaces.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
            </select>
            <label className="sr-only" htmlFor="project-select">Proje</label>
            <select id="project-select" value={project?.id ?? ''} onChange={(e) => ctx.setProjectId(e.target.value)} disabled={!active.length}>
              {!active.length && <option value="">Proje yok</option>}
              {active.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
            <label className="sr-only" htmlFor="env-select">Ortam</label>
            <select id="env-select" value={envType} onChange={(e) => { setEnvType(e.target.value); savePref('env', e.target.value) }} disabled={!project}>
              <option value="development">development</option>
              <option value="production">production</option>
            </select>
            <div className="spacer" />
            {meta.demo && <Badge tone="warning">DEMO</Badge>}
            <Badge tone="accent">{ws.plan} planı</Badge>
            <ThemeToggle />
          </div>
          <main className="content" id="main"><Page key={`${current.path}:${ws.id}`} /></main>
        </div>
      </div>
    </AppCtx.Provider>
  )
}
