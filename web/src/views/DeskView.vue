<!-- SPDX-License-Identifier: MIT -->
<!-- Desk page: status and Play / Stop, the Moonlight address, PIN pairing, devices, display and advanced settings. -->
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError, deskToConfig } from '../api/client'
import type { DeskConfig, PairedClient } from '../api/types'
import AppIcon from '../components/AppIcon.vue'
import ConfirmDialog from '../components/ConfirmDialog.vue'
import CoverArt from '../components/CoverArt.vue'
import DeskSettings from '../components/DeskSettings.vue'
import PinField from '../components/PinField.vue'
import StateBadge from '../components/StateBadge.vue'
import { canStart, copyText, errorText, isRunning, useLive } from '../composables'
import { coverStyle, deskProgress, FPS_PRESETS, isPresetResolution, moonlightAddress, RESOLUTION_PRESETS, resolutionKey } from '../desk'
import { fmtDateTime, t } from '../i18n'
import { store } from '../store'
import { toast } from '../toast'

const props = defineProps<{ id: string }>()
const router = useRouter()
useLive()

const s = store.state
const desk = computed(() => s.desks.find((d) => d.id === props.id) ?? null)
const notFound = ref(false)
const busy = ref(false)
const clients = ref<PairedClient[]>([])

const running = computed(() => (desk.value ? isRunning(desk.value) : false))
const pairable = computed(() => desk.value?.state === 'Ready' || desk.value?.state === 'Streaming')
const progress = computed(() => (desk.value ? deskProgress(desk.value.state) : null))

async function load(): Promise<void> {
  try {
    store.upsertDesk(await api.getDesk(props.id))
    notFound.value = false
  } catch (e) {
    if (e instanceof ApiError && e.isNotFound) notFound.value = true
    else toast(errorText(e), 'error')
    return
  }
  await loadClients()
}

async function loadClients(): Promise<void> {
  try {
    clients.value = await api.listClients(props.id)
  } catch {
    // The worker is not running; the service does not know the clients then.
    clients.value = []
  }
}

onMounted(() => {
  void load()
  if (s.addresses === null) void store.refreshAddresses()
})
watch(() => props.id, load)
watch(
  () => desk.value?.state,
  (now, before) => {
    if (now && before && now !== before && (now === 'Ready' || now === 'Stopped')) void loadClients()
  },
)

async function lifecycle(action: 'start' | 'stop' | 'restart'): Promise<void> {
  busy.value = true
  try {
    const fn = { start: api.startDesk, stop: api.stopDesk, restart: api.restartDesk }[action]
    store.upsertDesk(await fn.call(api, props.id))
  } catch (e) {
    toast(errorText(e), 'error')
  } finally {
    busy.value = false
  }
}

// ---- connection
const addresses = computed(() => (desk.value && s.addresses?.length ? s.addresses.map((a) => moonlightAddress(desk.value!, a)) : []))

async function copy(text: string): Promise<void> {
  toast((await copyText(text)) ? t('common.copied') : text)
}

const pin = ref('')
const pairName = ref('')
const pairing = ref(false)
const pinTried = ref(false)
const pinValid = computed(() => /^\d{4}$/.test(pin.value))

async function pair(): Promise<void> {
  pinTried.value = true
  if (!pinValid.value || pairing.value) return
  pairing.value = true
  const name = pairName.value.trim() || t('desk.pairNamePlaceholder')
  try {
    await api.pair(props.id, { pin: pin.value, name })
    toast(t('desk.paired', { name }))
    pin.value = ''
    pairName.value = ''
    pinTried.value = false
    await loadClients()
  } catch (e) {
    toast(errorText(e), 'error')
  } finally {
    pairing.value = false
  }
}

const revoking = ref<PairedClient | null>(null)
async function revoke(): Promise<void> {
  const c = revoking.value
  if (!c) return
  revoking.value = null
  try {
    await api.revokeClient(props.id, c.id)
    await loadClients()
  } catch (e) {
    toast(errorText(e), 'error')
  }
}

