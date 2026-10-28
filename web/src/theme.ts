// SPDX-License-Identifier: MIT
// Light / dark / system theme. "system" follows prefers-color-scheme; the choice is kept in localStorage.

import { ref } from 'vue'

export type ThemeMode = 'light' | 'dark' | 'system'
export const THEME_KEY = 'splitplay.theme'

function saved(): ThemeMode {
  try {
    const v = globalThis.localStorage?.getItem(THEME_KEY)
    if (v === 'light' || v === 'dark' || v === 'system') return v
  } catch {
    // Storage blocked; follow the system.
  }
  return 'system'
}

export const themeMode = ref<ThemeMode>(saved())

export function applyTheme(mode: ThemeMode = themeMode.value): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  if (mode === 'system') root.removeAttribute('data-theme')
  else root.setAttribute('data-theme', mode)
}

export function setTheme(mode: ThemeMode): void {
  themeMode.value = mode
  try {
    globalThis.localStorage?.setItem(THEME_KEY, mode)
  } catch {
    // Not persisted; fine.
  }
  applyTheme(mode)
}
