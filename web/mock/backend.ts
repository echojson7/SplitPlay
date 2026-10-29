// SPDX-License-Identifier: MIT
// In-memory mock of Web API v1, used by `npm run dev:mock` (through mock/vite-plugin.ts) and by unit tests.
// Pure TypeScript without Node or DOM APIs so that both environments can load it.

import type {
  Desk,
  DeskConfig,
  DeskMetrics,
  DeskState,
  HostService,
  HostSettings,
  LogLine,
  MetricsSnapshot,
  PairedClient,
} from '../src/api/types.ts'

export interface MockRequest {
  method: string
  /** Path below the API base, e.g. `/desks/desk-1/start`. */
  path: string
  query?: Record<string, string>
  headers?: Record<string, string | undefined>
  body?: unknown
}

export interface MockResponse {
  status: number
  headers?: Record<string, string>
  body?: unknown
  /** Raw bytes for non-JSON responses (config export). */
  bytes?: Uint8Array<ArrayBuffer>
}

export interface MockBackendOptions {
  /** Clock in milliseconds; injectable for tests. */
  now?: () => number
  /** Admin password already set (default: none, so `setupRequired` is true). */
  password?: string
  /**
   * The caller is the tray (signed in through the admin pipe, no cookie or CSRF token needed). Default: true while no
   * password is set (then only the tray can manage the host), false otherwise (a plain browser that logs in).
   */
  trusted?: boolean
  /** Delay between lifecycle steps after start/stop, in ms. */
  stepMs?: number
}

const SESSION_COOKIE = 'sp_session'
const ID_RE = /^[a-z0-9-]{1,32}$/
const VERSION = '0.1.0-mock'

interface DeskEntry {
  desk: Desk
  clients: PairedClient[]
  /** IP address the simulated Moonlight client streams from. */
  clientAddress: string
  /** Remaining lifecycle states and when the next one is due. */
  pending: DeskState[]
  nextAt: number
}

function err(status: number, code: string, message: string): MockResponse {
  return { status, body: { error: { code, message } } }
}

function ok(body: unknown, status = 200): MockResponse {
  return { status, body }
}

const IDLE_METRICS: DeskMetrics = { fps: 0, bitrateKbps: 0, encodeMsP50: 0, encodeMsP99: 0, lossPct: 0, rttMs: null, clients: 0 }

function clone<T>(v: T): T {
  return JSON.parse(JSON.stringify(v)) as T
}

function randomToken(): string {
  let s = ''
  for (let i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16)
  return s
}

function parseCookies(header: string | undefined): Record<string, string> {
  const out: Record<string, string> = {}
  for (const part of (header ?? '').split(';')) {
    const i = part.indexOf('=')
    if (i > 0) out[part.slice(0, i).trim()] = part.slice(i + 1).trim()
  }
  return out
}

export function validateConfigBody(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return 'body must be an object'
  const c = body as Partial<DeskConfig>
  if (typeof c.id !== 'string' || !ID_RE.test(c.id)) return 'id must match [a-z0-9-]{1,32}'
  if (typeof c.name !== 'string' || !c.name.trim()) return 'name is required'
  if (typeof c.user !== 'string' || !c.user.trim()) return 'user is required'
  if (typeof c.gpu !== 'string' || !(c.gpu === 'auto' || /^0x[0-9a-fA-F]+$/.test(c.gpu))) return 'gpu must be auto or 0x…'
  const p = c.profile
  if (!p || typeof p.fps !== 'number' || p.fps < 10 || p.fps > 500) return 'profile.fps must be 10..500'
  if (!['h264', 'hevc', 'av1'].includes(p.codec)) return 'profile.codec must be h264, hevc or av1'
  if (typeof p.width !== 'number' || typeof p.height !== 'number' || p.width < 320 || p.height < 240)
    return 'profile resolution is invalid'
  if (typeof p.bitrateKbps !== 'number' || p.bitrateKbps < 500) return 'profile.bitrateKbps is invalid'
  return null
}

/** Local Windows accounts of the mock host and their passwords. */
const MOCK_USERS: ReadonlyArray<{
  name: string
  fullName: string | null
  isAdmin: boolean
  password: string
  console?: boolean
  managed?: boolean
}> = [
  { name: 'owner', fullName: 'Владелец ПК', isAdmin: true, password: 'owner', console: true },
  { name: 'anna', fullName: 'Анна', isAdmin: false, password: 'anna' },
  { name: 'misha', fullName: 'Михаил', isAdmin: false, password: 'misha' },
  { name: 'splitplay-guest', fullName: null, isAdmin: false, password: 'guest', managed: true },
]

