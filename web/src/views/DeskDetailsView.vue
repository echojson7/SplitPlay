<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { api, ApiError } from '../api/client'
import type { PairedClient } from '../api/types'
import PairDialog from '../components/PairDialog.vue'
import StateBadge from '../components/StateBadge.vue'
import { canStart, canStop, errorText, gpuLabel, isRunning, useLive } from '../composables'
import { fmtBitrate, fmtDateTime, fmtNumber, t } from '../i18n'
import { store } from '../store'

const props = defineProps<{ id: string }>()
const router = useRouter()
useLive()

const s = store.state
const desk = computed(() => s.desks.find((d) => d.id === props.id) ?? null)
const notFound = ref(false)
const error = ref<string | null>(null)
const info = ref<string | null>(null)
const busy = ref(false)
const clients = ref<PairedClient[]>([])
const pairOpen = ref(false)
const deleteUser = ref(false)

const host = computed(() => (typeof location !== 'undefined' ? location.hostname : '127.0.0.1'))
const pairable = computed(() => desk.value?.state === 'Ready' || desk.value?.state === 'Streaming')

async function load(): Promise<void> {
  try {
    store.upsertDesk(await api.getDesk(props.id))
    notFound.value = false
  } catch (e) {
    if (e instanceof ApiError && e.isNotFound) notFound.value = true
    else error.value = errorText(e)
    return
  }
  await loadClients()
}

async function loadClients(): Promise<void> {
  try {
    clients.value = await api.listClients(props.id)
  } catch {
    // The worker is not running; the service may not know the clients yet.
    clients.value = []
  }
}

onMounted(load)
watch(() => props.id, load)
// Client list changes when a desk comes up; refresh it on state changes.
watch(
  () => desk.value?.state,
  (now, before) => {
    if (now && before && now !== before && (now === 'Ready' || now === 'Stopped')) void loadClients()
  },
)

async function lifecycle(action: 'start' | 'stop' | 'restart'): Promise<void> {
  busy.value = true
  error.value = null
  try {
    const fn = { start: api.startDesk, stop: api.stopDesk, restart: api.restartDesk }[action]
    store.upsertDesk(await fn.call(api, props.id))
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = false
  }
}

async function revoke(c: PairedClient): Promise<void> {
  if (!confirm(t('desk.revokeConfirm', { name: c.name }))) return
  try {
    await api.revokeClient(props.id, c.id)
    await loadClients()
  } catch (e) {
    error.value = errorText(e)
  }
}

async function remove(): Promise<void> {
  if (!desk.value) return
  if (!confirm(t('desk.deleteConfirm', { name: desk.value.name }))) return
  try {
    await api.deleteDesk(props.id, deleteUser.value)
    store.removeDesk(props.id)
    await router.push('/')
  } catch (e) {
    error.value = errorText(e)
  }
}

function onPaired(name: string): void {
  info.value = `${t('pair.success')}: ${name}`
  void loadClients()
}
</script>

