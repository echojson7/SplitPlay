<!-- SPDX-License-Identifier: MIT -->
<!-- Desks page: one row per desk with Start / Stop, PIN pairing and the add-desk wizard (route desk-new). -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { api } from '../api/client'
import type { Desk, DeskConfig, HostUser } from '../api/types'
import AddDeskWizard from '../components/AddDeskWizard.vue'
import AppIcon from '../components/AppIcon.vue'
import DeskCard from '../components/DeskCard.vue'
import PairDialog from '../components/PairDialog.vue'
import { errorText, useLive } from '../composables'
import { moonlightAddress } from '../desk'
import { t } from '../i18n'
import { store } from '../store'
import { toast } from '../toast'

useLive()
const route = useRoute()
const router = useRouter()
const s = store.state

const busy = ref<string | null>(null)
const pairId = ref<string | null>(null)
const pairDesk = computed(() => s.desks.find((d) => d.id === pairId.value) ?? null)

const wizardOpen = computed(() => route.name === 'desk-new')
const users = ref<HostUser[]>([])
const creating = ref(false)
const createError = ref<string | null>(null)

if (s.addresses === null) void store.refreshAddresses()

watch(
  wizardOpen,
  (open) => {
    if (!open) return
    createError.value = null
    // Without the list the user name is typed (older service).
    api
      .listHostUsers()
      .then((list) => (users.value = list))
      .catch(() => (users.value = []))
  },
  { immediate: true },
)

async function act(desk: Desk, action: 'start' | 'stop'): Promise<void> {
  busy.value = desk.id
  try {
    store.upsertDesk(action === 'start' ? await api.startDesk(desk.id) : await api.stopDesk(desk.id))
  } catch (e) {
    toast(errorText(e), 'error')
  } finally {
    busy.value = null
  }
}

async function create(config: DeskConfig): Promise<void> {
  creating.value = true
  createError.value = null
  let created: Desk
  try {
    created = await api.createDesk(config)
    store.upsertDesk(created)
  } catch (e) {
    createError.value = errorText(e)
    creating.value = false
    return
  }
  toast(t('wizard.created', { name: created.name }))
  try {
    store.upsertDesk(await api.startDesk(created.id))
  } catch (e) {
    toast(errorText(e), 'error')
  } finally {
    creating.value = false
  }
  await router.push(`/desks/${encodeURIComponent(created.id)}`)
}

function closeWizard(): void {
  if (!creating.value) void router.push('/')
}

function onPaired(name: string): void {
  toast(t('desk.paired', { name }))
}
</script>

<template>
  <section class="library">
    <header class="page-head">
      <h1>{{ t('nav.library') }}</h1>
      <RouterLink to="/desks/new" class="btn btn-accent" data-add-desk>
        <AppIcon name="plus" :size="16" />{{ t('library.add') }}
      </RouterLink>
    </header>

    <p v-if="!s.desksLoaded" class="muted">{{ t('common.loading') }}</p>
    <div v-else class="rows">
      <DeskCard
        v-for="d in s.desks"
        :key="d.id"
        :desk="d"
        :busy="busy === d.id"
        @start="act(d, 'start')"
        @stop="act(d, 'stop')"
        @pair="pairId = d.id"
      />
    </div>

    <PairDialog
      :desk-id="pairId ?? ''"
      :open="pairDesk !== null"
      :desk-name="pairDesk?.name"
      :address="pairDesk && s.addresses?.[0] ? moonlightAddress(pairDesk, s.addresses[0]) : ''"
      @close="pairId = null"
      @paired="onPaired"
    />
    <AddDeskWizard
      :open="wizardOpen"
      :users="users"
      :desks="s.desks"
      :busy="creating"
      :error="createError"
      @close="closeWizard"
      @create="create"
    />
  </section>
</template>

<style scoped>
.page-head .btn {
  gap: 8px;
}

@media (width <= 600px) {
  .page-head {
    margin-bottom: 16px;
  }
}
</style>
