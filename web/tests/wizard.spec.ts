// SPDX-License-Identifier: MIT
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { DeskConfig, HostUser } from '../src/api/types'
import AddDeskWizard from '../src/components/AddDeskWizard.vue'
import { deskIdFromUser, deskProgress, suggestBitrateKbps } from '../src/desk'

const users: HostUser[] = [
  { name: 'owner', fullName: 'Владелец ПК', isAdmin: true, isConsoleUser: true },
  { name: 'anna', fullName: 'Анна', isAdmin: false, isConsoleUser: false },
  { name: 'misha', fullName: 'Михаил', isAdmin: false, isConsoleUser: false },
]
const desks = [{ id: 'anna', user: 'anna' }]

function mountWizard(props: Record<string, unknown> = {}) {
  return mount(AddDeskWizard, { props: { open: true, users, desks, ...props } })
}

describe('desk helpers', () => {
  it('derives a valid, unique desk id from the user name', () => {
    expect(deskIdFromUser('Misha')).toBe('misha')
    expect(deskIdFromUser('Анна')).toBe('anna')
    expect(deskIdFromUser('anna', ['anna'])).toBe('anna-2')
    expect(deskIdFromUser('Player One!')).toBe('player-one')
    expect(deskIdFromUser('...')).toBe('desk')
    expect(deskIdFromUser('a'.repeat(40))).toMatch(/^[a-z0-9-]{1,32}$/)
  })

  it('suggests a bitrate that grows with pixels per second', () => {
    expect(suggestBitrateKbps(1920, 1080, 60)).toBe(35000)
    expect(suggestBitrateKbps(1280, 720, 60)).toBeLessThan(suggestBitrateKbps(3840, 2160, 120))
    expect(suggestBitrateKbps(7680, 4320, 240)).toBe(150000)
  })

  it('maps start states to progress', () => {
    expect(deskProgress('Provisioning')?.value).toBeCloseTo(0.2)
    expect(deskProgress('DisplayReady')?.value).toBeCloseTo(0.8)
    expect(deskProgress('Ready')).toBeNull()
    expect(deskProgress('Stopped')).toBeNull()
  })
})

describe('AddDeskWizard', () => {
  it('disables the console user and users that already have a desk', () => {
    const w = mountWizard()
    expect(w.find('[data-user="owner"]').attributes('disabled')).toBeDefined()
    expect(w.find('[data-user="anna"]').attributes('disabled')).toBeDefined()
    expect(w.find('[data-user="misha"]').attributes('disabled')).toBeUndefined()
  })

  it('does not leave step 1 without a user', async () => {
    const w = mountWizard()
    await w.find('form').trigger('submit')
    expect(w.find('[data-step="user"]').exists()).toBe(true)
    expect(w.text()).toContain('Выберите пользователя')
  })

  it('walks an existing user through password and display to a config', async () => {
    const w = mountWizard()
    await w.find('[data-user="misha"]').trigger('click')
    await w.find('form').trigger('submit')
    expect(w.find('[data-step="password"]').exists()).toBe(true)
    expect(w.text()).toContain('Пароль учётной записи Windows')

    // An existing account needs its password.
    await w.find('form').trigger('submit')
    expect(w.find('[data-step="password"]').exists()).toBe(true)
    expect(w.text()).toContain('Введите пароль')
    await w.find('input[name="password"]').setValue('secret')
    await w.find('form').trigger('submit')
    expect(w.find('[data-step="display"]').exists()).toBe(true)

    await w.find('[data-res="2560x1440"]').trigger('click')
    await w.find('[data-fps="144"]').trigger('click')
    expect(w.find('[data-create]').text()).toContain('Создать и запустить')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('create')![0]![0] as DeskConfig
    expect(cfg).toEqual({
      id: 'misha',
      name: 'Михаил',
      user: 'misha',
      password: 'secret',
      autostart: false,
      gpu: 'auto',
      profile: { fps: 144, width: 2560, height: 1440, bitrateKbps: suggestBitrateKbps(2560, 1440, 144), codec: 'hevc' },
    } satisfies DeskConfig)
  })

  it('creates a new account with an optional password and a custom resolution', async () => {
    const w = mountWizard()
    await w.find('[data-user-new]').trigger('click')
    await w.find('input[name="newUser"]').setValue('bad/name')
    await w.find('form').trigger('submit')
    expect(w.text()).toContain('Некорректное имя пользователя')
    await w.find('input[name="newUser"]').setValue('Petya')
    await w.find('form').trigger('submit')
    expect(w.text()).toContain('Пусто — пароль задастся автоматически')
    await w.find('form').trigger('submit')

    await w.find('[data-res="custom"]').trigger('click')
    await w.find('input[name="width"]').setValue('100')
    await w.find('form').trigger('submit')
    expect(w.emitted('create')).toBeUndefined()
    expect(w.text()).toContain('От 320×240 до 7680×4320')
    await w.find('input[name="width"]').setValue('2560')
    await w.find('input[name="height"]').setValue('1080')
    await w.find('form').trigger('submit')
    const cfg = w.emitted('create')![0]![0] as DeskConfig
    expect(cfg).toMatchObject({ id: 'petya', name: 'Petya', user: 'Petya', profile: { width: 2560, height: 1080, fps: 60 } })
    expect(cfg).not.toHaveProperty('password')
  })

  it('goes back a step and shows the service error', async () => {
    const w = mountWizard({ error: 'User misha is already used by another desk' })
    expect(w.find('[role="alert"]').text()).toContain('already used')
    await w.find('[data-user="misha"]').trigger('click')
    await w.find('form').trigger('submit')
    await w.find('[data-back]').trigger('click')
    expect(w.find('[data-step="user"]').exists()).toBe(true)
    expect(w.find('[data-user="misha"]').attributes('aria-checked')).toBe('true')
  })

  it('types the user name when the user list is unknown', async () => {
    const w = mountWizard({ users: [] })
    expect(w.find('[data-user-new]').exists()).toBe(false)
    await w.find('input[name="newUser"]').setValue('player2')
    await w.find('form').trigger('submit')
    expect(w.find('[data-step="password"]').exists()).toBe(true)
  })
})
