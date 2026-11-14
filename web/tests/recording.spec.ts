// SPDX-License-Identifier: MIT
// «Запись потока» in Diagnostics → Metrics: start, stop, copy the short report; the loss line's IDR requests.
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
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

async function metrics() {
  vi.stubGlobal('fetch', mockFetch(new MockBackend({ stepMs: 10 })))
  vi.stubGlobal('WebSocket', undefined)
  const router = createAppRouter()
  const w = mount(App, { global: { plugins: [router] }, attachTo: document.body })
  await router.push('/diagnostics/metrics')
  await settle()
  return w
}

describe('stream recording', () => {
  afterEach(() => {
    store.stopLive()
    vi.unstubAllGlobals()
    locale.value = 'ru'
  })

  it('records 30 minutes, stops and copies the report', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { ...globalThis.navigator, clipboard: { writeText } })
    const w = await metrics()
    const row = () => w.find('[data-recording]')
    expect(row().find('.title').text()).toBe('Запись потока')
    expect(row().text()).toContain('Записать 30 мин')
    expect(row().text()).not.toContain('Копировать')

    await row().findAll('button')[0]!.trigger('click')
    await settle()
    expect(row().find('.desc').text()).toContain('осталось 30 мин')
    expect(row().text()).toContain('Остановить')

    await row().findAll('button')[0]!.trigger('click')
    await settle()
    expect(row().find('.desc').text()).toContain('Готово')

    const copy = row().findAll('button').find((b) => b.text() === 'Копировать')!
    await copy.trigger('click')
    await settle()
    expect(writeText).toHaveBeenCalledOnce()
    expect(writeText.mock.calls[0]![0]).toContain('desk Gaming')
    w.unmount()
  })
})
