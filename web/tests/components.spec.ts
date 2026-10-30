// SPDX-License-Identifier: MIT
import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ApiClient } from '../src/api/client'
import type { Desk, DeskConfig } from '../src/api/types'
import DeskSettings from '../src/components/DeskSettings.vue'
import PairDialog from '../src/components/PairDialog.vue'
import StateBadge from '../src/components/StateBadge.vue'
import { MockBackend, mockFetch } from '../mock/backend'

describe('StateBadge', () => {
  it('shows the Russian state word and a tone class', () => {
    const w = mount(StateBadge, { props: { state: 'Streaming' } })
    expect(w.text()).toBe('Трансляция')
    expect(w.classes()).toContain('ok')
    expect(mount(StateBadge, { props: { state: 'Faulted' } }).classes()).toContain('bad')
    expect(mount(StateBadge, { props: { state: 'Connecting' } }).classes()).toContain('busy')
    expect(mount(StateBadge, { props: { state: 'Stopped' } }).classes()).toContain('idle')
  })
})

describe('PairDialog', () => {
  function mountDialog() {
    const backend = new MockBackend()
    const client = new ApiClient({ fetch: mockFetch(backend) })
    const pair = vi.spyOn(client, 'pair')
    const w = mount(PairDialog, { props: { deskId: 'anna', open: false, client, address: '192.168.1.20:48089' } })
    return { w, client, pair }
  }

  it('renders nothing while closed and shows the Moonlight address when open', async () => {
    const { w } = mountDialog()
    expect(w.find('input[name="pin"]').exists()).toBe(false)
    await w.setProps({ open: true })
    expect(w.text()).toContain('192.168.1.20:48089')
  })

  it('keeps only digits in the PIN and refuses short PINs', async () => {
    const { w, pair } = mountDialog()
    await w.setProps({ open: true })
    const pin = w.find('input[name="pin"]')
    await pin.setValue('1a2b')
    expect((pin.element as HTMLInputElement).value).toBe('12')
    await w.find('form').trigger('submit')
    await flushPromises()
    expect(pair).not.toHaveBeenCalled()
    expect(w.text()).toContain('PIN — это 4 цифры')
  })

  it('sends PIN and name, then emits paired and close', async () => {
    const { w, pair } = mountDialog()
    await w.setProps({ open: true })
    await w.find('input[name="pin"]').setValue('4321')
    await w.find('input[name="name"]').setValue('Steam Deck')
    await w.find('form').trigger('submit')
    await flushPromises()
    expect(pair).toHaveBeenCalledWith('anna', { pin: '4321', name: 'Steam Deck' })
    expect(w.emitted('paired')?.[0]).toEqual(['Steam Deck'])
    expect(w.emitted('close')).toBeTruthy()
  })

  it('shows the service error inline', async () => {
    const { w } = mountDialog()
    await w.setProps({ open: true })
    await w.find('input[name="pin"]').setValue('0000')
    await w.find('form').trigger('submit')
    await flushPromises()
    expect(w.find('[role="alert"]').text()).toContain('No Moonlight client is waiting')
    expect(w.emitted('paired')).toBeUndefined()
  })
})

describe('DeskSettings', () => {
  const desk: Desk = {
    id: 'anna',
    name: 'Аня',
    user: 'anna',
    basePort: 48089,
    autostart: false,
    gpu: 'auto',
    profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 50000, codec: 'hevc', dpbFrames: 16 },
    workerSettings: { encoder: 'nvenc' },
    state: 'Stopped',
    sessionId: null,
    metrics: { fps: 0, bitrateKbps: 0, encodeMsP99: 0, clients: 0 },
    lastError: null,
  }

  it('saves edits on top of the live desk and keeps fields it does not show', async () => {
    const w = mount(DeskSettings, { props: { desk, gpus: [{ luid: '0x1', name: 'RTX' }] } })
    await w.find('input[name="name"]').setValue('Аня 2')
    await w.find('input[name="bitrate"]').setValue('80')
    await w.find('select[name="codec"]').setValue('av1')
    await w.find('select[name="gpu"]').setValue('0x1')
    await w.find('input[name="autostart"]').setValue(true)
    await w.find('textarea[name="workerSettings"]').setValue('encoder=nvenc\nqp=20')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('save')![0]![0] as DeskConfig
    expect(cfg).toEqual({
      id: 'anna',
      name: 'Аня 2',
      user: 'anna',
      basePort: 48089,
      autostart: true,
      gpu: '0x1',
      profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 80000, codec: 'av1', dpbFrames: 16 },
      workerSettings: { encoder: 'nvenc', qp: '20' },
    } satisfies DeskConfig)
  })

  it('stores the gamepad choice as the worker setting and keeps values it does not list', async () => {
    const save = async (d: Desk, pick?: string) => {
      const w = mount(DeskSettings, { props: { desk: d } })
      if (pick) await w.find('select[name="gamepad"]').setValue(pick)
      expect((w.find('textarea[name="workerSettings"]').element as HTMLTextAreaElement).value).not.toMatch(/gamepad/i)
      await w.find('form').trigger('submit')
      return (w.emitted('save')![0]![0] as DeskConfig).workerSettings
    }
    expect(await save(desk)).toEqual({ encoder: 'nvenc' })
    expect(await save(desk, 'x360')).toEqual({ encoder: 'nvenc', gamepad: 'x360' })
    expect(await save({ ...desk, workerSettings: { encoder: 'nvenc', Gamepad: 'X360' } }, 'ds4')).toEqual({ encoder: 'nvenc' })
    expect(await save({ ...desk, workerSettings: { gamepad: 'auto' } })).toEqual({ gamepad: 'auto' })
  })

  it('locks everything but the stream profile while the desk runs', async () => {
    const w = mount(DeskSettings, { props: { desk: { ...desk, state: 'Ready' }, running: true } })
    expect(w.find('input[name="name"]').attributes('disabled')).toBeDefined()
    expect(w.find('input[name="user"]').attributes('disabled')).toBeDefined()
    expect(w.find('input[name="bitrate"]').attributes('disabled')).toBeUndefined()
    expect(w.text()).toContain('Остальное меняется на остановленном столе')
    await w.find('input[name="bitrate"]').setValue('20')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('save')![0]![0] as DeskConfig
    expect(cfg.name).toBe('Аня')
    expect(cfg.profile.bitrateKbps).toBe(20000)
  })

  it('shows errors and does not save an invalid config', async () => {
    const w = mount(DeskSettings, { props: { desk } })
    await w.find('input[name="bitrate"]').setValue('0')
    await w.find('input[name="user"]').setValue('misha')
    await w.find('form').trigger('submit')
    expect(w.emitted('save')).toBeUndefined()
    expect(w.text()).toContain('От 0,5 до 1000 Мбит/с')
    expect(w.text()).toContain('Введите пароль')
  })
})
