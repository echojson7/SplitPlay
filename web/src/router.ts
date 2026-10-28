// SPDX-License-Identifier: MIT
import { createRouter, createWebHashHistory, type RouteRecordRaw, type Router } from 'vue-router'
import { api } from './api/client'
import { store } from './store'

declare module 'vue-router' {
  interface RouteMeta {
    /** Reachable without a session (login, setup). */
    public?: boolean
    setup?: boolean
    /** Top bar tab to highlight. */
    section?: 'library' | 'settings' | 'diagnostics'
  }
}

const library = () => import('./views/LibraryView.vue')
const diagnostics = () => import('./views/DiagnosticsView.vue')

// Hash history: the service serves index.html from wwwroot and needs no SPA fallback routing.
export const routes: RouteRecordRaw[] = [
  { path: '/', name: 'library', component: library, meta: { section: 'library' } },
  { path: '/desks/new', name: 'desk-new', component: library, meta: { section: 'library' } },
  { path: '/desks/:id', name: 'desk', component: () => import('./views/DeskView.vue'), props: true, meta: { section: 'library' } },
  { path: '/desks/:id/edit', redirect: (to) => ({ name: 'desk', params: { id: to.params.id } }) },
  { path: '/settings', name: 'settings', component: () => import('./views/SettingsView.vue'), meta: { section: 'settings' } },
  { path: '/diagnostics', redirect: '/diagnostics/metrics' },
  {
    path: '/diagnostics/metrics',
    name: 'metrics',
    component: diagnostics,
    props: { tab: 'metrics' },
    meta: { section: 'diagnostics' },
  },
  { path: '/diagnostics/logs', name: 'logs', component: diagnostics, props: { tab: 'logs' }, meta: { section: 'diagnostics' } },
  { path: '/metrics', redirect: '/diagnostics/metrics' },
  { path: '/logs', redirect: '/diagnostics/logs' },
  { path: '/login', name: 'login', component: () => import('./views/LoginView.vue'), meta: { public: true } },
  { path: '/setup', name: 'setup', component: () => import('./views/LoginView.vue'), meta: { public: true, setup: true } },
  { path: '/:pathMatch(.*)*', redirect: '/' },
]

export const SETUP_SKIPPED_KEY = 'splitplay.setupSkipped'

function setupSkipped(): boolean {
  try {
    return globalThis.sessionStorage?.getItem(SETUP_SKIPPED_KEY) === '1'
  } catch {
    return false
  }
}

export function createAppRouter(): Router {
  const router = createRouter({ history: createWebHashHistory(), routes })

  router.beforeEach(async (to) => {
    if (!store.state.session) {
      try {
        await store.refreshSession()
      } catch {
        // Service offline or old: let the page load and show the offline banner.
        return true
      }
    }
    const s = store.state.session!
    if (s.setupRequired) {
      if (to.name !== 'setup' && !setupSkipped()) return { name: 'setup', query: { redirect: to.fullPath } }
      return true
    }
    if (to.meta.setup) return { name: s.authenticated ? 'settings' : 'login' }
    if (!s.authenticated && !to.meta.public) return { name: 'login', query: { redirect: to.fullPath } }
    if (s.authenticated && to.meta.public) return { name: 'library' }
    return true
  })

  api.onUnauthorized = () => {
    if (store.state.session) store.state.session = { ...store.state.session, authenticated: false }
    const current = router.currentRoute.value
    if (!current.meta.public) void router.push({ name: 'login', query: { redirect: current.fullPath } })
  }

  return router
}
