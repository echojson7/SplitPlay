// SPDX-License-Identifier: MIT
// App-wide reactive state: desks, metrics, auth session and service health.
// Live updates come from the /ws WebSocket; when it is unavailable the store polls the REST API.

import { reactive } from 'vue'
import { api, ApiClient, ApiError, parseLiveEvent } from './api/client'
import type { AuthSession, Desk, Health, MetricsSnapshot } from './api/types'

export interface StoreState {
  desks: Desk[]
  desksLoaded: boolean
  metrics: MetricsSnapshot | null
  session: AuthSession | null
  health: Health | null
  online: boolean
  live: 'off' | 'ws' | 'polling'
  /** LAN addresses of the host (GET /host/network); null until loaded, empty when unknown. */
  addresses: string[] | null
}

export const POLL_MS = 2000
const WS_OPEN_TIMEOUT_MS = 4000

export function createStore(client: ApiClient) {
  const state = reactive<StoreState>({
    desks: [],
    desksLoaded: false,
    metrics: null,
    session: null,
    health: null,
    online: true,
    live: 'off',
    addresses: null,
  })

  let ws: WebSocket | null = null
  let pollTimer: ReturnType<typeof setInterval> | null = null
  let wsRetryTimer: ReturnType<typeof setTimeout> | null = null
  let wanted = false

  function upsertDesk(desk: Desk): void {
    const i = state.desks.findIndex((d) => d.id === desk.id)
    if (i >= 0) state.desks.splice(i, 1, desk)
    else state.desks.push(desk)
  }

  function removeDesk(id: string): void {
    state.desks = state.desks.filter((d) => d.id !== id)
  }

  async function guard<T>(p: Promise<T>): Promise<T> {
    try {
      const v = await p
      state.online = true
      return v
    } catch (e) {
      if (e instanceof ApiError && e.status === 0) state.online = false
      throw e
    }
  }

  async function refreshDesks(): Promise<void> {
    state.desks = await guard(client.listDesks())
    state.desksLoaded = true
  }

  async function refreshMetrics(): Promise<void> {
    state.metrics = await guard(client.metrics())
  }

  async function refreshHealth(): Promise<void> {
    try {
      state.health = await guard(client.health())
    } catch {
      state.health = null
    }
  }

  /** Loads the host's LAN addresses once; an older service without the endpoint gives an empty list. */
  async function refreshAddresses(): Promise<void> {
    try {
      state.addresses = await guard(client.hostAddresses())
    } catch {
      state.addresses = []
    }
  }

  async function refreshSession(): Promise<AuthSession> {
    state.session = await guard(client.session())
    return state.session
  }

  function startPolling(): void {
    if (pollTimer) return
    state.live = 'polling'
    pollTimer = setInterval(() => {
      void refreshDesks().catch(() => undefined)
      void refreshMetrics().catch(() => undefined)
    }, POLL_MS)
  }

  function stopPolling(): void {
    if (pollTimer) clearInterval(pollTimer)
    pollTimer = null
  }

  function connectWs(): void {
    if (typeof WebSocket === 'undefined') {
      startPolling()
      return
    }
    let opened = false
    try {
      ws = new WebSocket(client.wsUrl())
    } catch {
      startPolling()
      return
    }
    // A socket that neither opens nor fails (a proxy holding the upgrade) must not stall updates.
    const openTimer = setTimeout(() => {
      if (!opened && wanted) startPolling()
    }, WS_OPEN_TIMEOUT_MS)
    ws.onopen = () => {
      opened = true
      clearTimeout(openTimer)
      stopPolling()
      state.live = 'ws'
    }
    ws.onmessage = (ev: MessageEvent) => {
      if (typeof ev.data !== 'string') return
      const evt = parseLiveEvent(ev.data)
      if (!evt) return
      if (evt.type === 'desk') upsertDesk(evt.desk)
      else if (evt.host || evt.desks) {
        state.metrics = { host: evt.host ?? state.metrics?.host ?? { gpus: [] }, desks: evt.desks ?? state.metrics?.desks ?? [] }
        for (const m of evt.desks ?? []) {
          const d = state.desks.find((x) => x.id === m.id)
          if (d) d.metrics = { ...d.metrics, ...m }
        }
      }
    }
    ws.onclose = () => {
      clearTimeout(openTimer)
      ws = null
      if (!wanted) return
      // Fall back to polling and retry the socket later (longer if it never opened).
      startPolling()
      wsRetryTimer = setTimeout(connectWs, opened ? 3000 : 15000)
    }
  }

  /** Starts live updates (idempotent). */
  function startLive(): void {
    if (wanted) return
    wanted = true
    void refreshDesks().catch(() => undefined)
    void refreshMetrics().catch(() => undefined)
    connectWs()
  }

  function stopLive(): void {
    wanted = false
    stopPolling()
    if (wsRetryTimer) clearTimeout(wsRetryTimer)
    wsRetryTimer = null
    ws?.close()
    ws = null
    state.live = 'off'
  }

  return {
    state,
    upsertDesk,
    removeDesk,
    refreshDesks,
    refreshMetrics,
    refreshHealth,
    refreshAddresses,
    refreshSession,
    startLive,
    stopLive,
  }
}

export type Store = ReturnType<typeof createStore>

export const store = createStore(api)