// ---- display quick settings
const resChoice = computed(() => {
  const p = desk.value?.profile
  return p && isPresetResolution(p.width, p.height) ? resolutionKey(p.width, p.height) : 'custom'
})
const customOpen = ref(false)
const customW = ref(1920)
const customH = ref(1080)
const fpsOptions = computed(() => {
  const f = desk.value?.profile.fps
  return f && !FPS_PRESETS.includes(f) ? [...FPS_PRESETS, f].sort((a, b) => a - b) : [...FPS_PRESETS]
})

async function saveConfig(config: DeskConfig): Promise<boolean> {
  busy.value = true
  try {
    store.upsertDesk(await api.updateDesk(props.id, config))
    toast(t('common.saved'))
    return true
  } catch (e) {
    toast(errorText(e), 'error')
    return false
  } finally {
    busy.value = false
  }
}

function saveProfile(change: Partial<DeskConfig['profile']>): Promise<boolean> {
  const config = deskToConfig(desk.value!)
  config.profile = { ...config.profile, ...change }
  return saveConfig(config)
}

function onResolution(e: Event): void {
  const value = (e.target as HTMLSelectElement).value
  if (value === 'custom') {
    customW.value = desk.value?.profile.width ?? 1920
    customH.value = desk.value?.profile.height ?? 1080
    customOpen.value = true
    return
  }
  customOpen.value = false
  const [w, h] = value.split('x').map(Number)
  void saveProfile({ width: w, height: h })
}

async function applyCustom(): Promise<void> {
  if (await saveProfile({ width: Number(customW.value), height: Number(customH.value) })) customOpen.value = false
}

function onFps(e: Event): void {
  void saveProfile({ fps: Number((e.target as HTMLSelectElement).value) })
}

// ---- advanced and delete
const settingsKey = ref(0)
async function saveAdvanced(config: DeskConfig): Promise<void> {
  if (await saveConfig(config)) settingsKey.value++
}

const deleting = ref(false)
const deleteUser = ref(false)
async function remove(): Promise<void> {
  try {
    await api.deleteDesk(props.id, deleteUser.value)
    deleting.value = false
    store.removeDesk(props.id)
    toast(t('desk.deleted'))
    await router.push('/')
  } catch (e) {
    toast(errorText(e), 'error')
  }
}
</script>

