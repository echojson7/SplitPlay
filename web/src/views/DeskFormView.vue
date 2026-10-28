<!-- SPDX-License-Identifier: MIT -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, deskToConfig } from '../api/client'
import type { DeskConfig } from '../api/types'
import DeskForm from '../components/DeskForm.vue'
import { errorText, isRunning } from '../composables'
import { t } from '../i18n'
import { store } from '../store'

const props = defineProps<{ id?: string }>()
const router = useRouter()
const s = store.state

const loaded = ref(false)
const error = ref<string | null>(null)
const busy = ref(false)
const desk = computed(() => (props.id ? (s.desks.find((d) => d.id === props.id) ?? null) : null))
const initial = computed<DeskConfig | null>(() => (desk.value ? deskToConfig(desk.value) : null))

onMounted(async () => {
  try {
    await Promise.all([
      props.id ? api.getDesk(props.id).then(store.upsertDesk) : store.refreshDesks(),
      store.refreshMetrics().catch(() => undefined),
    ])
  } catch (e) {
    error.value = errorText(e)
  } finally {
    loaded.value = true
  }
})

async function save(config: DeskConfig): Promise<void> {
  busy.value = true
  error.value = null
  try {
    const saved = props.id ? await api.updateDesk(props.id, config) : await api.createDesk(config)
    store.upsertDesk(saved)
    await router.push(`/desks/${saved.id}`)
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = false
  }
}

function cancel(): void {
  void router.push(props.id ? `/desks/${props.id}` : '/')
}
</script>

<template>
  <section>
    <h1>{{ id ? t('form.editTitle', { name: desk?.name ?? id }) : t('form.createTitle') }}</h1>
    <p v-if="error" class="notice bad" role="alert">{{ t('common.error', { message: error }) }}</p>
    <p v-if="!loaded" class="muted">{{ t('common.loading') }}</p>
    <DeskForm
      v-else-if="!id || initial"
      :key="id ?? 'new'"
      :initial="initial"
      :running="desk ? isRunning(desk) : false"
      :gpus="s.metrics?.host.gpus ?? []"
      :existing-ids="s.desks.map((d) => d.id)"
      :busy="busy"
      @submit="save"
      @cancel="cancel"
    />
  </section>
</template>
