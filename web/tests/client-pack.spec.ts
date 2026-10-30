// SPDX-License-Identifier: MIT
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiClient, deskToConfig } from '../src/api/client'
import type { Desk } from '../src/api/types'
import ConnectPack from '../src/components/ConnectPack.vue'
import { changeProfile, suggestBitrateKbps } from '../src/desk'
import { MockBackend, mockFetch } from '../mock/backend'

async function setup(id = 'anna') {
  const backend = new MockBackend()
  const client = new ApiClient({ fetch: mockFetch(backend) })
  const desk: Desk = await client.getDesk(id)
  return { backend, client, desk }
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('ApiClient client pack', () => {
  it('reads the pack and passes the address', async () => {
    const { client } = await setup()
    const pack = await client.clientPack('anna', '10.0.0.5')
    expect(pack.address).toBe('10.0.0.5:48089')
    expect(pack.settings).toMatchObject({ width: 1920, height: 1080, fps: 120, bitrateKbps: 50000, vsync: false, framePacing: false })
    expect(pack.arguments.slice(0, 3)).toEqual(['stream', '10.0.0.5:48089', 'Desktop'])
    expect(pack.arguments).toContain('--no-vsync')
  })

  it('downloads a script as a blob and reports a bad address', async () => {
    const { client } = await setup()
    const blob = await client.clientScript('anna', 'linux')
    expect(await blob.text()).toContain('stream 192.168.1.20:48089 Desktop')
    await expect(client.clientPack('anna', 'a;b')).rejects.toMatchObject({ status: 400, code: 'validation_failed' })
  })
})

describe('ConnectPack', () => {
  it('shows the recommended Moonlight settings in plain rows', async () => {
    const { client, desk } = await setup()
    const w = mount(ConnectPack, { props: { desk, client, address: '192.168.1.20' } })
    await flushPromises()
    const value = (key: string) => w.find(`[data-pack-value="${key}"]`).text()
    expect(value('resolution')).toBe('1920 × 1080 · 120 FPS')
    expect(value('bitrate')).toBe('50 Мбит/с')
    expect(value('codec')).toBe('HEVC')
    expect(value('vsync')).toBe('Выкл.')
    expect(value('display')).toBe('Полный экран')
    expect(value('decoder')).toBe('Аппаратный')
    expect(value('pacing')).toBe('Выкл.')
    expect(w.text()).toContain('Скрипт запуска')
    expect(w.find('[data-script="windows"]').exists()).toBe(true)
    expect(w.find('[data-script="linux"]').exists()).toBe(true)
  })

  it('reloads when the desk profile changes', async () => {
    const { client, desk } = await setup('guest')
    const spy = vi.spyOn(client, 'clientPack')
    const w = mount(ConnectPack, { props: { desk, client } })
    await flushPromises()
    expect(w.find('[data-pack-value="codec"]').text()).toBe('AV1')
    const config = deskToConfig(desk)
    config.profile = { ...config.profile, codec: 'h264', fps: 144 }
    await w.setProps({ desk: await client.updateDesk('guest', config) })
    await flushPromises()
    expect(spy).toHaveBeenCalledTimes(2)
    expect(w.find('[data-pack-value="codec"]').text()).toBe('H.264')
    expect(w.find('[data-pack-value="resolution"]').text()).toBe('2560 × 1440 · 144 FPS')
  })

  it('downloads the script under its file name', async () => {
    const { client, desk } = await setup()
    const script = vi.spyOn(client, 'clientScript')
    const created = vi.fn(() => 'blob:pack')
    Object.assign(URL, { createObjectURL: created, revokeObjectURL: vi.fn() })
    let downloaded = ''
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      downloaded = this.download
    })
    const w = mount(ConnectPack, { props: { desk, client, address: '192.168.1.20' } })
    await flushPromises()
    await w.find('[data-script="windows"]').trigger('click')
    await flushPromises()
    expect(script).toHaveBeenCalledWith('anna', 'windows', '192.168.1.20')
    expect(created).toHaveBeenCalled()
    expect(downloaded).toBe('splitplay-anna.cmd')
  })

  it('shows the error when the service has no pack', async () => {
    const { client, desk } = await setup()
    vi.spyOn(client, 'clientPack').mockRejectedValue(new Error('no endpoint'))
    const w = mount(ConnectPack, { props: { desk, client } })
    await flushPromises()
    expect(w.find('[data-pack-error]').text()).toContain('no endpoint')
    expect(w.find('[data-script="windows"]').exists()).toBe(false)
  })
})

describe('changeProfile', () => {
  it('lets a suggested bitrate follow the picture and keeps one the owner set', () => {
    const suggested = { width: 1920, height: 1080, fps: 60, bitrateKbps: suggestBitrateKbps(1920, 1080, 60) }
    expect(changeProfile(suggested, { fps: 120 })).toEqual({ width: 1920, height: 1080, fps: 120, bitrateKbps: 50000 })
    expect(changeProfile(suggested, { width: 3840, height: 2160 }).bitrateKbps).toBe(70000)
    const own = { ...suggested, bitrateKbps: 80000 }
    expect(changeProfile(own, { fps: 120 }).bitrateKbps).toBe(80000)
    expect(changeProfile(suggested, { fps: 120, bitrateKbps: 20000 }).bitrateKbps).toBe(20000)
  })
})
