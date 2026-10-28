<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed } from 'vue'
import type { DeskState } from '../api/types'
import { t } from '../i18n'

const props = defineProps<{ state: DeskState }>()

const tone = computed(() => {
  switch (props.state) {
    case 'Streaming':
    case 'Ready':
      return 'ok'
    case 'Faulted':
      return 'bad'
    case 'Recovering':
      return 'warn'
    case 'Stopped':
    case 'Defined':
      return 'idle'
    default:
      return 'busy'
  }
})
</script>

<template>
  <span :class="['badge', tone]" :data-state="state">{{ t(`state.${state}`) }}</span>
</template>

<style scoped>
.badge {
  display: inline-flex;
  align-items: center;
  gap: 0.4em;
  font-size: 0.85rem;
  padding: 0.1rem 0.55rem;
  border-radius: 999px;
  border: 1px solid currentcolor;
  white-space: nowrap;
}

.badge::before {
  content: '';
  width: 0.5em;
  height: 0.5em;
  border-radius: 50%;
  background: currentcolor;
}

.ok {
  color: var(--ok);
}

.bad {
  color: var(--bad);
}

.warn {
  color: var(--warn);
}

.busy {
  color: var(--info);
}

.idle {
  color: var(--muted);
}
</style>
