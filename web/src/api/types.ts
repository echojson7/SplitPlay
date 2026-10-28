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
}

/** Live metrics carried in the desk object. Unknown keys from the worker pass through untyped. */
export interface DeskMetrics {
  fps: number
  bitrateKbps: number
  encodeMsP99: number
  clients: number
}

/** Desk object as returned by GET /desks and GET /desks/{id}. */
export interface Desk extends Omit<DeskConfig, 'basePort'> {
  basePort: number
  state: DeskState
  sessionId: number | null
  metrics: DeskMetrics
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

/** Host settings (config.md `host`). Endpoint GET/PUT /host is a proposed contract addition. */
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
