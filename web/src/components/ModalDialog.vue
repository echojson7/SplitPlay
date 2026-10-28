<!-- SPDX-License-Identifier: MIT -->
<!-- Modal dialog (ContentDialog): dimmed backdrop, title, content and a command strip. Escape closes it. -->
<script setup lang="ts">
import { nextTick, onUnmounted, ref, watch } from 'vue'

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
    <div v-if="open" class="backdrop">
      <div ref="panel" class="modal" role="dialog" aria-modal="true" :aria-label="title" :style="{ width: `min(${width}px, calc(100vw - 32px))` }">
        <div class="body">
          <h2 class="dlg-title">{{ title }}</h2>
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
  background: var(--smoke);
  overflow-y: auto;
}

.modal {
  background: var(--dialog);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: var(--shadow-dialog);
  max-height: calc(100vh - 32px);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.body {
  padding: 24px 24px 0;
  overflow-y: auto;
}

.dlg-title {
  font-size: 20px;
  line-height: 28px;
  font-weight: 600;
  margin-bottom: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ContentDialog command area: buttons on a darker strip at the bottom. */
.body :deep(.dlg-foot) {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  margin: 24px -24px 0;
  padding: 24px;
  background: var(--dialog-footer);
  border-top: 1px solid var(--border);
}

.body :deep(.dlg-foot) > button {
  min-width: 120px;
}

.modal-enter-active,
.modal-leave-active {
  transition: opacity 0.15s ease;
}

.modal-enter-active .modal,
.modal-leave-active .modal {
  transition: transform 0.15s cubic-bezier(0.2, 0, 0, 1);
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
}

.modal-enter-from .modal {
  transform: scale(1.05);
}
</style>
