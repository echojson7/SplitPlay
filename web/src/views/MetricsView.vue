<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed } from 'vue'
import { useLive } from '../composables'
import { fmtBitrate, fmtNumber, t } from '../i18n'
import { store } from '../store'

useLive()
const s = store.state
const host = computed(() => s.metrics?.host ?? null)

function pct(used?: number, total?: number): number | null {
  if (used === undefined || !total) return null
  return Math.round((used / total) * 100)
}

function gb(mb?: number): string {
  return mb === undefined ? t('common.none') : t('unit.gb', { value: fmtNumber(mb / 1024, 1) })
}

function deskName(id: string): string {
  return s.desks.find((d) => d.id === id)?.name ?? id
}
</script>

<template>
  <section>
    <h1>{{ t('metrics.title') }}</h1>
    <p v-if="!host" class="muted">{{ t('metrics.noData') }}</p>
    <template v-else>
      <h2>{{ t('metrics.host') }}</h2>
      <div class="tiles">
        <div class="card tile">
          <div class="muted">{{ t('metrics.cpu') }}</div>
          <div class="big">{{ host.cpuPercent === undefined ? t('common.none') : t('unit.percent', { value: host.cpuPercent }) }}</div>
          <meter v-if="host.cpuPercent !== undefined" min="0" max="100" :value="host.cpuPercent" />
        </div>
        <div class="card tile">
          <div class="muted">{{ t('metrics.memory') }}</div>
          <div class="big">{{ gb(host.memoryUsedMb) }} <span class="muted small">/ {{ gb(host.memoryTotalMb) }}</span></div>
          <meter
            v-if="pct(host.memoryUsedMb, host.memoryTotalMb) !== null"
            min="0"
            max="100"
            :value="pct(host.memoryUsedMb, host.memoryTotalMb) ?? 0"
          />
        </div>
        <div class="card tile">
          <div class="muted">{{ t('metrics.compose') }}</div>
          <div class="big">{{ host.composeHz === undefined ? t('common.none') : `${host.composeHz} Hz` }}</div>
        </div>
      </div>

      <h2>{{ t('metrics.gpus') }}</h2>
      <div class="card table-wrap">
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
              <td>
                {{ g.name }}
                <div class="muted mono small">{{ g.luid }}</div>
              </td>
              <td class="num">{{ g.utilizationPercent ?? t('common.none') }}</td>
              <td class="num">{{ g.encoderPercent ?? t('common.none') }}</td>
              <td class="num">{{ gb(g.vramUsedMb) }} / {{ gb(g.vramTotalMb) }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <h2>{{ t('metrics.desks') }}</h2>
      <div class="card table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ t('desks.col.name') }}</th>
              <th class="num">{{ t('desks.col.fps') }}</th>
              <th class="num">{{ t('desks.col.bitrate') }}</th>
              <th class="num">{{ t('desk.encodeP99') }}</th>
              <th class="num">{{ t('desks.col.clients') }}</th>
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
  </section>
</template>

<style scoped>
.tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 1rem;
}

.big {
  font-size: 1.6rem;
  font-variant-numeric: tabular-nums;
}

.small {
  font-size: 0.8rem;
}

meter {
  width: 100%;
}
</style>
