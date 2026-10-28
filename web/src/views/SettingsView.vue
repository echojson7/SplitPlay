<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, ApiError } from '../api/client'
import type { HostSettings } from '../api/types'
import { errorText } from '../composables'
import { fmtDuration, t } from '../i18n'
import { store } from '../store'

const s = store.state
const host = ref<HostSettings | null>(null)
const unsupported = ref(false)
const error = ref<string | null>(null)
const info = ref<string | null>(null)
const busy = ref(false)

const COMPOSE_INTERVALS = [2, 3, 4, 5, 7, 8, 16]
const LOG_LEVELS = ['trace', 'debug', 'info', 'warn', 'error']

const currentPassword = ref('')
const newPassword = ref('')
const includeSecrets = ref(false)
const exportPassword = ref('')
const importInput = ref<HTMLInputElement | null>(null)

onMounted(async () => {
  void store.refreshHealth()
  try {
    host.value = await api.getHostSettings()
  } catch (e) {
    if (e instanceof ApiError && (e.status === 404 || e.status === 405)) unsupported.value = true
    else error.value = errorText(e)
  }
})

async function run(fn: () => Promise<void>, done: string): Promise<void> {
  busy.value = true
  error.value = null
  info.value = null
  try {
    await fn()
    info.value = done
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = false
  }
}

function saveHost(): Promise<void> {
  return run(async () => {
    if (host.value) host.value = await api.updateHostSettings({ ...host.value })
  }, t('common.saved'))
}

function changePassword(): Promise<void> {
  return run(async () => {
    if (newPassword.value.length < 8) throw new Error(t('setup.tooShort'))
    await api.setPassword(newPassword.value, s.session?.setupRequired ? undefined : currentPassword.value)
    currentPassword.value = ''
    newPassword.value = ''
    await store.refreshSession()
  }, t('settings.passwordChanged'))
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
  <section>
    <h1>{{ t('settings.title') }}</h1>
    <p v-if="error" class="notice bad" role="alert">{{ t('common.error', { message: error }) }}</p>
    <p v-if="info" class="notice ok" role="status">{{ info }}</p>

    <form v-if="host" class="card" @submit.prevent="saveHost">
      <div class="grid-2">
        <div class="field">
          <label for="h-port">{{ t('settings.webPort') }}</label>
          <input id="h-port" v-model.number="host.webPort" type="number" min="1" max="65535" />
        </div>
        <div class="field">
          <label for="h-log">{{ t('settings.logLevel') }}</label>
          <select id="h-log" v-model="host.logLevel">
            <option v-for="l in LOG_LEVELS" :key="l" :value="l">{{ l }}</option>
          </select>
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
      </div>
      <div class="check">
        <input id="h-lan" v-model="host.lanAccess" type="checkbox" />
        <label for="h-lan">{{ t('settings.lanAccess') }}</label>
      </div>
      <p class="muted">{{ t('settings.restartNote') }}</p>
      <div class="row">
        <div class="spacer" />
        <button type="submit" class="primary" :disabled="busy">{{ t('common.save') }}</button>
      </div>
    </form>
    <p v-else-if="unsupported" class="card muted">{{ t('common.notSupported') }}</p>
    <p v-else class="muted">{{ t('common.loading') }}</p>

    <h2>{{ t('settings.password') }}</h2>
    <form class="card" @submit.prevent="changePassword">
      <div class="grid-2">
        <div v-if="!s.session?.setupRequired" class="field">
          <label for="cur-pw">{{ t('settings.currentPassword') }}</label>
          <input id="cur-pw" v-model="currentPassword" type="password" autocomplete="current-password" />
        </div>
        <div class="field">
          <label for="new-pw">{{ t('settings.newPassword') }}</label>
          <input id="new-pw" v-model="newPassword" type="password" autocomplete="new-password" />
        </div>
      </div>
      <div class="row">
        <div class="spacer" />
        <button type="submit" :disabled="busy">{{ t('settings.changePassword') }}</button>
      </div>
    </form>

    <h2>{{ t('settings.config') }}</h2>
    <div class="card">
      <div class="row">
        <label class="check"><input v-model="includeSecrets" type="checkbox" /> {{ t('settings.exportSecrets') }}</label>
        <input
          v-if="includeSecrets"
          v-model="exportPassword"
          type="password"
          autocomplete="new-password"
          :placeholder="t('settings.exportPassword')"
          :aria-label="t('settings.exportPassword')"
        />
        <div class="spacer" />
        <button type="button" :disabled="busy || (includeSecrets && !exportPassword)" @click="exportConfig">
          {{ t('settings.export') }}
        </button>
      </div>
      <div class="row import">
        <input ref="importInput" type="file" accept=".zip,application/zip" :aria-label="t('settings.import')" />
        <div class="spacer" />
        <button type="button" :disabled="busy" @click="importConfig">{{ t('settings.import') }}</button>
      </div>
    </div>

    <h2>{{ t('settings.about') }}</h2>
    <div class="card">
      <dl v-if="s.health" class="props">
        <dt>SplitPlay</dt>
        <dd>{{ s.health.version }}</dd>
        <dt>{{ t('settings.uptime') }}</dt>
        <dd>{{ fmtDuration(s.health.uptimeSeconds) }}</dd>
      </dl>
      <p v-else class="muted">{{ t('app.offline') }}</p>
    </div>
  </section>
</template>

<style scoped>
.import {
  margin-top: 0.75rem;
}

.check {
  margin: 0;
}
</style>
