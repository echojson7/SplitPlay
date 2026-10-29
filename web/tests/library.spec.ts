// SPDX-License-Identifier: MIT
import { flushPromises, mount } from '@vue/test-utils'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { Desk } from '../src/api/types'
import DeskCard from '../src/components/DeskCard.vue'
import { store } from '../src/store'
import { toasts } from '../src/toast'
import LibraryView from '../src/views/LibraryView.vue'
import { MockBackend, mockFetch } from '../mock/backend'

const Blank = { render: () => null }

function testRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'library', component: LibraryView },
      { path: '/desks/new', name: 'desk-new', component: LibraryView },
      { path: '/desks/:id', name: 'desk', component: Blank },
    ],
  })
}

function desk(state: Desk['state'], extra: Partial<Desk> = {}): Desk {
  return {
    id: 'anna',
    name: 'Аня',
    user: 'anna',
    basePort: 48089,
    autostart: false,
    gpu: 'auto',
    profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 50000, codec: 'hevc' },
    state,
    sessionId: null,
    metrics: { fps: 0, bitrateKbps: 0, encodeMsP99: 0, clients: 0 },
    lastError: null,
    ...extra,
  }
}

async function mountCard(d: Desk) {
  const router = testRouter()
  await router.push('/')
  return mount(DeskCard, { props: { desk: d }, global: { plugins: [router] } })
}

describe('DeskCard', () => {
  it('shows name, user, resolution, fps and state', async () => {
    const w = await mountCard(desk('Ready'))
    expect(w.text()).toContain('Аня')
    expect(w.text()).toContain('anna')
    expect(w.text()).toContain('1920×1080 · 120 FPS')
    expect(w.text()).toContain('Готов')
    expect(w.find('a[data-action="settings"]').attributes('href')).toBe('/desks/anna')
  })

  it('starts a stopped desk; PIN waits until it runs', async () => {
    const w = await mountCard(desk('Stopped'))
    const play = w.find('[data-action="start"]')
    expect(play.text()).toContain('Запустить')
    expect(play.classes()).toContain('btn-accent')
    expect(w.find('[data-action="pair"]').attributes('disabled')).toBeDefined()
    await play.trigger('click')
    expect(w.emitted('start')).toHaveLength(1)
  })

  it('stops a running desk and opens PIN pairing', async () => {
    const w = await mountCard(desk('Ready'))
    await w.find('[data-action="stop"]').trigger('click')
    expect(w.emitted('stop')).toHaveLength(1)
    await w.find('[data-action="pair"]').trigger('click')
    expect(w.emitted('pair')).toHaveLength(1)
  })

  it('shows progress instead of the Play button while starting', async () => {
    const w = await mountCard(desk('Connecting'))
    expect(w.find('[data-action="start"]').exists()).toBe(false)
    const bar = w.find('[role="progressbar"]')
    expect(bar.attributes('aria-valuenow')).toBe('40')
    expect(bar.text()).toContain('Подключение')
    await w.find('[data-action="stop"]').trigger('click')
    expect(w.emitted('stop')).toHaveLength(1)
  })

  it('shows the connected device, FPS and ping while streaming', async () => {
    const w = await mountCard(
      desk('Streaming', {
        stream: {
          client: 'Ноутбук Ани',
          address: '192.168.1.37',
          width: 1920,
          height: 1080,
          fps: 120,
          codec: 'hevc',
          bitrateKbps: 50000,
          hdr: false,
        },
        metrics: { fps: 119.6, bitrateKbps: 38000, encodeMsP50: 3.4, encodeMsP99: 6.1, lossPct: 0, rttMs: 8, clients: 1 },
      }),
    )
    expect(w.find('[data-live]').text()).toBe('Ноутбук Ани · 120 FPS · 8 мс')
    expect(w.text()).not.toContain('1920×1080 · 120 FPS')
  })

  it('names an unpaired stream by its address', async () => {
    const stream = { client: null, address: '10.0.0.7', width: 1280, height: 720, fps: 60, codec: 'h264', bitrateKbps: 0, hdr: false }
    const w = await mountCard(desk('Streaming', { stream, metrics: { fps: 60, bitrateKbps: 0, encodeMsP99: 0, clients: 1 } }))
    expect(w.find('[data-live]').text()).toBe('10.0.0.7 · 60 FPS')
  })

  it('shows the last error of a faulted desk', async () => {
    const w = await mountCard(desk('Faulted', { lastError: 'seat: logon failed' }))
    expect(w.text()).toContain('seat: logon failed')
    expect(w.find('[data-action="stop"]').exists()).toBe(true)
  })
})

describe('LibraryView', () => {
  let backend: MockBackend

  beforeAll(() => {
    backend = new MockBackend({ stepMs: 60_000 })
    vi.stubGlobal('fetch', mockFetch(backend))
    vi.stubGlobal('WebSocket', undefined)
  })

  afterAll(() => {
    store.stopLive()
    vi.unstubAllGlobals()
  })

  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i++) {
      await flushPromises()
      await new Promise((r) => setTimeout(r, 5))
    }
  }

  it('lists the desks as cards with an add tile, and runs card actions', async () => {
    const router = testRouter()
    await router.push('/')
    const w = mount(LibraryView, { global: { plugins: [router] }, attachTo: document.body })
    await settle()

    expect(w.findAll('article[data-desk]')).toHaveLength(2)
    expect(w.find('[data-add-desk]').text()).toContain('Добавить стол')

    // Start the stopped desk: the card switches to the progress bar.
    await w.find('[data-desk="guest"] [data-action="start"]').trigger('click')
    await settle()
    expect(store.state.desks.find((d) => d.id === 'guest')?.state).toBe('Provisioning')
    expect(w.find('[data-desk="guest"] [role="progressbar"]').exists()).toBe(true)

    // PIN on the running desk opens the pairing dialog with the Moonlight address.
    await w.find('[data-desk="anna"] [data-action="pair"]').trigger('click')
    await settle()
    expect(w.find('[role="dialog"]').text()).toContain('192.168.1.20:48089')
    await w.find('input[name="pin"]').setValue('1234')
    await w.find('[role="dialog"] form').trigger('submit')
    await settle()
    expect(w.find('[role="dialog"]').exists()).toBe(false)
    expect(toasts.map((x) => x.text)).toContain('Устройство подключено: Ноутбук')

    // The add tile opens the wizard.
    await router.push('/desks/new')
    await settle()
    expect(w.find('[role="dialog"]').text()).toContain('Новый стол')
    expect(w.find('[data-user="misha"]').exists()).toBe(true)
    w.unmount()
  })
})
