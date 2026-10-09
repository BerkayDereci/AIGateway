import { describe, expect, it } from 'vitest'
import { parseSse } from './api'

describe('parseSse', () => {
  it('keeps partial frames for the next chunk and drops heartbeats', () => {
    const a = parseSse('event: started\ndata: {"requestId":"r"}\n\n: ping\n\nevent: text.delta\ndata: {"sequence":1,"te')
    expect(a.events).toEqual([{ event: 'started', data: { requestId: 'r' } }])
    const b = parseSse(a.rest + 'xt":"ğü"}\n\n')
    expect(b.events).toEqual([{ event: 'text.delta', data: { sequence: 1, text: 'ğü' } }])
    expect(b.rest).toBe('')
  })

  it('parses terminal error events', () => {
    const { events } = parseSse('event: error\ndata: {"code":"provider_error","partial":true}\n\n')
    expect(events[0]).toEqual({ event: 'error', data: { code: 'provider_error', partial: true } })
  })
})