/** LAN addresses reported by GET /host/network. */
const MOCK_ADDRESSES = ['192.168.1.20']

/** Takes `password` out of a desk body; returns an error message for a wrong password of an existing account. */
function takePassword(body: unknown): { password?: string; problem?: { status: number; code: string; message: string } } {
  if (typeof body !== 'object' || body === null || !('password' in body)) return {}
  const b = body as { password?: unknown; user?: unknown }
  const password = typeof b.password === 'string' && b.password !== '' ? b.password : undefined
  delete b.password
  const account = MOCK_USERS.find((u) => typeof b.user === 'string' && u.name === b.user.toLowerCase())
  if (account && password !== undefined && password !== account.password)
    return { problem: { status: 400, code: 'validation_failed', message: `wrong password for ${account.name}` } }
  return { password }
}

function consoleUserProblem(user: unknown): string | null {
  const account = MOCK_USERS.find((u) => u.console && typeof user === 'string' && u.name === user.toLowerCase())
  return account ? `'${account.name}' is signed in at this PC's console; pick another user` : null
}

/** Windows services of the mock host (GET /host/services). */
const MOCK_SERVICES: HostService[] = [
  { name: 'SplitPlay', displayName: 'SplitPlay Host Service', status: 'running', startType: 'automatic', canStart: false, canStop: true },
  { name: 'TermService', displayName: 'Службы удалённых рабочих столов', status: 'running', startType: 'automatic', canStart: false, canStop: true },
  {
    name: 'UmRdpService',
    displayName: 'Перенаправитель портов пользовательского режима служб удалённых рабочих столов',
    status: 'running',
    startType: 'manual',
    canStart: false,
    canStop: true,
  },
  { name: 'ViGEmBus', displayName: 'Virtual Gamepad Emulation Bus', status: 'missing', startType: null, canStart: false, canStop: false },
  { name: 'Audiosrv', displayName: 'Windows Audio', status: 'running', startType: 'automatic', canStart: false, canStop: true },
  { name: 'Steam Client Service', displayName: 'Steam Client Service', status: 'stopped', startType: 'manual', canStart: true, canStop: false },
]

interface ServiceEntry {
  service: HostService
  /** Status the service reaches at `doneAt`, after `starting`/`stopping`. */
  target?: 'running' | 'stopped'
  doneAt: number
}

const RUNNING: DeskState[] = [
  'Provisioning',
  'Connecting',
  'SessionReady',
  'DisplayReady',
  'Ready',
  'Streaming',
  'Recovering',
]

export class MockBackend {
  private readonly now: () => number
  private readonly stepMs: number
  private readonly startedAt: number
  private password: string | undefined
  private readonly trusted: boolean
  private readonly sessions = new Map<string, string>() // session id -> csrf token
  private readonly desks = new Map<string, DeskEntry>()
  private host: HostSettings = {
    webPort: 38299,
    lanAccess: false,
    basePortStart: 48089,
    basePortStride: 100,
    composeIntervalMs: 4,
    logLevel: 'info',
    keepAwake: true,
  }
  private clientSeq = 1
  private readonly services = new Map<string, ServiceEntry>()

  constructor(opts: MockBackendOptions = {}) {
    this.now = opts.now ?? (() => Date.now())
    this.stepMs = opts.stepMs ?? 700
    this.password = opts.password
    this.trusted = opts.trusted ?? opts.password === undefined
    this.startedAt = this.now()
    for (const s of MOCK_SERVICES) this.services.set(s.name.toLowerCase(), { service: clone(s), doneAt: 0 })
    this.seed()
  }

