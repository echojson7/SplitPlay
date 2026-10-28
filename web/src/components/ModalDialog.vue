<!-- SPDX-License-Identifier: MIT -->
<!-- Modal dialog: dimmed backdrop, panel with a title bar. Escape or the backdrop closes it. -->
<script setup lang="ts">
import { nextTick, onUnmounted, ref, watch } from 'vue'
import { t } from '../i18n'
import AppIcon from './AppIcon.vue'

const props = withDefaults(defineProps<{ open: boolean; title: string; width?: number }>(), { width: 440 })
const emit = defineEmits<{ close: [] }>()
const panel = ref<HTMLElement | null>(null)

function onKey(e: KeyboardEvent): void {
  if (e.key === 'Escape') emit('close')
}

watch(
  () => props.open,
  async (open) => {
    if (open) {
      document.addEventListener('keydown', onKey)
      await nextTick()
      const first = panel.value?.querySelector<HTMLElement>('[autofocus], input:not([disabled]), button.primary-action')
      first?.focus()
    } else {
      document.removeEventListener('keydown', onKey)
    }
  },
  { immediate: true, flush: 'post' },
)
onUnmounted(() => document.removeEventListener('keydown', onKey))
</script>

<template>
  <Transition name="modal">
    <div v-if="open" class="backdrop" @mousedown.self="emit('close')">
      <div ref="panel" class="modal" role="dialog" aria-modal="true" :aria-label="title" :style="{ width: `min(${width}px, calc(100vw - 32px))` }">
        <header>
          <h2>{{ title }}</h2>
          <button type="button" class="btn-ghost close" :aria-label="t('common.close')" @click="emit('close')">
            <AppIcon name="close" :size="16" />
          </button>
        </header>
        <div class="body">
          <slot />
        </div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.backdrop {
  position: fixed;
  inset: 0;
  z-index: 50;
  display: grid;
  place-items: center;
  padding: 16px;
  background: rgb(8 12 18 / 72%);
  backdrop-filter: blur(2px);
  overflow-y: auto;
}

.modal {
  background: linear-gradient(180deg, #2a3f55 0%, #1e2d3d 60%, #1b2838 100%);
  border: 1px solid rgb(102 192 244 / 18%);
  border-radius: 4px;
  box-shadow: 0 20px 60px rgb(0 0 0 / 60%);
  max-height: calc(100vh - 32px);
  display: flex;
  flex-direction: column;
}

header {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 16px 16px 0 24px;
}

header h2 {
  flex: 1;
  font-size: 20px;
  font-weight: 700;
  color: #fff;
}

.close {
  width: 32px;
  height: 32px;
  padding: 0;
}

.body {
  padding: 18px 24px 24px;
  overflow-y: auto;
}

.modal-enter-active,
.modal-leave-active {
  transition: opacity 0.18s ease;
}

.modal-enter-active .modal,
.modal-leave-active .modal {
  transition: transform 0.18s ease;
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
}

.modal-enter-from .modal {
  transform: translateY(8px) scale(0.98);
}
</style>
