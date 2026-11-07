// SPDX-License-Identifier: MIT
// Host warnings: a recent graphics driver reset and an overloaded encoder or VRAM show as rows on Desks and Metrics.
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
import type { HostWarning } from '../src/api/types'
import { locale } from '../src/i18n'
import { createAppRouter } from '../src/router'
import { store } from '../src/store'
import { MockBackend, mockFetch } from '../mock/backend'

async function settle(): Promise<void> {
  for (let i = 0; i < 30; i++) {
    await flushPromises()
    await new Promise((r) => setTimeout(r, 10))
  }
}

async function open(path: string, warnings?: HostWarning[]) {
  vi.stubGlobal('fetch', mockFetch(new MockBackend({ stepMs: 10, warnings })))
  vi.stubGlobal('WebSocket', undefined)
  const router = createAppRouter()
  const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
  await router.push(path)
  await settle()
  return w
}

describe('host warnings', () => {
  afterEach(() => {
    store.stopLive()
    store.state.metrics = null
    vi.unstubAllGlobals()
    locale.value = 'ru'
  })

  it('shows nothing when all is well', async () => {
    const w = await open('/')
    expect(w.find('[data-host-warnings]').exists()).toBe(false)
    w.unmount()
  })

  it('shows a driver reset, an overloaded encoder and full VRAM on the desks page', async () => {
    const w = await open('/', [
      { code: 'gpuReset', at: '2026-11-06T21:40:00Z', desks: 2 },
      { code: 'encoderBusy', gpu: 'NVIDIA GeForce RTX 5070', percent: 97 },
      { code: 'vramFull', gpu: 'NVIDIA GeForce RTX 5070', percent: 96 },
      { code: 'somethingNew' },
    ])
    const rows = w.findAll('[data-host-warnings] [data-warning]')
    expect(rows.map((r) => r.attributes('data-warning'))).toEqual(['gpuReset', 'encoderBusy', 'vramFull'])
    expect(rows[0]!.text()).toContain('Сбой драйвера видеокарты')
    expect(rows[0]!.text()).toContain('Перезапущено столов: 2')
    expect(rows[1]!.text()).toContain('Кодер видеокарты перегружен')
    expect(rows[1]!.text()).toContain('97 %')
    expect(rows[2]!.text()).toContain('RTX 5070')
    w.unmount()
  })

  it('shows them on the metrics page too', async () => {
    const w = await open('/diagnostics', [{ code: 'vramFull', gpu: 'RTX', percent: 96 }])
    expect(w.find('[data-warning="vramFull"]').exists()).toBe(true)
    w.unmount()
  })
})
