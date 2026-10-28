<!-- SPDX-License-Identifier: MIT -->
<!-- Library: a grid of desk cards with Play / Stop, PIN pairing and the add-desk wizard (route desk-new). -->
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
    <header class="shelf">
      <span class="label">{{ t('library.all') }}</span>
      <span class="count">{{ s.desks.length }}</span>
      <span class="rule" />
    </header>

    <p v-if="!s.desksLoaded" class="muted">{{ t('common.loading') }}</p>
    <div v-else class="grid">
      <DeskCard
        v-for="d in s.desks"
        :key="d.id"
        :desk="d"
        :busy="busy === d.id"
        @start="act(d, 'start')"
        @stop="act(d, 'stop')"
        @pair="pairId = d.id"
      />
      <RouterLink to="/desks/new" class="add" data-add-desk>
        <span class="add-icon"><AppIcon name="plus" :size="30" /></span>
        <span class="add-label">{{ t('library.add') }}</span>
      </RouterLink>
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
.shelf {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 18px;
}

.count {
  font-size: 12px;
  font-weight: 700;
  color: var(--dim);
}

.rule {
  flex: 1;
  height: 1px;
  background: linear-gradient(90deg, rgb(255 255 255 / 10%), transparent);
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 22px;
}

.add {
  min-height: 260px;
  border-radius: 4px;
  border: 2px dashed rgb(102 192 244 / 22%);
  background: rgb(33 50 68 / 35%);
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 14px;
  color: var(--muted);
  transition:
    border-color 0.18s ease,
    background 0.18s ease,
    color 0.18s ease,
    box-shadow 0.18s ease;
}

.add:hover {
  border-color: var(--accent);
  background: rgb(102 192 244 / 8%);
  color: #fff;
  box-shadow: 0 0 22px rgb(102 192 244 / 15%);
}

.add-icon {
  width: 64px;
  height: 64px;
  border-radius: 50%;
  display: grid;
  place-items: center;
  background: rgb(102 192 244 / 12%);
  color: var(--accent);
  transition:
    background 0.18s ease,
    transform 0.18s ease;
}

.add:hover .add-icon {
  background: var(--accent);
  color: #10202e;
  transform: scale(1.05);
}

.add-label {
  font-size: 15px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}
</style>
