// SPDX-License-Identifier: MIT
import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'
import { mockApiPlugin } from './mock/vite-plugin.ts'

// Modes:
//   npm run dev       -> proxies /api to a running SplitPlay service on 127.0.0.1:38299
//   npm run dev:mock  -> serves the in-memory mock API (mock/backend.ts), no service needed
//   npm run build     -> web/dist, copied into the service wwwroot at packaging time
export default defineConfig(({ mode }) => ({
  // Relative asset URLs so the bundle works from any wwwroot path.
  base: './',
  plugins: [vue(), ...(mode === 'mock' ? [mockApiPlugin()] : [])],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    strictPort: false,
    proxy:
      mode === 'mock'
        ? undefined
        : {
            '/api': { target: 'http://127.0.0.1:38299', changeOrigin: false, ws: true },
          },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    target: 'es2022',
  },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.spec.ts'],
    restoreMocks: true,
  },
}))
