// SPDX-License-Identifier: MIT
// Mock of GET /desks/{id}/client-pack and its scripts: the same shape and command line as the service's ClientPack.

import type { ClientPack, ClientPlatform, Desk } from '../src/api/types.ts'

const HOST_RE = /^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}[A-Za-z0-9])?)*$/

/** The service's rule (and the wizard's): 30 Mbit/s for 1080p60 scaled by the root of the pixel rate, 10–150 Mbit/s. */
function suggestBitrateKbps(width: number, height: number, fps: number): number {
  const scale = (width * height * fps) / (1920 * 1080 * 60)
  return Math.min(150000, Math.max(10000, Math.round((30000 * Math.sqrt(scale) * 1.15) / 5000) * 5000))
}

export function isValidPackHost(host: string): boolean {
  return HOST_RE.test(host)
}

export function mockClientPack(desk: Desk, host: string): ClientPack {
  const p = desk.profile
  const bitrateKbps = Math.min(500000, Math.max(500, p.bitrateKbps || suggestBitrateKbps(p.width, p.height, p.fps)))
  const codec = p.codec ?? 'hevc'
  const address = `${host}:${desk.basePort}`
  return {
    desk: desk.id,
    host,
    port: desk.basePort,
    address,
    app: 'Desktop',
    settings: { width: p.width, height: p.height, fps: p.fps, bitrateKbps, codec, vsync: false, displayMode: 'fullscreen', videoDecoder: 'hardware', framePacing: false },
    arguments: [
      'stream', address, 'Desktop',
      '--resolution', `${p.width}x${p.height}`,
      '--fps', String(p.fps),
      '--bitrate', String(bitrateKbps),
      '--video-codec', codec === 'h264' ? 'H.264' : codec.toUpperCase(),
      '--video-decoder', 'hardware',
      '--display-mode', 'fullscreen',
      '--no-vsync',
      '--no-frame-pacing',
    ],
    scripts: [
      { platform: 'windows', fileName: `splitplay-${desk.id}.cmd` },
      { platform: 'linux', fileName: `splitplay-${desk.id}.sh` },
    ],
  }
}

/** A short stand-in for the service's script (the real one also looks for Moonlight in the usual places). */
export function mockClientScript(pack: ClientPack, platform: ClientPlatform): string {
  const args = pack.arguments.join(' ')
  return platform === 'windows'
    ? `@echo off\r\nstart "" "%ProgramFiles%\\Moonlight Game Streaming\\Moonlight.exe" ${args}\r\n`
    : `#!/bin/sh\nexec moonlight ${args}\n`
}
