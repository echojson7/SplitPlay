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
import { setTheme, themeMode, type ThemeMode } from '../theme'
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
const THEMES = [
  { value: 'light', label: 'settings.themeLight' },
  { value: 'dark', label: 'settings.themeDark' },
  { value: 'system', label: 'settings.themeSystem' },
] as const satisfies ReadonlyArray<{ value: ThemeMode; label: string }>

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

function onTheme(e: Event): void {
  setTheme((e.target as HTMLSelectElement).value as ThemeMode)
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
    <header class="page-head">
      <h1>{{ t('settings.title') }}</h1>
    </header>

    <h2 class="group-title">{{ t('settings.appearance') }}</h2>
    <div class="rows">
      <div class="srow">
        <AppIcon name="sun" :size="16" />
        <label class="text" for="theme"><span class="title">{{ t('settings.theme') }}</span></label>
        <div class="ctl">
          <select id="theme" name="theme" :value="themeMode" @change="onTheme">
            <option v-for="m in THEMES" :key="m.value" :value="m.value" :data-theme-mode="m.value">{{ t(m.label) }}</option>
          </select>
        </div>
      </div>
      <div class="srow">
        <AppIcon name="globe" :size="16" />
        <label class="text" for="lang"><span class="title">{{ t('settings.language') }}</span></label>
        <div class="ctl">
          <select id="lang" :value="locale" @change="onLanguage">
            <option v-for="l in LANGUAGES" :key="l.value" :value="l.value">{{ l.label }}</option>
          </select>
        </div>
      </div>
    </div>

    <h2 class="group-title">{{ t('settings.password') }}</h2>
    <form class="rows" novalidate @submit.prevent="changePassword">
      <div v-if="!s.session?.setupRequired" class="srow">
        <AppIcon name="lock" :size="16" />
        <label class="text" for="cur-pw"><span class="title">{{ t('settings.currentPassword') }}</span></label>
        <div class="ctl"><input id="cur-pw" v-model="currentPassword" type="password" autocomplete="current-password" /></div>
      </div>
      <div class="srow">
        <AppIcon v-if="s.session?.setupRequired" name="lock" :size="16" />
        <span v-else class="row-pad" />
        <label class="text" for="new-pw"><span class="title">{{ t('settings.newPassword') }}</span></label>
        <div class="ctl"><input id="new-pw" v-model="newPassword" type="password" autocomplete="new-password" /></div>
      </div>
      <div class="srow">
        <span class="row-pad" />
        <label class="text" for="new-pw2"><span class="title">{{ t('settings.repeatPassword') }}</span></label>
        <div class="ctl"><input id="new-pw2" v-model="repeatPassword" type="password" autocomplete="new-password" /></div>
      </div>
      <div class="srow compact">
        <span class="row-pad" />
        <div class="text">
          <span v-if="passwordError" class="desc err" role="alert">{{ passwordError }}</span>
        </div>
        <div class="ctl">
          <button type="submit" class="btn-accent" :disabled="busy">
            {{ s.session?.setupRequired ? t('settings.setPassword') : t('settings.changePassword') }}
          </button>
        </div>
      </div>
    </form>

    <h2 class="group-title">{{ t('common.more') }}</h2>
    <div class="rows">
      <details class="more">
        <summary>
          <AppIcon name="gear" :size="16" />
          <span class="text"><span class="title">{{ t('settings.service') }}</span></span>
          <AppIcon class="chev" name="chevronDown" :size="16" />
        </summary>
        <div class="more-body inner">
          <form v-if="host" @submit.prevent="saveHost">
            <div class="irow">
              <label for="h-port">{{ t('settings.webPort') }}</label>
              <input id="h-port" v-model.number="host.webPort" type="number" min="1" max="65535" />
            </div>
            <div class="irow">
              <label for="h-bps">{{ t('settings.basePortStart') }}</label>
              <input id="h-bps" v-model.number="host.basePortStart" type="number" min="1029" max="65514" />
            </div>
            <div class="irow">
              <label for="h-stride">{{ t('settings.basePortStride') }}</label>
              <input id="h-stride" v-model.number="host.basePortStride" type="number" min="27" max="1000" />
            </div>
            <div class="irow">
              <label for="h-comp">{{ t('settings.composeInterval') }}</label>
              <select id="h-comp" v-model.number="host.composeIntervalMs">
                <option v-for="ms in COMPOSE_INTERVALS" :key="ms" :value="ms">
                  {{ t('settings.composeHz', { ms, hz: Math.round(1000 / ms) }) }}
                </option>
              </select>
            </div>
            <div class="irow">
              <label for="h-log">{{ t('settings.logLevel') }}</label>
              <select id="h-log" v-model="host.logLevel">
                <option v-for="l in LOG_LEVELS" :key="l" :value="l">{{ l }}</option>
              </select>
            </div>
            <div class="irow">
              <label for="h-lan">{{ t('settings.lanAccess') }}</label>
              <span class="switch"><input id="h-lan" v-model="host.lanAccess" type="checkbox" /></span>
            </div>
            <div class="irow end">
              <button type="submit" class="btn-accent" :disabled="busy">{{ t('common.save') }}</button>
            </div>
          </form>
          <p v-else-if="unsupported" class="muted pad">{{ t('common.notSupported') }}</p>
          <p v-else class="muted pad">{{ t('common.loading') }}</p>
        </div>
      </details>

      <details class="more">
        <summary>
          <AppIcon name="archive" :size="16" />
          <span class="text"><span class="title">{{ t('settings.backup') }}</span></span>
          <AppIcon class="chev" name="chevronDown" :size="16" />
        </summary>
        <div class="more-body inner">
          <div class="irow">
            <label for="b-secrets">{{ t('settings.exportSecrets') }}</label>
            <span class="switch"><input id="b-secrets" v-model="includeSecrets" type="checkbox" /></span>
          </div>
          <div v-if="includeSecrets" class="irow">
            <label for="b-pw">{{ t('settings.exportPassword') }}</label>
            <input id="b-pw" v-model="exportPassword" type="password" autocomplete="new-password" />
          </div>
          <div class="irow end">
            <button type="button" :disabled="busy || (includeSecrets && !exportPassword)" @click="exportConfig">
              {{ t('settings.export') }}
            </button>
          </div>
          <div class="irow">
            <input ref="importInput" class="file" type="file" accept=".zip,application/zip" :aria-label="t('settings.import')" />
            <button type="button" :disabled="busy" @click="importConfig">{{ t('settings.import') }}</button>
          </div>
        </div>
      </details>

      <div v-if="s.health" class="srow">
        <AppIcon name="info" :size="16" />
        <div class="text">
          <span class="title">{{ t('settings.version', { version: s.health.version }) }}</span>
          <span class="desc">{{ t('settings.uptime', { time: fmtDuration(s.health.uptimeSeconds) }) }}</span>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.ctl input,
.ctl select {
  width: 240px;
}

.row-pad {
  width: 16px;
  flex: none;
}

label.text {
  cursor: default;
}

.desc.err {
  color: var(--bad);
}

details.more > summary .text {
  flex: 1;
}

.inner {
  padding: 0 16px 0 48px;
}

.irow {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  min-height: 52px;
  padding: 10px 0;
  border-bottom: 1px solid var(--divider);
}

.irow:last-child {
  border-bottom: 0;
}

.irow.end {
  justify-content: flex-end;
}

.irow > input,
.irow > select {
  width: 240px;
  flex: none;
}

.irow > .file {
  flex: 1;
  width: auto;
}

.pad {
  padding: 16px 0;
}

@media (width <= 600px) {
  .srow > .icon,
  .row-pad {
    display: none;
  }

  .ctl input,
  .ctl select,
  .irow > input,
  .irow > select {
    width: 160px;
  }

  .inner {
    padding-left: 16px;
  }
}
</style>
