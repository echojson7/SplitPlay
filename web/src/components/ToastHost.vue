<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { dismissToast, toasts } from '../toast'
import AppIcon from './AppIcon.vue'
</script>

<template>
  <div class="toasts" aria-live="polite">
    <TransitionGroup name="toast">
      <div v-for="item in toasts" :key="item.id" :class="['toast', item.kind]" :role="item.kind === 'error' ? 'alert' : 'status'">
        <AppIcon :name="item.kind === 'error' ? 'close' : 'check'" :size="16" />
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
  padding: 10px 8px 10px 14px;
  border-radius: 3px;
  background: linear-gradient(90deg, #2a3f55, #22344a);
  color: #fff;
  font-size: 13px;
  box-shadow: 0 10px 28px rgb(0 0 0 / 50%);
  border-left: 3px solid var(--ok);
}

.toast.ok .icon {
  color: var(--ok);
}

.toast.error {
  border-left-color: var(--bad);
}

.toast.error .icon {
  color: #ff7a66;
}

.text {
  flex: 1;
  overflow-wrap: anywhere;
}

.x {
  width: 24px;
  height: 24px;
  padding: 0;
}

.toast-enter-active,
.toast-leave-active {
  transition:
    opacity 0.2s ease,
    transform 0.2s ease;
}

.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateX(16px);
}
</style>
