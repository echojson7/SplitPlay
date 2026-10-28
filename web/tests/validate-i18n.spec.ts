// SPDX-License-Identifier: MIT
import { describe, expect, it } from 'vitest'
import type { DeskConfig } from '../src/api/types'
import { validateDeskConfig } from '../src/api/validate'
import { locale, t, translate } from '../src/i18n'
import { en, ru, type MessageKey } from '../src/i18n/messages'

const ok: DeskConfig = {
  id: 'desk-1',
  name: 'Desk',
  user: 'splitplay-desk1',
  autostart: true,
  gpu: 'auto',
  profile: { fps: 500, width: 1920, height: 1080, bitrateKbps: 60000, codec: 'hevc' },
}

describe('validateDeskConfig', () => {
  it('accepts a valid config, including 500 FPS', () => {
    expect(validateDeskConfig(ok)).toEqual({})
  })

  it('flags each broken rule', () => {
    const e = validateDeskConfig(
      {
        ...ok,
        id: 'Desk 1',
        name: ' ',
        user: 'bad/user',
        basePort: 80,
        profile: { fps: 501, width: 100, height: 100, bitrateKbps: 10, codec: 'h264', fecPercent: 150, minFps: 600 },
      },
      [],
    )
    expect(e).toEqual({
      id: 'form.err.id',
      name: 'form.err.name',
      user: 'form.err.user',
      basePort: 'form.err.port',
      fps: 'form.err.fps',
      resolution: 'form.err.resolution',
      bitrate: 'form.err.bitrate',
      fecPercent: 'form.err.fec',
      minFps: 'form.err.minFps',
    })
  })

  it('rejects ids that already exist', () => {
    expect(validateDeskConfig(ok, ['desk-1']).id).toBe('form.err.idTaken')
  })
})

describe('i18n', () => {
  it('defaults to Russian', () => {
    expect(locale.value).toBe('ru')
    expect(t('nav.desks')).toBe('Столы')
  })

  it('interpolates parameters', () => {
    expect(translate('en', 'desk.notFound', { id: 'x' })).toBe('Desk x not found')
  })

  it('falls back to English for keys missing in Russian', () => {
    const key = 'nav.desks' as MessageKey
    const saved = ru[key]
    delete ru[key]
    try {
      expect(translate('ru', key)).toBe(en[key])
    } finally {
      ru[key] = saved
    }
  })

  it('has no Russian keys that English lacks and no unused placeholders mismatch', () => {
    for (const [k, v] of Object.entries(ru)) {
      expect(k in en, k).toBe(true)
      const ph = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort()
      expect(ph(v), k).toEqual(ph(en[k as MessageKey]))
    }
  })
})