  private seed(): void {
    const a = this.addDesk({
      id: 'anna',
      name: 'Аня',
      user: 'anna',
      basePort: 48089,
      autostart: true,
      gpu: 'auto',
      profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 50000, codec: 'hevc' },
    })
    a.desk.state = 'Streaming'
    a.desk.sessionId = 3
    a.desk.hasPassword = true
    a.desk.userManaged = false
    a.clients.push({ id: 'c0ffee00-0000-4000-8000-000000000001', name: 'Ноутбук Ани', pairedAt: '2026-10-20T18:00:00Z' })
    a.clientAddress = '192.168.1.37'
    this.connectClient(a)
    const b = this.addDesk({
      id: 'guest',
      name: 'Гостевой',
      user: 'splitplay-guest',
      basePort: 48189,
      autostart: false,
      gpu: '0x0000000000012345',
      profile: { fps: 60, width: 2560, height: 1440, bitrateKbps: 45000, codec: 'av1' },
    })
    b.desk.state = 'Stopped'
    b.desk.hasPassword = true
    b.desk.userManaged = true
  }

  private addDesk(config: DeskConfig): DeskEntry {
    const basePort = config.basePort ?? this.freeBasePort()
    const entry: DeskEntry = {
      desk: {
        ...clone(config),
        basePort,
        state: 'Defined',
        sessionId: null,
        metrics: { ...IDLE_METRICS },
        stream: null,
        lastError: null,
      },
      clients: [],
      clientAddress: `192.168.1.${40 + this.desks.size}`,
      pending: [],
      nextAt: 0,
    }
    this.desks.set(config.id, entry)
    return entry
  }

  private freeBasePort(): number {
    const used = new Set([...this.desks.values()].map((d) => d.desk.basePort))
    for (let k = 0; ; k++) {
      const p = this.host.basePortStart + this.host.basePortStride * k
      if (!used.has(p)) return p
    }
  }

  private clientStreams(e: DeskEntry): DeskState[] {
    return e.clients.length > 0 ? ['Streaming'] : []
  }

  /** A paired Moonlight client starts streaming to the desk (what the worker's client_connected reports). */
  private connectClient(e: DeskEntry): void {
    const client = e.clients[0]
    const p = e.desk.profile
    e.desk.stream = {
      client: client?.name ?? null,
      clientId: client?.id ?? null,
      address: e.clientAddress,
      width: p.width,
      height: p.height,
      fps: p.fps,
      codec: p.codec,
      bitrateKbps: p.bitrateKbps,
      hdr: false,
      startedAt: new Date(this.now()).toISOString(),
    }
  }

  /** Advances pending lifecycle transitions and refreshes simulated metrics. */
  private tick(): void {
    const t = this.now()
    for (const e of this.services.values()) {
      if (e.target && t >= e.doneAt) {
        const running = e.target === 'running'
        e.service = { ...e.service, status: e.target, canStart: !running, canStop: running }
        e.target = undefined
        // The mock cannot go offline: a stopped SplitPlay comes back as if the tray had started it again.
        if (!running && e.service.name === 'SplitPlay') this.changeService(e, 'running', 3)
      }
    }
    for (const e of this.desks.values()) {
      while (e.pending.length > 0 && t >= e.nextAt) {
        e.desk.state = e.pending.shift()!
        e.nextAt += this.stepMs
        if (e.desk.state === 'SessionReady') e.desk.sessionId = 2 + [...this.desks.keys()].indexOf(e.desk.id)
        if (e.desk.state === 'Stopped') e.desk.sessionId = null
        if (e.desk.state === 'Streaming') this.connectClient(e)
        else e.desk.stream = null
      }
      const p = e.desk.profile
      if (e.desk.state === 'Streaming') {
        // A good wired LAN: a steady frame rate, a little bitrate swing, rare small losses.
        const phase = Math.sin(t / 900 + p.fps)
        const jitter = Math.sin(t / 370 + p.width)
        const p50 = Math.round((2.6 + 0.4 * phase + 60 / p.fps) * 10) / 10
        e.desk.metrics = {
          fps: Math.round(p.fps * (0.992 + 0.006 * phase)),
          bitrateKbps: Math.round(p.bitrateKbps * (0.72 + 0.08 * phase)),
          encodeMsP50: p50,
          encodeMsP99: Math.round((p50 + 1.8 + 1.2 * Math.abs(jitter)) * 10) / 10,
          lossPct: jitter > 0.93 ? Math.round((jitter - 0.9) * 40) / 10 : 0,
          rttMs: Math.round(3 + 1.5 * jitter),
          clients: 1,
        }
      } else {
        e.desk.metrics = { ...IDLE_METRICS }
      }
    }
  }

  private schedule(e: DeskEntry, states: DeskState[]): void {
    e.pending = [...states]
    e.nextAt = this.now() + this.stepMs
  }

  /** Returns null when the request may pass, otherwise the error response. */
  private authorize(req: MockRequest, path: string): MockResponse | null {
    if (path === '/health' || path.startsWith('/auth/')) return null
    if (this.trusted) return null // The tray's own session.
    const auth = req.headers?.['authorization']
    if (auth?.startsWith('Bearer ')) return null // Tray/CLI token; not validated by the mock.
    const sid = parseCookies(req.headers?.['cookie'])[SESSION_COOKIE]
    const csrf = sid ? this.sessions.get(sid) : undefined
    if (!csrf) return err(401, 'unauthorized', 'Login required')
    if (req.method !== 'GET' && req.headers?.['x-csrf-token'] !== csrf) return err(403, 'csrf', 'Missing or wrong CSRF token')
    return null
  }

  handle(req: MockRequest): MockResponse {
    const method = req.method.toUpperCase()
    const path = req.path.replace(/\/+$/, '') || '/'
    const denied = this.authorize(req, path)
    if (denied) return denied
    this.tick()

    // --- health and auth
    if (path === '/health' && method === 'GET') {
      return ok({ status: 'ok', version: VERSION, uptimeSeconds: Math.floor((this.now() - this.startedAt) / 1000) })
    }
    if (path === '/auth/session' && method === 'GET') {
      const setupRequired = this.password === undefined
      const sid = parseCookies(req.headers?.['cookie'])[SESSION_COOKIE]
      const csrf = sid ? this.sessions.get(sid) : undefined
      if (csrf) return ok({ setupRequired, authenticated: true, csrfToken: csrf })
      return ok({ setupRequired, authenticated: this.trusted })
    }
    if (path === '/auth/login' && method === 'POST') {
      const pw = (req.body as { password?: unknown } | undefined)?.password
      if (this.password === undefined) return err(409, 'setup_required', 'Set the admin password first')
      if (pw !== this.password) return err(401, 'bad_password', 'Wrong password')
      return this.newSession()
    }
    if (path === '/auth/logout' && method === 'POST') {
      const sid = parseCookies(req.headers?.['cookie'])[SESSION_COOKIE]
      if (sid) this.sessions.delete(sid)
      return { status: 204, headers: { 'Set-Cookie': `${SESSION_COOKIE}=; Path=/; HttpOnly; Max-Age=0; SameSite=Strict` } }
    }
    if (path === '/auth/password' && method === 'POST') {
      const b = (req.body ?? {}) as { currentPassword?: unknown; newPassword?: unknown }
      if (typeof b.newPassword !== 'string' || b.newPassword.length < 8)
        return err(400, 'weak_password', 'Password must have at least 8 characters')
      if (this.password === undefined && !this.trusted)
        return err(401, 'unauthorized', 'The first admin password can only be set from the tray')
      if (this.password !== undefined && b.currentPassword !== this.password)
        return err(403, 'bad_password', 'Current password is wrong')
      const first = this.password === undefined
      this.password = b.newPassword
      // First-time setup logs the browser in right away.
      return first ? this.newSession() : { status: 204 }
    }

    // --- desks
    if (path === '/desks' && method === 'GET') return ok([...this.desks.values()].map((e) => clone(e.desk)))
    if (path === '/host/users' && method === 'GET')
      return ok(
        MOCK_USERS.map((u) => ({
          name: u.name,
          fullName: u.fullName,
          isAdmin: u.isAdmin,
          isConsoleUser: !!u.console,
          createdBySplitPlay: !!u.managed,
        })),
      )
    if (path === '/desks' && method === 'POST') {
      const taken = takePassword(req.body)
      if (taken.problem) return err(taken.problem.status, taken.problem.code, taken.problem.message)
      const consoleProblem = consoleUserProblem((req.body as { user?: unknown } | null)?.user)
      if (consoleProblem) return err(409, 'console_user', consoleProblem)
      const problem = validateConfigBody(req.body)
      if (problem) return err(400, 'invalid_config', problem)
      const c = req.body as DeskConfig
      if (this.desks.has(c.id)) return err(409, 'duplicate_id', `Desk ${c.id} already exists`)
      if ([...this.desks.values()].some((e) => e.desk.user.toLowerCase() === c.user.toLowerCase()))
        return err(409, 'duplicate_user', `User ${c.user} is already used by another desk`)
      if (c.basePort !== undefined && [...this.desks.values()].some((e) => e.desk.basePort === c.basePort))
        return err(409, 'duplicate_port', `Base port ${c.basePort} is already used`)
      const e = this.addDesk(c)
      e.desk.state = 'Stopped'
      e.desk.hasPassword = taken.password !== undefined
      e.desk.userManaged = !MOCK_USERS.some((u) => u.name === c.user.toLowerCase() && !u.managed)
      return ok(clone(e.desk), 201)
    }

    const m = /^\/desks\/([^/]+)(?:\/(start|stop|restart|clients|pair)(?:\/([^/]+))?)?$/.exec(path)
    if (m) {
      const id = decodeURIComponent(m[1]!)
      const action = m[2]
      const sub = m[3] ? decodeURIComponent(m[3]) : undefined
      const e = this.desks.get(id)
      if (!e) return err(404, 'not_found', `Desk ${id} not found`)
      const running = RUNNING.includes(e.desk.state)

      if (!action) {
        if (method === 'GET') return ok(clone(e.desk))
        if (method === 'PUT') {
          const taken = takePassword(req.body)
          if (taken.problem) return err(taken.problem.status, taken.problem.code, taken.problem.message)
          const consoleProblem = consoleUserProblem((req.body as { user?: unknown } | null)?.user)
          if (consoleProblem) return err(409, 'console_user', consoleProblem)
          const problem = validateConfigBody(req.body)
          if (problem) return err(400, 'invalid_config', problem)
          const c = req.body as DeskConfig
          if (c.id !== id) return err(400, 'invalid_config', 'id cannot be changed')
          if (running) {
            const { profile: _p, ...rest } = c
            const { profile: _q, ...cur } = deskConfigOf(e.desk)
            if (JSON.stringify(rest) !== JSON.stringify(cur))
              return err(409, 'desk_running', 'Only the profile can change while the desk runs')
          }
          const userChanged = c.user.toLowerCase() !== e.desk.user.toLowerCase()
          const resized = c.profile.width !== e.desk.profile.width || c.profile.height !== e.desk.profile.height
          Object.assign(e.desk, clone(c), { basePort: c.basePort ?? e.desk.basePort })
          if (taken.password !== undefined) e.desk.hasPassword = true
          else if (userChanged) e.desk.hasPassword = false
          if (userChanged) e.desk.userManaged = !MOCK_USERS.some((u) => u.name === c.user.toLowerCase())
          // The mock seat cannot resize a running session.
          if (running && resized) e.desk.resolutionPendingRestart = true
          return ok(clone(e.desk))
        }
        if (method === 'DELETE') {
          if (running) return err(409, 'desk_running', 'Stop the desk first')
          this.desks.delete(id)
          return { status: 204 }
        }
      }
      if (method === 'POST' && action === 'start') {
        if (running) return err(409, 'already_running', 'Desk is already running')
        e.desk.lastError = null
        e.desk.resolutionPendingRestart = false
        e.desk.state = 'Provisioning'
        // A desk with a paired device: the device connects once the desk is ready.
        this.schedule(e, ['Connecting', 'SessionReady', 'DisplayReady', 'Ready', ...this.clientStreams(e)])
        return ok(clone(e.desk), 202)
      }
      if (method === 'POST' && action === 'stop') {
        if (!running && e.desk.state !== 'Faulted') return err(409, 'not_running', 'Desk is not running')
        e.desk.state = 'Stopping'
        e.desk.stream = null
        this.schedule(e, ['Stopped'])
        return ok(clone(e.desk), 202)
      }
      if (method === 'POST' && action === 'restart') {
        e.desk.lastError = null
        e.desk.state = 'Stopping'
        e.desk.stream = null
        this.schedule(e, ['Stopped', 'Provisioning', 'Connecting', 'SessionReady', 'DisplayReady', 'Ready', ...this.clientStreams(e)])
        return ok(clone(e.desk), 202)
      }
      if (action === 'clients' && method === 'GET' && !sub) return ok(clone(e.clients))
      if (action === 'clients' && method === 'DELETE' && sub) {
        const before = e.clients.length
        e.clients = e.clients.filter((c) => c.id !== sub)
        return e.clients.length < before ? { status: 204 } : err(404, 'not_found', 'Client not found')
      }
      if (action === 'pair' && method === 'POST') {
        const b = (req.body ?? {}) as { pin?: unknown; name?: unknown }
        if (typeof b.pin !== 'string' || !/^\d{4}$/.test(b.pin)) return err(400, 'invalid_pin', 'PIN must be 4 digits')
        if (!['Ready', 'Streaming'].includes(e.desk.state))
          return err(409, 'desk_not_ready', 'The desk must be running to pair a client')
        if (b.pin === '0000') return err(408, 'pair_timeout', 'No Moonlight client is waiting with this PIN')
        const name = typeof b.name === 'string' && b.name.trim() ? b.name.trim() : 'Moonlight client'
        e.clients.push({
          id: `c0ffee00-0000-4000-8000-${String(++this.clientSeq).padStart(12, '0')}`,
          name,
          pairedAt: new Date(this.now()).toISOString(),
        })
        return { status: 204 }
      }
      return err(405, 'method_not_allowed', `${method} ${path}`)
    }

    // --- host services
    if (path === '/host/services' && method === 'GET')
      return ok([...this.services.values()].map((e) => clone(e.service)).filter((s) => s.name !== 'Steam Client Service' || s.status !== 'missing'))
    const sm = /^\/host\/services\/([^/]+)\/(start|stop)$/.exec(path)
    if (sm && method === 'POST') {
      const e = this.services.get(decodeURIComponent(sm[1]!).toLowerCase())
      if (!e) return err(404, 'not_found', `service '${decodeURIComponent(sm[1]!)}' is not one of the services SplitPlay manages`)
      if (sm[2] === 'start') {
        if (!e.service.canStart) return err(409, 'invalid_state', `${e.service.name} cannot be started (${e.service.status})`)
        this.changeService(e, 'running')
      } else {
        if (!e.service.canStop) return err(409, 'invalid_state', `${e.service.name} cannot be stopped (${e.service.status})`)
        this.changeService(e, 'stopped')
        // Dependents stop with it, as the SCM does.
        const umrdp = this.services.get('umrdpservice')
        if (e.service.name === 'TermService' && umrdp?.service.canStop) this.changeService(umrdp, 'stopped')
      }
      return ok(clone(e.service), 202)
    }

    // --- host
    if (path === '/metrics' && method === 'GET') return ok(this.metrics())
    if (path === '/logs' && method === 'GET') return ok(this.logs(req.query ?? {}))
    if (path === '/host' && method === 'GET') return ok(clone(this.host))
    if (path === '/host/network' && method === 'GET') return ok({ addresses: [...MOCK_ADDRESSES] })
    if (path === '/host' && method === 'PUT') {
      const h = req.body as Partial<HostSettings> | undefined
      if (!h || typeof h.webPort !== 'number' || h.webPort < 1 || h.webPort > 65535)
        return err(400, 'invalid_config', 'webPort is invalid')
      if (typeof h.composeIntervalMs !== 'number' || h.composeIntervalMs < 2 || h.composeIntervalMs > 17)
        return err(400, 'invalid_config', 'composeIntervalMs must be 2..17')
      this.host = { ...this.host, ...clone(h) } as HostSettings
      return ok(clone(this.host))
    }
    if (path === '/config/export' && method === 'GET') {
      // "PK\x05\x06" + 18 zero bytes = an empty but valid zip archive.
      const bytes = new Uint8Array(22)
      bytes.set([0x50, 0x4b, 0x05, 0x06])
      return {
        status: 200,
        headers: { 'Content-Type': 'application/zip', 'Content-Disposition': 'attachment; filename="splitplay-config.zip"' },
        bytes,
      }
    }
    if (path === '/config/import' && method === 'POST') return { status: 204 }

    return err(404, 'not_found', `${method} ${path} is not part of the mock API`)
  }

  private changeService(e: ServiceEntry, target: 'running' | 'stopped', steps = 2): void {
    e.service = { ...e.service, status: target === 'running' ? 'starting' : 'stopping', canStart: false, canStop: false }
    e.target = target
    e.doneAt = this.now() + this.stepMs * steps
  }

  private newSession(): MockResponse {
    const sid = randomToken()
    const csrf = randomToken()
    this.sessions.set(sid, csrf)
    return {
      status: 200,
      headers: { 'Set-Cookie': `${SESSION_COOKIE}=${sid}; Path=/; HttpOnly; SameSite=Strict` },
      body: { csrfToken: csrf },
    }
  }

  private metrics(): MetricsSnapshot {
    const t = this.now()
    const desks = [...this.desks.values()].map((e) => ({ id: e.desk.id, ...e.desk.metrics }))
    const streaming = desks.filter((d) => (d.fps ?? 0) > 0).length
    return {
      host: {
        cpuPercent: Math.round(18 + 9 * streaming + 4 * Math.sin(t / 1300)),
        memoryUsedMb: 14200 + 2100 * streaming,
        memoryTotalMb: 65536,
        composeHz: Math.round(1000 / this.host.composeIntervalMs),
        gpus: [
          {
            luid: '0x0000000000012345',
            name: 'NVIDIA GeForce RTX 4090',
            utilizationPercent: Math.round(35 + 20 * streaming + 5 * Math.sin(t / 700)),
            encoderPercent: Math.round(12 * streaming),
            vramUsedMb: 6100 + 1800 * streaming,
            vramTotalMb: 24564,
          },
          {
            luid: '0x0000000000067890',
            name: 'Intel(R) UHD Graphics 770',
            utilizationPercent: 3,
            encoderPercent: 0,
            vramUsedMb: 310,
            vramTotalMb: 2048,
          },
        ],
      },
      desks,
    }
  }

  private logs(q: Record<string, string>): LogLine[] {
    const tail = Math.min(Math.max(Number(q.tail ?? 200) || 200, 1), 5000)
    const levels = ['info', 'info', 'debug', 'info', 'warn', 'info', 'error']
    const components = ['service', 'worker', 'seat']
    const lines: LogLine[] = []
    const base = this.now() - 60_000
    for (let i = 0; i < 120; i++) {
      const component = components[i % 3]!
      const desk = component === 'service' && i % 2 === 0 ? null : ['anna', 'guest'][i % 2]!
      lines.push({
        time: new Date(base + i * 500).toISOString(),
        level: levels[i % levels.length]!,
        component,
        desk,
        message:
          component === 'worker'
            ? `heartbeat seq=${i} fps=${desk === 'anna' ? 120 : 0}`
            : component === 'seat'
              ? `rdp session state: logged_on (session ${2 + (i % 3)})`
              : `desk ${desk ?? '-'} supervisor tick`,
      })
    }
    return lines
      .filter((l) => !q.component || l.component === q.component)
      .filter((l) => !q.desk || l.desk === q.desk)
      .slice(-tail)
  }
}