<template>
  <section class="desk-page" :style="coverStyle(id)">
    <p v-if="notFound" class="inline-msg">{{ t('desk.notFound') }}</p>
    <p v-else-if="!desk" class="muted">{{ t('common.loading') }}</p>
    <template v-else>
      <header class="hero">
        <CoverArt fit="meet" />
        <div class="hero-text">
          <h1>{{ desk.name }}</h1>
          <span class="hero-user"><AppIcon name="user" :size="15" />{{ desk.user }}</span>
        </div>
      </header>

      <div class="playbar">
        <div v-if="progress" class="progress" role="progressbar" :aria-valuenow="Math.round(progress.value * 100)" aria-valuemin="0" aria-valuemax="100">
          <div class="bar"><span :style="{ width: `${progress.value * 100}%` }" /></div>
        </div>
        <button v-else-if="canStart(desk)" type="button" class="btn-play big" data-action="start" :disabled="busy" @click="lifecycle('start')">
          <AppIcon name="play" :size="18" />{{ t('desk.start') }}
        </button>
        <button v-else type="button" class="btn-stop big" data-action="stop" :disabled="busy" @click="lifecycle('stop')">
          <AppIcon name="stop" :size="15" />{{ t('desk.stop') }}
        </button>

        <div class="stat">
          <span class="label">{{ t('desk.status') }}</span>
          <StateBadge :state="desk.state" />
        </div>
        <div class="stat">
          <span class="label">{{ t('desk.resolution') }}</span>
          <span class="val">{{ desk.profile.width }}×{{ desk.profile.height }}</span>
        </div>
        <div class="stat">
          <span class="label">{{ t('desk.fps') }}</span>
          <span class="val">{{ desk.profile.fps }}</span>
        </div>
        <div class="stat">
          <span class="label">{{ t('desk.devices') }}</span>
          <span class="val">{{ clients.length }}</span>
        </div>
        <span class="spacer" />
        <button
          v-if="progress && desk.state !== 'Stopping'"
          type="button"
          class="btn-icon"
          :title="t('desk.stop')"
          :aria-label="t('desk.stop')"
          :disabled="busy"
          @click="lifecycle('stop')"
        >
          <AppIcon name="close" :size="18" />
        </button>
        <button
          v-else-if="running"
          type="button"
          class="btn-icon"
          :title="t('desk.restart')"
          :aria-label="t('desk.restart')"
          :disabled="busy"
          @click="lifecycle('restart')"
        >
          <AppIcon name="restart" :size="18" />
        </button>
      </div>

      <div v-if="desk.state === 'Faulted' && desk.lastError" class="inline-msg" role="alert">{{ desk.lastError }}</div>
      <div v-if="desk.resolutionPendingRestart" class="inline-msg warn">
        <span>{{ t('desk.pendingRestart') }}</span>
        <span class="spacer" />
        <button type="button" class="btn-ghost small-btn" :disabled="busy" @click="lifecycle('restart')">
          <AppIcon name="restart" :size="14" />{{ t('desk.restart') }}
        </button>
      </div>
      <div v-if="desk.userManaged === false && desk.hasPassword === false" class="inline-msg warn">{{ t('desk.passwordMissing') }}</div>

      <div class="columns">
        <div class="col-main">
          <section class="panel connection">
            <h2>{{ t('desk.connection') }}</h2>
            <div class="address">
              <span class="label">{{ t('desk.address') }}</span>
              <template v-if="addresses.length">
                <div v-for="(a, i) in addresses" :key="a" :class="['addr-row', { primary: i === 0 }]">
                  <code class="addr" data-address>{{ a }}</code>
                  <button type="button" class="btn-icon small" :title="t('common.copy')" :aria-label="t('common.copy')" @click="copy(a)">
                    <AppIcon name="copy" :size="16" />
                  </button>
                </div>
              </template>
              <div v-else class="addr-row primary">
                <code class="addr" data-address><span class="muted">{{ t('desk.noAddress') }}</span>:{{ desk.basePort }}</code>
              </div>
            </div>

            <form class="pair" novalidate @submit.prevent="pair">
              <div class="field">
                <span class="field-label">{{ t('desk.pairPin') }}</span>
                <PinField id="desk-pin" v-model="pin" :disabled="!pairable" :invalid="pinTried && !pinValid" :label="t('desk.pairPin')" />
              </div>
              <div class="field grow">
                <label for="pair-name">{{ t('desk.pairName') }}</label>
                <input
                  id="pair-name"
                  v-model="pairName"
                  name="pairName"
                  maxlength="64"
                  :placeholder="t('desk.pairNamePlaceholder')"
                  :disabled="!pairable"
                />
              </div>
              <button type="submit" class="btn-primary pair-btn" :disabled="!pairable || pairing">{{ t('desk.pairSubmit') }}</button>
            </form>
            <p v-if="!pairable" class="hint">{{ t('desk.pairNeedsRunning') }}</p>
            <p v-else-if="pinTried && !pinValid" class="err">{{ t('desk.pinInvalid') }}</p>
          </section>

          <section class="panel">
            <h2>{{ t('desk.devices') }}</h2>
            <p v-if="clients.length === 0" class="muted empty">{{ t('desk.noDevices') }}</p>
            <ul v-else class="devices">
              <li v-for="c in clients" :key="c.id">
                <span class="device-icon"><AppIcon name="laptop" :size="20" /></span>
                <span class="device">
                  <span class="device-name">{{ c.name }}</span>
                  <span v-if="c.pairedAt" class="device-date">{{ t('desk.pairedAt', { date: fmtDateTime(c.pairedAt) }) }}</span>
                </span>
                <button type="button" class="btn-ghost" :title="t('desk.revoke')" @click="revoking = c">{{ t('desk.revoke') }}</button>
              </li>
            </ul>
          </section>
        </div>

        <aside class="col-side">
          <section class="panel">
            <h2>{{ t('desk.display') }}</h2>
            <div class="field">
              <label for="q-res">{{ t('desk.resolution') }}</label>
              <select id="q-res" name="resolution" :value="customOpen ? 'custom' : resChoice" :disabled="busy" @change="onResolution">
                <option v-for="p in RESOLUTION_PRESETS" :key="resolutionKey(p.width, p.height)" :value="resolutionKey(p.width, p.height)">
                  {{ p.width }} × {{ p.height }}
                </option>
                <option value="custom">
                  {{ resChoice === 'custom' ? `${desk.profile.width} × ${desk.profile.height}` : t('desk.custom') }}
                </option>
              </select>
            </div>
            <div v-if="customOpen" class="custom">
              <input v-model.number="customW" name="width" type="number" min="320" max="7680" :aria-label="t('desk.width')" />
              <span class="muted">×</span>
              <input v-model.number="customH" name="height" type="number" min="240" max="4320" :aria-label="t('desk.height')" />
              <button type="button" class="btn-primary" :disabled="busy" @click="applyCustom">{{ t('desk.apply') }}</button>
            </div>
            <div class="field">
              <label for="q-fps">{{ t('desk.fps') }}</label>
              <select id="q-fps" name="fps" :value="desk.profile.fps" :disabled="busy" @change="onFps">
                <option v-for="f in fpsOptions" :key="f" :value="f">{{ f }} FPS</option>
              </select>
            </div>
          </section>
        </aside>
      </div>

      <details class="more">
        <summary><AppIcon name="gear" :size="16" />{{ t('common.more') }}<span class="spacer" /><AppIcon class="chev" name="chevronDown" :size="16" /></summary>
        <div class="more-body">
          <DeskSettings
            :key="`${desk.id}-${settingsKey}-${running}`"
            :desk="desk"
            :gpus="s.metrics?.host.gpus ?? []"
            :running="running"
            :busy="busy"
            @save="saveAdvanced"
          />
        </div>
      </details>

      <div class="danger-zone">
        <button
          type="button"
          class="btn-danger"
          data-action="delete"
          :disabled="running"
          :title="running ? t('desk.deleteRunning') : ''"
          @click="((deleteUser = false), (deleting = true))"
        >
          <AppIcon name="trash" :size="16" />{{ t('desk.delete') }}
        </button>
        <span v-if="running" class="hint">{{ t('desk.deleteRunning') }}</span>
      </div>

      <ConfirmDialog :open="deleting" :title="t('desk.deleteTitle', { name: desk.name })" :confirm-label="t('common.delete')" @confirm="remove" @close="deleting = false">
        <label v-if="desk.userManaged" class="switch">
          <input v-model="deleteUser" type="checkbox" name="deleteUser" />
          {{ t('desk.deleteUser') }}
        </label>
      </ConfirmDialog>
      <ConfirmDialog
        :open="revoking !== null"
        :title="t('desk.revokeTitle', { name: revoking?.name ?? '' })"
        :confirm-label="t('desk.revoke')"
        @confirm="revoke"
        @close="revoking = null"
      />
    </template>
  </section>
