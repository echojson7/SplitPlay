// SPDX-License-Identifier: MIT
// Desk page › Games: the desk's FPS limit for games and the games the service found in it.
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

describe('game FPS limits', () => {
  afterEach(() => {
    store.stopLive()
    vi.unstubAllGlobals()
    locale.value = 'ru'
  })

  it('lists the games of the desk, changes the limit and turns it off for one game', async () => {
    const backend = new MockBackend({ stepMs: 10 })
    vi.stubGlobal('fetch', mockFetch(backend))
    vi.stubGlobal('WebSocket', undefined)
    const router = createAppRouter()
    const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
    await router.push('/desks/anna')
    await settle()

    const group = w.find('[data-games]')
    expect(group.find('.title').text()).toBe('Лимит FPS')
    const select = group.find('select')
    expect((select.element as HTMLSelectElement).value).toBe('auto')
    const status = (exe: string) => w.find(`[data-game="${exe}"] [data-game-status]`).text()
    expect(w.find('[data-game="eldenring.exe"] .title').text()).toBe('eldenring')
    expect(status('eldenring.exe')).toBe('120 FPS')
    expect(status('factorio.exe')).toBe('Свой лимит в настройках NVIDIA')

    await select.setValue('60')
    await settle()
    expect((await api.getDesk('anna')).gameFpsLimit).toBe(60)
    expect(status('eldenring.exe')).toBe('60 FPS')

    await w.find('[data-game="eldenring.exe"] input[type="checkbox"]').setValue(false)
    await settle()
    expect(status('eldenring.exe')).toBe('Без лимита')
    expect((await api.listGames()).find((g) => g.exe === 'eldenring.exe')?.excluded).toBe(true)

    // Back to Auto: sent as null, not left out (left out keeps the value).
    await w.find('[data-games] select').setValue('auto')
    await settle()
    expect((await api.getDesk('anna')).gameFpsLimit ?? null).toBeNull()
    w.unmount()
  })

  it('shows that no games were found in a desk', async () => {
    const backend = new MockBackend({ stepMs: 10 })
    vi.stubGlobal('fetch', mockFetch(backend))
    vi.stubGlobal('WebSocket', undefined)
    const router = createAppRouter()
    const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
    await router.push('/desks/guest')
    await settle()
    expect(w.find('[data-games-none]').text()).toBe('Игры появятся здесь, когда запустятся в столе')
    w.unmount()
  })

  it('reads games, dropping malformed values', async () => {
    vi.stubGlobal('fetch', () =>
      Promise.resolve(
        new Response(
          JSON.stringify([
            { exe: 'a.exe', path: 3, desks: ['x', 1], limit: -5, status: 'weird', excluded: 'yes' },
            { path: 'no exe' },
            'junk',
          ]),
          { status: 200 },
        ),
      ),
    )
    expect(await api.listGames()).toEqual([
      { exe: 'a.exe', path: null, desks: ['x'], limit: null, status: 'pending', excluded: false, firstSeen: '', lastSeen: '' },
    ])
  })
})
