import { useState } from 'react'
import { Download, Send, Square } from 'lucide-react'
import workoutSchema from '../../../examples/workout.schema.json'
import { Async, Badge, Empty, ErrorState, Field, JsonTree, PageHeader, download } from '../ui'
import { useApp } from '../App'
import { ImagePicker, UsageSummary, useGenerate, usePublishedProfiles, type Asset } from './shared'

export default function Playground() {
  const { env, meta, canWrite } = useApp()
  const profiles = usePublishedProfiles()
  const [profile, setProfile] = useState('')
  const [system, setSystem] = useState('')
  const [prompt, setPrompt] = useState('')
  const [assets, setAssets] = useState<Asset[]>([])
  const [mode, setMode] = useState<'text' | 'json'>('text')
  const [schema, setSchema] = useState(JSON.stringify(workoutSchema, null, 2))
  const [streaming, setStreaming] = useState(true)
  const [schemaError, setSchemaError] = useState<string>()
  const gen = useGenerate()

  if (!canWrite) return <><PageHeader title="Playground" /><Empty title="Playground owner/admin rolü gerektirir">Viewer rolü inference başlatamaz.</Empty></>
  if (!env) return <><PageHeader title="Playground" /><Empty title="Önce bir proje seçin"><a href="#/projects">Projeler</a></Empty></>

  const send = () => {
    let parsed: unknown
    if (mode === 'json') {
      try { parsed = JSON.parse(schema) } catch { setSchemaError('Şema geçerli JSON değil.'); return }
    }
    setSchemaError(undefined)
    const messages = [
      ...(system.trim() ? [{ role: 'system', content: [{ type: 'text', text: system }] }] : []),
      { role: 'user', content: [...(prompt.trim() ? [{ type: 'text', text: prompt }] : []), ...assets.map((a) => ({ type: 'image', assetId: a.assetId }))] },
    ]
    // Assets are consumed by the request (deleted server-side afterwards).
    setAssets([])
    gen.run({
      profile: profile || profiles.data?.[0]?.name, messages, stream: streaming,
      output: mode === 'json' ? { format: 'json_schema', name: 'output', schema: parsed } : { format: 'text' },
      metadata: { feature: 'playground' },
    })
  }

  return (
    <>
      <PageHeader title="Playground" description="Yönetim oturumuyla çalışır; API anahtarı arayüze gelmez. Bütçe ve limit kuralları aynen uygulanır."
        actions={meta.demo ? <Badge tone="warning">DEMO</Badge> : undefined} />
      <Async state={profiles} empty={(d) => d.length === 0 && <Empty title={`${env.type} ortamına yayınlanmış profil yok`}><a href="#/profiles">Profil yayınla</a></Empty>}>
        {(list) => (
          <div className="grid-2" style={{ alignItems: 'start' }}>
            <form className="card stack" onSubmit={(e) => { e.preventDefault(); send() }}>
              <Field label="Profil">
                <select value={profile || list[0].name} onChange={(e) => setProfile(e.target.value)}>
                  {list.map((p) => <option key={p.id} value={p.name}>{p.name}</option>)}
                </select>
              </Field>
              <Field label="Sistem mesajı (isteğe bağlı)"><textarea value={system} onChange={(e) => setSystem(e.target.value)} rows={2} /></Field>
              <Field label="Mesaj"><textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} /></Field>
              <ImagePicker assets={assets} onChange={setAssets} />
              <div className="row">
                <div className="tabs" role="tablist" aria-label="Çıktı biçimi">
                  <button type="button" role="tab" aria-selected={mode === 'text'} onClick={() => setMode('text')}>Metin</button>
                  <button type="button" role="tab" aria-selected={mode === 'json'} onClick={() => setMode('json')}>JSON Schema</button>
                </div>
                <label className="check"><input type="checkbox" checked={streaming} onChange={(e) => setStreaming(e.target.checked)} /> Streaming</label>
              </div>
              {mode === 'json' && <Field label="JSON Schema" error={schemaError}><textarea className="mono" rows={8} value={schema} onChange={(e) => setSchema(e.target.value)} /></Field>}
              <div className="row">
                <button className="btn primary" disabled={gen.busy || (!prompt.trim() && !assets.length)}><Send size={16} /> Gönder</button>
                {gen.busy && <button type="button" className="btn danger" onClick={gen.cancel}><Square size={16} /> İptal</button>}
              </div>
            </form>
            <section className="card stack output" aria-live="polite" aria-busy={gen.busy}>
              <h2>Çıktı</h2>
              {!gen.busy && !gen.text && !gen.result && !gen.error && <p className="muted" style={{ margin: 0 }}>Henüz istek gönderilmedi.</p>}
              {gen.text && !gen.result?.output.json && <pre>{gen.text}</pre>}
              {gen.result?.output.text != null && !gen.text && <pre>{gen.result.output.text}</pre>}
              {gen.result?.output.json != null && (<>
                <div className="row" style={{ justifyContent: 'space-between' }}>
                  <Badge tone="success">Şemaya göre doğrulandı</Badge>
                  <button className="btn ghost" onClick={() => download('output.json', gen.result!.output.json)}><Download size={16} /> JSON indir</button>
                </div>
                <JsonTree value={gen.result.output.json} />
              </>)}
              {gen.error && <ErrorState error={new Error(`${gen.error.message} (${gen.error.code}${gen.error.partial ? ', kısmi çıktı' : ''})`)} />}
              {gen.result && <UsageSummary r={gen.result} />}
            </section>
          </div>
        )}
      </Async>
    </>
  )
}
