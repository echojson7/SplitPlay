// SPDX-License-Identifier: MIT
// Smoke test: the whole app (router, store, every page) against the mock API.
import { flushPromises, mount } from '@vue/test-utils'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
import { createAppRouter } from '../src/router'
import { store } from '../src/store'
import { MockBackend, mockFetch } from '../mock/backend'

async function settle(): Promise<void> {
  // Lazy route components load through dynamic imports; give them a few macrotasks.
  for (let i = 0; i < 10; i++) {
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

  it('walks through setup and every page without errors', async () => {
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
    expect(router.currentRoute.value.name).toBe('desks')
    expect(store.state.session).toMatchObject({ authenticated: true, setupRequired: false })

    expect(w.text()).toContain('Skyrim box')
    expect(w.findAll('tbody tr')).toHaveLength(3)

    await router.push('/desks/desk-1')
    await settle()
    expect(w.text()).toContain('Living room TV')
    expect(w.text()).toContain('Подключить клиент')

    await router.push('/desks/desk-1/edit')
    await settle()
    expect((w.find('input[name="name"]').element as HTMLInputElement).value).toBe('Skyrim box')

    await router.push('/desks/new')
    await settle()
    await w.find('input[name="id"]').setValue('new-desk')
    await w.find('input[name="name"]').setValue('New')
    await w.find('input[name="user"]').setValue('splitplay-new')
    await w.find('form').trigger('submit')
    await settle()
    expect(router.currentRoute.value.fullPath).toBe('/desks/new-desk')
    expect(w.text()).toContain('Остановлен')

    for (const path of ['/metrics', '/logs', '/settings']) {
      await router.push(path)
      await settle()
      expect(router.currentRoute.value.path).toBe(path)
    }
    expect(w.text()).toContain('Настройки хоста')
    await router.push('/metrics')
    await settle()
    expect(w.text()).toContain('NVIDIA GeForce RTX 4090')

    expect(errors).toEqual([])
    w.unmount()
  })
})
