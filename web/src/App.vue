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
      <RouterLink to="/" class="brand" :aria-label="t('app.title')">
        <BrandMark :size="26" />
        <span class="wordmark">SPLITPLAY</span>
      </RouterLink>
      <nav class="tabs" aria-label="main">
        <RouterLink to="/" :class="{ active: section === 'library' }">{{ t('nav.library') }}</RouterLink>
        <RouterLink to="/settings" :class="{ active: section === 'settings' }">{{ t('nav.settings') }}</RouterLink>
      </nav>
      <span class="spacer" />
      <RouterLink to="/diagnostics" :class="['diag-link', { active: section === 'diagnostics' }]">{{ t('nav.diagnostics') }}</RouterLink>
      <button
        v-if="s.session?.authenticated && !s.session.setupRequired"
        type="button"
        class="btn-ghost logout"
        :title="t('nav.logout')"
        :aria-label="t('nav.logout')"
        @click="logout"
      >
        <AppIcon name="logout" :size="18" />
      </button>
    </header>

    <div v-if="!s.online" class="strip bad" role="alert">{{ t('app.offline') }}</div>
    <div v-else-if="!bare && s.session?.setupRequired" class="strip warn">
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
  display: flex;
  align-items: center;
  gap: 28px;
  height: 56px;
  padding: 0 24px;
  background: var(--topbar);
  box-shadow: 0 2px 12px rgb(0 0 0 / 45%);
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  color: #dcdedf;
}

.wordmark {
  font-size: 17px;
  font-weight: 700;
  letter-spacing: 0.14em;
}

.brand:hover {
  color: #fff;
}

.tabs {
  display: flex;
  align-self: stretch;
  gap: 4px;
}

.tabs a {
  display: flex;
  align-items: center;
  padding: 0 14px;
  font-size: 15px;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: #dcdedf;
  border-bottom: 3px solid transparent;
  border-top: 3px solid transparent;
  transition:
    color 0.15s ease,
    border-color 0.15s ease;
}

.tabs a:hover {
  color: #fff;
}

.tabs a.active {
  color: var(--accent);
  border-bottom-color: var(--accent);
}

.diag-link {
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--muted);
}

.diag-link:hover,
.diag-link.active {
  color: var(--accent);
}

.logout {
  width: 36px;
  height: 36px;
  padding: 0;
  margin-right: -8px;
}

.strip {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 14px;
  padding: 7px 16px;
  font-size: 13px;
}

.strip.bad {
  background: rgb(226 83 63 / 85%);
  color: #fff;
}

.strip.warn {
  background: rgb(229 177 67 / 12%);
  color: #f0c869;
}

.strip.warn a {
  font-weight: 700;
  color: #fff;
}

.page {
  max-width: 1320px;
  margin: 0 auto;
  padding: 28px 32px 48px;
}

@media (width <= 720px) {
  .topbar {
    gap: 14px;
    padding: 0 16px;
  }

  .wordmark {
    display: none;
  }

  .tabs a {
    padding: 0 8px;
    font-size: 13px;
  }

  .page {
    padding: 20px 16px 40px;
  }
}
</style>
