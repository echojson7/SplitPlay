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
  <ModalDialog :open="open" :title="deskName ? `${t('desk.pinTitle')} · ${deskName}` : t('desk.pinTitle')" @close="emit('close')">
    <form class="pair" novalidate @submit.prevent="submit">
      <div v-if="address" class="field">
        <span class="field-label">{{ t('desk.address') }}</span>
        <span class="value">{{ address }}</span>
      </div>
      <div class="field">
        <label for="pair-pin">{{ t('desk.pairPin') }}</label>
        <PinField id="pair-pin" v-model="pin" :invalid="touched && !pinValid" :label="t('desk.pairPin')" />
        <span v-if="touched && !pinValid" class="err">{{ t('desk.pinInvalid') }}</span>
      </div>
      <div class="field">
        <label for="pair-name">{{ t('desk.pairName') }}</label>
        <input id="pair-name" v-model="name" name="name" maxlength="64" :placeholder="t('desk.pairNamePlaceholder')" />
      </div>
      <p v-if="error" class="inline-msg" role="alert">{{ error }}</p>
      <div class="dlg-foot">
        <button type="submit" class="btn-accent" :disabled="busy">{{ t('desk.pairSubmit') }}</button>
        <button type="button" @click="emit('close')">{{ t('common.cancel') }}</button>
      </div>
    </form>
  </ModalDialog>
</template>

<style scoped>
.pair {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.value {
  font-variant-numeric: tabular-nums;
  user-select: all;
}

.dlg-foot > button {
  flex: 1;
}
</style>
