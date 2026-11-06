// SPDX-License-Identifier: MIT
// HDR of a desk's display: the "HDR" row and the stream line when a client asked for HDR.
import { beforeEach, describe, expect, it } from 'vitest'
import type { DeskStream } from '../src/api/types'
import { hdrLabel, streamSummary } from '../src/desk'
import { locale } from '../src/i18n'

const stream: DeskStream = {
  client: 'Ноутбук Ани',
  address: '192.168.1.37',
  width: 2560,
  height: 1440,
  fps: 120,
  codec: 'hevc',
  bitrateKbps: 0,
  hdr: true,
}

describe('HDR', () => {
  beforeEach(() => {
    locale.value = 'ru'
  })

  it('names the display state and hides an unknown one', () => {
    expect(hdrLabel({ state: 'enabled' })).toBe('Включён')
    expect(hdrLabel({ state: 'off' })).toBe('Выключен в Windows')
    expect(hdrLabel({ state: 'unsupported' })).toBe('Недоступен')
    expect(hdrLabel({ state: 'unknown' })).toBeNull()
    expect(hdrLabel({ state: 'later' })).toBeNull()
    expect(hdrLabel(null)).toBeNull()
    expect(hdrLabel(undefined)).toBeNull()
  })

  it('says SDR when a client asks for HDR that the desk cannot give', () => {
    expect(streamSummary(stream, { state: 'unsupported' })).toBe('2560×1440 · 120 FPS · HEVC · SDR вместо HDR')
    expect(streamSummary(stream, { state: 'off' })).toBe('2560×1440 · 120 FPS · HEVC · SDR вместо HDR')
    expect(streamSummary(stream, { state: 'enabled' })).toBe('2560×1440 · 120 FPS · HEVC · HDR')
    // Unknown (an older service): what the client asked for.
    expect(streamSummary(stream)).toBe('2560×1440 · 120 FPS · HEVC · HDR')
    expect(streamSummary({ ...stream, hdr: false }, { state: 'unsupported' })).toBe('2560×1440 · 120 FPS · HEVC')
  })
})