function deskConfigOf(desk: Desk): DeskConfig {
  return {
    id: desk.id,
    name: desk.name,
    user: desk.user,
    basePort: desk.basePort,
    autostart: desk.autostart,
    gpu: desk.gpu,
    profile: desk.profile,
  }
}

/** Adapts the mock to a `fetch` function for tests: `new ApiClient({ fetch: mockFetch(backend) })`. */
export function mockFetch(backend: MockBackend, origin = 'http://127.0.0.1:38299', apiBase = '/api/v1'): typeof fetch {
  let cookie = ''
  return async (input: Parameters<typeof fetch>[0], init?: RequestInit): Promise<Response> => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, origin)
    const headers: Record<string, string> = {}
    new Headers(init?.headers).forEach((v, k) => {
      headers[k.toLowerCase()] = v
    })
    if (cookie) headers['cookie'] = cookie
    let body: unknown
    if (typeof init?.body === 'string' && init.body) body = JSON.parse(init.body)
    const query: Record<string, string> = {}
    url.searchParams.forEach((v, k) => {
      query[k] = v
    })
    const path = url.pathname.startsWith(apiBase) ? url.pathname.slice(apiBase.length) : url.pathname
    const res = backend.handle({ method: init?.method ?? 'GET', path, query, headers, body })
    const setCookie = res.headers?.['Set-Cookie']
    if (setCookie) {
      const pair = setCookie.split(';')[0] ?? ''
      cookie = pair.endsWith('=') ? '' : pair
    }
    const outHeaders = new Headers(res.headers)
    outHeaders.delete('Set-Cookie')
    if (res.bytes) return new Response(res.bytes, { status: res.status, headers: outHeaders })
    if (res.body === undefined || res.status === 204) return new Response(null, { status: res.status, headers: outHeaders })
    outHeaders.set('Content-Type', 'application/json')
    return new Response(JSON.stringify(res.body), { status: res.status, headers: outHeaders })
  }
}
