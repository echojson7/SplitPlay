// SPDX-License-Identifier: MIT
// The tray app hosts the UI in WebView2 and signs it in through the service's admin pipe (web-api.md).

interface TrayHost {
  postMessage(message: unknown): void
}

/** The tray app hosting this page, or undefined in a plain browser. */
export function trayHost(): TrayHost | undefined {
  return (globalThis as { chrome?: { webview?: TrayHost } }).chrome?.webview
}
