<!-- SPDX-License-Identifier: MIT -->
<!-- Diagnostics: metrics and logs behind two tabs. -->
<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { t } from '../i18n'
import LogsView from './LogsView.vue'
import MetricsView from './MetricsView.vue'

defineProps<{ tab: 'metrics' | 'logs' }>()
</script>

<template>
  <section class="diag-page">
    <nav class="tabs" :aria-label="t('nav.diagnostics')">
      <RouterLink to="/diagnostics/metrics" :class="{ active: tab === 'metrics' }">{{ t('diag.metrics') }}</RouterLink>
      <RouterLink to="/diagnostics/logs" :class="{ active: tab === 'logs' }">{{ t('diag.logs') }}</RouterLink>
    </nav>
    <MetricsView v-if="tab === 'metrics'" />
    <LogsView v-else />
  </section>
</template>

<style scoped>
.diag-page {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.tabs {
  display: flex;
  gap: 4px;
  border-bottom: 1px solid rgb(255 255 255 / 8%);
}

.tabs a {
  padding: 10px 16px;
  font-size: 13px;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--muted);
  border-bottom: 2px solid transparent;
  margin-bottom: -1px;
  transition:
    color 0.15s ease,
    border-color 0.15s ease;
}

.tabs a:hover {
  color: #fff;
}

.tabs a.active {
  color: var(--accent);
  border-color: var(--accent);
}
</style>
