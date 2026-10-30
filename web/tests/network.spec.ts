// SPDX-License-Identifier: MIT
// Several desks on one PC: network problems in Settings, busy-port errors in the desk's language.
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
import { api } from '../src/api/client'
import { deskError, lastErrorText } from '../src/desk'
import { locale } from '../src/i18n'
import { createAppRouter } from '../src/router'
import { store } from '../src/store'
import { MockBackend, mockFetch, type MockBackendOptions } from '../mock/backend'

async function settle(): Promise<void> {
  for (let i = 0; i < 30; i++) {
    await flushPromises()
    await new Promise((r) => setTimeout(r, 10))
  }
}

async function settings(network?: MockBackendOptions['network']) {
  vi.stubGlobal('fetch', mockFetch(new MockBackend({ stepMs: 10, network })))
  vi.stubGlobal('WebSocket', undefined)
  const router = createAppRouter()
  const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
  await router.push('/settings')
  await settle()
  return w
}

describe('network status', () => {
  afterEach(() => {
    store.stopLive()
    vi.unstubAllGlobals()
    locale.value = 'ru'
  })

  it('shows no network rows when the network is private and mDNS is allowed', async () => {
    const w = await settings()
    expect(w.find('[data-network-problems]').exists()).toBe(false)
    w.unmount()
  })

  it('shows "Network · Public" and "mDNS · Off" with a one-line hint', async () => {
    const w = await settings({ profile: 'Public', mdnsAllowed: false })
    const pub = w.find('[data-network-public]')
    expect(pub.find('.title').text()).toBe('Сеть')
    expect(pub.find('.ctl').text()).toBe('Общедоступная')
    expect(pub.find('.desc').text()).toContain('частной')
    const mdns = w.find('[data-mdns-off]')
    expect(mdns.find('.title').text()).toBe('mDNS')
    expect(mdns.find('.ctl').text()).toBe('Выключен')
    w.unmount()
  })

  it('shows only the row that has a problem; unknown is not a problem', async () => {
    const w = await settings({ profile: null, mdnsAllowed: false })
    expect(w.find('[data-network-public]').exists()).toBe(false)
    expect(w.find('[data-mdns-off]').exists()).toBe(true)
    w.unmount()
  })

  it('reads the profile and mDNS state, dropping malformed values', async () => {
    vi.stubGlobal('fetch', () =>
      Promise.resolve(new Response(JSON.stringify({ addresses: ['10.0.0.5', 7], profile: 'Hotel', mdnsAllowed: 'no' }), { status: 200 })),
    )
    expect(await api.hostNetwork()).toEqual({ addresses: ['10.0.0.5'], profile: null, mdnsAllowed: null })
    expect(await api.hostAddresses()).toEqual(['10.0.0.5'])
  })
})

describe('port errors', () => {
  afterEach(() => {
    locale.value = 'ru'
  })

  it('translates the service port messages and leaves others alone', () => {
    expect(lastErrorText('Port 48089 busy: sunshine.exe')).toBe('Порт 48089 занят: sunshine.exe')
    expect(lastErrorText('Port 48100 reserved by Windows')).toBe('Порт 48100 зарезервирован Windows')
    expect(lastErrorText('worker exited with code 3')).toBe('worker exited with code 3')
    locale.value = 'en'
    expect(lastErrorText('Port 48089 busy: sunshine.exe')).toBe('Port 48089 busy: sunshine.exe')
  })

  it('shows a busy port while recovering, other errors only when faulted', () => {
    expect(deskError({ state: 'Recovering', lastError: 'Port 48099 busy: obs64.exe' })).toBe('Порт 48099 занят: obs64.exe')
    expect(deskError({ state: 'Recovering', lastError: 'worker exited with code 3' })).toBeNull()
    expect(deskError({ state: 'Faulted', lastError: 'worker exited with code 3' })).toBe('worker exited with code 3')
    expect(deskError({ state: 'Ready', lastError: null })).toBeNull()
  })
})
