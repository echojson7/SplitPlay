// SPDX-License-Identifier: MIT
// Short notifications in the corner of the window.

import { reactive } from 'vue'

export interface Toast {
  id: number
  kind: 'ok' | 'error'
  text: string
}

export const toasts = reactive<Toast[]>([])
let seq = 0

export function dismissToast(id: number): void {
  const i = toasts.findIndex((t) => t.id === id)
  if (i >= 0) toasts.splice(i, 1)
}

export function toast(text: string, kind: Toast['kind'] = 'ok', ms = kind === 'error' ? 6000 : 2500): void {
  const id = ++seq
  toasts.push({ id, kind, text })
  while (toasts.length > 4) toasts.shift()
  setTimeout(() => dismissToast(id), ms)
}
