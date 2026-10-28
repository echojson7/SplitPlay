<!-- SPDX-License-Identifier: MIT -->
<!-- Small inline SVG icons on a 24×24 grid, drawn for SplitPlay. Stroke icons follow currentColor. -->
<script lang="ts">
function gearPath(): string {
  // Eight trapezoid teeth around the centre.
  const at = (r: number, deg: number) => {
    const a = (deg * Math.PI) / 180
    return `${(12 + r * Math.cos(a)).toFixed(2)} ${(12 + r * Math.sin(a)).toFixed(2)}`
  }
  const pts: string[] = []
  for (let i = 0; i < 8; i++) {
    const base = i * 45
    pts.push(at(7.4, base - 15), at(10, base - 8), at(10, base + 8), at(7.4, base + 15))
  }
  return `M${pts.join(' L')} Z`
}

const ICONS = {
  play: { fill: 'M7 4.5v15l12.5-7.5z' },
  stop: { fill: 'M6 6h12v12H6z' },
  plus: { stroke: 'M12 5v14M5 12h14' },
  gear: { stroke: `${gearPath()} M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0z` },
  copy: { stroke: 'M9 9h10v11H9z M5 15V4h10' },
  check: { stroke: 'M5 12.5l4.5 4.5L19 7.5' },
  close: { stroke: 'M6 6l12 12M18 6L6 18' },
  chevronDown: { stroke: 'M6 9l6 6 6-6' },
  chevronLeft: { stroke: 'M15 5l-7 7 7 7' },
  user: { stroke: 'M16 8a4 4 0 1 1-8 0a4 4 0 1 1 8 0z M4.5 20c.8-3.6 3.8-6 7.5-6s6.7 2.4 7.5 6' },
  monitor: { stroke: 'M3 4.5h18v12H3z M8.5 20.5h7 M12 16.5v4' },
  restart: { stroke: 'M19.5 12a7.5 7.5 0 1 1-2.2-5.3 M19.5 4v4.5H15' },
  logout: { stroke: 'M10 4H5v16h5 M14.5 8l4 4-4 4 M18.5 12H9' },
  trash: { stroke: 'M4.5 7h15 M9.5 7V4.5h5V7 M6.5 7l1 13h9l1-13' },
  laptop: { stroke: 'M5 5.5h14v10H5z M2.5 18.5h19' },
} as const

export type IconName = keyof typeof ICONS

function iconPaths(name: IconName): { fill?: string; stroke?: string } {
  return ICONS[name]
}
</script>

<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{ name: IconName; size?: number }>(), { size: 18 })
const paths = computed(() => iconPaths(props.name))
</script>

<template>
  <svg class="icon" :width="size" :height="size" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
    <path v-if="paths.fill" :d="paths.fill" fill="currentColor" />
    <path
      v-if="paths.stroke"
      :d="paths.stroke"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
    />
  </svg>
</template>

<style scoped>
.icon {
  flex: none;
  display: block;
}
</style>
