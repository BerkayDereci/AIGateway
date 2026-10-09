import { useEffect, useState } from 'react'
import { Download, Plus, ScanText, Trash2 } from 'lucide-react'
import workoutSchema from '../../../examples/workout.schema.json'
import workoutRequest from '../../../examples/workout.request.json'
import { Async, Badge, Empty, ErrorState, Field, PageHeader, download } from '../ui'
import { useApp } from '../App'
import { ImagePicker, UsageSummary, useGenerate, usePublishedProfiles, type Asset } from './shared'

type Exercise = { name: string | null; sets: number | null; reps: string | null; weightKg: number | null; needsReview: boolean; note: string | null }
type Program = { title: string | null; days: { name: string | null; exercises: Exercise[] }[]; warnings: string[] }
const systemPrompt = workoutRequest.messages[0].content[0].text as string
const num = (v: string) => (v === '' ? null : Number(v))

export default function Workout() {
  const { env, canWrite } = useApp()
  const profiles = usePublishedProfiles()
  const [profile, setProfile] = useState('')
  const [assets, setAssets] = useState<Asset[]>([])
  const [program, setProgram] = useState<Program>()
  const [confirmed, setConfirmed] = useState(false)
  const gen = useGenerate()
  useEffect(() => { if (gen.result?.output.json) setProgram(structuredClone(gen.result.output.json as Program)) }, [gen.result])

  if (!canWrite) return <><PageHeader title="Antrenman içe aktarma" /><Empty title="Bu demo owner/admin rolü gerektirir" /></>
  if (!env) return <><PageHeader title="Antrenman içe aktarma" /><Empty title="Önce bir proje seçin" /></>

  const extract = async (name: string) => {
    setProgram(undefined); setConfirmed(false)
    await gen.run({
      profile: name, stream: false, metadata: { feature: 'workout-import' },
      messages: [
        { role: 'system', content: [{ type: 'text', text: systemPrompt }] },
        { role: 'user', content: [{ type: 'text', text: 'Read this workout schedule and return the requested JSON.' }, ...assets.map((a) => ({ type: 'image', assetId: a.assetId }))] },
      ],
      output: { format: 'json_schema', name: 'workout_program', schema: workoutSchema },
    })
    setAssets([])
  }

  const update = (f: (p: Program) => void) => setProgram((p) => { const c = structuredClone(p!); f(c); return c })

  return (
    <>
      <PageHeader title="Antrenman içe aktarma" description="Program fotoğrafını düzenlenebilir JSON'a çevirir. Yalnızca görselde görüneni aktarır; tıbbi değerlendirme veya egzersiz önerisi değildir." />
      <Async state={profiles} empty={(d) => d.length === 0 && <Empty title="Yayınlanmış profil yok">Görsel ve JSON Schema destekli bir modelle profil yayınlayın.</Empty>}>
        {(list) => (
          <div className="card stack">
            <Field label="Profil" hint="Profil modeli görsel ve JSON Schema desteklemeli.">
              <select value={profile || list[0].name} onChange={(e) => setProfile(e.target.value)}>{list.map((p) => <option key={p.id}>{p.name}</option>)}</select>
            </Field>
            <ImagePicker assets={assets} onChange={setAssets} />
            <div className="row">
              <button className="btn primary" disabled={!assets.length || gen.busy} onClick={() => extract(profile || list[0].name)}><ScanText size={16} /> {gen.busy ? 'Okunuyor…' : 'Programı çıkar'}</button>
              {gen.busy && <button className="btn danger" onClick={gen.cancel}>İptal</button>}
            </div>
            {gen.error && <ErrorState error={new Error(`${gen.error.message} (${gen.error.code})`)} />}
          </div>
        )}
      </Async>
      {program && (
        <section className="card stack" aria-label="Çıkarılan program">
          {gen.result && <UsageSummary r={gen.result} />}
          {program.warnings.length > 0 && (
            <div className="state error" style={{ background: 'var(--warning-soft)', color: 'var(--warning)' }} role="note">
              <ul style={{ margin: 0 }}>{program.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
            </div>
          )}
          <Field label="Program adı"><input value={program.title ?? ''} onChange={(e) => update((p) => { p.title = e.target.value || null })} /></Field>
          {program.days.length === 0 && <p className="muted">Görselde gün bulunamadı. Elle ekleyebilirsiniz.</p>}
          {program.days.map((day, d) => (
            <fieldset key={d} className="card tight stack">
              <legend className="sr-only">Gün {d + 1}</legend>
              <div className="row">
                <div style={{ flex: 1 }}><Field label={`Gün ${d + 1}`}><input value={day.name ?? ''} onChange={(e) => update((p) => { p.days[d].name = e.target.value || null })} /></Field></div>
                <button className="icon-btn" aria-label={`Gün ${d + 1} sil`} onClick={() => update((p) => { p.days.splice(d, 1) })}><Trash2 size={16} /></button>
              </div>
              {day.exercises.map((ex, i) => (
                <div key={i} className={`exercise ${ex.needsReview ? 'review' : ''}`}>
                  <Field label="Egzersiz"><input value={ex.name ?? ''} onChange={(e) => update((p) => { p.days[d].exercises[i].name = e.target.value || null })} /></Field>
                  <Field label="Set"><input type="number" min={0} value={ex.sets ?? ''} onChange={(e) => update((p) => { p.days[d].exercises[i].sets = num(e.target.value) })} /></Field>
                  <Field label="Tekrar"><input value={ex.reps ?? ''} onChange={(e) => update((p) => { p.days[d].exercises[i].reps = e.target.value || null })} /></Field>
                  <Field label="Ağırlık (kg)"><input type="number" step="0.5" min={0} value={ex.weightKg ?? ''} onChange={(e) => update((p) => { p.days[d].exercises[i].weightKg = num(e.target.value) })} /></Field>
                  <Field label="Not"><input value={ex.note ?? ''} onChange={(e) => update((p) => { p.days[d].exercises[i].note = e.target.value || null })} /></Field>
                  <div className="row">
                    {ex.needsReview && <Badge tone="warning">Kontrol et</Badge>}
                    <label className="check"><input type="checkbox" checked={!ex.needsReview} onChange={(e) => update((p) => { p.days[d].exercises[i].needsReview = !e.target.checked })} /> Doğru</label>
                    <button className="icon-btn" aria-label="Egzersizi sil" onClick={() => update((p) => { p.days[d].exercises.splice(i, 1) })}><Trash2 size={16} /></button>
                  </div>
                </div>
              ))}
              <button className="btn ghost" onClick={() => update((p) => { p.days[d].exercises.push({ name: null, sets: null, reps: null, weightKg: null, needsReview: false, note: null }) })}><Plus size={16} /> Egzersiz ekle</button>
            </fieldset>
          ))}
          <button className="btn ghost" onClick={() => update((p) => { p.days.push({ name: null, exercises: [] }) })}><Plus size={16} /> Gün ekle</button>
          <label className="check"><input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} /> Programı görselle karşılaştırdım ve düzelttim.</label>
          <button className="btn primary" disabled={!confirmed} onClick={() => download('workout-program.json', program)}><Download size={16} /> JSON indir</button>
        </section>
      )}
    </>
  )
}
