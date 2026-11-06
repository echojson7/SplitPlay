<!-- SPDX-License-Identifier: MIT -->
<!-- Moonlight waits for a PIN: one row per such desk, on every page, with the PIN field (desk.pinRequest). -->
<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from 'vue'
import { api as defaultApi, ApiClient } from '../api/client'
import type { Desk } from '../api/types'
import { errorText, useLive } from '../composables'
import { t } from '../i18n'
import { toast } from '../toast'
import AppIcon from './AppIcon.vue'
import PinField from './PinField.vue'

const props = withDefaults(defineProps<{ desks: readonly Desk[]; client?: ApiClient; live?: boolean }>(), {
  client: () => defaultApi,
  live: false,
})
// In the app shell the rows must appear on every page, also those that do not follow the desks themselves.
if (props.live) useLive()

const waiting = computed(() => props.desks.filter((d) => d.pinRequest))
const pins = reactive<Record<string, string>>({})
const names = reactive<Record<string, string>>({})
const tried = reactive<Record<string, boolean>>({})
const errors = reactive<Record<string, string | null>>({})
const busy = ref<string | null>(null)
const fields = ref<InstanceType<typeof PinField>[]>([])

function valid(id: string): boolean {
  return /^\d{4}$/.test(pins[id] ?? '')
}

// A new request gets the focus, unless the user is typing somewhere else.
watch(
  () => waiting.value.map((d) => `${d.id}@${d.pinRequest?.since ?? ''}`).join(','),
  async (now, before) => {
    const old = new Set((before ?? '').split(','))
    const fresh = now.split(',').filter((k) => k && !old.has(k))
    if (fresh.length === 0) return
    for (const key of fresh) {
      const id = key.slice(0, key.lastIndexOf('@'))
      errors[id] = null
    }
    await nextTick()
    const active = document.activeElement
    if (!active || active === document.body) fields.value[0]?.focus()
  },
  { immediate: true },
)

async function send(desk: Desk): Promise<void> {
  tried[desk.id] = true
  if (!valid(desk.id) || busy.value) return
  busy.value = desk.id
  errors[desk.id] = null
  const name = (names[desk.id] ?? '').trim() || t('desk.pairNamePlaceholder')
  try {
    await props.client.pair(desk.id, { pin: pins[desk.id]!, name })
    toast(t('desk.paired', { name }))
    pins[desk.id] = ''
    names[desk.id] = ''
    tried[desk.id] = false
  } catch (e) {
    errors[desk.id] = errorText(e)
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <div v-if="waiting.length" class="rows pin-requests" aria-live="polite">
    <form
      v-for="d in waiting"
      :key="d.id"
      class="srow"
      :data-pin-request="d.id"
      novalidate
      @submit.prevent="send(d)"
    >
      <AppIcon name="link" :size="16" class="accent" />
      <div class="text">
        <span class="title">{{ t('pin.waiting') }} · {{ d.name || d.id }}</span>
        <span v-if="tried[d.id] && !valid(d.id)" class="desc err">{{ t('desk.pinInvalid') }}</span>
        <span v-else-if="errors[d.id]" class="desc err" role="alert">{{ errors[d.id] }}</span>
        <span v-else-if="d.pinRequest?.address" class="desc">{{ d.pinRequest.address }}</span>
      </div>
      <div class="ctl">
        <PinField
          :id="`pin-${d.id}`"
          ref="fields"
          :model-value="pins[d.id] ?? ''"
          :invalid="tried[d.id] && !valid(d.id)"
          :label="t('desk.pairPin')"
          @update:model-value="(v) => (pins[d.id] = v)"
        />
        <input
          v-model="names[d.id]"
          class="pair-name"
          name="pairName"
          maxlength="64"
          :aria-label="t('desk.pairName')"
          :placeholder="t('desk.pairNamePlaceholder')"
        />
        <button type="submit" class="btn-accent" :disabled="busy === d.id">{{ t('desk.pairSubmit') }}</button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.pin-requests {
  margin-bottom: 16px;
}

.accent {
  color: var(--accent);
}

.ctl {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.pair-name {
  width: 140px;
}

@media (width <= 640px) {
  .srow {
    flex-wrap: wrap;
  }

  .ctl {
    width: 100%;
    justify-content: flex-start;
  }
}
</style>