</template>

<style scoped>
.desk-page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.hero {
  position: relative;
  height: 160px;
  border-radius: 4px 4px 0 0;
  overflow: hidden;
  margin-bottom: -16px;
}

.hero-text {
  position: absolute;
  left: 28px;
  right: 28px;
  bottom: 22px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.hero h1 {
  font-size: 38px;
  line-height: 1.05;
  text-shadow: 0 3px 14px rgb(0 0 0 / 55%);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.hero-user {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: rgb(255 255 255 / 80%);
  font-size: 14px;
  text-shadow: 0 1px 4px rgb(0 0 0 / 60%);
}

.playbar {
  position: relative;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 14px 32px;
  padding: 16px 24px;
  background: linear-gradient(90deg, rgb(14 20 27 / 92%) 0%, rgb(22 32 45 / 92%) 100%);
  border-radius: 0 0 4px 4px;
  box-shadow: 0 6px 18px rgb(0 0 0 / 30%);
}

.big {
  height: 48px;
  min-width: 200px;
  font-size: 17px;
}

.progress {
  width: 200px;
  height: 48px;
  display: flex;
  align-items: center;
}

.bar {
  width: 100%;
  height: 8px;
  border-radius: 4px;
  background: rgb(255 255 255 / 8%);
  overflow: hidden;
}

.bar span {
  display: block;
  height: 100%;
  border-radius: 4px;
  background: repeating-linear-gradient(-45deg, #1a9fff 0 8px, #47bfff 8px 16px);
  background-size: 22.6px 22.6px;
  animation: stripes 0.8s linear infinite;
  transition: width 0.5s ease;
}

@keyframes stripes {
  to {
    background-position: 22.6px 0;
  }
}

.stat {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.stat .label {
  font-size: 11px;
}

.val {
  color: #fff;
  font-size: 15px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}

.small-btn {
  height: 28px;
  padding: 0 10px;
  font-size: 13px;
}

.inline-msg {
  margin: 0;
}

.columns {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 300px;
  gap: 16px;
  align-items: start;
}

.col-main,
.col-side {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
}

.address {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-bottom: 18px;
  margin-bottom: 18px;
  border-bottom: 1px solid rgb(255 255 255 / 6%);
}

.addr-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.addr {
  font-family: var(--mono);
  font-size: 18px;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
}

.addr-row.primary .addr {
  font-size: 30px;
  font-weight: 700;
  color: var(--accent);
  letter-spacing: 0.01em;
  text-shadow: 0 0 18px rgb(102 192 244 / 25%);
}

.pair {
  display: flex;
  align-items: flex-end;
  flex-wrap: wrap;
  gap: 16px;
}

.pair .grow {
  flex: 1;
  min-width: 160px;
}

.pair .grow input {
  height: 52px;
  font-size: 15px;
}

.pair-btn {
  height: 52px;
  min-width: 140px;
  font-size: 15px;
}

.connection .hint,
.connection .err {
  margin: 12px 0 0;
}

.empty {
  margin: 0;
}

.devices {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.devices li {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 10px;
  border-radius: 3px;
  background: rgb(0 0 0 / 18%);
}

.device-icon {
  width: 38px;
  height: 38px;
  border-radius: 3px;
  display: grid;
  place-items: center;
  background: rgb(102 192 244 / 12%);
  color: var(--accent);
  flex: none;
}

.device {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.device-name {
  color: #fff;
  font-weight: 700;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.device-date {
  font-size: 12px;
  color: var(--muted);
}

.col-side .panel {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.col-side .panel h2 {
  margin-bottom: 0;
}

.custom {
  display: flex;
  align-items: center;
  gap: 8px;
}

.custom input {
  width: 0;
  flex: 1;
}

.danger-zone {
  display: flex;
  align-items: center;
  gap: 14px;
  padding-top: 8px;
}

@media (width <= 860px) {
  .columns {
    grid-template-columns: 1fr;
  }
}

@media (width <= 600px) {
  .hero {
    height: 140px;
  }

  .hero h1 {
    font-size: 28px;
  }

  .playbar {
    gap: 12px 20px;
    padding: 14px 16px;
  }

  .big,
  .progress {
    width: 100%;
    min-width: 0;
  }

  .addr-row.primary .addr {
    font-size: 22px;
  }
}
</style>
