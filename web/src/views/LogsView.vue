<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { api } from '../api/client'
import type { LogLine } from '../api/types'
import { errorText } from '../composables'
import { fmtDateTime, t } from '../i18n'
import { store } from '../store'

const COMPONENTS = ['service', 'worker', 'seat', 'stubworker', 'tray', 'cli']
const TAILS = [100, 200, 500, 1000, 5000]

const filter = reactive({ component: '', desk: '', tail: 200 })
const follow = ref(true)
const lines = ref<LogLine[]>([])
const error = ref<string | null>(null)
const loading = ref(false)
let timer: ReturnType<typeof setInterval> | undefined

async function load(): Promise<void> {
  loading.value = true
  try {
    lines.value = await api.logs({ component: filter.component || undefined, desk: filter.desk || undefined, tail: filter.tail })
    error.value = null
  } catch (e) {
    error.value = errorText(e)
  } finally {
    loading.value = false
  }
}

function schedule(): void {
  clearInterval(timer)
  timer = follow.value ? setInterval(() => void load(), 3000) : undefined
}

onMounted(() => {
  void store.refreshDesks().catch(() => undefined)
  void load()
  schedule()
})
onUnmounted(() => clearInterval(timer))
watch(filter, () => void load())
watch(follow, schedule)
</script>

<template>
  <div class="logs">
    <div class="filters">
      <select v-model="filter.component" :aria-label="t('logs.component')">
        <option value="">{{ t('logs.component') }}: {{ t('logs.all') }}</option>
        <option v-for="c in COMPONENTS" :key="c" :value="c">{{ c }}</option>
      </select>
      <select v-model="filter.desk" :aria-label="t('logs.desk')">
        <option value="">{{ t('logs.desk') }}: {{ t('logs.all') }}</option>
        <option v-for="d in store.state.desks" :key="d.id" :value="d.id">{{ d.name }}</option>
      </select>
      <select v-model.number="filter.tail" :aria-label="t('logs.tail')">
        <option v-for="n in TAILS" :key="n" :value="n">{{ t('logs.tail') }}: {{ n }}</option>
      </select>
      <label class="switch"><input v-model="follow" type="checkbox" /> {{ t('logs.follow') }}</label>
      <span class="spacer" />
      <button type="button" :disabled="loading" @click="load">{{ t('common.refresh') }}</button>
    </div>
    <p v-if="error" class="inline-msg" role="alert">{{ error }}</p>
    <div class="panel log">
      <p v-if="lines.length === 0" class="muted">{{ t('logs.empty') }}</p>
      <div v-for="(l, i) in lines" :key="i" :class="['line', `lvl-${l.level}`]">
        <span class="time">{{ fmtDateTime(l.time) }}</span>
        <span class="lvl">{{ l.level }}</span>
        <span class="comp">{{ l.component }}{{ l.desk ? `/${l.desk}` : '' }}</span>
        <span class="msg">{{ l.message }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.logs {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
}

.filters select {
  width: auto;
}

.inline-msg {
  margin: 0;
}

.log {
  font-family: var(--mono);
  font-size: 12px;
  max-height: calc(100vh - 250px);
  min-height: 200px;
  overflow: auto;
  padding: 12px 16px;
}

.line {
  display: grid;
  grid-template-columns: max-content 5ch 18ch 1fr;
  gap: 12px;
  white-space: pre-wrap;
  word-break: break-word;
  padding: 3px 0;
  color: var(--text-2);
}

.time {
  color: var(--faint);
}

.lvl {
  text-transform: uppercase;
  color: var(--muted);
}

.comp {
  color: var(--primary-ink);
}

.lvl-warn .lvl {
  color: var(--warning-ink);
}

.lvl-error .lvl,
.lvl-fatal .lvl {
  color: var(--danger-ink);
}

@media (width <= 640px) {
  .line {
    grid-template-columns: 1fr;
    gap: 0;
    margin-bottom: 6px;
  }
}
</style>
