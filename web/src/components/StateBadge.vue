<!-- SPDX-License-Identifier: MIT -->
<!-- Desk state: a small status dot and the state word. -->
<script setup lang="ts">
import { computed } from 'vue'
import type { DeskState } from '../api/types'
import { stateTone } from '../desk'
import { t } from '../i18n'

const props = defineProps<{ state: DeskState }>()
const tone = computed(() => stateTone(props.state))
</script>

<template>
  <span :class="['status', tone]" :data-state="state">{{ t(`state.${state}`) }}</span>
</template>
