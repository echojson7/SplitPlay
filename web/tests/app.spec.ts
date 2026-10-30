// SPDX-License-Identifier: MIT
// Smoke test: the whole app (router, store, every page) against the mock API.
import { flushPromises, mount } from '@vue/test-utils'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
import { createAppRouter } from '../src/router'
import { store } from '../src/store'
import { MockBackend, mockFetch } from '../mock/backend'

async function settle(): Promise<void> {
  // Lazy route components load through dynamic imports; give them a few macrotasks
  // (a cold transform of a view on a busy machine can take more than 100 ms).
  for (let i = 0; i < 30; i++) {
    await flushPromises()
    await new Promise((r) => setTimeout(r, 10))
  }
}

describe('app smoke', () => {
  const errors: unknown[] = []

  beforeAll(() => {
    vi.stubGlobal('fetch', mockFetch(new MockBackend({ stepMs: 10 })))
    // No WebSocket in the mock: the store falls back to polling.
    vi.stubGlobal('WebSocket', undefined)
    vi.spyOn(console, 'error').mockImplementation((...args) => errors.push(args))
    vi.spyOn(console, 'warn').mockImplementation((...args) => errors.push(args))
  })

  afterAll(() => {
    store.stopLive()
    vi.unstubAllGlobals()
  })

  it('walks through setup, the wizard and every page without errors', async () => {
    const router = createAppRouter()
    const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
    await router.push('/')
    await settle()

    // First start: no password, redirected to setup.
    expect(router.currentRoute.value.name).toBe('setup')
    await w.find('input#pw').setValue('a-long-password')
    await w.find('input#pw2').setValue('a-long-password')
    await w.find('form').trigger('submit')
    await settle()
    expect(router.currentRoute.value.name).toBe('library')
    expect(store.state.session).toMatchObject({ authenticated: true, setupRequired: false })

    expect(w.find('nav.nav').text()).toContain('Столы')
    // The Services button sits on the app name row of the nav pane.
    expect(w.find('.navpane .approw [data-services-button]').text()).toBe('Службы')
    expect(w.findAll('article[data-desk]')).toHaveLength(2)
    expect(w.text()).toContain('Аня')

    await router.push('/desks/anna')
    await settle()
    expect(w.text()).toContain('Ноутбук Ани')
    // The "Now" group: the streaming device and its live numbers.
    expect(w.find('[data-live-device]').text()).toBe('Ноутбук Ани192.168.1.37')
    expect(w.find('[data-live-stream]').text()).toBe('1920×1080 · 120 FPS · HEVC · 50 Мбит/с')
    expect(w.find('[data-live-fps]').text()).toMatch(/^\d+$/)
    expect(w.find('[data-live-ping]').text()).toMatch(/^\d+ мс$/)
    expect(w.find('[data-live-encode]').text()).toMatch(/^\d+,\d \/ \d+,\d мс$/)
    expect(w.find('[data-live-loss]').text()).toMatch(/ %$/)
    expect(w.find('[data-address]').text()).toBe('192.168.1.20:48089')
    expect(w.find('details.more').attributes('open')).toBeUndefined()

    // Add a desk through the wizard: it is created, started and opened.
    await router.push('/desks/new')
    await settle()
    await w.find('[data-user="misha"]').trigger('click')
    await w.find('[role="dialog"] form').trigger('submit')
    await w.find('input[name="password"]').setValue('misha')
    await w.find('[role="dialog"] form').trigger('submit')
    await w.find('[data-res="1280x720"]').trigger('click')
    await w.find('[role="dialog"] form').trigger('submit')
    await settle()
    expect(router.currentRoute.value.fullPath).toBe('/desks/misha')
    expect(store.state.desks.find((d) => d.id === 'misha')?.profile).toMatchObject({ width: 1280, height: 720, fps: 60 })
    expect(w.find('h1').text()).toBe('Михаил')
    expect(w.find('[data-live-none]').text()).toBe('Никто не подключён')
    expect(w.find('[data-live-device]').exists()).toBe(false)

    for (const path of ['/settings', '/diagnostics/metrics', '/diagnostics/logs']) {
      await router.push(path)
      await settle()
      expect(router.currentRoute.value.path).toBe(path)
    }
    await router.push('/settings')
    await settle()
    expect(w.text()).toContain('Пароль администратора')
    await router.push('/metrics')
    await settle()
    expect(router.currentRoute.value.path).toBe('/diagnostics/metrics')
    expect(w.text()).toContain('NVIDIA GeForce RTX 4090')

    expect(errors).toEqual([])
    w.unmount()
  })
})
