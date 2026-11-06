// SPDX-License-Identifier: MIT
// Settings › Processor: «Cores for desks» Off/Auto with what Auto does on this CPU.
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
import { api } from '../src/api/client'
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

describe('cores for desks', () => {
  afterEach(() => {
    store.stopLive()
    vi.unstubAllGlobals()
    locale.value = 'ru'
  })

  it('shows the CPU and what Auto does, and switching to Auto lists the cores of each desk', async () => {
    const backend = new MockBackend({ stepMs: 10 })
    vi.stubGlobal('fetch', mockFetch(backend))
    vi.stubGlobal('WebSocket', undefined)
    const router = createAppRouter()
    const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
    await router.push('/settings')
    await settle()

    const row = w.find('[data-desk-cores]')
    expect(row.find('.title').text()).toBe('Ядра для столов')
    expect(row.find('[data-cpu-hint]').text()).toBe('AMD Ryzen 9 7950X3D 16-Core Processor · Каждому столу свой CCD, первому — с 3D V-Cache')
    expect(row.find('[data-cpu-plan]').exists()).toBe(false)
    const select = row.find('select')
    expect((select.element as HTMLSelectElement).value).toBe('off')

    await select.setValue('auto')
    await settle()
    expect((await api.getHostSettings()).deskCores).toBe('auto')
    expect(w.find('[data-cpu-plan]').text()).toBe('Аня: 16–31 · Гостевой: 0–15')
    w.unmount()
  })

  it('reads the CPU, dropping malformed values', async () => {
    vi.stubGlobal('fetch', () =>
      Promise.resolve(
        new Response(
          JSON.stringify({ name: '', cores: -1, threads: 'x', kind: 'quantum', mode: 'max', desks: [{ id: 'a', cpus: [1, -2, 1.5] }, { cpus: [] }] }),
          { status: 200 },
        ),
      ),
    )
    expect(await api.hostCpu()).toEqual({
      name: null,
      cores: 0,
      threads: 0,
      kind: 'uniform',
      mode: 'off',
      desks: [{ id: 'a', cpus: [1] }],
      auto: [],
    })
  })
})
