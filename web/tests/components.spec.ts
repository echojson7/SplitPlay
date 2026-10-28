// SPDX-License-Identifier: MIT
import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ApiClient } from '../src/api/client'
import type { DeskConfig } from '../src/api/types'
import DeskForm from '../src/components/DeskForm.vue'
import PairDialog from '../src/components/PairDialog.vue'
import StateBadge from '../src/components/StateBadge.vue'
import { MockBackend, mockFetch } from '../mock/backend'

describe('StateBadge', () => {
  it('shows the Russian state label and a tone class', () => {
    const w = mount(StateBadge, { props: { state: 'Streaming' } })
    expect(w.text()).toBe('Трансляция')
    expect(w.classes()).toContain('ok')
    const f = mount(StateBadge, { props: { state: 'Faulted' } })
    expect(f.classes()).toContain('bad')
  })
})

describe('PairDialog', () => {
  function mountDialog() {
    const backend = new MockBackend()
    const client = new ApiClient({ fetch: mockFetch(backend) })
    const pair = vi.spyOn(client, 'pair')
    const w = mount(PairDialog, { props: { deskId: 'desk-1', open: false, client }, attachTo: document.body })
    return { w, client, pair }
  }

  it('keeps only digits in the PIN and refuses short PINs', async () => {
    const { w, pair } = mountDialog()
    await w.setProps({ open: true })
    const pin = w.find('input[name="pin"]')
    await pin.setValue('1a2b')
    expect((pin.element as HTMLInputElement).value).toBe('12')
    await w.find('form').trigger('submit')
    await flushPromises()
    expect(pair).not.toHaveBeenCalled()
    expect(w.text()).toContain('PIN состоит из 4 цифр')
    w.unmount()
  })

  it('sends PIN and name, then emits paired and close', async () => {
    const { w, pair } = mountDialog()
    await w.setProps({ open: true })
    await w.find('input[name="pin"]').setValue('4321')
    await w.find('input[name="name"]').setValue('Steam Deck')
    await w.find('form').trigger('submit')
    await flushPromises()
    expect(pair).toHaveBeenCalledWith('desk-1', { pin: '4321', name: 'Steam Deck' })
    expect(w.emitted('paired')?.[0]).toEqual(['Steam Deck'])
    expect(w.emitted('close')).toBeTruthy()
    w.unmount()
  })

  it('shows the service error', async () => {
    const { w } = mountDialog()
    await w.setProps({ open: true })
    await w.find('input[name="pin"]').setValue('0000')
    await w.find('form').trigger('submit')
    await flushPromises()
    expect(w.find('[role="alert"]').text()).toContain('No Moonlight client is waiting')
    expect(w.emitted('paired')).toBeUndefined()
    w.unmount()
  })
})

