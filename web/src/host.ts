// SPDX-License-Identifier: MIT
// The tray app hosts the UI in WebView2 and signs it in through the service's admin pipe (web-api.md).

interface TrayHost {
  postMessage(message: unknown): void
}

/** The tray app hosting this page, or undefined in a plain browser. */
export function trayHost(): TrayHost | undefined {
  return (globalThis as { chrome?: { webview?: TrayHost } }).chrome?.webview
}

/** Minimum time between two sign-in requests to the tray. */
export const TRAY_SIGN_IN_INTERVAL_MS = 10_000

let lastTraySignIn = -Infinity

/**
 * Asks the tray to sign this page in through the admin pipe (it reloads the page when that works). At most once per
 * TRAY_SIGN_IN_INTERVAL_MS; false in a plain browser or when throttled.
 */
export function requestTraySignIn(now: number = Date.now()): boolean {
  const host = trayHost()
  if (!host || now - lastTraySignIn < TRAY_SIGN_IN_INTERVAL_MS) return false
  lastTraySignIn = now
  host.postMessage('auth')
  return true
}

/** Tests: forget the last request. */
export function resetTraySignIn(): void {
  lastTraySignIn = -Infinity
}
