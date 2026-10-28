// SPDX-License-Identifier: MIT
import { onMounted, onUnmounted } from 'vue'
import { ApiError } from './api/client'
import type { Desk, DeskState, GpuInfo } from './api/types'
import { t } from './i18n'
import { store } from './store'

/** Keeps live desk and metrics updates running while the component is mounted. */
let liveUsers = 0
export function useLive(): void {
  onMounted(() => {
    liveUsers++
    store.startLive()
  })
  onUnmounted(() => {
    liveUsers--
    // Defer so that a view replacing this one can claim live updates first.
    setTimeout(() => {
      if (liveUsers === 0) store.stopLive()
    }, 0)
  })
}

export function errorText(e: unknown): string {
  if (e instanceof ApiError) return e.message
  return e instanceof Error ? e.message : String(e)
}

const RUNNING_STATES: readonly DeskState[] = [
  'Provisioning',
  'Connecting',
  'SessionReady',
  'DisplayReady',
  'Ready',
  'Streaming',
  'Recovering',
]

export function isRunning(desk: Desk): boolean {
  return RUNNING_STATES.includes(desk.state)
}

export function canStart(desk: Desk): boolean {
  return desk.state === 'Stopped' || desk.state === 'Defined'
}

export function canStop(desk: Desk): boolean {
  return isRunning(desk) || desk.state === 'Faulted'
}

export function gpuLabel(gpu: string, gpus: readonly GpuInfo[] | undefined): string {
  if (gpu === 'auto') return t('adv.gpuAuto')
  return gpus?.find((g) => g.luid.toLowerCase() === gpu.toLowerCase())?.name ?? gpu
}

/** Copies text to the clipboard; falls back to a hidden textarea where the Clipboard API is missing. */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (globalThis.navigator?.clipboard) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // Fall through to the textarea copy.
  }
  try {
    const area = document.createElement('textarea')
    area.value = text
    area.style.position = 'fixed'
    area.style.opacity = '0'
    document.body.appendChild(area)
    area.select()
    const ok = document.execCommand('copy')
    area.remove()
    return ok
  } catch {
    return false
  }
}
