<!-- SPDX-License-Identifier: MIT -->
<!-- The desk's page icon (Notion-style): a rounded tile with a monitor glyph in the desk colour. -->
<script setup lang="ts">
withDefaults(defineProps<{ size?: number }>(), { size: 52 })
</script>

<template>
  <span class="glyph" :style="{ width: `${size}px`, height: `${size}px`, borderRadius: `${Math.round(size * 0.28)}px` }" aria-hidden="true">
    <svg :width="Math.round(size * 0.52)" :height="Math.round(size * 0.52)" viewBox="0 0 24 24">
      <rect x="2.5" y="4" width="19" height="12.5" rx="2.5" fill="currentColor" fill-opacity="0.14" stroke="currentColor" stroke-width="1.8" />
      <path d="M8.5 20.5h7 M12 16.5v4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" fill="none" />
      <path d="M10.5 8 L14.5 10.25 L10.5 12.5 Z" fill="currentColor" />
    </svg>
  </span>
</template>

<style scoped>
.glyph {
  display: grid;
  place-items: center;
  flex: none;
  background: var(--card);
  color: var(--cover-ink, var(--primary));
  border: 1px solid var(--border);
  box-shadow: var(--shadow-sm);
}

/* Dark theme: lift the desk colour so the glyph reads on the dark tile. */
:root[data-theme='dark'] .glyph {
  color: color-mix(in srgb, var(--cover-ink, var(--primary)) 55%, #fff);
}

@media (prefers-color-scheme: dark) {
  :root:not([data-theme='light']) .glyph {
    color: color-mix(in srgb, var(--cover-ink, var(--primary)) 55%, #fff);
  }
}
</style>
