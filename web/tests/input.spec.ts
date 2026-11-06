// SPDX-License-Identifier: MIT
// RDP input of a desk: the "Input" row tells where keyboard and mouse go now.
import { beforeEach, describe, expect, it } from 'vitest'
import { inputLabel } from '../src/desk'
import { locale } from '../src/i18n'

describe('input row', () => {
  beforeEach(() => {
    locale.value = 'ru'
  })

  it('names the RDP path and stays hidden for Windows input', () => {
    expect(inputLabel('rdp')).toBe('RDP')
    expect(inputLabel('rdp_mixed')).toBe('RDP, мышь в играх через Windows')
    expect(inputLabel('rdp_unavailable')).toBe('Windows, RDP недоступен')
    expect(inputLabel('windows')).toBeNull()
    expect(inputLabel(null)).toBeNull()
    expect(inputLabel(undefined)).toBeNull()
  })

  it('has English strings', () => {
    locale.value = 'en'
    expect(inputLabel('rdp_mixed')).toBe('RDP, game mouse via Windows')
  })
})
