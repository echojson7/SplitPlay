// SPDX-License-Identifier: MIT
// Typed client for Web API v1 (docs/contracts/web-api.md). The UI talks to the service only through this module.

import type {
  ApiErrorBody,
  AuthSession,
  Desk,
  DeskConfig,
  DeskProfile,
  Health,
  HostNetwork,
  HostService,
  HostSettings,
  HostUser,
  LiveEvent,
  LoginResult,
  LogLine,
  LogQuery,
  MetricsSnapshot,
  PairedClient,
  PairRequest,
} from './types'

export const DEFAULT_API_BASE = '/api/v1'

export class ApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
  }

  get isUnauthorized(): boolean {
    return this.status === 401
  }

  get isNotFound(): boolean {
    return this.status === 404
  }
}

export interface ApiClientOptions {
  /** API base URL, default `/api/v1`. May be absolute. */
  baseUrl?: string
  /** fetch implementation; injected in tests. */
  fetch?: typeof fetch
  /** Bearer token (tray, CLI). The browser UI uses the session cookie instead. */
  token?: string
  /** Called on any 401 so the app can route to the login page. */
  onUnauthorized?: () => void
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE'

interface RequestOptions {
  body?: unknown
  rawBody?: BodyInit
  contentType?: string
  query?: Record<string, string | number | boolean | undefined>
  expect?: 'json' | 'blob' | 'none'
}

function isErrorBody(value: unknown): value is ApiErrorBody {
  if (typeof value !== 'object' || value === null || !('error' in value)) return false
  const err = (value as { error: unknown }).error
  return typeof err === 'object' && err !== null && 'code' in err
}

/** Accepts either a bare array or an object wrapping it under `key` (the contract does not fix which). */
function unwrapList<T>(value: unknown, key: string): T[] {
  if (Array.isArray(value)) return value as T[]
  if (typeof value === 'object' && value !== null) {
    const inner = (value as Record<string, unknown>)[key]
    if (Array.isArray(inner)) return inner as T[]
  }
  return []
}

export class ApiClient {
  private readonly baseUrl: string
  private readonly fetchImpl: typeof fetch
  private readonly token: string | undefined
  /** Called on any 401; the app sets it to route to the login page. */
  onUnauthorized: (() => void) | undefined
  private csrfToken: string | undefined

  constructor(options: ApiClientOptions = {}) {
    this.baseUrl = (options.baseUrl ?? DEFAULT_API_BASE).replace(/\/+$/, '')
    // Bind so that a bare global fetch is not called with the client as `this`.
    this.fetchImpl = options.fetch ?? ((input, init) => globalThis.fetch(input, init))
    this.token = options.token
    this.onUnauthorized = options.onUnauthorized
  }

  /** Current CSRF token (from login or /auth/session); sent on every state-changing request. */
  get csrf(): string | undefined {
    return this.csrfToken
  }

  url(path: string, query?: RequestOptions['query']): string {
    let url = `${this.baseUrl}${path}`
    if (query) {
      const params = new URLSearchParams()
      for (const [k, v] of Object.entries(query)) {
        if (v !== undefined && v !== '') params.set(k, String(v))
      }
      const qs = params.toString()
      if (qs) url += `?${qs}`
    }
    return url
  }

  private async request<T>(method: Method, path: string, opts: RequestOptions = {}): Promise<T> {
    const headers: Record<string, string> = { Accept: 'application/json' }
    let body: BodyInit | undefined
    if (opts.rawBody !== undefined) {
      body = opts.rawBody
      if (opts.contentType) headers['Content-Type'] = opts.contentType
    } else if (opts.body !== undefined) {
      body = JSON.stringify(opts.body)
      headers['Content-Type'] = 'application/json'
    }
    if (method !== 'GET' && this.csrfToken) headers['X-CSRF-Token'] = this.csrfToken
    if (this.token) headers['Authorization'] = `Bearer ${this.token}`

    let res: Response
    try {
      res = await this.fetchImpl(this.url(path, opts.query), {
        method,
        headers,
        body,
        credentials: 'same-origin',
      })
    } catch (e) {
      throw new ApiError(0, 'network_error', e instanceof Error ? e.message : String(e))
    }

    if (!res.ok) {
      let code = `http_${res.status}`
      let message = res.statusText || `HTTP ${res.status}`
      try {
        const data: unknown = await res.json()
        if (isErrorBody(data)) {
          code = data.error.code
          message = data.error.message || message
        }
      } catch {
        // Body is not JSON; keep the HTTP status as the error.
      }
      if (res.status === 401) this.onUnauthorized?.()
      throw new ApiError(res.status, code, message)
    }

    const expect = opts.expect ?? 'json'
    if (expect === 'none' || res.status === 204) return undefined as T
    if (expect === 'blob') return (await res.blob()) as T
    const text = await res.text()
    return (text ? JSON.parse(text) : undefined) as T
  }

