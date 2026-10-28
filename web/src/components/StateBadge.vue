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
  gap: 8px;
  font-size: 13px;
  font-weight: 700;
  white-space: nowrap;
}

.dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: currentcolor;
  flex: none;
}

.ok {
  color: var(--ok);
}

.ok .dot {
  box-shadow: 0 0 8px rgb(139 197 63 / 80%);
}

.busy {
  color: var(--busy);
}

.busy .dot {
  animation: pulse 1.2s ease-in-out infinite;
}

.warn {
  color: var(--warn);
}

.bad {
  color: #ff7a66;
}

.bad .dot {
  box-shadow: 0 0 8px rgb(226 83 63 / 70%);
}

.idle {
  color: var(--muted);
}

@keyframes pulse {
  50% {
    opacity: 0.35;
  }
}
</style>
