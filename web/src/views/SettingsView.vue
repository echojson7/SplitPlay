<!-- SPDX-License-Identifier: MIT -->
<!-- Settings: admin password and language up front; service options and backup under «Advanced». -->
<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, ApiError } from '../api/client'
import type { HostSettings } from '../api/types'
import AppIcon from '../components/AppIcon.vue'
import { errorText } from '../composables'
import { fmtDuration, locale, setLocale, t, type Locale } from '../i18n'
import { store } from '../store'
import { toast } from '../toast'

const s = store.state
const host = ref<HostSettings | null>(null)
const unsupported = ref(false)
const busy = ref(false)

const COMPOSE_INTERVALS = [2, 3, 4, 5, 7, 8, 16]
const LOG_LEVELS = ['trace', 'debug', 'info', 'warn', 'error']
const LANGUAGES: ReadonlyArray<{ value: Locale; label: string }> = [
  { value: 'ru', label: 'Русский' },
  { value: 'en', label: 'English' },
]

const currentPassword = ref('')
const newPassword = ref('')
const repeatPassword = ref('')
const passwordError = ref<string | null>(null)
const includeSecrets = ref(false)
const exportPassword = ref('')
const importInput = ref<HTMLInputElement | null>(null)

onMounted(async () => {
  void store.refreshHealth()
  try {
    host.value = await api.getHostSettings()
  } catch (e) {
    if (e instanceof ApiError && (e.status === 404 || e.status === 405)) unsupported.value = true
    else toast(errorText(e), 'error')
  }
})

async function run(fn: () => Promise<void>, done: string): Promise<void> {
  busy.value = true
  try {
    await fn()
    if (done) toast(done)
  } catch (e) {
    toast(errorText(e), 'error')
  } finally {
    busy.value = false
  }
}

function saveHost(): Promise<void> {
  return run(async () => {
    if (host.value) host.value = await api.updateHostSettings({ ...host.value })
  }, t('common.saved'))
}

async function changePassword(): Promise<void> {
  passwordError.value = null
  if (newPassword.value.length < 8) return void (passwordError.value = t('setup.tooShort'))
  if (newPassword.value !== repeatPassword.value) return void (passwordError.value = t('setup.mismatch'))
  await run(async () => {
    await api.setPassword(newPassword.value, s.session?.setupRequired ? undefined : currentPassword.value)
    currentPassword.value = ''
    newPassword.value = ''
    repeatPassword.value = ''
    await store.refreshSession()
  }, t('settings.passwordChanged'))
}

function onLanguage(e: Event): void {
  setLocale((e.target as HTMLSelectElement).value as Locale)
}

function exportConfig(): Promise<void> {
  return run(async () => {
    const blob = await api.exportConfig(includeSecrets.value, exportPassword.value || undefined)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `splitplay-config-${new Date().toISOString().slice(0, 10)}.zip`
    a.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }, '')
}

function importConfig(): Promise<void> {
  const file = importInput.value?.files?.[0]
  if (!file) return Promise.resolve()
  return run(async () => {
    await api.importConfig(file)
    await store.refreshDesks()
    if (importInput.value) importInput.value.value = ''
  }, t('settings.imported'))
}
</script>

