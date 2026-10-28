<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { api } from './api/client'
import AppIcon from './components/AppIcon.vue'
import BrandMark from './components/BrandMark.vue'
import ToastHost from './components/ToastHost.vue'
import { t } from './i18n'
import { store } from './store'

const route = useRoute()
const router = useRouter()
const bare = computed(() => route.meta.public === true)
const section = computed(() => route.meta.section ?? 'library')
const s = store.state

let healthTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  void store.refreshHealth()
  healthTimer = setInterval(() => void store.refreshHealth(), 10000)
})
onUnmounted(() => clearInterval(healthTimer))

async function logout(): Promise<void> {
  try {
    await api.logout()
  } finally {
    store.stopLive()
    s.session = null
    await router.push({ name: 'login' })
  }
}
</script>

<template>
  <div :class="['shell', { bare }]">
    <header v-if="!bare" class="topbar">
      <div class="topbar-inner">
        <RouterLink to="/" class="brand" :aria-label="t('app.title')">
          <BrandMark :size="28" />
          <span class="wordmark">SplitPlay</span>
        </RouterLink>
        <nav class="tabs segmented" aria-label="main">
          <RouterLink to="/" :class="{ active: section === 'library' }">{{ t('nav.library') }}</RouterLink>
          <RouterLink to="/settings" :class="{ active: section === 'settings' }">{{ t('nav.settings') }}</RouterLink>
        </nav>
        <span class="spacer" />
        <RouterLink to="/diagnostics" :class="['diag-link', { active: section === 'diagnostics' }]">{{ t('nav.diagnostics') }}</RouterLink>
        <button
          v-if="s.session?.authenticated && !s.session.setupRequired"
          type="button"
          class="btn-icon btn-ghost logout"
          :title="t('nav.logout')"
          :aria-label="t('nav.logout')"
          @click="logout"
        >
          <AppIcon name="logout" :size="18" />
        </button>
      </div>
    </header>

    <div v-if="!s.online" class="strip bad" role="alert"><span class="strip-dot" />{{ t('app.offline') }}</div>
    <div v-else-if="!bare && s.session?.setupRequired" class="strip warn">
      <span class="strip-dot" />
      <span>{{ t('app.noPassword') }}</span>
      <RouterLink to="/settings">{{ t('app.setPassword') }}</RouterLink>
    </div>

    <main :class="{ page: !bare }">
      <RouterView />
    </main>
    <ToastHost />
  </div>
</template>

<style scoped>
.topbar {
  position: sticky;
  top: 0;
  z-index: 20;
  background: color-mix(in srgb, var(--card) 78%, transparent);
  backdrop-filter: saturate(1.6) blur(14px);
  border-bottom: 1px solid var(--border);
}

.topbar-inner {
  display: flex;
  align-items: center;
  gap: 24px;
  max-width: 1320px;
  height: 60px;
  margin: 0 auto;
  padding: 0 32px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--text);
}

.wordmark {
  font-size: 16px;
  font-weight: 700;
  letter-spacing: -0.01em;
}

.brand:hover {
  color: var(--text);
  opacity: 0.85;
}

.tabs > a {
  min-width: 96px;
}

.diag-link {
  display: inline-flex;
  align-items: center;
  height: 32px;
  padding: 0 12px;
  border-radius: 999px;
  font-size: 13px;
  font-weight: 500;
  color: var(--muted);
  transition:
    color 0.15s var(--ease),
    background 0.15s var(--ease);
}

.diag-link:hover {
  color: var(--text);
  background: var(--fill);
}

.diag-link.active {
  color: var(--primary-ink);
  background: var(--primary-soft);
}

.logout {
  width: 36px;
  height: 36px;
  margin-right: -8px;
  color: var(--muted);
}

.strip {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 10px;
  padding: 9px 16px;
  font-size: 13px;
  font-weight: 500;
  border-bottom: 1px solid var(--border);
}

.strip-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: currentcolor;
  flex: none;
}

.strip.bad {
  background: var(--danger-soft);
  color: var(--danger-ink);
}

.strip.warn {
  background: var(--warning-soft);
  color: var(--warning-ink);
}

.strip.warn a {
  font-weight: 600;
  color: var(--primary);
}

.page {
  max-width: 1320px;
  margin: 0 auto;
  padding: 32px 32px 56px;
}

@media (width <= 720px) {
  .topbar-inner {
    gap: 12px;
    padding: 0 16px;
  }

  .wordmark {
    display: none;
  }

  .tabs > a {
    min-width: 0;
    padding: 0 12px;
  }

  .diag-link {
    padding: 0 8px;
  }

  .page {
    padding: 20px 16px 40px;
  }
}
</style>
