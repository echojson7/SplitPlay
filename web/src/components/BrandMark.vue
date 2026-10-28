<!-- SPDX-License-Identifier: MIT -->
<!-- SplitPlay logo: a rounded tile with one screen split in two panes and a play sign across the split. -->
<script setup lang="ts">
withDefaults(defineProps<{ size?: number }>(), { size: 28 })
</script>

<template>
  <svg class="mark" :width="size" :height="size" viewBox="0 0 32 32" aria-hidden="true">
    <defs>
      <linearGradient id="splitplay-mark" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#338ef7" />
        <stop offset="1" stop-color="#006fee" />
      </linearGradient>
    </defs>
    <rect width="32" height="32" rx="9" fill="url(#splitplay-mark)" />
    <rect x="6.5" y="9" width="8.5" height="14" rx="2.2" fill="#fff" fill-opacity="0.5" />
    <rect x="17" y="9" width="8.5" height="14" rx="2.2" fill="#fff" fill-opacity="0.92" />
    <path d="M13.2 12.4 L20.4 16 L13.2 19.6 Z" fill="#006fee" stroke="#fff" stroke-width="1.2" stroke-linejoin="round" />
  </svg>
</template>

<style scoped>
.mark {
  flex: none;
  display: block;
  border-radius: 9px;
  box-shadow: 0 1px 2px rgb(0 111 238 / 25%);
}
</style>
