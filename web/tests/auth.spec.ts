// SPDX-License-Identifier: MIT
// Sign-in: the tray is signed in by the service; a plain browser logs in with the admin password.
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
import { createAppRouter } from '../src/router'
import { store } from '../src/store'
import { MockBackend, mockFetch } from '../mock/backend'

async function settle(): Promise<void> {
  for (let i = 0; i < 30; i++) {
    await flushPromises()
    await new Promise((r) => setTimeout(r, 10))
  }
}

describe('sign-in', () => {
  afterEach(() => {
    store.stopLive()
    store.state.session = null
    vi.unstubAllGlobals()
  })

  it('sends a browser without a password to the tray', async () => {
    vi.stubGlobal('fetch', mockFetch(new MockBackend({ stepMs: 10, trusted: false })))
    vi.stubGlobal('WebSocket', undefined)
    const router = createAppRouter()
    const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
    await router.push('/settings')
    await settle()
    expect(router.currentRoute.value.name).toBe('login')
    expect(w.find('[data-use-tray]').text()).toContain('трее')
    expect(w.find('input#pw').exists()).toBe(false)
    w.unmount()
  })

  it('lets the tray in without a login and asks it to sign in again on a 401', async () => {
    const backend = new MockBackend({ stepMs: 10 })
    vi.stubGlobal('fetch', mockFetch(backend))
    vi.stubGlobal('WebSocket', undefined)
    const postMessage = vi.fn()
    vi.stubGlobal('chrome', { webview: { postMessage } })
    const router = createAppRouter()
    const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
    await router.push('/settings')
    await settle()
    expect(router.currentRoute.value.name).toBe('settings')
    // No nagging about a password inside the tray.
    expect(w.find('.inline-msg.warn').exists()).toBe(false)

    vi.stubGlobal('fetch', () => Promise.resolve(new Response(JSON.stringify({ error: { code: 'unauthorized', message: 'x' } }), { status: 401 })))
    await store.refreshDesks().catch(() => undefined)
    expect(postMessage).toHaveBeenCalledWith('auth')
    w.unmount()
  })
})
