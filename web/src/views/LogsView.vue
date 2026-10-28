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
  <section>
    <h1>{{ t('logs.title') }}</h1>
    <div class="row filters">
      <label>
        {{ t('logs.component') }}
        <select v-model="filter.component">
          <option value="">{{ t('logs.all') }}</option>
          <option v-for="c in COMPONENTS" :key="c" :value="c">{{ c }}</option>
        </select>
      </label>
      <label>
        {{ t('logs.desk') }}
        <select v-model="filter.desk">
          <option value="">{{ t('logs.all') }}</option>
          <option v-for="d in store.state.desks" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
      </label>
      <label>
        {{ t('logs.tail') }}
        <select v-model.number="filter.tail">
          <option v-for="n in TAILS" :key="n" :value="n">{{ n }}</option>
        </select>
      </label>
      <label class="check"><input v-model="follow" type="checkbox" /> {{ t('logs.follow') }}</label>
      <div class="spacer" />
      <button type="button" :disabled="loading" @click="load">{{ t('common.refresh') }}</button>
    </div>
    <p v-if="error" class="notice bad" role="alert">{{ t('common.error', { message: error }) }}</p>
    <div class="card log">
      <p v-if="lines.length === 0" class="muted">{{ t('logs.empty') }}</p>
      <div v-for="(l, i) in lines" :key="i" :class="['line', `lvl-${l.level}`]">
        <span class="muted">{{ fmtDateTime(l.time) }}</span>
        <span class="lvl">{{ l.level }}</span>
        <span class="comp">{{ l.component }}{{ l.desk ? `/${l.desk}` : '' }}</span>
        <span class="msg">{{ l.message }}</span>
      </div>
    </div>
  </section>
</template>

<style scoped>
.filters {
  margin-bottom: 1rem;
  gap: 1rem;
}

.check {
  margin: 0;
}

.log {
  font-family: var(--mono);
  font-size: 0.82rem;
  max-height: 70vh;
  overflow: auto;
}

.line {
  display: grid;
  grid-template-columns: max-content 5ch 16ch 1fr;
  gap: 0.75rem;
  white-space: pre-wrap;
  word-break: break-word;
  padding: 0.1rem 0;
}

.lvl {
  text-transform: uppercase;
}

.lvl-warn .lvl {
  color: var(--warn);
}

.lvl-error .lvl,
.lvl-fatal .lvl {
  color: var(--bad);
}

@media (width <= 640px) {
  .line {
    grid-template-columns: 1fr;
    gap: 0;
    margin-bottom: 0.4rem;
  }
}
</style>
