// SPDX-License-Identifier: MIT
// The "Services" flyout and its API calls, against the mock backend.
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiClient, ApiError } from '../src/api/client'
import ServicesFlyout from '../src/components/ServicesFlyout.vue'
import { MockBackend, mockFetch } from '../mock/backend'

describe('services API (mock)', () => {
  it('lists the related services and starts and stops them', async () => {
    let now = 0
    const client = new ApiClient({ fetch: mockFetch(new MockBackend({ now: () => now, stepMs: 100 })) })
    const list = await client.listServices()
    expect(list.map((s) => s.name)).toEqual(['SplitPlay', 'TermService', 'UmRdpService', 'ViGEmBus', 'Audiosrv', 'Steam Client Service'])
    expect(list.find((s) => s.name === 'ViGEmBus')).toMatchObject({ status: 'missing', canStart: false, canStop: false })

    // Names with spaces are URL-encoded.
    expect(await client.startService('Steam Client Service')).toMatchObject({ status: 'starting' })
    now += 1000
    expect((await client.listServices()).find((s) => s.name === 'Steam Client Service')?.status).toBe('running')

    // Stopping TermService stops its dependent UmRdpService too.
    expect(await client.stopService('TermService')).toMatchObject({ status: 'stopping', canStop: false })
    expect((await client.listServices()).find((s) => s.name === 'UmRdpService')?.status).toBe('stopping')
  })

  it('refuses names outside the list and services that cannot change', async () => {
    const client = new ApiClient({ fetch: mockFetch(new MockBackend()) })
    await expect(client.stopService('Spooler')).rejects.toMatchObject({ status: 404, code: 'not_found' })
    await expect(client.startService('ViGEmBus')).rejects.toMatchObject({ status: 409, code: 'invalid_state' })
    await expect(client.startService('Audiosrv')).rejects.toBeInstanceOf(ApiError)
  })
})

describe('ServicesFlyout', () => {
  let backend: MockBackend
  let now = 0

  beforeEach(() => {
    now = 0
    backend = new MockBackend({ now: () => now, stepMs: 100 })
    vi.stubGlobal('fetch', mockFetch(backend))
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.useRealTimers()
    document.body.innerHTML = ''
  })

  async function openFlyout() {
    const w = mount(ServicesFlyout, { attachTo: document.body })
    expect(w.find('[data-services-flyout]').exists()).toBe(false)
    await w.find('[data-services-button]').trigger('click')
    await flushPromises()
    return w
  }

  it('shows each service with its label, status and one button', async () => {
    const w = await openFlyout()
    expect(w.find('[data-services-button]').attributes('aria-expanded')).toBe('true')
    const rows = w.findAll('[data-service]')
    expect(rows.map((r) => r.attributes('data-service'))).toEqual([
      'SplitPlay',
      'TermService',
      'UmRdpService',
      'ViGEmBus',
      'Audiosrv',
      'Steam Client Service',
    ])
    const row = (name: string) => w.find(`[data-service="${name}"]`)
    expect(row('TermService').text()).toBe('Удалённый рабочий столРаботаетОстановить')
    expect(row('TermService').find('.status').classes()).toContain('ok')
    expect(row('ViGEmBus').text()).toBe('Драйвер геймпадов ViGEmBusНе установлена')
    expect(row('ViGEmBus').find('button').exists()).toBe(false)
    expect(row('Steam Client Service').find('[data-action="start"]').text()).toBe('Запустить')
    expect(row('UmRdpService').find('.name').attributes('title')).toContain('Перенаправитель')
    w.unmount()
  })

  it('stops a service without asking when nothing depends on it', async () => {
    const w = await openFlyout()
    await w.find('[data-service="UmRdpService"] [data-action="stop"]').trigger('click')
    await flushPromises()
    expect(document.querySelector('[role="dialog"][aria-modal="true"]')).toBeNull()
    expect(w.find('[data-service="UmRdpService"]').text()).toContain('Останавливается')
    expect(w.find('[data-service="UmRdpService"] button').exists()).toBe(false)
    w.unmount()
  })

  it.each([
    ['TermService', 'Остановить удалённый рабочий стол?', 'Сеансы всех столов завершатся.'],
    ['SplitPlay', 'Остановить SplitPlay?', 'Все столы остановятся, окно потеряет связь со службой.'],
    ['Audiosrv', 'Остановить звук Windows?', 'Звук пропадёт на всех столах и на этом ПК.'],
  ])('asks before stopping %s', async (name, title, text) => {
    const stop = vi.spyOn(ApiClient.prototype, 'stopService')
    const w = await openFlyout()
    await w.find(`[data-service="${name}"] [data-action="stop"]`).trigger('click')
    await flushPromises()
    const dialog = document.querySelector<HTMLElement>('[role="dialog"][aria-modal="true"]')
    expect(dialog?.getAttribute('aria-label')).toBe(title)
    expect(dialog?.textContent).toContain(text)
    expect(stop).not.toHaveBeenCalled()

    // Clicking inside the dialog does not close the flyout.
    dialog!.querySelector<HTMLButtonElement>('[data-confirm]')!.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    dialog!.querySelector<HTMLButtonElement>('[data-confirm]')!.click()
    await flushPromises()
    expect(stop).toHaveBeenCalledWith(name)
    expect(w.find('[data-services-flyout]').exists()).toBe(true)
    expect(w.find(`[data-service="${name}"]`).text()).toContain('Останавливается')
    stop.mockRestore()
    w.unmount()
  })

  it('refreshes while open and stops polling when closed', async () => {
    vi.useFakeTimers()
    const list = vi.spyOn(ApiClient.prototype, 'listServices')
    const w = mount(ServicesFlyout, { attachTo: document.body })
    await w.find('[data-services-button]').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenCalledTimes(1)

    await w.find('[data-service="Steam Client Service"] [data-action="start"]').trigger('click')
    await flushPromises()
    expect(w.find('[data-service="Steam Client Service"]').text()).toContain('Запускается')
    now += 1000
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(list).toHaveBeenCalledTimes(2)
    expect(w.find('[data-service="Steam Client Service"]').text()).toContain('Работает')

    // A click outside closes it; no more requests.
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    await flushPromises()
    expect(w.find('[data-services-flyout]').exists()).toBe(false)
    await vi.advanceTimersByTimeAsync(10000)
    expect(list).toHaveBeenCalledTimes(2)
    list.mockRestore()
    w.unmount()
  })

  it('says when the service does not answer', async () => {
    vi.stubGlobal('fetch', () => Promise.reject(new TypeError('Failed to fetch')))
    const w = await openFlyout()
    expect(w.find('[data-services-flyout] [role="alert"]').text()).toBe('Служба SplitPlay не отвечает')
    w.unmount()
  })
})
