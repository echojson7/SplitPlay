<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { api } from '../api/client'
import type { Recording } from '../api/types'
import HostWarnings from '../components/HostWarnings.vue'
import { copyText, errorText, useLive } from '../composables'
import { fmtGb, fmtPercent } from '../desk'
import { fmtBitrate, fmtNumber, t } from '../i18n'
import { store } from '../store'
import { toast } from '../toast'

useLive()
const s = store.state
const host = computed(() => s.metrics?.host ?? null)

function pct(used?: number | null, total?: number | null): number | null {
  if (used === undefined || used === null || !total) return null
  return Math.round((used / total) * 100)
}

const gb = fmtGb

// ---- «Запись потока»: 30 minutes of every streaming desk as a short text to copy
const recording = ref<Recording | null>(null)
const recBusy = ref(false)
const now = ref(Date.now())
let recTimer: ReturnType<typeof setInterval> | undefined

async function loadRecording(): Promise<void> {
  try {
    recording.value = await api.recording()
    now.value = Date.now()
  } catch {
    // An older service has no recording: the row stays hidden.
    recording.value = null
  }
}

const recStatus = computed(() => {
  const r = recording.value
  if (!r?.startedAt) return null
  const end = r.endsAt ? Date.parse(r.endsAt) : now.value
  if (r.running) return t('rec.left', { minutes: Math.max(1, Math.ceil((end - now.value) / 60_000)) })
  return t('rec.done', { minutes: fmtNumber(Math.max(0, (end - Date.parse(r.startedAt)) / 60_000), 1) })
})

async function recAction(action: 'start' | 'stop'): Promise<void> {
  recBusy.value = true
  try {
    recording.value = action === 'start' ? await api.startRecording(30) : await api.stopRecording()
    now.value = Date.now()
  } catch (e) {
    toast(errorText(e), 'error')
  } finally {
    recBusy.value = false
  }
}

// Copies the report already on screen, inside the click (browsers allow the clipboard only then), and refreshes it
// afterwards; the text is also shown below the row to select by hand.
async function copyReport(): Promise<void> {
  const text = recording.value?.report
  if (!text) return
  const copied = await copyText(text)
  toast(copied ? t('common.copied') : t('rec.copyFailed'), copied ? undefined : 'error')
  if (recording.value?.running) void loadRecording()
}

onMounted(() => {
  void loadRecording()
  recTimer = setInterval(() => {
    now.value = Date.now()
    if (recording.value?.running) void loadRecording()
  }, 10_000)
})
onUnmounted(() => clearInterval(recTimer))

function deskName(id: string): string {
  return s.desks.find((d) => d.id === id)?.name ?? id
}
</script>

<template>
  <div class="metrics">
    <HostWarnings />
    <p v-if="!host" class="muted">{{ t('metrics.noData') }}</p>
    <template v-else>
      <div class="rows">
        <div class="srow compact">
          <span class="text"><span class="title">{{ t('metrics.cpu') }}</span></span>
          <div class="ctl">
            <span class="val">{{ host.cpuPercent == null ? t('common.none') : t('unit.percent', { value: host.cpuPercent }) }}</span>
            <div class="pbar meter"><span :style="{ width: `${host.cpuPercent ?? 0}%` }" /></div>
          </div>
        </div>
        <div class="srow compact">
          <span class="text"><span class="title">{{ t('metrics.memory') }}</span></span>
          <div class="ctl">
            <span class="val">{{ gb(host.memoryUsedMb) }} / {{ gb(host.memoryTotalMb) }}</span>
            <div class="pbar meter"><span :style="{ width: `${pct(host.memoryUsedMb, host.memoryTotalMb) ?? 0}%` }" /></div>
          </div>
        </div>
        <div class="srow compact">
          <span class="text"><span class="title">{{ t('metrics.compose') }}</span></span>
          <div class="ctl">
            <span class="val">{{ host.composeHz == null ? t('common.none') : `${host.composeHz} Hz` }}</span>
            <span class="meter" />
          </div>
        </div>
        <div v-if="recording" class="srow compact" data-recording>
          <span class="text">
            <span class="title">{{ t('rec.title') }}</span>
            <span class="desc">{{ recStatus ?? t('rec.hint') }}</span>
          </span>
          <div class="ctl">
            <button v-if="recording.running" type="button" :disabled="recBusy" @click="recAction('stop')">
              {{ t('rec.stop') }}
            </button>
            <button v-else type="button" :disabled="recBusy" @click="recAction('start')">{{ t('rec.start') }}</button>
            <button v-if="recording.report" type="button" class="btn-accent" @click="copyReport">{{ t('common.copy') }}</button>
          </div>
        </div>
        <details v-if="recording?.report" class="rec-report">
          <summary>{{ t('rec.show') }}</summary>
          <pre data-recording-report>{{ recording.report }}</pre>
        </details>
      </div>

      <h2 class="group-title">{{ t('metrics.gpus') }}</h2>
      <div class="panel table-wrap">
        <table>
          <thead>
            <tr>
              <th>GPU</th>
              <th class="num">{{ t('metrics.gpuLoad') }}</th>
              <th class="num">{{ t('metrics.encoder') }}</th>
              <th class="num">{{ t('metrics.vram') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="g in host.gpus" :key="g.luid">
              <td>{{ g.name }}</td>
              <td class="num">{{ fmtPercent(g.utilizationPercent) }}</td>
              <td class="num">{{ fmtPercent(g.encoderPercent) }}</td>
              <td class="num">{{ gb(g.vramUsedMb) }} / {{ gb(g.vramTotalMb) }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <h2 class="group-title">{{ t('metrics.desks') }}</h2>
      <div class="panel table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ t('logs.desk') }}</th>
              <th class="num">FPS</th>
              <th class="num">{{ t('metrics.bitrate') }}</th>
              <th class="num">{{ t('metrics.encodeP99') }}</th>
              <th class="num">{{ t('live.gpu') }}</th>
              <th class="num">{{ t('metrics.encoder') }}</th>
              <th class="num">{{ t('metrics.vram') }}</th>
              <th class="num">{{ t('desk.devices') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="d in s.metrics?.desks ?? []" :key="d.id">
              <td>{{ deskName(d.id) }}</td>
              <td class="num">{{ fmtNumber(d.fps ?? 0) }}</td>
              <td class="num">{{ fmtBitrate(d.bitrateKbps ?? 0) }}</td>
              <td class="num">{{ t('unit.ms', { value: fmtNumber(d.encodeMsP99 ?? 0, 2) }) }}</td>
              <td class="num" data-gpu>{{ fmtPercent(d.gpuPercent) }}</td>
              <td class="num">{{ fmtPercent(d.encoderPercent) }}</td>
              <td class="num">{{ gb(d.vramMb) }}</td>
              <td class="num">{{ d.clients ?? 0 }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </div>
</template>

<style scoped>
.rec-report {
  padding: 4px 16px 8px;
}

.rec-report pre {
  max-height: 320px;
  overflow: auto;
  font-size: 12px;
  user-select: text;
}

.val {
  font-variant-numeric: tabular-nums;
  color: var(--text-2);
}

.meter {
  width: 160px;
}

@media (width <= 600px) {
  .meter {
    width: 64px;
  }
}
</style>
