// SPDX-License-Identifier: MIT
import { createRouter, createWebHashHistory, type RouteRecordRaw, type Router } from 'vue-router'
import { api } from './api/client'
import { store } from './store'

// Hash history: the service serves index.html from wwwroot and needs no SPA fallback routing.
export const routes: RouteRecordRaw[] = [
  { path: '/', name: 'desks', component: () => import('./views/DesksView.vue') },
  { path: '/desks/new', name: 'desk-new', component: () => import('./views/DeskFormView.vue') },
  { path: '/desks/:id', name: 'desk', component: () => import('./views/DeskDetailsView.vue'), props: true },
  { path: '/desks/:id/edit', name: 'desk-edit', component: () => import('./views/DeskFormView.vue'), props: true },
  { path: '/metrics', name: 'metrics', component: () => import('./views/MetricsView.vue') },
  { path: '/logs', name: 'logs', component: () => import('./views/LogsView.vue') },
  { path: '/settings', name: 'settings', component: () => import('./views/SettingsView.vue') },
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
    if (s.authenticated && to.meta.public) return { name: 'desks' }
    return true
  })

  api.onUnauthorized = () => {
    if (store.state.session) store.state.session = { ...store.state.session, authenticated: false }
    const current = router.currentRoute.value
    if (!current.meta.public) void router.push({ name: 'login', query: { redirect: current.fullPath } })
  }

  return router
}
