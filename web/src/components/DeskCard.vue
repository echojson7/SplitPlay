<!-- SPDX-License-Identifier: MIT -->
<!-- Library card of one desk: soft cover with the desk icon, state chip, picture settings and Start / Stop. -->
<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import type { Desk } from '../api/types'
import { canStart } from '../composables'
import { coverStyle, deskProgress, stateTone } from '../desk'
import { t } from '../i18n'
import AppIcon from './AppIcon.vue'
import CoverArt from './CoverArt.vue'
import DeskGlyph from './DeskGlyph.vue'
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
    </RouterLink>
    <div class="glyph-wrap"><DeskGlyph :size="48" /></div>

    <div class="info">
      <div class="title-row">
        <RouterLink :to="link" class="name">{{ desk.name }}</RouterLink>
        <StateBadge :state="desk.state" />
      </div>
      <div class="meta">
        <span class="who" :title="t('desk.user')"><AppIcon name="user" :size="14" /><span class="ell">{{ desk.user }}</span></span>
        <span class="dotsep" />
        <span class="spec">{{ desk.profile.width }}×{{ desk.profile.height }} · {{ desk.profile.fps }} FPS</span>
        <template v-if="desk.metrics.clients > 0">
          <span class="dotsep" />
          <span class="who" :title="t('desk.devices')"><AppIcon name="laptop" :size="14" />{{ desk.metrics.clients }}</span>
        </template>
      </div>
      <p v-if="desk.state === 'Faulted' && desk.lastError" class="fault" :title="desk.lastError">{{ desk.lastError }}</p>

      <div class="actions">
        <div
          v-if="progress"
          class="progress"
          role="progressbar"
          :aria-valuenow="Math.round(progress.value * 100)"
          aria-valuemin="0"
          aria-valuemax="100"
        >
          <div class="progress-top">
            <span>{{ t(`state.${progress.state}`) }}</span>
            <span class="pct">{{ Math.round(progress.value * 100) }}%</span>
          </div>
          <div class="bar"><span :style="{ width: `${progress.value * 100}%` }" /></div>
        </div>
        <button
          v-else-if="canStart(desk)"
          type="button"
          class="btn-success main"
          data-action="start"
          :disabled="busy"
          @click="emit('start')"
        >
          <AppIcon name="play" :size="15" />{{ t('desk.start') }}
        </button>
        <button v-else type="button" class="btn-danger main" data-action="stop" :disabled="busy" @click="emit('stop')">
          <AppIcon name="stop" :size="13" />{{ t('desk.stop') }}
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
  position: relative;
  display: flex;
  flex-direction: column;
  border-radius: var(--radius-lg);
  overflow: hidden;
  background: var(--card);
  border: 1px solid var(--border);
  box-shadow: var(--shadow-sm);
  transition:
    transform 0.18s var(--ease),
    box-shadow 0.18s var(--ease),
    border-color 0.18s var(--ease);
  min-width: 0;
}

.card:hover {
  transform: translateY(-2px);
  box-shadow: var(--shadow-md);
  border-color: var(--border-strong);
}

.cover {
  position: relative;
  display: block;
  height: 88px;
  overflow: hidden;
}

.idle .cover {
  filter: saturate(0.5);
}

.glyph-wrap {
  position: absolute;
  top: 60px;
  left: 18px;
}

.info {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 28px 18px 18px;
  flex: 1;
}

.title-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  min-width: 0;
}

.name {
  font-size: 17px;
  font-weight: 600;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.name:hover {
  color: var(--primary);
}

.meta {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--muted);
  font-size: 13px;
  min-width: 0;
}

.dotsep {
  flex: none;
  width: 3px;
  height: 3px;
  border-radius: 50%;
  background: var(--faint);
}

.who {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ell {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spec {
  flex: none;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.fault {
  margin: 0;
  font-size: 12px;
  color: var(--danger-ink);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: auto;
  padding-top: 12px;
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
  font-weight: 500;
  color: var(--primary-ink);
}

.pct {
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.bar {
  height: 6px;
  border-radius: 3px;
  background: var(--fill);
  overflow: hidden;
}

.bar span {
  position: relative;
  display: block;
  height: 100%;
  border-radius: 3px;
  background: var(--primary);
  overflow: hidden;
  transition: width 0.5s var(--ease);
}

.bar span::after {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(90deg, transparent, rgb(255 255 255 / 45%), transparent);
  animation: shimmer 1.4s linear infinite;
}

@keyframes shimmer {
  from {
    transform: translateX(-100%);
  }

  to {
    transform: translateX(100%);
  }
}
</style>