describe('DeskForm', () => {
  it('emits a valid config with bitrate converted to kbit/s', async () => {
    const w = mount(DeskForm, {
      props: { gpus: [{ luid: '0x1', name: 'RTX' }], existingIds: ['desk-1'] },
    })
    await w.find('input[name="id"]').setValue('desk-2')
    await w.find('input[name="name"]').setValue('Second')
    await w.find('input[name="user"]').setValue('splitplay-desk2')
    await w.find('select[name="gpu"]').setValue('0x1')
    await w.find('input[name="fps"]').setValue('500')
    await w.find('input[name="bitrate"]').setValue('150')
    await w.find('select[name="codec"]').setValue('av1')
    await w.find('select[name="resolution"]').setValue('2560x1440')
    await w.find('form').trigger('submit')
    const emitted = w.emitted('submit')
    expect(emitted).toHaveLength(1)
    expect(emitted![0]![0]).toEqual({
      id: 'desk-2',
      name: 'Second',
      user: 'splitplay-desk2',
      autostart: false,
      gpu: '0x1',
      profile: { fps: 500, width: 2560, height: 1440, bitrateKbps: 150000, codec: 'av1' },
    } satisfies DeskConfig)
  })

  it('shows errors and does not submit an invalid config', async () => {
    const w = mount(DeskForm, { props: { existingIds: ['desk-1'] } })
    await w.find('input[name="id"]').setValue('desk-1')
    await w.find('input[name="fps"]').setValue('600')
    await w.find('form').trigger('submit')
    expect(w.emitted('submit')).toBeUndefined()
    expect(w.text()).toContain('Такой идентификатор уже есть')
    expect(w.text()).toContain('FPS должен быть от 10 до 500')
  })

  it('locks everything but the profile while the desk runs', () => {
    const initial: DeskConfig = {
      id: 'desk-1',
      name: 'A',
      user: 'u1',
      basePort: 48089,
      autostart: true,
      gpu: 'auto',
      profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 60000, codec: 'hevc', dpbFrames: 16 },
    }
    const w = mount(DeskForm, { props: { initial, running: true } })
    expect(w.find('input[name="id"]').attributes('disabled')).toBeDefined()
    expect(w.find('input[name="user"]').attributes('disabled')).toBeDefined()
    expect(w.find('input[name="fps"]').attributes('disabled')).toBeUndefined()
  })

  const users = [
    { name: 'owner', fullName: 'PC Owner', isAdmin: true, isConsoleUser: true },
    { name: 'gamer', fullName: 'Second Player', isAdmin: false, isConsoleUser: false },
  ]

  it('picks an existing Windows user and requires its password', async () => {
    const w = mount(DeskForm, { props: { users } })
    await w.find('input[name="id"]').setValue('desk-2')
    await w.find('input[name="name"]').setValue('Second')
    // The console user cannot be picked.
    expect(w.find('option[value="owner"]').attributes('disabled')).toBeDefined()
    await w.find('select[name="userPick"]').setValue('gamer')
    expect(w.find('input[name="user"]').exists()).toBe(false)
    await w.find('form').trigger('submit')
    expect(w.emitted('submit')).toBeUndefined()
    expect(w.text()).toContain('Введите пароль Windows этой учётной записи')

    await w.find('input[name="password"]').setValue('gamer-pass')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('submit')![0]![0] as DeskConfig
    expect(cfg.user).toBe('gamer')
    expect(cfg.password).toBe('gamer-pass')
  })

  it('creates a new user with an optional password', async () => {
    const w = mount(DeskForm, { props: { users } })
    await w.find('input[name="id"]').setValue('desk-3')
    await w.find('input[name="name"]').setValue('Third')
    await w.find('select[name="userPick"]').setValue('\u0000new')
    await w.find('input[name="user"]').setValue('player3')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('submit')![0]![0] as DeskConfig
    expect(cfg.user).toBe('player3')
    expect(cfg).not.toHaveProperty('password')
  })

  it('keeps the stored password when editing without typing one', async () => {
    const initial: DeskConfig = {
      id: 'desk-1',
      name: 'A',
      user: 'gamer',
      basePort: 48089,
      autostart: false,
      gpu: 'auto',
      profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 60000, codec: 'hevc' },
      workerSettings: { encoder: 'nvenc' },
    }
    const w = mount(DeskForm, { props: { initial, users, hasPassword: true, running: true } })
    expect((w.find('select[name="userPick"]').element as HTMLSelectElement).value).toBe('gamer')
    await w.find('select[name="resolution"]').setValue('3840x2160')
    expect(w.text()).toContain('иначе после перезапуска')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('submit')![0]![0] as DeskConfig
    expect(cfg).not.toHaveProperty('password')
    expect(cfg.profile).toMatchObject({ width: 3840, height: 2160 })
    expect(cfg.workerSettings).toEqual({ encoder: 'nvenc' })
  })

  it('keeps profile fields the form does not edit', async () => {
    const initial: DeskConfig = {
      id: 'desk-1',
      name: 'A',
      user: 'u1',
      basePort: 48089,
      autostart: true,
      gpu: 'auto',
      profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 60000, codec: 'hevc', dpbFrames: 16, pacingMbps: 800 },
    }
    const w = mount(DeskForm, { props: { initial } })
    await w.find('input[name="fps"]').setValue('240')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('submit')![0]![0] as DeskConfig
    expect(cfg.profile).toMatchObject({ fps: 240, dpbFrames: 16, pacingMbps: 800 })
    expect(cfg.basePort).toBe(48089)
  })
})
