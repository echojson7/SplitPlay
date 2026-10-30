// SPDX-License-Identifier: MIT
// Client-side checks for desk configs (rules from docs/contracts/config.md). The service validates again.

import type { MessageKey } from '../i18n/messages'
import type { DeskConfig } from './types'

export const DESK_ID_RE = /^[a-z0-9-]{1,32}$/
// Local account names: up to 20 characters, none of " / \ [ ] : ; | = , + * ? < > @, not only dots/spaces.
const WINDOWS_USER_RE = /^[^"/\\[\]:;|=,+*?<>@]{1,20}$/

export const FPS_MIN = 10
export const FPS_MAX = 500
export const PORT_MIN = 1029 // basePort-5 must stay >= 1024
export const PORT_MAX = 65514 // basePort+21 must stay <= 65535

export type DeskConfigErrors = Partial<Record<'id' | 'name' | 'user' | 'basePort' | 'fps' | 'resolution' | 'bitrate' | 'fecPercent' | 'minFps' | 'pacingMbps', MessageKey>>

function isInt(n: unknown): n is number {
  return typeof n === 'number' && Number.isInteger(n)
}

export function validateDeskConfig(c: DeskConfig, existingIds: readonly string[] = []): DeskConfigErrors {
  const e: DeskConfigErrors = {}
  if (!DESK_ID_RE.test(c.id)) e.id = 'form.err.id'
  else if (existingIds.includes(c.id)) e.id = 'form.err.idTaken'
  if (!c.name.trim()) e.name = 'form.err.name'
  if (!WINDOWS_USER_RE.test(c.user) || /^[. ]+$/.test(c.user)) e.user = 'form.err.user'
  if (c.basePort !== undefined && (!isInt(c.basePort) || c.basePort < PORT_MIN || c.basePort > PORT_MAX))
    e.basePort = 'form.err.port'
  const p = c.profile
  if (!isInt(p.fps) || p.fps < FPS_MIN || p.fps > FPS_MAX) e.fps = 'form.err.fps'
  if (!isInt(p.width) || !isInt(p.height) || p.width < 320 || p.height < 240 || p.width > 7680 || p.height > 4320)
    e.resolution = 'form.err.resolution'
  if (!isInt(p.bitrateKbps) || p.bitrateKbps < 500 || p.bitrateKbps > 1_000_000) e.bitrate = 'form.err.bitrate'
  if (p.fecPercent !== undefined && (!isInt(p.fecPercent) || p.fecPercent < 0 || p.fecPercent > 100))
    e.fecPercent = 'form.err.fec'
  if (p.minFps !== undefined && (!isInt(p.minFps) || p.minFps < 1 || p.minFps > p.fps)) e.minFps = 'form.err.minFps'
  if (p.pacingMbps !== undefined && (!isInt(p.pacingMbps) || p.pacingMbps < 1 || p.pacingMbps > 100_000))
    e.pacingMbps = 'form.err.pacing'
  return e
}

export function hasErrors(e: DeskConfigErrors): boolean {
  return Object.keys(e).length > 0
}
