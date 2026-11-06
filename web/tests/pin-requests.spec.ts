// SPDX-License-Identifier: MIT
import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ApiClient } from '../src/api/client'
import type { Desk } from '../src/api/types'
import PinRequests from '../src/components/PinRequests.vue'
import { toasts } from '../src/toast'
import { MockBackend, mockFetch } from '../mock/backend'

function desk(id: string, name: string, pinRequest: Desk['pinRequest']): Desk {
  return {
    id,
    name,
    user: id,
    basePort: 48089,
    autostart: false,
    gpu: 'auto',
    profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 50000, codec: 'hevc' },
    state: 'Ready',
    sessionId: 2,
    metrics: { fps: 0, bitrateKbps: 0, encodeMsP99: 0, clients: 0 },
    pinRequest,
    lastError: null,
  }
}

const WAITING = { address: '192.168.1.37', since: '2026-11-06T21:00:00Z' }

describe('PinRequests', () => {
  function mountRows(desks: Desk[]) {
    const backend = new MockBackend()
    const client = new ApiClient({ fetch: mockFetch(backend) })
    const pair = vi.spyOn(client, 'pair')
    const w = mount(PinRequests, { props: { desks, client }, attachTo: document.body })
    return { w, pair, backend }
  }

  it('shows nothing while no Moonlight client waits', () => {
    const { w } = mountRows([desk('anna', 'Аня', null), desk('guest', 'Гостевой', undefined)])
    expect(w.find('form').exists()).toBe(false)
    w.unmount()
  })

  it('shows one row per waiting desk with its name and the client address, and focuses the PIN', async () => {
    const { w } = mountRows([desk('anna', 'Аня', WAITING), desk('guest', 'Гостевой', null), desk('misha', 'Миша', WAITING)])
    await flushPromises()
    const rows = w.findAll('form[data-pin-request]')
    expect(rows.map((r) => r.attributes('data-pin-request'))).toEqual(['anna', 'misha'])
    expect(rows[0]!.text()).toContain('Moonlight ждёт PIN · Аня')
    expect(rows[0]!.text()).toContain('192.168.1.37')
    expect(document.activeElement?.id).toBe('pin-anna')
    w.unmount()
  })

  it('refuses a short PIN and sends a valid one to the right desk', async () => {
    const { w, pair, backend } = mountRows([desk('anna', 'Аня', WAITING)])
    backend.requestPin('anna')
    const row = w.find('form[data-pin-request="anna"]')
    await row.find('input[name="pin"]').setValue('12')
    await row.trigger('submit')
    expect(pair).not.toHaveBeenCalled()
    expect(row.text()).toContain('PIN — это 4 цифры')

    await row.find('input[name="pin"]').setValue('4321')
    await row.find('input[name="pairName"]').setValue('Steam Deck')
    await row.trigger('submit')
    await flushPromises()
    expect(pair).toHaveBeenCalledWith('anna', { pin: '4321', name: 'Steam Deck' })
    expect(toasts.map((x) => x.text)).toContain('Устройство подключено: Steam Deck')
    expect((row.find('input[name="pin"]').element as HTMLInputElement).value).toBe('')
    w.unmount()
  })

  it('shows the service error in the row', async () => {
    const { w } = mountRows([desk('anna', 'Аня', WAITING)])
    const row = w.find('form[data-pin-request="anna"]')
    await row.find('input[name="pin"]').setValue('0000')
    await row.trigger('submit')
    await flushPromises()
    expect(row.find('[role="alert"]').text()).toContain('No Moonlight client is waiting')
    w.unmount()
  })
})

describe('mock backend PIN requests', () => {
  it('asks for a PIN when a desk without a paired device is up, and drops it once paired', async () => {
    let now = 0
    const backend = new MockBackend({ now: () => now, stepMs: 10 })
    const client = new ApiClient({ fetch: mockFetch(backend) })
    await client.startDesk('guest')
    now += 1000
    const ready = (await client.listDesks()).find((d) => d.id === 'guest')!
    expect(ready.state).toBe('Ready')
    expect(ready.pinRequest).toMatchObject({ address: expect.any(String) })

    await client.pair('guest', { pin: '1234', name: 'Laptop' })
    expect((await client.listDesks()).find((d) => d.id === 'guest')!.pinRequest).toBeNull()
  })
})
