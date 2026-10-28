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
    <aside v-if="!bare" class="navpane">
      <RouterLink to="/" class="appname" :aria-label="t('app.title')">
        <BrandMark :size="16" />
        <span class="appname-text">SplitPlay</span>
      </RouterLink>
      <nav class="nav" aria-label="main">
        <RouterLink to="/" :class="{ active: section === 'library' }">
          <AppIcon name="monitor" :size="16" /><span>{{ t('nav.library') }}</span>
        </RouterLink>
        <RouterLink to="/settings" :class="{ active: section === 'settings' }">
          <AppIcon name="gear" :size="16" /><span>{{ t('nav.settings') }}</span>
        </RouterLink>
        <RouterLink to="/diagnostics" :class="{ active: section === 'diagnostics' }">
          <AppIcon name="activity" :size="16" /><span>{{ t('nav.diagnostics') }}</span>
        </RouterLink>
      </nav>
      <span class="spacer" />
      <button
        v-if="s.session?.authenticated && !s.session.setupRequired"
        type="button"
        class="navitem logout"
        :title="t('nav.logout')"
        @click="logout"
      >
        <AppIcon name="logout" :size="16" /><span>{{ t('nav.logout') }}</span>
      </button>
    </aside>

    <main :class="{ page: !bare }">
      <div v-if="!s.online" class="inline-msg strip" role="alert">{{ t('app.offline') }}</div>
      <div v-else-if="!bare && s.session?.setupRequired" class="inline-msg warn strip">
        <span class="spacer">{{ t('app.noPassword') }}</span>
        <RouterLink to="/settings" class="btn">{{ t('app.setPassword') }}</RouterLink>
      </div>
      <RouterView />
    </main>
    <ToastHost />
  </div>
</template>

<style scoped>
.shell {
  display: grid;
  grid-template-columns: 280px minmax(0, 1fr);
  min-height: 100vh;
}

.shell.bare {
  display: block;
}

.navpane {
  position: sticky;
  top: 0;
  height: 100vh;
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 4px 0 8px;
}

.appname {
  display: flex;
  align-items: center;
  gap: 12px;
  height: 48px;
  padding: 0 16px;
  margin-bottom: 4px;
  color: var(--text);
  font-size: 12px;
}

.nav {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.nav > a,
.navitem {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: flex-start;
  gap: 16px;
  height: 36px;
  margin: 0 4px;
  padding: 0 12px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text);
  font-size: 14px;
  text-align: left;
}

.nav > a:hover,
.navitem:hover:not(:disabled) {
  background: var(--subtle-hover);
}

.nav > a.active {
  background: var(--nav-selected);
}

.nav > a.active::before {
  content: '';
  position: absolute;
  left: 0;
  top: 50%;
  width: 3px;
  height: 16px;
  margin-top: -8px;
  border-radius: 2px;
  background: var(--accent);
}

.page {
  width: 100%;
  max-width: 1040px;
  padding: 28px 40px 40px;
}

.strip {
  margin-bottom: 16px;
}

@media (width <= 640px) {
  .shell {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: auto 1fr;
  }

  .navpane {
    z-index: 20;
    height: auto;
    flex-direction: row;
    align-items: center;
    gap: 2px;
    padding: 4px 8px;
    background: var(--bg);
    border-bottom: 1px solid var(--divider);
  }

  .appname {
    height: 40px;
    padding: 0 8px;
    margin: 0;
  }

  .appname-text,
  .nav > a .icon,
  .logout span {
    display: none;
  }

  .nav {
    flex-direction: row;
    gap: 2px;
  }

  .nav > a,
  .navitem {
    height: 40px;
    margin: 0;
    padding: 0 10px;
  }

  .nav > a.active::before {
    left: 50%;
    top: auto;
    bottom: 0;
    width: 16px;
    height: 3px;
    margin: 0 0 0 -8px;
  }

  .page {
    padding: 16px 16px 32px;
  }
}
</style>
