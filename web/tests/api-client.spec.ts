// SPDX-License-Identifier: MIT
import { describe, expect, it, vi } from 'vitest'
import { ApiClient, ApiError, deskToConfig, parseLiveEvent } from '../src/api/client'
import type { DeskConfig } from '../src/api/types'
import { MockBackend, mockFetch } from '../mock/backend'

function setup(opts: ConstructorParameters<typeof MockBackend>[0] = {}) {
  let now = 1_000_000
  const backend = new MockBackend({ now: () => now, stepMs: 100, ...opts })
  const fetchSpy = vi.fn(mockFetch(backend))
  const client = new ApiClient({ fetch: fetchSpy })
  return { backend, client, fetchSpy, advance: (ms: number) => (now += ms) }
}

const NEW_DESK: DeskConfig = {
  id: 'desk-9',
  name: 'Test',
  user: 'splitplay-test',
  autostart: false,
  gpu: 'auto',
  profile: { fps: 240, width: 2560, height: 1440, bitrateKbps: 80000, codec: 'av1' },
}

describe('ApiClient', () => {
  it('reads health and builds URLs under /api/v1', async () => {
    const { client, fetchSpy } = setup()
    const h = await client.health()
    expect(h.status).toBe('ok')
    expect(fetchSpy.mock.calls[0]![0]).toBe('/api/v1/health')
  })

  it('reports setupRequired when no password is set', async () => {
    const { client } = setup()
    expect(await client.session()).toEqual({ setupRequired: true, authenticated: false, csrfToken: undefined })
  })

  it('lists desks with live state', async () => {
    const { client } = setup()
    const desks = await client.listDesks()
    expect(desks.map((d) => d.id)).toEqual(['anna', 'guest'])
    expect(desks[0]!.state).toBe('Streaming')
    expect(desks[1]!.state).toBe('Stopped')
  })

  it('simulates a streaming device with live numbers', async () => {
    const { client, advance } = setup()
    const anna = await client.getDesk('anna')
    expect(anna.stream).toMatchObject({ client: 'Ноутбук Ани', address: '192.168.1.37', width: 1920, height: 1080, fps: 120, codec: 'hevc' })
    expect(anna.metrics.fps).toBeGreaterThan(110)
    expect(anna.metrics.rttMs).toBeGreaterThan(0)
    expect(anna.metrics.encodeMsP50).toBeLessThan(anna.metrics.encodeMsP99)
    const m = await client.metrics()
    expect(m.desks[0]).toMatchObject({ id: 'anna', clients: 1 })
    expect(m.desks[0]!.lossPct).toBeGreaterThanOrEqual(0)

    // Stopping ends the stream; the next start brings the paired device back.
    await client.stopDesk('anna')
    expect((await client.getDesk('anna')).stream).toBeNull()
    advance(200)
    const stopped = await client.getDesk('anna')
    expect(stopped.metrics).toMatchObject({ fps: 0, rttMs: null, clients: 0 })
    await client.startDesk('anna')
    advance(1000)
    const again = await client.getDesk('anna')
    expect(again.state).toBe('Streaming')
    expect(again.stream?.client).toBe('Ноутбук Ани')
    expect((await client.getDesk('guest')).stream).toBeNull()
  })

  it('accepts list responses wrapped in an object', async () => {
    const fetchImpl = vi.fn(async () => new Response(JSON.stringify({ desks: [{ id: 'x' }] }), { status: 200 }))
    const client = new ApiClient({ fetch: fetchImpl })
    expect((await client.listDesks()).map((d) => d.id)).toEqual(['x'])
  })

  it('creates a desk, then walks it through start to Ready', async () => {
    const { client, advance } = setup()
    const created = await client.createDesk(NEW_DESK)
    expect(created.state).toBe('Stopped')
    expect(created.basePort).toBe(48289) // lowest free basePortStart + 100k
    const started = await client.startDesk('desk-9')
    expect(started.state).toBe('Provisioning')
    advance(1000)
    expect((await client.getDesk('desk-9')).state).toBe('Ready')
    const stopped = await client.stopDesk('desk-9')
    expect(stopped.state).toBe('Stopping')
    advance(200)
    expect((await client.getDesk('desk-9')).state).toBe('Stopped')
  })

  it('turns error bodies into ApiError with code and status', async () => {
    const { client } = setup()
    const err = await client.createDesk({ ...NEW_DESK, id: 'anna' }).catch((e: unknown) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(409)
    expect((err as ApiError).code).toBe('duplicate_id')
    const nf = await client.getDesk('nope').catch((e: unknown) => e)
    expect((nf as ApiError).isNotFound).toBe(true)
  })

  it('maps network failures to status 0', async () => {
    const client = new ApiClient({ fetch: () => Promise.reject(new TypeError('Failed to fetch')) })
    const err = await client.health().catch((e: unknown) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(0)
    expect((err as ApiError).code).toBe('network_error')
  })

  it('logs in, then sends the CSRF token on state-changing requests only', async () => {
    const { client, fetchSpy } = setup({ password: 'correct horse' })
    expect((await client.session()).authenticated).toBe(false)
    await expect(client.listDesks()).rejects.toMatchObject({ status: 401 })
    await expect(client.login('wrong')).rejects.toMatchObject({ status: 401, code: 'bad_password' })
    const res = await client.login('correct horse')
    expect(res.csrfToken).toMatch(/^[0-9a-f]{32}$/)
    expect(client.csrf).toBe(res.csrfToken)

    await client.listDesks()
    const getInit = fetchSpy.mock.calls.at(-1)![1]!
    expect(new Headers(getInit.headers).has('X-CSRF-Token')).toBe(false)

    await client.startDesk('guest')
    const postInit = fetchSpy.mock.calls.at(-1)![1]!
    expect(postInit.method).toBe('POST')
    expect(new Headers(postInit.headers).get('X-CSRF-Token')).toBe(res.csrfToken)

    const s = await client.session()
    expect(s).toMatchObject({ authenticated: true, setupRequired: false, csrfToken: res.csrfToken })
  })

  it('calls onUnauthorized on 401', async () => {
    const backend = new MockBackend({ password: 'secret-pass' })
    const onUnauthorized = vi.fn()
    const client = new ApiClient({ fetch: mockFetch(backend), onUnauthorized })
    await expect(client.metrics()).rejects.toBeInstanceOf(ApiError)
    expect(onUnauthorized).toHaveBeenCalledOnce()
  })

  it('first-time password setup logs the browser in', async () => {
    const { client } = setup()
    await client.setPassword('a-long-password')
    expect(await client.session()).toMatchObject({ setupRequired: false, authenticated: true })
  })

  it('pairs a client and lists, then revokes it', async () => {
    const { client } = setup()
    await expect(client.pair('anna', { pin: '12a4', name: 'x' })).rejects.toMatchObject({ code: 'invalid_pin' })
    await client.pair('anna', { pin: '1234', name: 'Laptop' })
    const clients = await client.listClients('anna')
    expect(clients.map((c) => c.name)).toEqual(['Ноутбук Ани', 'Laptop'])
    await client.revokeClient('anna', clients[1]!.id)
    expect(await client.listClients('anna')).toHaveLength(1)
  })

  it('refuses pairing on a stopped desk', async () => {
    const { client } = setup()
    await expect(client.pair('guest', { pin: '1234', name: 'x' })).rejects.toMatchObject({ status: 409 })
  })

  it('only lets the profile change while a desk runs', async () => {
    const { client } = setup()
    const desk = await client.getDesk('anna')
    const cfg = deskToConfig(desk)
    const updated = await client.updateDesk('anna', { ...cfg, profile: { ...cfg.profile, fps: 240 } })
    expect(updated.profile.fps).toBe(240)
    await expect(client.updateDesk('anna', { ...cfg, name: 'Renamed' })).rejects.toMatchObject({ code: 'desk_running' })
  })

  it('passes log filters as query parameters', async () => {
    const { client, fetchSpy } = setup()
    const lines = await client.logs({ component: 'worker', desk: 'anna', tail: 5 })
    expect(fetchSpy.mock.calls.at(-1)![0]).toBe('/api/v1/logs?component=worker&desk=anna&tail=5')
    expect(lines.length).toBeLessThanOrEqual(5)
    expect(lines.every((l) => l.component === 'worker' && l.desk === 'anna')).toBe(true)
  })

  it('normalises metrics and exposes GPUs', async () => {
    const { client } = setup()
    const m = await client.metrics()
    expect(m.host.gpus.length).toBeGreaterThan(0)
    expect(m.desks.map((d) => d.id)).toEqual(['anna', 'guest'])
    const empty = new ApiClient({ fetch: async () => new Response('{}', { status: 200 }) })
    expect(await empty.metrics()).toEqual({ host: { gpus: [] }, desks: [] })
  })

  it('deletes with ?deleteUser=true only when asked', async () => {
    const { client, fetchSpy } = setup()
    await client.deleteDesk('guest', true)
    expect(fetchSpy.mock.calls.at(-1)![0]).toBe('/api/v1/desks/guest?deleteUser=true')
    await client.createDesk(NEW_DESK)
    await client.deleteDesk('desk-9')
    expect(fetchSpy.mock.calls.at(-1)![0]).toBe('/api/v1/desks/desk-9')
  })

  it('reads the host LAN addresses and tolerates an empty answer', async () => {
    const { client, fetchSpy } = setup()
    expect(await client.hostAddresses()).toEqual(['192.168.1.20'])
    expect(fetchSpy.mock.calls.at(-1)![0]).toBe('/api/v1/host/network')
    const empty = new ApiClient({ fetch: async () => new Response('{}', { status: 200 }) })
    expect(await empty.hostAddresses()).toEqual([])
  })

  it('lists host users with the console user flagged', async () => {
    const { client } = setup()
    const users = await client.listHostUsers()
    expect(users.find((u) => u.isConsoleUser)?.name).toBe('owner')
    expect(users.map((u) => u.name)).toContain('misha')
  })

  it('downloads the config export as a blob', async () => {
    const { client } = setup()
    const blob = await client.exportConfig()
    expect(blob.size).toBe(22)
  })

  it('derives the WebSocket URL from the page origin', () => {
    const client = new ApiClient()
    expect(client.wsUrl('http://127.0.0.1:38299/#/desks')).toBe('ws://127.0.0.1:38299/api/v1/ws')
    expect(client.wsUrl('https://host.lan:38299/')).toBe('wss://host.lan:38299/api/v1/ws')
  })
})

describe('parseLiveEvent', () => {
  it('accepts desk and metrics events and ignores the rest', () => {
    expect(parseLiveEvent('{"type":"desk","desk":{"id":"a"}}')).toMatchObject({ type: 'desk' })
    expect(parseLiveEvent('{"type":"metrics","host":{"gpus":[]}}')).toMatchObject({ type: 'metrics' })
    expect(parseLiveEvent('{"type":"other"}')).toBeNull()
    expect(parseLiveEvent('not json')).toBeNull()
  })
})