<template>
  <section>
    <p><RouterLink to="/">← {{ t('nav.desks') }}</RouterLink></p>
    <p v-if="notFound" class="notice bad">{{ t('desk.notFound', { id }) }}</p>
    <p v-else-if="!desk" class="muted">{{ t('common.loading') }}</p>
    <template v-else>
      <div class="row">
        <h1>{{ desk.name }}</h1>
        <StateBadge :state="desk.state" />
        <div class="spacer" />
        <button v-if="canStart(desk)" type="button" class="primary" :disabled="busy" @click="lifecycle('start')">
          {{ t('desk.start') }}
        </button>
        <button v-if="canStop(desk)" type="button" :disabled="busy" @click="lifecycle('stop')">{{ t('desk.stop') }}</button>
        <button type="button" :disabled="busy || canStart(desk)" @click="lifecycle('restart')">{{ t('desk.restart') }}</button>
        <button type="button" :disabled="!pairable" :title="pairable ? '' : t('pair.notRunning')" @click="pairOpen = true">
          {{ t('desk.pair') }}
        </button>
        <RouterLink :to="`/desks/${desk.id}/edit`" class="button">{{ t('common.edit') }}</RouterLink>
      </div>

      <p v-if="error" class="notice bad" role="alert">{{ t('common.error', { message: error }) }}</p>
      <p v-if="info" class="notice ok" role="status">{{ info }}</p>
      <p v-if="desk.lastError" class="notice bad">{{ t('desk.lastError') }}: {{ desk.lastError }}</p>
      <p v-if="desk.resolutionPendingRestart" class="notice warn">{{ t('desk.resolutionPendingRestart') }}</p>
      <p v-if="desk.userManaged === false && desk.hasPassword === false" class="notice warn">{{ t('desk.passwordMissing') }}</p>

      <div class="grid-2 cards">
        <div class="card">
          <dl class="props">
            <dt>Id</dt>
            <dd class="mono">{{ desk.id }}</dd>
            <dt>{{ t('desk.user') }}</dt>
            <dd class="mono">{{ desk.user }}</dd>
            <dt>{{ t('desk.session') }}</dt>
            <dd>{{ desk.sessionId ?? t('common.none') }}</dd>
            <dt>{{ t('desk.basePort') }}</dt>
            <dd class="mono">{{ desk.basePort }}</dd>
            <dt>{{ t('desk.moonlightAddress') }}</dt>
            <dd class="mono">{{ host }}:{{ desk.basePort }}</dd>
            <dt>{{ t('desks.col.gpu') }}</dt>
            <dd>{{ gpuLabel(desk.gpu, s.metrics?.host.gpus) }}</dd>
            <dt>{{ t('desk.autostart') }}</dt>
            <dd>{{ desk.autostart ? t('common.yes') : t('common.no') }}</dd>
          </dl>
        </div>
        <div class="card">
          <dl class="props">
            <dt>{{ t('desk.profile') }}</dt>
            <dd>
              {{ desk.profile.width }}×{{ desk.profile.height }} @ {{ desk.profile.fps }} ·
              {{ desk.profile.codec.toUpperCase() }} · {{ fmtBitrate(desk.profile.bitrateKbps) }}
            </dd>
            <dt>{{ t('desks.col.fps') }}</dt>
            <dd>{{ fmtNumber(desk.metrics.fps) }}</dd>
            <dt>{{ t('desks.col.bitrate') }}</dt>
            <dd>{{ fmtBitrate(desk.metrics.bitrateKbps) }}</dd>
            <dt>{{ t('desk.encodeP99') }}</dt>
            <dd>{{ t('unit.ms', { value: fmtNumber(desk.metrics.encodeMsP99, 2) }) }}</dd>
            <dt>{{ t('desks.col.clients') }}</dt>
            <dd>{{ desk.metrics.clients }}</dd>
          </dl>
        </div>
      </div>

      <h2>{{ t('desk.clients') }}</h2>
      <div class="card">
        <p v-if="clients.length === 0" class="muted">{{ t('desk.noClients') }}</p>
        <table v-else>
          <tbody>
            <tr v-for="c in clients" :key="c.id">
              <td>{{ c.name }}</td>
              <td class="muted">{{ c.pairedAt ? t('desk.pairedAt', { date: fmtDateTime(c.pairedAt) }) : '' }}</td>
              <td class="num"><button type="button" class="danger" @click="revoke(c)">{{ t('desk.revoke') }}</button></td>
            </tr>
          </tbody>
        </table>
      </div>

      <h2>{{ t('common.delete') }}</h2>
      <div class="card row">
        <template v-if="isRunning(desk)">
          <span class="muted">{{ t('desk.deleteRunning') }}</span>
        </template>
        <template v-else>
          <label class="check"><input v-model="deleteUser" type="checkbox" /> {{ t('desk.deleteUser') }}</label>
          <div class="spacer" />
          <button type="button" class="danger" @click="remove">{{ t('common.delete') }}</button>
        </template>
      </div>

      <PairDialog :desk-id="desk.id" :open="pairOpen" @close="pairOpen = false" @paired="onPaired" />
    </template>
  </section>
</template>

<style scoped>
.cards {
  gap: 1rem;
}

.check {
  margin: 0;
}
</style>
