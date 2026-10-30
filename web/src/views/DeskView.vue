<!-- SPDX-License-Identifier: MIT -->
<!-- Desk page: status and Play / Stop, the stream right now, the Moonlight address, PIN pairing, devices, display and advanced settings. -->
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { api, ApiError, deskToConfig } from '../api/client'
import type { DeskConfig, PairedClient } from '../api/types'
import AppIcon from '../components/AppIcon.vue'
import ConfirmDialog from '../components/ConfirmDialog.vue'
import ConnectPack from '../components/ConnectPack.vue'
import DeskSettings from '../components/DeskSettings.vue'
import PinField from '../components/PinField.vue'
import StateBadge from '../components/StateBadge.vue'
import { canStart, copyText, errorText, isRunning, useLive } from '../composables'
import {
  changeProfile,
  deskError,
  deskProgress,
  deviceName,
  fmtMs,
  FPS_PRESETS,
  isPresetResolution,
  liveStream,
  moonlightAddress,
  RESOLUTION_PRESETS,
  resolutionKey,
  streamSummary,
} from '../desk'
import { fmtDateTime, fmtNumber, t } from '../i18n'
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
const error = computed(() => (desk.value ? deskError(desk.value) : null))

// ---- now: the connected device and live numbers (desk events and the 2 s metrics push)
const stream = computed(() => (desk.value ? liveStream(desk.value) : null))
const live = computed(() => {
  const m = desk.value?.metrics
  if (!m) return null
  return {
    fps: fmtNumber(m.fps ?? 0),
    ping: m.rttMs != null ? fmtMs(m.rttMs, false) : t('common.none'),
    encode:
      m.encodeMsP50 !== undefined
        ? t('live.encodeValue', { p50: fmtNumber(m.encodeMsP50, 1, 1), p99: fmtNumber(m.encodeMsP99 ?? 0, 1, 1) })
        : fmtMs(m.encodeMsP99 ?? 0),
    loss: m.lossPct !== undefined ? t('unit.percent', { value: fmtNumber(m.lossPct, 1) }) : t('common.none'),
  }
})

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
  config.profile = changeProfile(config.profile, change)
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
  <section class="desk-page">
    <p v-if="notFound" class="inline-msg">{{ t('desk.notFound') }}</p>
    <p v-else-if="!desk" class="muted">{{ t('common.loading') }}</p>
    <template v-else>
      <header class="page-head">
        <nav class="crumbs" aria-label="breadcrumb">
          <RouterLink to="/">{{ t('nav.library') }}</RouterLink>
          <AppIcon class="sep" name="chevronRight" :size="16" />
          <h1>{{ desk.name }}</h1>
        </nav>
      </header>

      <div class="msgs">
        <div v-if="error" class="inline-msg" role="alert">{{ error }}</div>
        <div v-if="desk.resolutionPendingRestart" class="inline-msg warn">
          <span class="spacer">{{ t('desk.pendingRestart') }}</span>
          <button type="button" :disabled="busy" @click="lifecycle('restart')">{{ t('desk.restart') }}</button>
        </div>
        <div v-if="desk.userManaged === false && desk.hasPassword === false" class="inline-msg warn">{{ t('desk.passwordMissing') }}</div>
      </div>

      <div class="rows">
        <div class="srow">
          <AppIcon name="monitor" :size="16" />
          <div class="text">
            <span class="title">{{ desk.user }}</span>
            <span class="desc">{{ desk.profile.width }}×{{ desk.profile.height }} · {{ desk.profile.fps }} FPS</span>
          </div>
          <div class="ctl">
            <div
              v-if="progress"
              class="progress"
              role="progressbar"
              :aria-valuenow="Math.round(progress.value * 100)"
              aria-valuemin="0"
              aria-valuemax="100"
            >
              <span class="ptext">{{ t(`state.${progress.state}`) }}</span>
              <div class="pbar"><span :style="{ width: `${progress.value * 100}%` }" /></div>
            </div>
            <StateBadge v-else :state="desk.state" />
            <button
              v-if="running && !progress"
              type="button"
              class="btn-icon"
              :title="t('desk.restart')"
              :aria-label="t('desk.restart')"
              :disabled="busy"
              @click="lifecycle('restart')"
            >
              <AppIcon name="restart" :size="16" />
            </button>
            <button v-if="!progress && canStart(desk)" type="button" class="btn-accent main" data-action="start" :disabled="busy" @click="lifecycle('start')">
              {{ t('desk.start') }}
            </button>
            <button v-else-if="!progress || desk.state !== 'Stopping'" type="button" class="main" data-action="stop" :disabled="busy" @click="lifecycle('stop')">
              {{ t('desk.stop') }}
            </button>
          </div>
        </div>
      </div>

      <h2 class="group-title">{{ t('live.title') }}</h2>
      <div class="rows" data-live>
        <div v-if="!stream" class="srow compact" data-live-none>
          <AppIcon name="laptop" :size="16" />
          <span class="text muted">{{ t('live.none') }}</span>
        </div>
        <template v-else>
          <div class="srow compact">
            <AppIcon name="laptop" :size="16" />
            <div class="text"><span class="title">{{ t('live.device') }}</span></div>
            <div class="ctl val" data-live-device>
              <span>{{ deviceName(stream) }}</span>
              <span v-if="stream.client && stream.address" class="muted">{{ stream.address }}</span>
            </div>
          </div>
          <div class="srow compact">
            <AppIcon name="monitor" :size="16" />
            <div class="text"><span class="title">{{ t('live.stream') }}</span></div>
            <div class="ctl val" data-live-stream>{{ streamSummary(stream) }}</div>
          </div>
          <div class="srow compact">
            <AppIcon name="activity" :size="16" />
            <div class="text"><span class="title">{{ t('live.fps') }}</span></div>
            <div class="ctl val" data-live-fps>{{ live?.fps }}</div>
          </div>
          <div class="srow compact">
            <span class="row-pad" />
            <div class="text"><span class="title">{{ t('live.ping') }}</span></div>
            <div class="ctl val" data-live-ping>{{ live?.ping }}</div>
          </div>
          <div class="srow compact">
            <span class="row-pad" />
            <div class="text"><span class="title">{{ t('live.encode') }}</span></div>
            <div class="ctl val" data-live-encode>{{ live?.encode }}</div>
          </div>
          <div class="srow compact">
            <span class="row-pad" />
            <div class="text"><span class="title">{{ t('live.loss') }}</span></div>
            <div class="ctl val" data-live-loss>{{ live?.loss }}</div>
          </div>
        </template>
      </div>

      <h2 class="group-title">{{ t('desk.connection') }}</h2>
      <div class="rows">
        <div class="srow">
          <AppIcon name="globe" :size="16" />
          <div class="text">
            <span class="title">{{ t('desk.address') }}</span>
          </div>
          <div class="ctl">
            <template v-if="addresses.length">
              <span class="addr" data-address>{{ addresses[0] }}</span>
              <button type="button" @click="copy(addresses[0]!)">{{ t('common.copy') }}</button>
            </template>
            <span v-else class="addr" data-address>{{ t('desk.noAddress') }}:{{ desk.basePort }}</span>
          </div>
        </div>
        <div v-for="a in addresses.slice(1)" :key="a" class="srow compact">
          <span class="row-pad" />
          <div class="text" />
          <div class="ctl">
            <span class="addr">{{ a }}</span>
            <button type="button" @click="copy(a)">{{ t('common.copy') }}</button>
          </div>
        </div>
        <form class="srow pair-row" novalidate @submit.prevent="pair">
          <AppIcon name="link" :size="16" />
          <div class="text">
            <span class="title">{{ t('desk.pinTitle') }}</span>
            <span v-if="!pairable" class="desc">{{ t('desk.pairNeedsRunning') }}</span>
            <span v-else-if="pinTried && !pinValid" class="desc err">{{ t('desk.pinInvalid') }}</span>
          </div>
          <div class="ctl">
            <PinField id="desk-pin" v-model="pin" :disabled="!pairable" :invalid="pinTried && !pinValid" :label="t('desk.pairPin')" />
            <input
              id="pair-name"
              v-model="pairName"
              class="pair-name"
              name="pairName"
              maxlength="64"
              :aria-label="t('desk.pairName')"
              :placeholder="t('desk.pairNamePlaceholder')"
              :disabled="!pairable"
            />
            <button type="submit" class="btn-accent" :disabled="!pairable || pairing">{{ t('desk.pairSubmit') }}</button>
          </div>
        </form>
      </div>

      <h2 class="group-title">{{ t('pack.title') }}</h2>
      <ConnectPack :desk="desk" :address="s.addresses?.[0] ?? ''" />

      <h2 class="group-title">{{ t('desk.devices') }}</h2>
      <div class="rows">
        <div v-if="clients.length === 0" class="srow compact">
          <AppIcon name="laptop" :size="16" />
          <span class="text muted">{{ t('desk.noDevices') }}</span>
        </div>
        <div v-for="c in clients" :key="c.id" class="srow">
          <AppIcon name="laptop" :size="16" />
          <div class="text">
            <span class="title">{{ c.name }}</span>
            <span v-if="c.pairedAt" class="desc">{{ t('desk.pairedAt', { date: fmtDateTime(c.pairedAt) }) }}</span>
          </div>
          <div class="ctl">
            <button type="button" @click="revoking = c">{{ t('desk.revoke') }}</button>
          </div>
        </div>
      </div>

      <h2 class="group-title">{{ t('desk.display') }}</h2>
      <div class="rows">
        <div class="srow">
          <AppIcon name="monitor" :size="16" />
          <label class="text" for="q-res"><span class="title">{{ t('desk.resolution') }}</span></label>
          <div class="ctl">
            <select id="q-res" name="resolution" :value="customOpen ? 'custom' : resChoice" :disabled="busy" @change="onResolution">
              <option v-for="p in RESOLUTION_PRESETS" :key="resolutionKey(p.width, p.height)" :value="resolutionKey(p.width, p.height)">
                {{ p.width }} × {{ p.height }}
              </option>
              <option value="custom">
                {{ resChoice === 'custom' ? `${desk.profile.width} × ${desk.profile.height}` : t('desk.custom') }}
              </option>
            </select>
          </div>
        </div>
        <div v-if="customOpen" class="srow compact">
          <span class="row-pad" />
          <div class="text" />
          <div class="ctl">
            <input v-model.number="customW" class="num-in" name="width" type="number" min="320" max="7680" :aria-label="t('desk.width')" />
            <span class="muted">×</span>
            <input v-model.number="customH" class="num-in" name="height" type="number" min="240" max="4320" :aria-label="t('desk.height')" />
            <button type="button" class="btn-accent" :disabled="busy" @click="applyCustom">{{ t('desk.apply') }}</button>
          </div>
        </div>
        <div class="srow">
          <span class="row-pad" />
          <label class="text" for="q-fps"><span class="title">{{ t('desk.fps') }}</span></label>
          <div class="ctl">
            <select id="q-fps" name="fps" :value="desk.profile.fps" :disabled="busy" @change="onFps">
              <option v-for="f in fpsOptions" :key="f" :value="f">{{ f }} FPS</option>
            </select>
          </div>
        </div>
        <details class="more">
          <summary>
            <AppIcon name="sliders" :size="16" />
            <span class="text"><span class="title">{{ t('common.more') }}</span></span>
            <AppIcon class="chev" name="chevronDown" :size="16" />
          </summary>
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
      </div>

      <div class="rows danger">
        <div class="srow">
          <AppIcon name="trash" :size="16" />
          <div class="text">
            <span class="title">{{ t('desk.delete') }}</span>
            <span v-if="running" class="desc">{{ t('desk.deleteRunning') }}</span>
          </div>
          <div class="ctl">
            <button type="button" data-action="delete" :disabled="running" @click="((deleteUser = false), (deleting = true))">
              {{ t('common.delete') }}
            </button>
          </div>
        </div>
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
.msgs {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.msgs:not(:empty) {
  margin-bottom: 16px;
}

.main {
  min-width: 120px;
}

.progress {
  width: 160px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ptext {
  font-size: 12px;
  line-height: 16px;
  color: var(--text-2);
}

.addr {
  font-variant-numeric: tabular-nums;
  user-select: all;
}

.ctl.val {
  font-variant-numeric: tabular-nums;
  min-width: 0;
  flex: 0 1 auto;
  text-align: right;
}

.row-pad {
  width: 16px;
  flex: none;
}

.pair-name {
  width: 180px;
}

.ctl select {
  width: 200px;
}

.num-in {
  width: 88px;
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

details.more > .more-body {
  padding: 4px 16px 16px 48px;
}

.danger {
  margin-top: 28px;
}

@media (width <= 760px) {
  .pair-row {
    flex-wrap: wrap;
  }

  .pair-row .ctl {
    width: 100%;
    padding-left: 32px;
  }

  .pair-row .pair-name {
    flex: 1;
    width: auto;
  }
}

@media (width <= 600px) {
  .srow > .icon,
  .row-pad {
    display: none;
  }

  .pair-row .ctl {
    padding-left: 0;
  }

  details.more > .more-body {
    padding-left: 16px;
  }

  .ctl select {
    width: 150px;
  }

  .main {
    min-width: 0;
  }

  .progress {
    width: 96px;
  }
}
</style>
