<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { api } from './api/client'
import { locale, setLocale, t, type Locale } from './i18n'
import { store } from './store'

const route = useRoute()
const router = useRouter()
const bare = computed(() => route.meta.public === true)
const s = store.state

let healthTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  void store.refreshHealth()
  healthTimer = setInterval(() => void store.refreshHealth(), 10000)
})
onUnmounted(() => clearInterval(healthTimer))

function onLocale(e: Event): void {
  setLocale((e.target as HTMLSelectElement).value as Locale)
}

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
    <header v-if="!bare" class="top">
      <RouterLink to="/" class="brand">
        <strong>{{ t('app.title') }}</strong>
        <span class="muted">{{ t('app.subtitle') }}</span>
      </RouterLink>
      <nav aria-label="main">
        <RouterLink to="/">{{ t('nav.desks') }}</RouterLink>
        <RouterLink to="/metrics">{{ t('nav.metrics') }}</RouterLink>
        <RouterLink to="/logs">{{ t('nav.logs') }}</RouterLink>
        <RouterLink to="/settings">{{ t('nav.settings') }}</RouterLink>
      </nav>
      <div class="spacer" />
      <label class="lang">
        <span class="sr-only">{{ t('app.language') }}</span>
        <select :value="locale" :aria-label="t('app.language')" @change="onLocale">
          <option value="ru">RU</option>
          <option value="en">EN</option>
        </select>
      </label>
      <button v-if="s.session?.authenticated" type="button" @click="logout">{{ t('nav.logout') }}</button>
    </header>

    <main>
      <div v-if="!s.online" class="notice bad" role="alert">{{ t('app.offline') }}</div>
      <div v-if="!bare && s.session?.setupRequired" class="notice warn row">
        <span>{{ t('app.setupBanner') }}</span>
        <RouterLink to="/setup" class="button">{{ t('app.setupBannerAction') }}</RouterLink>
      </div>
      <RouterView />
    </main>

    <footer v-if="!bare && s.health" class="muted">
      {{ t('app.version', { version: s.health.version }) }}
    </footer>
  </div>
</template>

<style scoped>
.shell {
  max-width: 1200px;
  margin: 0 auto;
  padding: 0 16px 2rem;
}

.top {
  display: flex;
  align-items: center;
  gap: 1rem;
  flex-wrap: wrap;
  padding: 0.75rem 0;
  border-bottom: 1px solid var(--border);
  margin-bottom: 1.25rem;
}

.brand {
  display: flex;
  flex-direction: column;
  text-decoration: none;
  color: var(--text);
  line-height: 1.15;
}

.brand .muted {
  font-size: 0.78rem;
}

nav {
  display: flex;
  gap: 0.25rem;
  flex-wrap: wrap;
}

nav a {
  padding: 0.35rem 0.7rem;
  border-radius: 6px;
  color: var(--text);
  text-decoration: none;
}

nav a.router-link-exact-active,
nav a:hover {
  background: var(--surface-2);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

footer {
  margin-top: 2rem;
  font-size: 0.8rem;
}
</style>
