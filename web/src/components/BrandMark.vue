<!-- SPDX-License-Identifier: MIT -->
<!-- SplitPlay logo: one screen split in two panes with a play sign across the split. -->
<script setup lang="ts">
withDefaults(defineProps<{ size?: number }>(), { size: 28 })
</script>

<template>
  <svg class="mark" :width="size" :height="size" viewBox="0 0 32 32" aria-hidden="true">
    <rect x="2" y="5" width="13" height="22" rx="3" fill="#66c0f4" />
    <rect x="17" y="5" width="13" height="22" rx="3" fill="#1a6fd8" />
    <path d="M12.5 10.5 L23 16 L12.5 21.5 Z" fill="#fff" />
  </svg>
</template>

<style scoped>
.mark {
  flex: none;
  display: block;
  filter: drop-shadow(0 0 6px rgb(102 192 244 / 35%));
}
</style>