<template>
  <section class="settings-page">
    <h1>{{ t('settings.title') }}</h1>

    <div class="grid">
      <form class="panel" novalidate @submit.prevent="changePassword">
        <h2>{{ t('settings.password') }}</h2>
        <div class="stack">
          <div v-if="!s.session?.setupRequired" class="field">
            <label for="cur-pw">{{ t('settings.currentPassword') }}</label>
            <input id="cur-pw" v-model="currentPassword" type="password" autocomplete="current-password" />
          </div>
          <div class="field">
            <label for="new-pw">{{ t('settings.newPassword') }}</label>
            <input id="new-pw" v-model="newPassword" type="password" autocomplete="new-password" />
          </div>
          <div class="field">
            <label for="new-pw2">{{ t('settings.repeatPassword') }}</label>
            <input id="new-pw2" v-model="repeatPassword" type="password" autocomplete="new-password" />
          </div>
          <p v-if="passwordError" class="err" role="alert">{{ passwordError }}</p>
          <div class="actions">
            <button type="submit" class="btn-primary" :disabled="busy">
              {{ s.session?.setupRequired ? t('settings.setPassword') : t('settings.changePassword') }}
            </button>
          </div>
        </div>
      </form>

      <div class="panel">
        <h2>{{ t('settings.language') }}</h2>
        <div class="field">
          <label for="lang" class="sr-only">{{ t('settings.language') }}</label>
          <select id="lang" :value="locale" @change="onLanguage">
            <option v-for="l in LANGUAGES" :key="l.value" :value="l.value">{{ l.label }}</option>
          </select>
        </div>
        <p v-if="s.health" class="about muted">
          {{ t('settings.version', { version: s.health.version }) }} ·
          {{ t('settings.uptime', { time: fmtDuration(s.health.uptimeSeconds) }) }}
        </p>
      </div>
    </div>

    <details class="more">
      <summary><AppIcon name="gear" :size="16" />{{ t('common.more') }}<span class="spacer" /><AppIcon class="chev" name="chevronDown" :size="16" /></summary>
      <div class="more-body">
        <h3 class="label sub">{{ t('settings.service') }}</h3>
        <form v-if="host" class="stack" @submit.prevent="saveHost">
          <div class="fields">
            <div class="field">
              <label for="h-port">{{ t('settings.webPort') }}</label>
              <input id="h-port" v-model.number="host.webPort" type="number" min="1" max="65535" />
            </div>
            <div class="field">
              <label for="h-bps">{{ t('settings.basePortStart') }}</label>
              <input id="h-bps" v-model.number="host.basePortStart" type="number" min="1029" max="65514" />
            </div>
            <div class="field">
              <label for="h-stride">{{ t('settings.basePortStride') }}</label>
              <input id="h-stride" v-model.number="host.basePortStride" type="number" min="27" max="1000" />
            </div>
            <div class="field">
              <label for="h-comp">{{ t('settings.composeInterval') }}</label>
              <select id="h-comp" v-model.number="host.composeIntervalMs">
                <option v-for="ms in COMPOSE_INTERVALS" :key="ms" :value="ms">
                  {{ t('settings.composeHz', { ms, hz: Math.round(1000 / ms) }) }}
                </option>
              </select>
            </div>
            <div class="field">
              <label for="h-log">{{ t('settings.logLevel') }}</label>
              <select id="h-log" v-model="host.logLevel">
                <option v-for="l in LOG_LEVELS" :key="l" :value="l">{{ l }}</option>
              </select>
            </div>
          </div>
          <label class="switch">
            <input id="h-lan" v-model="host.lanAccess" type="checkbox" />
            {{ t('settings.lanAccess') }}
          </label>
          <div class="actions">
            <button type="submit" class="btn-primary" :disabled="busy">{{ t('common.save') }}</button>
          </div>
        </form>
        <p v-else-if="unsupported" class="muted">{{ t('common.notSupported') }}</p>
        <p v-else class="muted">{{ t('common.loading') }}</p>

        <h3 class="label sub">{{ t('settings.backup') }}</h3>
        <div class="backup">
          <label class="switch">
            <input v-model="includeSecrets" type="checkbox" />
            {{ t('settings.exportSecrets') }}
          </label>
          <input
            v-if="includeSecrets"
            v-model="exportPassword"
            type="password"
            autocomplete="new-password"
            :placeholder="t('settings.exportPassword')"
            :aria-label="t('settings.exportPassword')"
          />
          <button type="button" :disabled="busy || (includeSecrets && !exportPassword)" @click="exportConfig">
            {{ t('settings.export') }}
          </button>
        </div>
        <div class="backup">
          <input ref="importInput" class="file" type="file" accept=".zip,application/zip" :aria-label="t('settings.import')" />
          <button type="button" :disabled="busy" @click="importConfig">{{ t('settings.import') }}</button>
        </div>
      </div>
    </details>
  </section>
</template>

<style scoped>
.settings-page {
  display: flex;
  flex-direction: column;
  gap: 20px;
  max-width: 920px;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  gap: 16px;
  align-items: start;
}

.stack {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.err {
  margin: 0;
}

.actions {
  display: flex;
  justify-content: flex-end;
}

.actions button {
  min-width: 160px;
  height: 38px;
}

.about {
  margin: 18px 0 0;
  font-size: 12px;
}

.sub {
  margin: 14px 0 12px;
}

.backup {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
  margin-bottom: 10px;
}

.file {
  height: auto;
  padding: 6px;
  flex: 1;
  min-width: 200px;
}
</style>
