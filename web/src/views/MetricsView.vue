<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed } from 'vue'
import { useLive } from '../composables'
import { fmtBitrate, fmtNumber, t } from '../i18n'
import { store } from '../store'

useLive()
const s = store.state
const host = computed(() => s.metrics?.host ?? null)

function pct(used?: number | null, total?: number | null): number | null {
  if (used === undefined || used === null || !total) return null
  return Math.round((used / total) * 100)
}

function gb(mb?: number | null): string {
  return mb === undefined || mb === null ? t('common.none') : t('unit.gb', { value: fmtNumber(mb / 1024, 1) })
}

function deskName(id: string): string {
  return s.desks.find((d) => d.id === id)?.name ?? id
}
</script>

<template>
  <div class="metrics">
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
              <td class="num">{{ g.utilizationPercent ?? t('common.none') }}</td>
              <td class="num">{{ g.encoderPercent ?? t('common.none') }}</td>
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
              <th class="num">{{ t('desk.devices') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="d in s.metrics?.desks ?? []" :key="d.id">
              <td>{{ deskName(d.id) }}</td>
              <td class="num">{{ fmtNumber(d.fps ?? 0) }}</td>
              <td class="num">{{ fmtBitrate(d.bitrateKbps ?? 0) }}</td>
              <td class="num">{{ t('unit.ms', { value: fmtNumber(d.encodeMsP99 ?? 0, 2) }) }}</td>
              <td class="num">{{ d.clients ?? 0 }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </div>
</template>

<style scoped>
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
