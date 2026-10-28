<!-- SPDX-License-Identifier: MIT -->
<!-- SplitPlay app icon: one screen split in two panes, in the accent colour. -->
<script setup lang="ts">
withDefaults(defineProps<{ size?: number }>(), { size: 16 })
</script>

<template>
  <svg class="mark" :width="size" :height="size" viewBox="0 0 16 16" aria-hidden="true">
    <rect x="1" y="2.5" width="6.4" height="11" rx="1.4" fill="currentColor" />
    <rect x="8.6" y="2.5" width="6.4" height="11" rx="1.4" fill="currentColor" opacity="0.55" />
  </svg>
</template>

<style scoped>
.mark {
  flex: none;
  display: block;
  color: var(--accent);
}
</style>
