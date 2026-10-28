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
  if (gpu === 'auto') return t('desks.gpuAuto')
  return gpus?.find((g) => g.luid.toLowerCase() === gpu.toLowerCase())?.name ?? gpu
}
