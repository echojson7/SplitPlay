// SPDX-License-Identifier: MIT
// Minimal reactive i18n: `t(key, params)` reads the current locale and falls back to English.

import { ref } from 'vue'
import { DEFAULT_LOCALE, FALLBACK_LOCALE, LOCALES, type Locale, type MessageKey } from './messages'

const STORAGE_KEY = 'splitplay.locale'

function initialLocale(): Locale {
  try {
    const saved = globalThis.localStorage?.getItem(STORAGE_KEY)
    if (saved && saved in LOCALES) return saved as Locale
  } catch {
    // Storage blocked; use the default.
  }
  return DEFAULT_LOCALE
}

export const locale = ref<Locale>(initialLocale())

export function setLocale(next: Locale): void {
  locale.value = next
  try {
    globalThis.localStorage?.setItem(STORAGE_KEY, next)
  } catch {
    // Not persisted; fine.
  }
  if (typeof document !== 'undefined') document.documentElement.lang = next
}

export type MessageParams = Record<string, string | number>

export function translate(loc: Locale, key: MessageKey, params?: MessageParams): string {
  const text = LOCALES[loc][key] ?? LOCALES[FALLBACK_LOCALE][key] ?? key
  if (!params) return text
  return text.replace(/\{(\w+)\}/g, (m, name: string) => (name in params ? String(params[name]) : m))
}

export function t(key: MessageKey, params?: MessageParams): string {
  return translate(locale.value, key, params)
}

/** Number formatting for the current locale. */
export function fmtNumber(value: number, maximumFractionDigits = 0): string {
  return new Intl.NumberFormat(locale.value === 'ru' ? 'ru-RU' : 'en-US', { maximumFractionDigits }).format(value)
}

export function fmtBitrate(kbps: number): string {
  return kbps >= 1000 ? t('unit.mbps', { value: fmtNumber(kbps / 1000, 1) }) : t('unit.kbps', { value: fmtNumber(kbps) })
}

export function fmtDateTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return new Intl.DateTimeFormat(locale.value === 'ru' ? 'ru-RU' : 'en-US', {
    dateStyle: 'short',
    timeStyle: 'medium',
  }).format(d)
}

export function fmtDuration(seconds: number): string {
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = Math.floor(seconds % 60)
  const hms = [h, m, s].map((n) => String(n).padStart(2, '0')).join(':')
  return d > 0 ? `${d}d ${hms}` : hms
}

export type { Locale, MessageKey }