  // --- health and auth ---------------------------------------------------------------------

  health(): Promise<Health> {
    return this.request('GET', '/health')
  }

  async session(): Promise<AuthSession> {
    const raw = await this.request<Partial<AuthSession> | undefined>('GET', '/auth/session')
    const s: AuthSession = {
      setupRequired: raw?.setupRequired === true,
      authenticated: raw?.authenticated === true,
      csrfToken: raw?.csrfToken,
    }
    if (s.csrfToken) this.csrfToken = s.csrfToken
    return s
  }

  async login(password: string): Promise<LoginResult> {
    const res = (await this.request<LoginResult | undefined>('POST', '/auth/login', { body: { password } })) ?? {}
    if (res.csrfToken) this.csrfToken = res.csrfToken
    return res
  }

  async logout(): Promise<void> {
    await this.request('POST', '/auth/logout', { expect: 'none' })
    this.csrfToken = undefined
  }

  /** Sets the first admin password (no `currentPassword`) or changes it. */
  setPassword(newPassword: string, currentPassword?: string): Promise<void> {
    return this.request('POST', '/auth/password', {
      body: currentPassword === undefined ? { newPassword } : { currentPassword, newPassword },
      expect: 'none',
    })
  }

  // --- desks -------------------------------------------------------------------------------

  async listDesks(): Promise<Desk[]> {
    return unwrapList<Desk>(await this.request('GET', '/desks'), 'desks')
  }

  getDesk(id: string): Promise<Desk> {
    return this.request('GET', `/desks/${encodeURIComponent(id)}`)
  }

  createDesk(config: DeskConfig): Promise<Desk> {
    return this.request('POST', '/desks', { body: config })
  }

  /** Replaces the desk config. While the desk runs, the service accepts only `profile` changes. */
  updateDesk(id: string, config: DeskConfig): Promise<Desk> {
    return this.request('PUT', `/desks/${encodeURIComponent(id)}`, { body: config })
  }

  deleteDesk(id: string, deleteUser = false): Promise<void> {
    return this.request('DELETE', `/desks/${encodeURIComponent(id)}`, {
      query: { deleteUser: deleteUser ? 'true' : undefined },
      expect: 'none',
    })
  }

  startDesk(id: string): Promise<Desk> {
    return this.request('POST', `/desks/${encodeURIComponent(id)}/start`)
  }

  stopDesk(id: string): Promise<Desk> {
    return this.request('POST', `/desks/${encodeURIComponent(id)}/stop`)
  }

  restartDesk(id: string): Promise<Desk> {
    return this.request('POST', `/desks/${encodeURIComponent(id)}/restart`)
  }

  async listClients(id: string): Promise<PairedClient[]> {
    return unwrapList<PairedClient>(await this.request('GET', `/desks/${encodeURIComponent(id)}/clients`), 'clients')
  }

  pair(id: string, req: PairRequest): Promise<void> {
    return this.request('POST', `/desks/${encodeURIComponent(id)}/pair`, { body: req, expect: 'none' })
  }

  revokeClient(id: string, clientId: string): Promise<void> {
    return this.request('DELETE', `/desks/${encodeURIComponent(id)}/clients/${encodeURIComponent(clientId)}`, {
      expect: 'none',
    })
  }

  // --- host --------------------------------------------------------------------------------

  async metrics(): Promise<MetricsSnapshot> {
    const raw = await this.request<Partial<MetricsSnapshot> | undefined>('GET', '/metrics')
    return {
      host: { ...raw?.host, gpus: raw?.host?.gpus ?? [] },
      desks: unwrapList(raw?.desks ?? [], 'desks'),
    }
  }

