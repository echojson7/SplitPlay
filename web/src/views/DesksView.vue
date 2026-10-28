<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink } from 'vue-router'
import { api } from '../api/client'
import type { Desk } from '../api/types'
import StateBadge from '../components/StateBadge.vue'
import { canStart, canStop, errorText, gpuLabel, useLive } from '../composables'
import { fmtBitrate, fmtNumber, t } from '../i18n'
import { store } from '../store'

useLive()
const s = store.state
const error = ref<string | null>(null)
const busy = ref<string | null>(null)

async function act(desk: Desk, action: 'start' | 'stop'): Promise<void> {
  busy.value = desk.id
  error.value = null
  try {
    store.upsertDesk(action === 'start' ? await api.startDesk(desk.id) : await api.stopDesk(desk.id))
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <section>
    <div class="row">
      <h1>{{ t('desks.title') }}</h1>
      <div class="spacer" />
      <RouterLink to="/desks/new" class="button primary">{{ t('desks.new') }}</RouterLink>
    </div>
    <p v-if="error" class="notice bad" role="alert">{{ t('common.error', { message: error }) }}</p>
    <p v-if="!s.desksLoaded" class="muted">{{ t('common.loading') }}</p>
    <p v-else-if="s.desks.length === 0" class="card muted">{{ t('desks.empty') }}</p>
    <div v-else class="card table-wrap">
      <table>
        <thead>
          <tr>
            <th>{{ t('desks.col.name') }}</th>
            <th>{{ t('desks.col.state') }}</th>
            <th class="num">{{ t('desks.col.fps') }}</th>
            <th class="num">{{ t('desks.col.bitrate') }}</th>
            <th>{{ t('desks.col.gpu') }}</th>
            <th class="num">{{ t('desks.col.clients') }}</th>
            <th class="num">{{ t('desks.col.port') }}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="d in s.desks" :key="d.id" :data-desk="d.id">
            <td>
              <RouterLink :to="`/desks/${d.id}`">{{ d.name }}</RouterLink>
              <div class="muted mono small">{{ d.id }}</div>
            </td>
            <td>
              <StateBadge :state="d.state" />
              <div v-if="d.lastError" class="error small" :title="d.lastError">{{ d.lastError }}</div>
            </td>
            <td class="num">{{ fmtNumber(d.metrics.fps) }} <span class="muted">/ {{ d.profile.fps }}</span></td>
            <td class="num">{{ fmtBitrate(d.metrics.bitrateKbps) }}</td>
            <td>{{ gpuLabel(d.gpu, s.metrics?.host.gpus) }}</td>
            <td class="num">{{ d.metrics.clients }}</td>
            <td class="num mono">{{ d.basePort }}</td>
            <td class="num">
              <button v-if="canStart(d)" type="button" :disabled="busy === d.id" @click="act(d, 'start')">
                {{ t('desk.start') }}
              </button>
              <button v-else-if="canStop(d)" type="button" :disabled="busy === d.id" @click="act(d, 'stop')">
                {{ t('desk.stop') }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<style scoped>
.small {
  font-size: 0.78rem;
  max-width: 28ch;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
