// SPDX-License-Identifier: MIT
// Types for Web API v1 (docs/contracts/web-api.md, docs/contracts/config.md).
// Shapes the contract leaves open are marked "assumed" and parsed leniently by the client.

export const DESK_STATES = [
  'Defined',
  'Provisioning',
  'Connecting',
  'SessionReady',
  'DisplayReady',
  'Ready',
  'Streaming',
  'Recovering',
  'Faulted',
  'Stopping',
  'Stopped',
] as const

export type DeskState = (typeof DESK_STATES)[number]

export const CODECS = ['h264', 'hevc', 'av1'] as const
export type Codec = (typeof CODECS)[number]

export const GPU_PRIORITIES = ['normal', 'high', 'realtime'] as const
export type GpuPriority = (typeof GPU_PRIORITIES)[number]

/** Desk streaming profile (config.md `desks[].profile`). */
export interface DeskProfile {
  fps: number
  width: number
  height: number
  bitrateKbps: number
  codec: Codec
  fecPercent?: number
  minFps?: number
  dpbFrames?: number
  pacingMbps?: number
  gpuPriority?: GpuPriority
}

/** Desk configuration as sent to POST/PUT /desks (config.md `desks[]`). */
export interface DeskConfig {
  id: string
  name: string
  user: string
  /** Omitted: the service picks a free base port. */
  basePort?: number
  autostart: boolean
  /** `"auto"` or an adapter LUID string `"0x…"`. */
  gpu: string
  profile: DeskProfile
  /** Stop the desk session's BluetoothUserService instance (default true). */
  disableBluetoothUserService?: boolean
  /** Give the desk user's Steam its own IPC name so it runs next to the console user's Steam (default true). */
  steamIsolation?: boolean
  /** Extra Sunshine settings passed to the worker verbatim. */
  workerSettings?: Record<string, string>
  /**
   * Windows password of `user`, sent only on create/update: verified for an existing account, used for a new one.
   * The service stores it encrypted and never returns it (see `Desk.hasPassword`).
   */
  password?: string
}

/** GET /host/users: an enabled local account a desk can use. */
export interface HostUser {
  name: string
  fullName?: string | null
  isAdmin: boolean
  /** Signed in at the PC's console: cannot be a desk user (its session would be taken over). */
  isConsoleUser: boolean
  createdBySplitPlay?: boolean
}

/** GET /host/network: LAN IPv4 addresses of the host, those with a default gateway first. */
export interface HostNetwork {
  addresses: string[]
}

export const SERVICE_STATUSES = ['running', 'stopped', 'starting', 'stopping', 'missing'] as const
export type ServiceStatus = (typeof SERVICE_STATUSES)[number]

/** GET /host/services: a Windows service SplitPlay depends on. */
export interface HostService {
  /** Service name: SplitPlay, TermService, UmRdpService, ViGEmBus, Audiosrv, Steam Client Service. */
  name: string
  /** Windows display name; the UI shows its own label per `name`. */
  displayName: string
  status: ServiceStatus
  /** automatic, automaticDelayed, manual, disabled, boot, system; null when missing. */
  startType: string | null
  canStart: boolean
  canStop: boolean
}

/** Live metrics carried in the desk object. Unknown keys from the worker pass through untyped. */
export interface DeskMetrics {
  /** Frames actually sent over the last second. */
  fps: number
  /** Video bitrate actually sent. */
  bitrateKbps: number
  /** Host capture-to-encoded time over the last second, median, ms (older services: absent). */
  encodeMsP50?: number
  encodeMsP99: number
  /** Frames the client reported lost, percent of the frames sent. */
  lossPct?: number
  /** Round-trip time to the Moonlight client, ms; null until one is measured (older services: absent). */
  rttMs?: number | null
  clients: number
}

/** The Moonlight stream a desk is serving (`desk.stream`): who is connected and what it asked for. */
export interface DeskStream {
  /** Paired device name; null when the worker does not know it. */
  client: string | null
  /** Paired client id (as in GET /desks/{id}/clients). */
  clientId?: string | null
  /** Client IP address. */
  address: string | null
  width: number
  height: number
  /** Requested frame rate. */
  fps: number
  codec: Codec | string | null
  /** Requested bitrate (the Moonlight setting). */
  bitrateKbps: number
  hdr: boolean
  startedAt?: string | null
}

/** Desk object as returned by GET /desks and GET /desks/{id}. */
export interface Desk extends Omit<DeskConfig, 'basePort' | 'password'> {
  basePort: number
  /** True when SplitPlay created the account; false for an existing account picked by the owner. */
  userManaged?: boolean
  /** A password for `user` is stored. */
  hasPassword?: boolean
  /** The resolution changed while running and the session could not be resized: applies after a restart. */
  resolutionPendingRestart?: boolean
  state: DeskState
  sessionId: number | null
  metrics: DeskMetrics
  /** The stream being served; null (or absent from older services) when nobody is connected. */
  stream?: DeskStream | null
  lastError: string | null
}

export interface Health {
  status: string
  version: string
  uptimeSeconds: number
}

/**
 * GET /auth/session. The contract fixes only `setupRequired`; `authenticated` and `csrfToken`
 * are assumed (the CSRF token "is returned by login and by GET /auth/session").
 */
export interface AuthSession {
  setupRequired: boolean
  authenticated: boolean
  csrfToken?: string
}

/** POST /auth/login response (assumed). */
export interface LoginResult {
  csrfToken?: string
}

/** Paired Moonlight client (assumed shape). */
export interface PairedClient {
  id: string
  name: string
  pairedAt?: string
}

export interface PairRequest {
  pin: string
  name: string
}

/** GPU adapter entry inside host metrics (assumed shape). */
export interface GpuInfo {
  luid: string
  name: string
  utilizationPercent?: number
  encoderPercent?: number
  vramUsedMb?: number
  vramTotalMb?: number
}

/** Host part of GET /metrics (assumed shape). */
export interface HostMetrics {
  cpuPercent?: number
  memoryUsedMb?: number
  memoryTotalMb?: number
  composeHz?: number
  gpus: GpuInfo[]
}

/** Per-desk part of GET /metrics (assumed shape). */
export interface DeskMetricsEntry extends Partial<DeskMetrics> {
  id: string
}

export interface MetricsSnapshot {
  host: HostMetrics
  desks: DeskMetricsEntry[]
}

export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'fatal'

/** One log line from GET /logs (assumed shape, mirrors the structured JSON log lines). */
export interface LogLine {
  time: string
  level: LogLevel | string
  component: string
  desk?: string | null
  message: string
}

export interface LogQuery {
  component?: string
  desk?: string
  tail?: number
}

/** Host settings (config.md `host`, GET/PUT /host). */
export interface HostSettings {
  webPort: number
  lanAccess: boolean
  basePortStart: number
  basePortStride: number
  composeIntervalMs: number
  logLevel: string
}

/** Events pushed over GET /ws. */
export type LiveEvent =
  | { type: 'desk'; desk: Desk }
  | ({ type: 'metrics' } & Partial<MetricsSnapshot>)

export interface ApiErrorBody {
  error: { code: string; message: string }
}
