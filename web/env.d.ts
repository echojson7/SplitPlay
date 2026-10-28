// SPDX-License-Identifier: MIT
/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the Web API, default `/api/v1` (same origin as the SPA). */
  readonly VITE_API_BASE?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
