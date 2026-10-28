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
      <div class="tiles">
        <div class="panel tile">
          <span class="label">{{ t('metrics.cpu') }}</span>
          <span class="big">{{ host.cpuPercent == null ? t('common.none') : t('unit.percent', { value: host.cpuPercent }) }}</span>
          <div class="meter"><span :style="{ width: `${host.cpuPercent ?? 0}%` }" /></div>
        </div>
        <div class="panel tile">
          <span class="label">{{ t('metrics.memory') }}</span>
          <span class="big">{{ gb(host.memoryUsedMb) }} <small class="muted">/ {{ gb(host.memoryTotalMb) }}</small></span>
          <div class="meter"><span :style="{ width: `${pct(host.memoryUsedMb, host.memoryTotalMb) ?? 0}%` }" /></div>
        </div>
        <div class="panel tile">
          <span class="label">{{ t('metrics.compose') }}</span>
          <span class="big">{{ host.composeHz == null ? t('common.none') : `${host.composeHz} Hz` }}</span>
        </div>
      </div>

      <div class="panel table-wrap">
        <h2>{{ t('metrics.gpus') }}</h2>
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
              <td class="strong">{{ g.name }}</td>
              <td class="num">{{ g.utilizationPercent ?? t('common.none') }}</td>
              <td class="num">{{ g.encoderPercent ?? t('common.none') }}</td>
              <td class="num">{{ gb(g.vramUsedMb) }} / {{ gb(g.vramTotalMb) }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="panel table-wrap">
        <h2>{{ t('metrics.desks') }}</h2>
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
              <td class="strong">{{ deskName(d.id) }}</td>
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
.metrics {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 16px;
}

.tile {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.big {
  font-size: 28px;
  font-weight: 700;
  color: #fff;
  font-variant-numeric: tabular-nums;
}

.big small {
  font-size: 14px;
  font-weight: 400;
}

.meter {
  height: 4px;
  border-radius: 2px;
  background: rgb(0 0 0 / 35%);
  overflow: hidden;
}

.meter span {
  display: block;
  height: 100%;
  background: linear-gradient(90deg, #06bfff, #2d73ff);
  transition: width 0.5s ease;
}

.strong {
  color: #fff;
}
</style>
