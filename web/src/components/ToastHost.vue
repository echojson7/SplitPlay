<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { dismissToast, toasts } from '../toast'
import AppIcon from './AppIcon.vue'
</script>

<template>
  <div class="toasts" aria-live="polite">
    <TransitionGroup name="toast">
      <div v-for="item in toasts" :key="item.id" :class="['toast', item.kind]" :role="item.kind === 'error' ? 'alert' : 'status'">
        <span :class="['status', item.kind === 'error' ? 'bad' : 'ok']" />
        <span class="text">{{ item.text }}</span>
        <button type="button" class="btn-icon x" aria-label="×" @click="dismissToast(item.id)">
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
  z-index: 60;
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: min(360px, calc(100vw - 32px));
}

.toast {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 48px;
  padding: 6px 6px 6px 16px;
  background: var(--flyout);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: var(--shadow-flyout);
  color: var(--text);
}

.status {
  gap: 0;
}

.text {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
}

.x {
  color: var(--text-2);
}

.toast-enter-active,
.toast-leave-active {
  transition:
    opacity 0.15s ease,
    transform 0.15s ease;
}

.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateY(8px);
}
</style>
