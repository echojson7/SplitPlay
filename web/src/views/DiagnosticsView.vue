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
    <header class="page-head">
      <h1>{{ t('nav.diagnostics') }}</h1>
    </header>
    <nav class="pivot" :aria-label="t('nav.diagnostics')">
      <RouterLink to="/diagnostics/metrics" :class="{ active: tab === 'metrics' }">{{ t('diag.metrics') }}</RouterLink>
      <RouterLink to="/diagnostics/logs" :class="{ active: tab === 'logs' }">{{ t('diag.logs') }}</RouterLink>
    </nav>
    <MetricsView v-if="tab === 'metrics'" />
    <LogsView v-else />
  </section>
</template>

<style scoped>
.diag-page .page-head {
  margin-bottom: 12px;
}
</style>
