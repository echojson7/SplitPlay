// SPDX-License-Identifier: MIT
import { afterEach, describe, expect, it } from 'vitest'
import { applyTheme, setTheme, THEME_KEY, themeMode } from '../src/theme'

describe('theme', () => {
  afterEach(() => {
    setTheme('system')
    localStorage.removeItem(THEME_KEY)
  })

  it('sets data-theme for light and dark and keeps the choice', () => {
    setTheme('dark')
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
    expect(localStorage.getItem(THEME_KEY)).toBe('dark')
    expect(themeMode.value).toBe('dark')
    setTheme('light')
    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
  })

  it('follows the system without data-theme', () => {
    applyTheme('dark')
    applyTheme('system')
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
  })
})
