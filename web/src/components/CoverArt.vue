<!-- SPDX-License-Identifier: MIT -->
<!-- Generated cover art: gradient in the desk's colours (--cover-* from coverStyle) with two screens drawn on it. -->
<script setup lang="ts">
withDefaults(defineProps<{ fit?: 'slice' | 'meet' }>(), { fit: 'slice' })
</script>

<template>
  <div class="art" aria-hidden="true">
    <div class="glow" />
    <svg class="screens" viewBox="0 0 320 160" :preserveAspectRatio="`xMaxYMid ${fit}`">
      <g fill="none" stroke="#fff" stroke-linejoin="round" stroke-linecap="round">
        <path d="M-20 170 L140 -10" stroke-opacity="0.06" stroke-width="40" />
        <path d="M60 170 L220 -10" stroke-opacity="0.04" stroke-width="22" />
        <rect x="168" y="26" width="104" height="66" rx="4" stroke-opacity="0.32" stroke-width="3" />
        <path d="M206 92 v12 M196 104 h20" stroke-opacity="0.32" stroke-width="3" />
        <rect x="214" y="52" width="104" height="66" rx="4" stroke-opacity="0.55" stroke-width="3" fill="#fff" fill-opacity="0.06" />
        <path d="M252 118 v12 M242 130 h20" stroke-opacity="0.55" stroke-width="3" />
        <path d="M256 74 l14 11 -14 11 z" fill="#fff" fill-opacity="0.5" stroke="none" />
      </g>
    </svg>
    <div class="shade" />
  </div>
</template>

<style scoped>
.art {
  position: absolute;
  inset: 0;
  background: linear-gradient(135deg, var(--cover-dark, #1f4e79) 0%, var(--cover-mid, #2a8bc9) 100%);
}

.glow {
  position: absolute;
  inset: 0;
  background: radial-gradient(90% 120% at 85% 0%, var(--cover-light, #7fd3ff) 0%, transparent 60%);
  opacity: 0.35;
}

.screens {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
}

.shade {
  position: absolute;
  inset: 0;
  background: linear-gradient(180deg, transparent 40%, rgb(10 16 24 / 70%) 100%);
}
</style>
