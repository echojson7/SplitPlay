<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { dismissToast, toasts } from '../toast'
import AppIcon from './AppIcon.vue'
</script>

<template>
  <div class="toasts" aria-live="polite">
    <TransitionGroup name="toast">
      <div v-for="item in toasts" :key="item.id" :class="['toast', item.kind]" :role="item.kind === 'error' ? 'alert' : 'status'">
        <AppIcon :name="item.kind === 'error' ? 'close' : 'check'" :size="22" />
        <span class="text">{{ item.text }}</span>
        <button type="button" class="btn-ghost x" aria-label="×" @click="dismissToast(item.id)">
          <AppIcon name="close" :size="12" />
        </button>
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.toasts {
  position: fixed;
  right: 16px;
  bottom: 16px;
  z-index: 80;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 8px;
  max-width: calc(100vw - 32px);
  pointer-events: none;
}

.toast {
  pointer-events: auto;
  display: flex;
  align-items: center;
  gap: 10px;
  max-width: 420px;
  padding: 10px 8px 10px 12px;
  border-radius: var(--radius);
  background: var(--card);
  border: 1px solid var(--border);
  color: var(--text);
  font-size: 13px;
  box-shadow: var(--shadow-md);
}

.toast > .icon {
  width: 22px;
  height: 22px;
  padding: 4px;
  border-radius: 50%;
}

.toast.ok > .icon {
  background: var(--success-soft);
  color: var(--success-ink);
}

.toast.error > .icon {
  background: var(--danger-soft);
  color: var(--danger-ink);
}

.text {
  flex: 1;
  overflow-wrap: anywhere;
}

.x {
  width: 24px;
  height: 24px;
  padding: 0;
  border-radius: 50%;
}

.toast-enter-active,
.toast-leave-active {
  transition:
    opacity 0.18s var(--ease),
    transform 0.18s var(--ease);
}

.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateY(8px);
}
</style>