  async logs(query: LogQuery = {}): Promise<LogLine[]> {
    const raw = await this.request('GET', '/logs', {
      query: { component: query.component, desk: query.desk, tail: query.tail },
    })
    return unwrapList<LogLine>(raw, 'lines')
  }

  /** Enabled local Windows accounts that can be picked as a desk user. */
  async listHostUsers(): Promise<HostUser[]> {
    return unwrapList<HostUser>(await this.request('GET', '/host/users'), 'users')
  }

  /** LAN IPv4 addresses of the host: a desk is added in Moonlight as `<address>:<basePort>`. */
  async hostAddresses(): Promise<string[]> {
    const raw = await this.request<Partial<HostNetwork> | undefined>('GET', '/host/network')
    return Array.isArray(raw?.addresses) ? raw.addresses.filter((a): a is string => typeof a === 'string') : []
  }

  /** The Windows services SplitPlay depends on, with their state. */
  async listServices(): Promise<HostService[]> {
    return unwrapList<HostService>(await this.request('GET', '/host/services'), 'services')
  }

  /** Starts one of the listed services; answers with its entry (usually `starting`). */
  startService(name: string): Promise<HostService> {
    return this.request('POST', `/host/services/${encodeURIComponent(name)}/start`)
  }

  /** Stops one of the listed services. Stopping `SplitPlay` takes this UI down; `TermService` ends every desk session. */
  stopService(name: string): Promise<HostService> {
    return this.request('POST', `/host/services/${encodeURIComponent(name)}/stop`)
  }

  /** Host settings: the `host` object of config.json. */
  getHostSettings(): Promise<HostSettings> {
    return this.request('GET', '/host')
  }

  /** Replaces the host settings; a `webPort` change applies after a service restart. */
  updateHostSettings(settings: HostSettings): Promise<HostSettings> {
    return this.request('PUT', '/host', { body: settings })
  }

  exportConfig(includeSecrets = false, password?: string): Promise<Blob> {
    return this.request('GET', '/config/export', {
      query: includeSecrets ? { includeSecrets: 'true', password } : undefined,
      expect: 'blob',
    })
  }

  importConfig(zip: Blob): Promise<void> {
    return this.request('POST', '/config/import', { rawBody: zip, contentType: 'application/zip', expect: 'none' })
  }

  // --- live updates ------------------------------------------------------------------------

  /** WebSocket URL for `/ws`, resolved against `origin` (defaults to the page location). */
  wsUrl(origin: string = globalThis.location?.href ?? 'http://127.0.0.1:38299/'): string {
    const u = new URL(this.url('/ws'), origin)
    u.protocol = u.protocol === 'https:' ? 'wss:' : 'ws:'
    return u.toString()
  }
}

/** Parses one `/ws` message; returns null for anything that is not a known event. */
export function parseLiveEvent(data: string): LiveEvent | null {
  try {
    const msg: unknown = JSON.parse(data)
    if (typeof msg !== 'object' || msg === null) return null
    const type = (msg as { type?: unknown }).type
    if (type === 'desk' && typeof (msg as { desk?: unknown }).desk === 'object') return msg as LiveEvent
    if (type === 'metrics') return msg as LiveEvent
    return null
  } catch {
    return null
  }
}

/** Builds a desk config from an existing desk (for the edit form). */
export function deskToConfig(desk: Desk): DeskConfig {
  const profile: DeskProfile = { ...desk.profile }
  const config: DeskConfig = {
    id: desk.id,
    name: desk.name,
    user: desk.user,
    basePort: desk.basePort,
    autostart: desk.autostart,
    gpu: desk.gpu,
    profile,
  }
  // Settings the form does not show are sent back unchanged (PUT replaces the whole desk).
  if (desk.disableBluetoothUserService !== undefined) config.disableBluetoothUserService = desk.disableBluetoothUserService
  if (desk.workerSettings) config.workerSettings = { ...desk.workerSettings }
  return config
}

/** The app-wide client instance. */
export const api = new ApiClient({ baseUrl: import.meta.env.VITE_API_BASE ?? DEFAULT_API_BASE })
