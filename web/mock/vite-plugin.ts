// SPDX-License-Identifier: MIT
// Vite dev-server plugin that serves the in-memory mock API under /api/v1 (`npm run dev:mock`).

import type { IncomingMessage, ServerResponse } from 'node:http'
import type { Plugin } from 'vite'
import { MockBackend } from './backend.ts'

async function readBody(req: IncomingMessage): Promise<Buffer> {
  const chunks: Buffer[] = []
  for await (const chunk of req) chunks.push(chunk as Buffer)
  return Buffer.concat(chunks)
}

export function mockApiPlugin(apiBase = '/api/v1'): Plugin {
  return {
    name: 'splitplay-mock-api',
    apply: 'serve',
    configureServer(server) {
      const backend = new MockBackend()
      server.config.logger.info(`  SplitPlay mock API on ${apiBase} (no service needed)`)
      server.middlewares.use(apiBase, (req: IncomingMessage, res: ServerResponse) => {
        void (async () => {
          const url = new URL(req.url ?? '/', 'http://localhost')
          const raw = await readBody(req)
          let body: unknown
          const type = req.headers['content-type'] ?? ''
          if (raw.length > 0 && type.includes('application/json')) {
            try {
              body = JSON.parse(raw.toString('utf8'))
            } catch {
              res.statusCode = 400
              res.setHeader('Content-Type', 'application/json')
              res.end(JSON.stringify({ error: { code: 'bad_json', message: 'Body is not valid JSON' } }))
              return
            }
          }
          const headers: Record<string, string | undefined> = {}
          for (const [k, v] of Object.entries(req.headers)) headers[k] = Array.isArray(v) ? v.join(', ') : v
          const out = backend.handle({
            method: req.method ?? 'GET',
            path: url.pathname,
            query: Object.fromEntries(url.searchParams),
            headers,
            body,
          })
          res.statusCode = out.status
          for (const [k, v] of Object.entries(out.headers ?? {})) res.setHeader(k, v)
          if (out.bytes) {
            res.end(Buffer.from(out.bytes))
          } else if (out.body !== undefined && out.status !== 204) {
            res.setHeader('Content-Type', 'application/json')
            res.end(JSON.stringify(out.body))
          } else {
            res.end()
          }
        })()
      })
    },
  }
}
