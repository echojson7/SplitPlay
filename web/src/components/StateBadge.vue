<!-- SPDX-License-Identifier: MIT -->
<!-- Coloured status dot with a short state word. -->
<script setup lang="ts">
import { computed } from 'vue'
import type { DeskState } from '../api/types'
import { stateTone } from '../desk'
import { t } from '../i18n'

const props = defineProps<{ state: DeskState }>()
const tone = computed(() => stateTone(props.state))
</script>

<template>
  <span :class="['state', tone]" :data-state="state">
    <span class="dot" />
    {{ t(`state.${state}`) }}
  </span>
</template>

<style scoped>
.state {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 26px;
  padding: 0 10px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  transition:
    background 0.18s ease,
    color 0.18s ease;
}

.dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex: none;
}

.ok {
  background: var(--success-soft);
  color: var(--success-ink);
}

.ok .dot {
  background: var(--success);
}

.busy {
  background: var(--primary-soft);
  color: var(--primary-ink);
}

.busy .dot {
  background: var(--primary);
  animation: pulse 1.2s ease-in-out infinite;
}

.warn {
  background: var(--warning-soft);
  color: var(--warning-ink);
}

.warn .dot {
  background: var(--warning);
}

.bad {
  background: var(--danger-soft);
  color: var(--danger-ink);
}

.bad .dot {
  background: var(--danger);
}

.idle {
  background: var(--idle-soft);
  color: var(--idle-ink);
}

.idle .dot {
  background: var(--faint);
}

@keyframes pulse {
  50% {
    opacity: 0.35;
  }
}
</style>
