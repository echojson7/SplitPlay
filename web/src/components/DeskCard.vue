<!-- SPDX-License-Identifier: MIT -->
<!-- Library card of one desk: cover, status, picture settings and the Play / Stop button. -->
<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import type { Desk } from '../api/types'
import { canStart } from '../composables'
import { coverStyle, deskProgress, stateTone } from '../desk'
import { t } from '../i18n'
import AppIcon from './AppIcon.vue'
import CoverArt from './CoverArt.vue'
import StateBadge from './StateBadge.vue'

const props = withDefaults(defineProps<{ desk: Desk; busy?: boolean }>(), { busy: false })
const emit = defineEmits<{ start: []; stop: []; pair: [] }>()

const tone = computed(() => stateTone(props.desk.state))
const progress = computed(() => deskProgress(props.desk.state))
const pairable = computed(() => props.desk.state === 'Ready' || props.desk.state === 'Streaming')
const link = computed(() => `/desks/${encodeURIComponent(props.desk.id)}`)
</script>

<template>
  <article :class="['card', tone]" :style="coverStyle(desk.id)" :data-desk="desk.id">
    <RouterLink :to="link" class="cover" :aria-label="desk.name">
      <CoverArt />
      <span class="cover-name">{{ desk.name }}</span>
    </RouterLink>

    <div class="info">
      <div class="line">
        <StateBadge :state="desk.state" />
        <span class="spec">{{ desk.profile.width }}×{{ desk.profile.height }} · {{ desk.profile.fps }} FPS</span>
      </div>
      <div class="line sub">
        <span class="who" :title="t('desk.user')"><AppIcon name="user" :size="14" />{{ desk.user }}</span>
        <span v-if="desk.metrics.clients > 0" class="who" :title="t('desk.devices')">
          <AppIcon name="laptop" :size="14" />{{ desk.metrics.clients }}
        </span>
      </div>
      <p v-if="desk.state === 'Faulted' && desk.lastError" class="fault" :title="desk.lastError">{{ desk.lastError }}</p>

      <div class="actions">
        <div v-if="progress" class="progress" role="progressbar" :aria-valuenow="Math.round(progress.value * 100)" aria-valuemin="0" aria-valuemax="100">
          <div class="progress-top">
            <span>{{ t(`state.${progress.state}`) }}</span>
            <span class="pct">{{ Math.round(progress.value * 100) }}%</span>
          </div>
          <div class="bar"><span :style="{ width: `${progress.value * 100}%` }" /></div>
        </div>
        <button
          v-else-if="canStart(desk)"
          type="button"
          class="btn-play main"
          data-action="start"
          :disabled="busy"
          @click="emit('start')"
        >
          <AppIcon name="play" :size="16" />{{ t('desk.start') }}
        </button>
        <button v-else type="button" class="btn-stop main" data-action="stop" :disabled="busy" @click="emit('stop')">
          <AppIcon name="stop" :size="14" />{{ t('desk.stop') }}
        </button>

        <button
          v-if="progress && desk.state !== 'Stopping'"
          type="button"
          class="btn-icon"
          data-action="stop"
          :title="t('desk.stop')"
          :aria-label="t('desk.stop')"
          :disabled="busy"
          @click="emit('stop')"
        >
          <AppIcon name="close" :size="16" />
        </button>
        <button
          v-else
          type="button"
          class="btn-icon"
          data-action="pair"
          :title="pairable ? t('desk.pinTitle') : t('desk.pairNeedsRunning')"
          :aria-label="t('desk.pinTitle')"
          :disabled="!pairable"
          @click="emit('pair')"
        >
          {{ t('desk.pin') }}
        </button>
        <RouterLink :to="link" class="btn btn-icon" data-action="settings" :title="t('desk.settings')" :aria-label="t('desk.settings')">
          <AppIcon name="gear" :size="18" />
        </RouterLink>
      </div>
    </div>
  </article>
</template>

<style scoped>
.card {
  display: flex;
  flex-direction: column;
  border-radius: 4px;
  overflow: hidden;
  background: linear-gradient(180deg, #24384d 0%, #1d2d3e 100%);
  box-shadow: 0 4px 14px rgb(0 0 0 / 30%);
  transition:
    transform 0.18s ease,
    box-shadow 0.18s ease;
  min-width: 0;
}

.card:hover {
  transform: translateY(-3px);
  box-shadow:
    0 0 0 1px rgb(102 192 244 / 35%),
    0 12px 30px rgb(0 0 0 / 45%);
}

.cover {
  position: relative;
  display: block;
  height: 148px;
  overflow: hidden;
  color: #fff;
  transition: filter 0.25s ease;
}

.idle .cover {
  filter: saturate(0.45) brightness(0.82);
}

.card:hover .cover {
  filter: none;
}

.ok .cover::after {
  content: '';
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 3px;
  background: var(--ok);
  box-shadow: 0 0 12px var(--ok);
}

.bad .cover::after {
  content: '';
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 3px;
  background: var(--bad);
}

.cover-name {
  position: absolute;
  left: 16px;
  right: 16px;
  bottom: 12px;
  font-size: 22px;
  font-weight: 700;
  line-height: 1.15;
  text-shadow: 0 2px 8px rgb(0 0 0 / 55%);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.info {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 14px 16px 16px;
  flex: 1;
}

.line {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  min-width: 0;
}

.spec {
  font-size: 13px;
  color: var(--text);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.sub {
  justify-content: flex-start;
  gap: 16px;
  color: var(--muted);
  font-size: 13px;
}

.who {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fault {
  margin: 0;
  font-size: 12px;
  color: #ff8a7a;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: auto;
  padding-top: 6px;
}

.main {
  flex: 1;
  min-width: 0;
}

.progress {
  flex: 1;
  min-width: 0;
  height: 40px;
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 6px;
}

.progress-top {
  display: flex;
  justify-content: space-between;
  font-size: 12px;
  font-weight: 700;
  color: var(--accent);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.pct {
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.bar {
  height: 6px;
  border-radius: 3px;
  background: rgb(0 0 0 / 35%);
  overflow: hidden;
}

.bar span {
  display: block;
  height: 100%;
  border-radius: 3px;
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
</style>
