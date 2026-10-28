// SPDX-License-Identifier: MIT
// Pure desk helpers shared by the library, the add-desk wizard and the desk page.

import type { Desk, DeskConfig, DeskState } from './api/types'
import { DESK_ID_RE } from './api/validate'

export interface ResolutionPreset {
  width: number
  height: number
  /** Short marketing name shown under the numbers. */
  tag: string
}

export const RESOLUTION_PRESETS: readonly ResolutionPreset[] = [
  { width: 1280, height: 720, tag: 'HD' },
  { width: 1920, height: 1080, tag: 'Full HD' },
  { width: 2560, height: 1440, tag: 'QHD' },
  { width: 3840, height: 2160, tag: '4K' },
]

export const FPS_PRESETS: readonly number[] = [60, 120, 144, 240]

export function resolutionKey(width: number, height: number): string {
  return `${width}x${height}`
}

export function isPresetResolution(width: number, height: number): boolean {
  return RESOLUTION_PRESETS.some((p) => p.width === width && p.height === height)
}

/** A bitrate that suits the picture: 30 Mbit/s for 1080p60, scaled by pixels per second, 10–150 Mbit/s. */
export function suggestBitrateKbps(width: number, height: number, fps: number): number {
  const scale = (width * height * fps) / (1920 * 1080 * 60)
  const kbps = Math.round((30000 * Math.sqrt(scale) * 1.15) / 5000) * 5000
  return Math.min(150000, Math.max(10000, kbps))
}

const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y', к: 'k', л: 'l', м: 'm',
  н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'h', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'sch',
  ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya',
}

/** Desk id from a Windows user name: lower-case latin, digits and dashes, unique among `taken`. */
export function deskIdFromUser(user: string, taken: readonly string[] = []): string {
  const latin = [...user.trim().toLowerCase()].map((c) => TRANSLIT[c] ?? c).join('')
  let base = latin
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 28)
    .replace(/-+$/, '')
  if (!base) base = 'desk'
  let id = base
  for (let n = 2; taken.includes(id); n++) id = `${base}-${n}`
  return DESK_ID_RE.test(id) ? id : 'desk'
}

/** Lifecycle steps while a desk starts; the progress bar walks through them. */
const START_STEPS: readonly DeskState[] = ['Provisioning', 'Connecting', 'SessionReady', 'DisplayReady']

export interface DeskProgress {
  /** 0..1 */
  value: number
  state: DeskState
}

/** Progress of a starting (or stopping) desk, null when the desk is settled. */
export function deskProgress(state: DeskState): DeskProgress | null {
  const i = START_STEPS.indexOf(state)
  if (i >= 0) return { value: (i + 1) / (START_STEPS.length + 1), state }
  if (state === 'Recovering') return { value: 0.5, state }
  if (state === 'Stopping') return { value: 0.5, state }
  return null
}

export type StateTone = 'ok' | 'busy' | 'warn' | 'bad' | 'idle'

export function stateTone(state: DeskState): StateTone {
  switch (state) {
    case 'Ready':
    case 'Streaming':
      return 'ok'
    case 'Faulted':
      return 'bad'
    case 'Recovering':
      return 'warn'
    case 'Stopped':
    case 'Defined':
      return 'idle'
    default:
      return 'busy'
  }
}

/** Builds the config for a new desk from the wizard's three answers. */
export function newDeskConfig(opts: {
  user: string
  fullName?: string | null
  password: string
  width: number
  height: number
  fps: number
  existingIds: readonly string[]
}): DeskConfig {
  const user = opts.user.trim()
  const config: DeskConfig = {
    id: deskIdFromUser(user, opts.existingIds),
    name: opts.fullName?.trim() || user,
    user,
    autostart: false,
    gpu: 'auto',
    profile: {
      fps: opts.fps,
      width: opts.width,
      height: opts.height,
      bitrateKbps: suggestBitrateKbps(opts.width, opts.height, opts.fps),
      codec: 'hevc',
    },
  }
  if (opts.password !== '') config.password = opts.password
  return config
}

export function moonlightAddress(desk: Pick<Desk, 'basePort'>, address: string): string {
  return `${address}:${desk.basePort}`
}
