<!-- SPDX-License-Identifier: MIT -->
<!-- PIN pairing dialog: sends the PIN shown by Moonlight to POST /desks/{id}/pair. -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api as defaultApi, ApiClient } from '../api/client'
import { errorText } from '../composables'
import { t } from '../i18n'
import ModalDialog from './ModalDialog.vue'
import PinField from './PinField.vue'

const props = withDefaults(
  defineProps<{ deskId: string; open: boolean; client?: ApiClient; deskName?: string; address?: string }>(),
  { client: () => defaultApi, deskName: '', address: '' },
)
const emit = defineEmits<{ close: []; paired: [name: string] }>()

const pin = ref('')
const name = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const touched = ref(false)
const pinValid = computed(() => /^\d{4}$/.test(pin.value))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    pin.value = ''
    name.value = ''
    error.value = null
    touched.value = false
  },
)

async function submit(): Promise<void> {
  touched.value = true
  if (!pinValid.value || busy.value) return
  busy.value = true
  error.value = null
  const clientName = name.value.trim() || t('desk.pairNamePlaceholder')
  try {
    await props.client.pair(props.deskId, { pin: pin.value, name: clientName })
    emit('paired', clientName)
    emit('close')
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <ModalDialog :open="open" :title="deskName ? `${t('desk.pin')} · ${deskName}` : t('desk.pinTitle')" @close="emit('close')">
    <form class="pair" novalidate @submit.prevent="submit">
      <div v-if="address" class="addr">
        <span class="label">{{ t('desk.address') }}</span>
        <span class="value">{{ address }}</span>
      </div>
      <div class="field center">
        <span class="field-label">{{ t('desk.pairPin') }}</span>
        <PinField id="pair-pin" v-model="pin" :invalid="touched && !pinValid" :label="t('desk.pairPin')" />
        <span v-if="touched && !pinValid" class="err">{{ t('desk.pinInvalid') }}</span>
      </div>
      <div class="field">
        <label for="pair-name">{{ t('desk.pairName') }}</label>
        <input id="pair-name" v-model="name" name="name" maxlength="64" :placeholder="t('desk.pairNamePlaceholder')" />
      </div>
      <p v-if="error" class="inline-msg" role="alert">{{ error }}</p>
      <div class="actions">
        <button type="button" class="btn-ghost" @click="emit('close')">{{ t('common.cancel') }}</button>
        <button type="submit" class="btn-primary" :disabled="busy">{{ t('desk.pairSubmit') }}</button>
      </div>
    </form>
  </ModalDialog>
</template>

<style scoped>
.pair {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.addr {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 14px;
  background: var(--fill);
  border-radius: var(--radius);
}

.addr .label {
  font-size: 13px;
  color: var(--muted);
}

.addr .value {
  font-variant-numeric: tabular-nums;
  color: var(--primary-ink);
  font-size: 15px;
  font-weight: 600;
}

.center {
  align-items: center;
  padding: 4px 0;
}

.inline-msg {
  margin: 0;
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.actions .btn-primary {
  min-width: 130px;
}
</style>
