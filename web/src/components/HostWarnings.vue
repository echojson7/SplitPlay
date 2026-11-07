<!-- SPDX-License-Identifier: MIT -->
<!-- Host problems from the live metrics (GPU encoder or VRAM overloaded, a recent graphics driver reset). Nothing when all is well. -->
<script setup lang="ts">
import { computed } from 'vue'
import type { HostWarning } from '../api/types'
import { fmtPercent } from '../desk'
import { locale, t } from '../i18n'
import { store } from '../store'
import AppIcon from './AppIcon.vue'

const KNOWN = ['gpuReset', 'encoderBusy', 'vramFull']
const warnings = computed<HostWarning[]>(() => (store.state.metrics?.host.warnings ?? []).filter((w) => KNOWN.includes(w.code)))

function time(iso?: string | null): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return new Intl.DateTimeFormat(locale.value === 'ru' ? 'ru-RU' : 'en-US', { timeStyle: 'short' }).format(d)
}

function title(w: HostWarning): string {
  if (w.code === 'gpuReset') return t('warning.gpuReset')
  return w.code === 'encoderBusy' ? t('warning.encoderBusy') : t('warning.vramFull')
}

function desc(w: HostWarning): string {
  if (w.code === 'gpuReset') return w.desks ? t('warning.gpuResetDesks', { count: w.desks }) : t('warning.gpuResetNoDesks')
  return w.gpu ?? ''
}

function value(w: HostWarning): string {
  return w.code === 'gpuReset' ? time(w.at) : fmtPercent(w.percent)
}
</script>

<template>
  <div v-if="warnings.length" class="rows host-warnings" data-host-warnings>
    <div v-for="w in warnings" :key="w.code + (w.gpu ?? '')" class="srow" :data-warning="w.code">
      <AppIcon :name="w.code === 'gpuReset' ? 'restart' : 'chip'" :size="16" />
      <div class="text">
        <span class="title">{{ title(w) }}</span>
        <span v-if="desc(w)" class="desc" :title="desc(w)">{{ desc(w) }}</span>
      </div>
      <div class="ctl">{{ value(w) }}</div>
    </div>
  </div>
</template>

<style scoped>
.host-warnings {
  margin-bottom: 16px;
}

.host-warnings .ctl {
  color: var(--text-2);
  font-size: 14px;
}
</style>
