<!-- SPDX-License-Identifier: MIT -->
<!-- One desk in the list: name and picture settings, status, Start / Stop and a "more" menu (PIN, settings). -->
<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import type { Desk } from '../api/types'
import { canStart } from '../composables'
import { deskProgress, liveStream, stateTone, streamLine } from '../desk'
import { t } from '../i18n'
import AppIcon from './AppIcon.vue'
import StateBadge from './StateBadge.vue'

const props = withDefaults(defineProps<{ desk: Desk; busy?: boolean }>(), { busy: false })
const emit = defineEmits<{ start: []; stop: []; pair: [] }>()
const router = useRouter()

const tone = computed(() => stateTone(props.desk.state))
const progress = computed(() => deskProgress(props.desk.state))
const pairable = computed(() => props.desk.state === 'Ready' || props.desk.state === 'Streaming')
const link = computed(() => `/desks/${encodeURIComponent(props.desk.id)}`)
const live = computed(() => {
  const stream = liveStream(props.desk)
  return stream ? streamLine(stream, props.desk.metrics) : null
})

// ---- "more" menu
const menuOpen = ref(false)
const menuWrap = ref<HTMLElement | null>(null)

function onOutside(e: Event): void {
  if (!menuWrap.value?.contains(e.target as Node)) menuOpen.value = false
}
function onKey(e: KeyboardEvent): void {
  if (e.key === 'Escape') menuOpen.value = false
}
watch(menuOpen, (open) => {
  if (open) {
    document.addEventListener('pointerdown', onOutside)
    document.addEventListener('keydown', onKey)
  } else {
    document.removeEventListener('pointerdown', onOutside)
    document.removeEventListener('keydown', onKey)
  }
})
onUnmounted(() => (menuOpen.value = false))

function pair(): void {
  menuOpen.value = false
  emit('pair')
}

/** The whole row opens the desk; its own controls do not. */
function openRow(e: MouseEvent): void {
  if ((e.target as Element).closest('button, a, [role="menu"]')) return
  void router.push(link.value)
}
</script>

<template>
  <article :class="['srow', 'clickable', 'desk', tone]" :data-desk="desk.id" @click="openRow">
    <AppIcon class="row-icon" name="monitor" :size="16" />
    <div class="text">
      <RouterLink :to="link" class="title name">{{ desk.name }}</RouterLink>
      <span v-if="live" class="desc" data-live>{{ live }}</span>
      <span v-else class="desc">{{ desk.user }} · {{ desk.profile.width }}×{{ desk.profile.height }} · {{ desk.profile.fps }} FPS</span>
      <span v-if="desk.state === 'Faulted' && desk.lastError" class="desc" :title="desk.lastError">{{ desk.lastError }}</span>
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
      <StateBadge v-else :state="desk.state" class="state" />

      <button v-if="!progress && canStart(desk)" type="button" class="btn-accent main" data-action="start" :disabled="busy" @click="emit('start')">
        {{ t('desk.start') }}
      </button>
      <button
        v-else-if="!progress || desk.state !== 'Stopping'"
        type="button"
        class="main"
        data-action="stop"
        :disabled="busy"
        @click="emit('stop')"
      >
        {{ t('desk.stop') }}
      </button>

      <div ref="menuWrap" class="menu-wrap">
        <button
          type="button"
          class="btn-icon"
          data-action="more"
          aria-haspopup="menu"
          :aria-expanded="menuOpen"
          :title="t('common.more')"
          :aria-label="t('common.more')"
          @click="menuOpen = !menuOpen"
        >
          <AppIcon name="more" :size="16" />
        </button>
        <div v-show="menuOpen" class="menu" role="menu">
          <button type="button" role="menuitem" data-action="pair" :disabled="!pairable" @click="pair">
            <AppIcon name="link" :size="16" />{{ t('desk.pinTitle') }}
          </button>
          <RouterLink :to="link" role="menuitem" class="btn" data-action="settings" @click="menuOpen = false">
            <AppIcon name="gear" :size="16" />{{ t('nav.settings') }}
          </RouterLink>
        </div>
      </div>
    </div>
  </article>
</template>

<style scoped>
.name {
  color: var(--text);
}

.main {
  min-width: 120px;
}

.progress {
  width: 140px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ptext {
  font-size: 12px;
  line-height: 16px;
  color: var(--text-2);
}

.menu-wrap {
  position: relative;
}

.menu {
  position: absolute;
  top: calc(100% + 4px);
  right: 0;
  z-index: 30;
  min-width: 220px;
  padding: 4px;
  background: var(--flyout);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: var(--shadow-flyout);
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.menu > button,
.menu > a {
  justify-content: flex-start;
  gap: 12px;
  width: 100%;
  height: 36px;
  padding: 0 12px;
  background: transparent;
  border-color: transparent;
  color: var(--text);
}

.menu > button:hover:not(:disabled),
.menu > a:hover {
  background: var(--subtle-hover);
}

.menu > button:disabled {
  color: var(--disabled);
}

@media (width <= 600px) {
  .row-icon {
    display: none;
  }

  .main {
    min-width: 0;
  }

  .progress {
    width: 96px;
  }
}
</style>
